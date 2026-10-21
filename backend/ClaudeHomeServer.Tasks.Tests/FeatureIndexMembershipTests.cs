using System.Collections;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Золотые тесты индекса «чат → фичи» (контракт С0, шаг Б1): FeatureProjector.BuildIndexCore.
// Чистая функция: задачи владельца (List<TaskItem>) + узкий снимок сессий (FeatureIndexSession)
// → карта членства (Chats) и строки-заготовки фич (Features). Хранилище сессий не трогает.
//
// Членство (сильнейший вид на пару «чат × фича»):
//   executor     — Session.TaskId → задача с FeatureId == F (или сама эпик F);
//   curator      — epic.SourceSessionId == чат;
//   orchestrator — epic/дочерняя задача запускала исполнителя из этого чата (ExecutorLaunchedBySessionId).
// SourceSessionId дочерних задач — НЕ членство (слабая связь).
public class FeatureIndexMembershipTests
{
    // ── Хелперы сборки фикстур ─────────────────────────────────────────────────────────────

    private static TaskItem Epic(string id, string? sourceSessionId = null,
        string? executorLaunchedBy = null, string? projectId = null) => new()
    {
        Id = id,
        Role = TaskRoles.Feature,
        FeatureId = null,
        SourceSessionId = sourceSessionId,
        ExecutorLaunchedBySessionId = executorLaunchedBy,
        ProjectId = projectId,
    };

    private static TaskItem Child(string id, string? featureId, string role = TaskRoles.Step,
        string? stepRef = null, string? lane = null,
        string? executorLaunchedBy = null, string? sourceSessionId = null) => new()
    {
        Id = id,
        FeatureId = featureId,
        Role = role,
        StepRef = stepRef,
        Lane = lane,
        ExecutorLaunchedBySessionId = executorLaunchedBy,
        SourceSessionId = sourceSessionId,
    };

    private static FeatureIndexSession Session(string id, string? taskId = null,
        string? projectId = null, bool archived = false) =>
        new(id, taskId, projectId, archived);

    // ── 1. Ручная задача без featureId: её чат не в фиче ───────────────────────────────────

    // Задача, созданная вручную (FeatureId=null, не эпик) → её чат-исполнитель не входит ни в
    // одну фичу («догадок не делаем»). Чат задачи с FeatureId — в фиче.
    [Fact]
    public void Членство_РучнаяЗадачаБезFeatureId_ЧатНеВФиче()
    {
        var owner = new List<TaskItem>
        {
            Epic("epic1"),
            Child("t1", "epic1"),                       // шаг фичи epic1
            Child("manual", null, role: TaskRoles.Step), // ручная задача, без featureId
        };
        var sessions = new[]
        {
            Session("s1", taskId: "t1"),
            Session("s2", taskId: "manual"),
        };

        var index = FeatureProjector.BuildIndexCore(owner, sessions);

        // Чат t1 — в фиче epic1 (executor).
        index.Chats.Should().ContainKey("s1");
        index.Chats["s1"].Should().ContainSingle()
            .Which.Should().Be(new FeatureIndexChatEntry("epic1", "executor", TaskRoles.Step, null, null));
        // Чат ручной задачи — ни в одной фиче.
        index.Chats.Should().NotContainKey("s2");
    }

    // ── 2. Смена featureId: членство переезжает (вычисляется, а не хранится) ────────────────

    // tasks_update сменил FeatureId задачи → членство чата переезжает само: членство
    // вычисляется из текущих полей, а не хранится.
    [Fact]
    public void Членство_СменаFeatureId_Переезжает()
    {
        var owner = new List<TaskItem>
        {
            Epic("epic1"),
            Epic("epic2"),
            Child("t1", "epic1"),
        };
        var sessions = new[] { Session("s1", taskId: "t1") };

        // До смены: чат в epic1.
        FeatureProjector.BuildIndexCore(owner, sessions).Chats["s1"][0].FeatureId.Should().Be("epic1");

        // Смена featureId (tasks_update).
        owner.Single(t => t.Id == "t1").FeatureId = "epic2";

        // После смены: членство переехало в epic2, из epic1 чат выпал.
        var after = FeatureProjector.BuildIndexCore(owner, sessions);
        after.Chats["s1"].Should().ContainSingle().Which.FeatureId.Should().Be("epic2");
        after.Features.Select(f => f.FeatureId).Should().BeEquivalentTo(new[] { "epic1", "epic2" });
    }

    // ── 3. Удалённая задача: висячий Session.TaskId, чат уходит из фичи ─────────────────────

    // Задача удалена → Session.TaskId висячий → чат-исполнитель выходит из фичи (тот же
    // осознанный приём, что у ParentSessionId: «всплывает в корень»).
    [Fact]
    public void Членство_УдалённаяЗадача_ЧатУходитИзФичи()
    {
        var epic = Epic("epic1");
        var task = Child("t1", "epic1");
        var sessions = new[] { Session("s1", taskId: "t1") };

        // До удаления: чат в фиче.
        FeatureProjector.BuildIndexCore([epic, task], sessions).Chats.Should().ContainKey("s1");

        // Удаляем задачу (эпик остаётся).
        var ownerAfter = new List<TaskItem> { epic };
        var after = FeatureProjector.BuildIndexCore(ownerAfter, sessions);

        // Висячий TaskId: чат больше ни в одной фиче.
        after.Chats.Should().NotContainKey("s1");
    }

    // ── 4. Удалённый эпик: фича исчезает целиком, чаты за ней не тянутся ────────────────────

    // Эпик удалён → дочерние задачи с висячим FeatureId проектор «не видит» как фичу: строка
    // фичи исчезает, и чат-исполнитель её задачи за ней не тянется.
    [Fact]
    public void Членство_УдалённыйЭпик_ФичаИсчезает()
    {
        var task = Child("t1", "epic1");
        var sessions = new[] { Session("s1", taskId: "t1") };

        // До удаления эпика: фича есть, чат в ней.
        var withEpic = FeatureProjector.BuildIndexCore([Epic("epic1"), task], sessions);
        withEpic.Features.Select(f => f.FeatureId).Should().ContainSingle().Which.Should().Be("epic1");
        withEpic.Chats.Should().ContainKey("s1");

        // Удаляем эпик (дочерняя задача t1 с висячим FeatureId остаётся).
        var after = FeatureProjector.BuildIndexCore([task], sessions);

        // Фича исчезла целиком: ни строки, ни членства.
        after.Features.Should().BeEmpty();
        after.Chats.Should().BeEmpty();
    }

    // ── 5. Мультифича у куратора + порядок по силе связи ───────────────────────────────────

    // Кураторский чат ведёт ДВА эпика (оба SourceSessionId = чат) → обе фичи в его списке.
    // Тот же чат ещё и исполнитель задачи первой фичи → на пару «чат × epic1» сильнейший вид
    // curator перекрывает executor. Порядок записей — по силе связи (curator перед executor).
    [Fact]
    public void Членство_МультифичаКуратора_ПорядокПоСилеСвязи()
    {
        var owner = new List<TaskItem>
        {
            Epic("epicA", sourceSessionId: "curator"),
            Epic("epicB", sourceSessionId: "curator"),
            Child("tA", "epicA"),
        };
        var sessions = new[] { Session("curator", taskId: "tA") };

        var index = FeatureProjector.BuildIndexCore(owner, sessions);

        // Кураторский чат — в ОБЕИХ фичах.
        index.Chats.Should().ContainKey("curator");
        var entries = index.Chats["curator"];
        entries.Select(e => e.FeatureId).Should().BeEquivalentTo(new[] { "epicA", "epicB" });

        // На пару «чат × epicA» сильнейший вид — curator (перекрывает executor по tA).
        var a = entries.Single(e => e.FeatureId == "epicA");
        a.Kind.Should().Be("curator");
        a.TaskRole.Should().BeNull("curator перекрывает executor — role не показываем");
        // epicB — только curator (чат не исполнитель её задач).
        entries.Single(e => e.FeatureId == "epicB").Kind.Should().Be("curator");

        // Порядок по силе связи: curator (1) перед executor (3).
        // Здесь обе записи curator — порядок по Features; проверяем, что curator всегда ≤ executor.
        var strengths = entries.Select(e => e.Kind switch
        {
            "curator" => 1,
            "orchestrator" => 2,
            _ => 3,
        }).ToList();
        strengths.Should().BeInAscendingOrder("список упорядочен по убыванию силы связи");
    }

    // Сила связи: один чат — curator одной фичи и executor другой.
    // Волна 3 (Б-ord): «своя фича» (executor по TaskId) ПЕРЕД curator — приоритет TaskId
    // перекрывает силу связи. До Б-ord curator был первым (сильнейший вид).
    [Fact]
    public void Членство_СилаСвязи_CuratorПередExecutor()
    {
        var owner = new List<TaskItem>
        {
            Epic("epicA", sourceSessionId: "chatX"),   // chatX — куратор epicA
            Epic("epicB"),
            Child("tB", "epicB"),                        // chatX — исполнитель epicB
        };
        var sessions = new[] { Session("chatX", taskId: "tB") };

        var index = FeatureProjector.BuildIndexCore(owner, sessions);

        var entries = index.Chats["chatX"];
        entries.Should().HaveCount(2);
        // Б-ord: своя фича (epicB, executor по TaskId) — первая, curator (epicA) — вторая.
        entries[0].Should().Be(new FeatureIndexChatEntry("epicB", "executor", TaskRoles.Step, null, null));
        entries[1].Should().Be(new FeatureIndexChatEntry("epicA", "curator", null, null, null));
    }

    // ── 6. Чужая задача не видна (изоляция per-owner) ───────────────────────────────────────

    // Задачи другого владельца (OwnerId ≠ ownerId) не попадают в owner-срез → их чаты и фичи
    // не видны. BuildIndexCore получает уже отобранный по владельцу список (fail-closed).
    [Fact]
    public void Членство_ЧужаяЗадача_НеВидна()
    {
        // «Мой» эпик и задача; «чужой» эпик и задача другого владельца.
        var mine = new List<TaskItem>
        {
            Epic("epicMine"),
            Child("tMine", "epicMine"),
        };
        // Чужая задача ссылается на чужой эпик; её чат-исполнитель — sOther.
        var other = new List<TaskItem>
        {
            Epic("epicOther"),
            Child("tOther", "epicOther"),
        };
        var sessions = new[]
        {
            Session("sMine", taskId: "tMine"),
            Session("sOther", taskId: "tOther"),
        };

        // owner-срез = только «мои» задачи (GetByOwner отфильтровал чужие).
        var index = FeatureProjector.BuildIndexCore(mine, sessions);

        // Чат моей задачи — в моей фиче.
        index.Chats.Should().ContainKey("sMine");
        index.Chats["sMine"][0].FeatureId.Should().Be("epicMine");
        // Чат чужой задачи (sOther → tOther) не резолвится: tOther не в owner-срезе.
        index.Chats.Should().NotContainKey("sOther");
        // Чужая фича строку не даёт.
        index.Features.Select(f => f.FeatureId).Should().NotContain("epicOther");
    }

    // ── 6b. Per-owner изоляция в едином правиле членства (FeatureChatMembership, минор 4) ────

    // Карта FeatureChatMembership строится по задачам и сессиям ВЛАДЕЛЬЦА: снимок (snapshot)
    // — результат GetAllOwnedBy(UserId), чужая сессия в нём не живёт. Даже если чужой чат
    // несёт TaskId задачи владельца (executor-ось его бы поймала), без снимка-владельца он в
    // карту не попадёт. Контроль: тот же чат, ДОБАВЛЕННЫЙ в снимок, карта ловит (executor) —
    // значит, именно per-owner-фильтр снимка даёт изоляцию, а не executor-правило.
    [Fact]
    public void Членство_ЧужойЧат_НеПопадает_PerOwner()
    {
        var owner = new List<TaskItem>
        {
            Epic("epicA"),
            Child("stepA", "epicA"),
        };
        // Снимок владельца: только его чат sMine (TaskId=stepA). Чужий чат sOther (другой
        // владелец) в снимке НЕ живёт — GetAllOwnedBy(OwnerId) его отфильтровал.
        var ownerSnapshot = new[] { Session("sMine", taskId: "stepA") };

        var map = FeatureChatMembership.Build(owner, ownerSnapshot);

        // Чат владельца — в карте (executor → epicA).
        map.Should().ContainKey("sMine");
        map["sMine"].Should().BeEquivalentTo(new[] { "epicA" });
        // Чужой чат (sOther) не в снимке → не в карте, даже при TaskId=stepA.
        map.Should().NotContainKey("sOther", "чужой чат вне снимка-владельца → per-owner изоляция");

        // Контроль: тот же чат, добавленный в снимок, executor-ось ловит → изоляция именно
        // в per-owner-фильтре снимка, не в правиле.
        var withOther = new[] { Session("sMine", taskId: "stepA"), Session("sOther", taskId: "stepA") };
        FeatureChatMembership.Build(owner, withOther).Should().ContainKey("sOther");
    }

    // ── Мутация: O(E×T) — owner сканируется константное число раз, а не на каждый эпик ──────

    // Ядро индекса (BuildIndexCore) перечисливает список задач владельца константное число раз
    // (byId + epics + orchestrator + ОДИН ToLookup-скан по FeatureId = 4) — O(E+T), а не O(E×T)
    // (полный скан owner на каждый эпик — старый FeatureTasks). Тест считает перечисления:
    // на 50 эпиках мутация O(E×T) даёт 4+2E = 104 перечисления, исправленный код — 4 (порог 10
    // — между ними).
    [Fact]
    public void Мутация_Индекс_OwnerСканируетсяКонстантно_НеOExT()
    {
        const int epics = 50;
        const int childrenPerEpic = 100;
        var owner = new List<TaskItem>(epics * (1 + childrenPerEpic));
        for (var i = 0; i < epics; i++)
        {
            var epicId = $"epic-{i}";
            owner.Add(Epic(epicId));
            for (var j = 0; j < childrenPerEpic; j++)
                owner.Add(Child($"{epicId}-t{j}", epicId));
        }
        var counting = new CountingOwner(owner);
        var sessions = Array.Empty<FeatureIndexSession>();

        FeatureProjector.BuildIndexCore(counting, sessions);

        counting.Enumerations.Should().BeLessThanOrEqualTo(10,
            "owner перечисливается константное число раз (O(E+T)), а не на каждый эпик (O(E×T))");
    }

    // Обёртка, считающая вызовы GetEnumerator(): BuildIndexCore принимает IReadOnlyList,
    // поэтому каждый скан (ToDictionary/Where/foreach/ToLookup) диспатчится через этот метод.
    // Обёртка, а не наследник List: у наследника `new`-GetEnumerator НЕ становится
    // реализацией интерфейса (interface map держит метод базовой List<T>) — проверено.
    private sealed class CountingOwner : IReadOnlyList<TaskItem>
    {
        private readonly List<TaskItem> _inner;
        public int Enumerations { get; private set; }
        public CountingOwner(List<TaskItem> items) => _inner = items;
        public TaskItem this[int index] => _inner[index];
        public int Count => _inner.Count;
        public IEnumerator<TaskItem> GetEnumerator()
        {
            Enumerations++;
            return _inner.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ── Мутация к ключевому правилу: SourceSessionId дочерних — НЕ членство ─────────────────

    // Ключевое правило Б1: SourceSessionId дочерних задач — слабая связь, в членство НЕ входит
    // (входит только SourceSessionId ЭПИКА как curator). Мутация: считаем SourceSessionId
    // дочерней задачи членством (curator) → чат, создавший дочернюю задачу, ошибочно попадает
    // в фичу. Без мутации тест зелёный.
    [Fact]
    public void Мутация_SourceSessionIdДочерних_НеЧленство()
    {
        // Эпик без SourceSessionId; дочерняя задача создана из чата "creator" (SourceSessionId).
        var owner = new List<TaskItem>
        {
            Epic("epic1"),
            Child("t1", "epic1", sourceSessionId: "creator"),
        };
        // Чат-создатель дочерней задачи НЕ исполнитель (TaskId=null) и НЕ куратор эпика.
        var sessions = new[] { Session("creator") };

        var index = FeatureProjector.BuildIndexCore(owner, sessions);

        // SourceSessionId дочерней — слабая связь: чат-создатель в членство не входит.
        index.Chats.Should().NotContainKey("creator",
            "SourceSessionId дочерней задачи — не членство (входит только SourceSessionId эпика)");
    }

    // ── 9. Порядок фич у чата: своя фича (TaskId) первой (волна 3, Б-ord) ────────────────────

    // Чат — исполнитель шага фичи B (TaskId → tB, FeatureId=epicB) И куратор фичи A.
    // В индексе: B первой (своя фича), A второй (curator). Сила связи (curator=1 < executor=3)
    // НЕ перекрывает приоритет TaskId: PreferFirst ставит B первым, несмотря на curator.
    [Fact]
    public void Индекс_СвояФичаПервой_ExecutorИCurator()
    {
        var owner = new List<TaskItem>
        {
            Epic("epicA", sourceSessionId: "chatX"),   // chatX — куратор epicA
            Epic("epicB"),
            Child("tB", "epicB"),                       // chatX — исполнитель epicB (TaskId=tB)
        };
        var sessions = new[] { Session("chatX", taskId: "tB") };

        var index = FeatureProjector.BuildIndexCore(owner, sessions);

        var entries = index.Chats["chatX"];
        entries.Should().HaveCount(2);
        // Своя фича (epicB, executor по TaskId) — первая.
        entries[0].FeatureId.Should().Be("epicB", "фича, чья задача — TaskId чата, первой");
        entries[0].Kind.Should().Be("executor");
        // Кураторская фича (epicA) — вторая.
        entries[1].FeatureId.Should().Be("epicA");
        entries[1].Kind.Should().Be("curator");
    }

    // Чат без TaskId (оркестратор/куратор, не исполнитель) → прежний порядок (сила связи).
    [Fact]
    public void Индекс_БезTaskId_ПрежнийПорядок()
    {
        var owner = new List<TaskItem>
        {
            Epic("epicA", sourceSessionId: "chatX"),
            Epic("epicB", sourceSessionId: "chatX"),
        };
        // chatX — куратор обеих фич, но НЕ исполнитель (TaskId=null).
        var sessions = new[] { Session("chatX") };

        var index = FeatureProjector.BuildIndexCore(owner, sessions);

        var entries = index.Chats["chatX"];
        entries.Should().HaveCount(2);
        // Обе curator, без TaskId → PreferFirst не меняет порядок (оба null → исходный).
        // Порядок — по featureOrder (OrderRuns): обе idle, детерминизм по id.
        entries.Select(e => e.FeatureId).Should().BeEquivalentTo(new[] { "epicA", "epicB" });
    }

    // OwnFeatureId: TaskId → задача с FeatureId → возвращает FeatureId.
    [Fact]
    public void OwnFeatureId_ЗадачаСFeatureId_ВозвращаетFeatureId()
    {
        var byId = new Dictionary<string, TaskItem>
        {
            ["t1"] = new TaskItem { Id = "t1", FeatureId = "epicX", Role = TaskRoles.Step },
            ["epicY"] = new TaskItem { Id = "epicY", FeatureId = null, Role = TaskRoles.Feature },
            ["manual"] = new TaskItem { Id = "manual", FeatureId = null, Role = TaskRoles.Step },
        };
        var sessions = new[]
        {
            Session("s1", taskId: "t1"),
            Session("s2", taskId: "epicY"),
            Session("s3", taskId: "manual"),
            Session("s4"), // без TaskId
        };

        FeatureProjector.OwnFeatureId("s1", sessions, byId).Should().Be("epicX");
        FeatureProjector.OwnFeatureId("s2", sessions, byId).Should().Be("epicY", "эпик — сама фича");
        FeatureProjector.OwnFeatureId("s3", sessions, byId).Should().BeNull("ручная задача без FeatureId");
        FeatureProjector.OwnFeatureId("s4", sessions, byId).Should().BeNull("нет TaskId");
    }

    // PreferFirst: элемент с preferredId — первым, остальное в исходном порядке.
    [Fact]
    public void PreferFirst_ПереставляетПервым_ОстальноеНеМеняется()
    {
        var items = new List<string> { "a", "b", "c" };
        var result = FeatureProjector.PreferFirst(items, s => s, "c");
        result.Should().BeEquivalentTo(new[] { "c", "a", "b" });

        // preferredId == null → исходный порядок.
        FeatureProjector.PreferFirst(items, s => s, null).Should().BeEquivalentTo(new[] { "a", "b", "c" });
        // preferredId не найден → исходный порядок.
        FeatureProjector.PreferFirst(items, s => s, "z").Should().BeEquivalentTo(new[] { "a", "b", "c" });
    }

    // Оба маршрута совпадают: индекс (BuildIndexCore) и ProjectForSession (Build + OrderRuns +
    // OwnFeatureId + PreferFirst) дают одинаковый порядок фич для чата в двух фичах.
    [Fact]
    public void ОбаМаршрута_Совпадают_СвояФичаПервой()
    {
        var owner = new List<TaskItem>
        {
            Epic("epicA", sourceSessionId: "chatX"),   // chatX — куратор epicA
            Epic("epicB", executorLaunchedBy: "chatX"), // chatX — оркестратор epicB (в ProjectForSession)
            Child("tB", "epicB"),                       // chatX — исполнитель epicB (TaskId=tB)
        };
        var sessions = new[] { Session("chatX", taskId: "tB") };

        // Маршрут 1: индекс.
        var index = FeatureProjector.BuildIndexCore(owner, sessions);
        var indexOrder = index.Chats["chatX"].Select(e => e.FeatureId).ToList();

        // Маршрут 2: ProjectForSession (эмулируем: Build + OrderRuns + OwnFeatureId + PreferFirst).
        var byId = owner.ToDictionary(t => t.Id);
        var epicsForSession = owner
            .Where(t => t.Role == TaskRoles.Feature
                && (t.SourceSessionId == "chatX" || t.ExecutorLaunchedBySessionId == "chatX"))
            .ToList();
        var runs = epicsForSession
            .Select(epic => FeatureProjector.Build(epic, FeatureTasksStatic(epic, owner)))
            .ToList();
        var ordered = FeatureProjector.OrderRuns(runs);
        var own = FeatureProjector.OwnFeatureId("chatX", sessions, byId);
        var sessionOrder = FeatureProjector.PreferFirst(ordered, r => r.FeatureId, own)
            .Select(r => r.FeatureId).ToList();

        // Оба маршрута: epicB (своя) первым, epicA вторым.
        indexOrder.Should().BeEquivalentTo(new[] { "epicB", "epicA" });
        sessionOrder.Should().BeEquivalentTo(new[] { "epicB", "epicA" });
    }

    // Вспомогательный: FeatureTasks (эпик + дочерние) — статический дубль для теста.
    private static List<TaskItem> FeatureTasksStatic(TaskItem epic, IReadOnlyList<TaskItem> owner)
    {
        var all = new List<TaskItem> { epic };
        all.AddRange(owner.Where(t => t.FeatureId == epic.Id));
        return all;
    }
}
