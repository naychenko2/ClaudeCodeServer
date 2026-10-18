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

    // Сила связи: один чат — curator одной фичи и executor другой → curator ПЕРЕД executor.
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
        // curator (epicA) сильнейший → первый; executor (epicB) → второй.
        entries.Should().HaveCount(2);
        entries[0].Should().Be(new FeatureIndexChatEntry("epicA", "curator", null, null, null));
        entries[1].Should().Be(new FeatureIndexChatEntry("epicB", "executor", TaskRoles.Step, null, null));
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
}
