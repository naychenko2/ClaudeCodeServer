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
//  • убрать отсев закрытых — тест «закрытая фича» красный;
//  • FS-O2c: убрать ветку возраста из StaleOf (FeatureBrief.cs) — красный ровно
//    «ГолосСтаршеПорогаПоВозрасту…» (та же фикстура свежая по стадии/категории — молчит).
//
// P2 (расхождения трекера в том же хвосте), мутации:
//  • убрать `lines.Add(tracker)` — тесты «расхождение…», «несколько видов…», «волна после мержа…»
//    и «оркестратор не-эпика» красные (строки нет вовсе);
//  • не отдавать снимок сессий в Project (третий аргумент — null) — тест «reported_open…» красный
//    (FeatureTracker fail-closed: вида нет, хотя он есть в трекере);
//  • не отсеивать merged (убрать `Kinds.Merged`) — тест «смерженная фича…» красный: хвост nag-ил
//    бы по фиче, которую P1 уже признал смерженной.
//
// P3 (FS-XT2, третий повод: финал принят + мержа нет + закрытый хвост), мутации:
//  • убрать `run.Ready &&` (проверку IsFinalAccepted) — красный ровно «ФиналНеПринят…»;
//  • убрать `run.Merge is null &&` — красный ровно «МержЗаведён…»;
//  • убрать `run.ClosedTailKeys.Count > 0` — красные «ХвостаНет…» и «СнятиеХвоста…»
//    (строка с нулём появилась бы там, где хвоста уже нет).
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
        public bool SetWikiMark(string sessionId, string? mark) => false;
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

    // FS-O2c (c2): возраст голоса — отдельный сигнал: та же запись, свежая по стадии и категории,
    // но с записи прошло 13 ч — больше FeatureBrief.StaleAgeHours (12). Стадии и категории не
    // менялись: краснеет ровно ветка возраста в StaleOf (FeatureBrief.cs).
    [Fact]
    public async Task ГолосСтаршеПорогаПоВозрасту_НапоминаетОбновить()
    {
        var epic = NewFeature();
        var run = new FeatureProjector(_tasks).Project(epic.Id, Owner)!;
        _tasks.SetFeatureNote(epic.Id, "всё идёт", KeyChat, byUser: false,
            FeatureBrief.ActiveStageOf(run), FeatureBrief.KindAtWriteOf(run));
        (await Build(KeyChat)).Should().BeNull("контроль: та же запись свежая по возрасту — молчит");

        epic.Note = epic.Note! with { At = DateTime.UtcNow.AddHours(-13) };

        var section = await Build(KeyChat);
        section.Should().NotBeNull("возраст больше порога — напоминание срабатывает по нему");
        section!.Text.Should().Contain("устарел");
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

    // П1 (волна после мержа) + FS-AC1: новый шаг после мержа снимает merged → волна говорит
    // через цепочечный вид (unreviewed). Открытый эпик после мержа — не расхождение и не повод
    // (решение Андрея 11.11): строка не должна ссылаться на закрытие эпика. Мутация «вернуть
    // ветку эпика» в FeatureTracker краснит тест (вид вернулся бы в строку).
    [Fact]
    public async Task ВолнаПослеМержа_ТолькоЦепочечныйВидБезЗакрытияЭпика()
    {
        var epic = NewFeature();
        AcceptedFinalAndMerge(epic.Id);
        // Новый шаг ПОСЛЕ закрытия мержа (CreatedAt — сейчас) → WaveAfterMerge, merged снят.
        var wave = _tasks.Create(null, Owner, new CreateTaskRequest("шаг после мержа",
            Role: TaskRoles.Step, StepRef: "S9", FeatureId: epic.Id, Lane: "back"));
        wave.Status = TaskItemStatus.Done;
        wave.CompletedAt = DateTime.UtcNow.AddHours(-1);

        var line = TrackerLine(await Build(KeyChat));

        line.Should().Contain("unreviewed ×1", "новый шаг сдан без покрывающего витка");
        line.Should().NotContain("epic_open_after_merge", "открытый эпик после мержа — не расхождение (FS-AC1)");
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

    // ─── P3 (FS-XT2): финал принят + мержа нет + закрытый хвост ─────────────────────────────

    // c1: хвост из двух шагов → строка с числом и обоими путями решения. Шаги сданы без
    // покрывающего витка (тот же посев, что у unreviewed) — по учёту это закрытый хвост.
    [Fact]
    public async Task ФиналПринятМержаНетХвостЕсть_НапоминаетОЗакрытыхШагах()
    {
        var epic = NewFeature();
        AcceptedFinal(epic.Id);
        LateSubmittedStep(epic.Id, "S1");
        LateSubmittedStep(epic.Id, "S2");

        var line = TailLine(await Build(KeyChat));

        line.Should().Contain("«Сводка фичи»", "хвост поимённый по фичам — строка называет свою");
        line.Should().Contain("2 закрытых непринятых шагов", "форма числа — как у 409 о хвосте (TasksController.cs:1643)");
        line.Should().Contain("заведи ревью").And.Contain("сними с причиной", "оба пути решения — по разрезу §8.3");
    }

    // Хвоста нет: шаг покрыт принятым ревью (корзина accepted) — повода нет.
    [Fact]
    public async Task ХвостаНет_ОЗакрытыхШагахМолчит()
    {
        var epic = NewFeature();
        AcceptedFinal(epic.Id);
        var step = LateSubmittedStep(epic.Id);
        var review = _tasks.Create(null, Owner, new CreateTaskRequest("ревью S1",
            Role: TaskRoles.Review, FeatureId: epic.Id, ReviewOf: [step.Id]));
        review.Status = TaskItemStatus.Done;
        review.CompletedAt = DateTime.UtcNow.AddHours(-1);
        review.Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted };

        var section = await Build(KeyChat);
        section.Should().NotBeNull("голос не записан — строка о голосе остаётся");
        section!.Text.Should().NotContain("закрытых непринятых", "шаг принят: закрытый хвост пуст");
    }

    // Задача мержа заведена (Todo): решение о мерже уже принято оркестратором — повод гаснет.
    [Fact]
    public async Task МержЗаведён_ОЗакрытыхШагахМолчит()
    {
        var epic = NewFeature();
        AcceptedFinal(epic.Id);
        LateSubmittedStep(epic.Id);
        _tasks.Create(null, Owner, new CreateTaskRequest("мерж", Role: TaskRoles.Merge, FeatureId: epic.Id));

        var section = await Build(KeyChat);
        section.Should().NotBeNull();
        section!.Text.Should().NotContain("закрытых непринятых", "merge-задача есть — напоминать не о чём");
    }

    // Финал не принят (вердикт returned): фича не на месте мержа — повода нет.
    // Мутация «убрать run.Ready» (c2) — красный ровно этот тест.
    [Fact]
    public async Task ФиналНеПринят_ОЗакрытыхШагахМолчит()
    {
        var epic = NewFeature();
        var final = _tasks.Create(null, Owner, new CreateTaskRequest("финал", Role: TaskRoles.Final, FeatureId: epic.Id));
        final.Status = TaskItemStatus.Done;
        final.CompletedAt = DateTime.UtcNow.AddHours(-3);
        final.Verification = new TaskVerification { Verdict = TaskVerdicts.Returned };
        LateSubmittedStep(epic.Id);

        var section = await Build(KeyChat);
        section.Should().NotBeNull();
        section!.Text.Should().NotContain("закрытых непринятых", "финал вернулся — фича не готова к мержу");
    }

    // «После снятия хвоста повод гаснет» (критерий §8.5 Х2): снятие тем же методом, что кнопка мержа.
    [Fact]
    public async Task СнятиеХвостаГаситПовод()
    {
        var epic = NewFeature();
        AcceptedFinal(epic.Id);
        LateSubmittedStep(epic.Id, "S1");
        LateSubmittedStep(epic.Id, "S2");
        TailLine(await Build(KeyChat)).Should().Contain("2 закрытых непринятых шагов", "фикстура: повод активен");

        _tasks.WithdrawMergeTail(epic.Id, ["S1", "S2"], Owner).Withdrawn.Should().HaveCount(2);

        var section = await Build(KeyChat);
        section.Should().NotBeNull("голос не записан — строка о голосе остаётся");
        section!.Text.Should().NotContain("закрытых непринятых", "хвост снят — ключи пусты, повод гаснет сам");
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

    // Принятый финал без мержа (FS-XT2): гард role=merge (TaskManager) требует принятого финала,
    // поэтому он создаётся первым. Финал без ReviewOf не трогает цепочки шагов
    // (ExtractTargetKeys пуст): шаг остаётся сданным непринятым — закрытым хвостом.
    private void AcceptedFinal(string epicId)
    {
        var final = _tasks.Create(null, Owner, new CreateTaskRequest("финал", Role: TaskRoles.Final, FeatureId: epicId));
        final.Status = TaskItemStatus.Done;
        final.CompletedAt = DateTime.UtcNow.AddHours(-3);
        final.Verification = new TaskVerification { Verdict = TaskVerdicts.Accepted };
    }

    // Принятый финал + закрытый мерж. Мерж закрыт час назад — «сейчас» для волны после мержа.
    private void AcceptedFinalAndMerge(string epicId)
    {
        AcceptedFinal(epicId);
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

    // Строка именно о закрытом хвосте (P3): её начало — «У фичи»; строки о голосе («Голос(а)
    // фичи») и о трекере («В трекере фичи») под этот префикс не попадают.
    private static string TailLine(PromptSection? section)
    {
        section.Should().NotBeNull();
        return section!.Text.Split('\n').Single(l => l.StartsWith("У фичи"));
    }
}
