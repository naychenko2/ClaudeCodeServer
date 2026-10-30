using Microsoft.Extensions.DependencyInjection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Projector;
using ClaudeHomeServer.Services.Turn;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tasks.Tests;

// Волна 6, B2: напоминание оркестратору об устаревшем голосе фичи — хвостовая секция хода.
//
// Мутации (проверены прогоном, см. отчёт задачи):
//  • убрать InTurnTail — тест «хвост хода» красный (секция уехала бы в системный блок);
//  • не сверять сессию с ключевым чатом (убрать `!= sessionId`) — тест «чужой чат» красный;
//  • не проверять Stale — тест «свежий голос» красный; не проверять Note is null — тест «нет голоса» красный;
//  • убрать отсев закрытых — тест «закрытая фича» красный.
//
// P2 (расхождения трекера в том же хвосте), мутации:
//  • убрать `lines.Add(tracker)` — тесты «расхождение…», «несколько видов…», «волна после мержа…»
//    и «оркестратор не-эпика» красные (строки нет вовсе);
//  • не отдавать снимок сессий в Project (третий аргумент — null) — тест «reported_open…» красный
//    (FeatureTracker fail-closed: вида нет, хотя он есть в трекере);
//  • не отсеивать merged (убрать `Kinds.Merged`) — тест «смерженная фича…» красный: хвост nag-ил
//    бы по фиче, которую P1 уже признал смерженной.
public class FeatureVoiceReminderContributorTests : IDisposable
{
    private const string Owner = "u";
    private const string KeyChat = "chat-key";
    // Чат исполнителя задачи (для reported_open: он должен быть в снимке и свободен).
    private const string ExecChat = "chat-exec";
    // Префикс строки о расхождениях (P2) — по нему тест берёт именно её, а не строку о голосе.
    private const string TrackerLinePrefix = "В трекере фичи";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vr_" + Guid.NewGuid().ToString("N"));
    private readonly TaskManager _tasks;
    private readonly FeatureVoiceReminderContributor _sut;

    // Active — «свободный» чат (занятый = Starting/Working/Waiting, FeatureChatDoor.Snapshot):
    // тесты про reported_open проверяют путь с открытым чатом исполнителя.
    private sealed class Sessions(params string[] ids) : ISessionDirectory
    {
        public Session? GetById(string id) => ids.Contains(id) ? new Session { Id = id, Status = SessionStatus.Active } : null;
        public IReadOnlyCollection<Session> GetAll() => ids.Select(i => new Session { Id = i, Status = SessionStatus.Active }).ToList();
        public string? ResolveOwnerId(Session s) => Owner;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
    }

    public FeatureVoiceReminderContributorTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _tasks = new TaskManager(config);
        _sut = new FeatureVoiceReminderContributor(_tasks, new FeatureProjector(_tasks),
            new ServiceCollection().AddSingleton<ISessionDirectory>(
                new Sessions(KeyChat, "chat-other", ExecChat)).BuildServiceProvider());
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private TaskItem NewFeature(string title = "Сводка фичи")
    {
        var epic = _tasks.Create(null, Owner, new CreateTaskRequest(title, Role: TaskRoles.Feature));
        epic.ExecutorLaunchedBySessionId = KeyChat;
        return epic;
    }

    private async Task<PromptSection?> Build(string sessionId)
    {
        var ctx = new PromptSessionContext(new Session { Id = sessionId }, Owner, null, "/root");
        var result = await _sut.BuildAsync(ctx, null);
        return result?.Sections.SingleOrDefault();
    }

    [Fact]
    public async Task КлючевойЧатБезГолоса_НапоминаетЗаписать_ХвостомХода()
    {
        NewFeature();

        var section = await Build(KeyChat);

        section.Should().NotBeNull();
        section!.InTurnTail.Should().BeTrue("секция в системном блоке обнулила бы prefix cache");
        section.Text.Should().Contain("«Сводка фичи»").And.Contain("featureNote").And.Contain("featureEstimate");
    }

    [Fact]
    public async Task УстаревшийГолос_НапоминаетОбновить()
    {
        var epic = NewFeature();
        // Стадия записи не совпадает с активной — голос устарел (StaleOf)
        _tasks.SetFeatureNote(epic.Id, "было ревью", KeyChat, byUser: false, "review", "running");

        var section = await Build(KeyChat);

        section!.Text.Should().Contain("«Сводка фичи» устарел").And.Contain("этап сменился");
    }

    [Fact]
    public async Task СвежийГолос_Молчит()
    {
        var epic = NewFeature();
        var run = new FeatureProjector(_tasks).Project(epic.Id, Owner)!;
        _tasks.SetFeatureNote(epic.Id, "всё идёт", KeyChat, byUser: false,
            FeatureBrief.ActiveStageOf(run), FeatureBrief.KindAtWriteOf(run));

        (await Build(KeyChat)).Should().BeNull();
    }

    [Fact]
    public async Task ЧужойЧат_Молчит()
    {
        NewFeature();

        (await Build("chat-other")).Should().BeNull("напоминание только в ключевом чате оркестратора");
    }

    [Fact]
    public async Task ЗакрытаяФича_Молчит()
    {
        var epic = NewFeature();
        epic.Status = TaskItemStatus.Done;

        (await Build(KeyChat)).Should().BeNull();
    }

    // ─── P2: расхождения трекера — та же хвостовая секция ──────────────────────────────────

    // Строка о расхождении: вид и количество, без id задач (они — в «Сверке трекера» tasks_get).
    [Fact]
    public async Task Расхождение_ДаетСтрокуСВидомИКоличеством()
    {
        var epic = NewFeature();
        LateSubmittedStep(epic.Id);

        var line = TrackerLine(await Build(KeyChat));

        line.Should().Contain("«Сводка фичи»", "фич в хвосте до трёх — строка обязана называть свою");
        line.Should().Contain("1 расхождение", "склонение при числе: 1 — единственное");
        line.Should().Contain("unreviewed ×1", "имя вида — то же, что в блоке «Сверка трекера»");
        line.Should().NotContain(epic.Id).And.NotContain("S1", "задачи здесь не перечисляются");
    }

    // Пустой список молчит: хвост не раздувается без причины (тот же мотив, что у InTurnTail).
    [Fact]
    public async Task НетРасхождений_СтрокиОТрекереНет()
    {
        NewFeature();

        var section = await Build(KeyChat);

        section.Should().NotBeNull("напоминание о голосе остаётся: расхождений нет — но голос не записан");
        section!.Text.Should().NotContain(TrackerLinePrefix);
    }

    // Два вида в одной строке, по счётчику на каждый (порядок: по числу, затем по имени вида).
    [Fact]
    public async Task НесколькоВидов_ОднаСтрокаСоСчётчикамиПоВидам()
    {
        var epic = NewFeature();
        LateSubmittedStep(epic.Id);          // unreviewed
        ReportedOpenTask(epic.Id);           // reported_open

        var line = TrackerLine(await Build(KeyChat));

        line.Should().Contain("2 расхождения");
        line.Should().Contain("reported_open ×1").And.Contain("unreviewed ×1");
    }

    // reported_open существует только по снимку сессий (FeatureTracker fail-closed): без него
    // хвост называл бы меньшее число расхождений, чем страница фичи и tasks_get.
    [Fact]
    public async Task ReportedOpen_БеретсяИзСнимкаСессийВладельца()
    {
        var epic = NewFeature();
        ReportedOpenTask(epic.Id);

        TrackerLine(await Build(KeyChat)).Should().Contain("reported_open ×1",
            "чат исполнителя в снимке и свободен — вид есть; без снимка его не было бы вовсе");
    }

    // П1 (мерж, принятие): смерженная фича молчит — TerminalState назвал её merged, хвост не nag-ит.
    [Fact]
    public async Task СмерженнаяФича_ОРасхожденияхМолчит()
    {
        var epic = NewFeature();
        AcceptedFinalAndMerge(epic.Id);

        (await Build(KeyChat)).Should().BeNull("merged — тот же отсев, что у закрытой");
    }

    // П1 (волна после мержа): новый шаг после мержа снимает merged → открытый эпик становится
    // расхождением, и оно в хвосте: это правит оркестратор, сервер только показывает.
    [Fact]
    public async Task ВолнаПослеМержа_ЭпикОткрытПослеМержа_РасхождениеВХвосте()
    {
        var epic = NewFeature();
        AcceptedFinalAndMerge(epic.Id);
        // Новый шаг ПОСЛЕ закрытия мержа (CreatedAt — сейчас) → WaveAfterMerge, merged снят.
        var wave = _tasks.Create(null, Owner, new CreateTaskRequest("шаг после мержа",
            Role: TaskRoles.Step, StepRef: "S9", FeatureId: epic.Id, Lane: "back"));
        wave.Status = TaskItemStatus.Done;
        wave.CompletedAt = DateTime.UtcNow.AddHours(-1);

        var line = TrackerLine(await Build(KeyChat));

        line.Should().Contain("epic_open_after_merge ×1", "эпик открыт, вся работа сдана, мерж закрыт");
        line.Should().Contain("unreviewed ×1", "новый шаг сдан без покрывающего витка");
    }

    // Ключевой чат фичи — не чат эпика: цепочка ведёт его по задачам, которые он запускал
    // (FeatureKeyChain). Оркестратор, ведущий работу из своего чата, получает строку там же.
    [Fact]
    public async Task ОркестраторНеЭпика_СтрокаВКлючевомЧатеФичи()
    {
        var epic = _tasks.Create(null, Owner, new CreateTaskRequest("Сводка фичи", Role: TaskRoles.Feature)); // своего чата нет
        LateSubmittedStep(epic.Id, launchedFrom: KeyChat);

        TrackerLine(await Build(KeyChat)).Should().Contain("unreviewed ×1");
        (await Build("chat-other")).Should().BeNull("не ключевой чат — молчит");
    }

    // ─── Посев под виды FeatureTracker ─────────────────────────────────────────────────────

    // Шаг, сданный 2 часа назад без покрывающего витка → unreviewed (порог TrackerGraceMinutes —
    // 30 мин; API «закрыть с датой» нет, поэтому дата ставится на живой задаче, как в
    // TasksToolsetTrackerTests).
    private TaskItem LateSubmittedStep(string epicId, string stepRef = "S1", string? launchedFrom = null)
    {
        var step = _tasks.Create(null, Owner, new CreateTaskRequest($"шаг {stepRef}",
            Role: TaskRoles.Step, StepRef: stepRef, FeatureId: epicId, Lane: "back"));
        if (launchedFrom is not null) step.ExecutorLaunchedBySessionId = launchedFrom;
        step.Status = TaskItemStatus.Done;
        step.CompletedAt = DateTime.UtcNow.AddHours(-2);
        return step;
    }

    // Исполнитель отчитался (ClaudeResult), задача InProgress, чат в снимке и свободен,
    // тишина дольше порога → reported_open.
    private TaskItem ReportedOpenTask(string epicId)
    {
        var step = _tasks.Create(null, Owner, new CreateTaskRequest("шаг S2",
            Role: TaskRoles.Step, StepRef: "S2", FeatureId: epicId, Lane: "back"));
        step.Status = TaskItemStatus.InProgress;
        step.ClaudeResult = "готово";
        step.LinkedSessionId = ExecChat;
        step.UpdatedAt = DateTime.UtcNow.AddHours(-2);
        return step;
    }

    // Принятый финал + закрытый мерж: гард role=merge (TaskManager) требует принятого финала,
    // поэтому он создаётся первым. Мерж закрыт час назад — «сейчас» для волны после мержа.
    private void AcceptedFinalAndMerge(string epicId)
    {
        var final = _tasks.Create(null, Owner, new CreateTaskRequest("финал", Role: TaskRoles.Final, FeatureId: epicId));
        final.Status = TaskItemStatus.Done;
        final.CompletedAt = DateTime.UtcNow.AddHours(-3);
        final.Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted };
        var merge = _tasks.Create(null, Owner, new CreateTaskRequest("мерж", Role: TaskRoles.Merge, FeatureId: epicId));
        merge.Status = TaskItemStatus.Done;
        merge.CompletedAt = DateTime.UtcNow.AddHours(-1);
    }

    // Строка именно о расхождениях: её начало — общий префикс, строку о голосе тест не берёт.
    private static string TrackerLine(PromptSection? section)
    {
        section.Should().NotBeNull();
        return section!.Text.Split('\n').Single(l => l.StartsWith(TrackerLinePrefix));
    }
}
