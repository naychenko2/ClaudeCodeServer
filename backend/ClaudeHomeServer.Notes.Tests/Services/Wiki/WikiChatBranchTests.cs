using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// S6e-3 (урезан): ветка и продолжение чата при разборе (ADR-035 §12б этап 3).
// 1) Ветка с уже разобранным родителем: общий префикс до точки ветвления (10 записей)
//    не попадает во вход модели и не разбирается заново — его вырезает слайсер по плашке
//    StoredBranchedFromMessage (фактический начальный курсор ветки — точка ветвления).
//    Дыры в коде нет — это закреплено тестом: мутация «убрать подъём start за плашку»
//    (WikiChatSlicer.cs:94-95) краснит Ветка_ПрефиксНеВоВходеМодели.
// 2) Продолжение: записка-передачи нет во входе модели (вырезана S6j — закреплено на
//    уровне AnalyzeAsync, а не только слайсера); хроника продолжения ссылается на
//    хронику прежнего чата — путь к ней строит только ChroniclePath (мутация «убрать
//    ссылку» краснит Продолжение_...).
// 3) Изоляция по владельцу прежнего чата (хвост ревью S6e-3, п.1): чужой чат не раскрывается
//    ссылкой в хронике продолжения — закреплена проверка владельца (WikiChatIngestService.cs:530),
//    мутация «снять ResolveOwnerId(prev) != ownerId» краснит Продолжение_ПрежнийЧатЧужогоВладельца_...
// Цепка настоящая (захват → очередь → применение → хроника), модель — подменённый
// ICheapTextRunner, как в WikiChatIngestTests.
public class WikiChatBranchTests : IDisposable
{
    private const string User = "u1";
    private const string OtherOwner = "u2";
    private const string Section = "projects/p1";
    private const string ParentId = "chat-parent";
    private const string BranchId = "chat-branch";
    private const string ContId = "chat-cont";
    private const string PageTitle = "Сущность";
    private const string PagePath = "Проекты/p1/Сущность.md";

    // Маркер живёт только в репликах префикса: по нему тест различает «модель видит
    // префикс» и «модель видит только хвост за точкой ветвления».
    private const string PrefixClaim = "Решение: перенести сборку на Linux";
    private const string PrefixMarker = "префикс-маркер 12 октября";
    private const string PrefixTail = "Почини Dockerfile";
    private const string BranchClaim = "Договорились: ветка правит Dockerfile";
    private const string ContClaim = "Продолжаем: собрал на Linux, тесты 44 из 44";
    private const string HandoffText = "Предыдущий чат: обсуждали сборку и решили перенести её на Linux";

    private static string Claims(string claim) =>
        $$"""{"claims":[{"text":"{{claim}}","quote":"{{claim}}"}]}""";

    private static readonly string Plan1 = $$"""
        {"chronicle":{"about":0,"decided":[0],"learned":[],"open_questions":[]},
        "operations":[{"op":"append","page":"{{PageTitle}}","claim":0}]}
        """;

    private static readonly string PageBase = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{PageTitle}}

        ## Сводка

        ## Хронология

        ## Противоречия

        ## История
        """;

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _apply;
    private readonly FakeSessions _sessions = new();

    public WikiChatBranchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_chat_branch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, null, new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config);
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        // Базовая страница знаний, которую пополняет план модели (тот же порядок фикстур,
        // что в WikiChatIngestTests).
        var r = _keeper.WritePageAsync(User, Section, PagePath, PageBase).GetAwaiter().GetResult();
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Root => WikiPaths.Root(_dir, User);

    private void AddSession(string id, string? branchedFrom = null, string? continuedFrom = null)
    {
        _sessions.All.Add(new Session
        {
            Id = id,
            OwnerId = User,
            ProjectId = "p1",
            Name = "Чат про сборку",
            BranchedFromSessionId = branchedFrom,
            ContinuedFromSessionId = continuedFrom,
        });
    }

    private void SetHistory(string id, List<StoredMessage> history) => _sessions.History[id] = history;

    // Общий префикс родителя и ветки — ровно 10 записей (m1 служебная, m2…m10 содержательные).
    private static List<StoredMessage> PrefixHistory() =>
    [
        new StoredSessionStartedMessage("claude", "default"),
        new StoredUserMessage($"{PrefixClaim}. {PrefixMarker}."),
        new StoredTextMessage("Принято, готовлю план."),
        new StoredUserMessage("Давай."),
        new StoredTextMessage("План готов."),
        new StoredUserMessage("Выполняй."),
        new StoredTextMessage("Готово, собрал."),
        new StoredUserMessage("Проверил, падает."),
        new StoredTextMessage("Чиню."),
        new StoredUserMessage($"{PrefixTail}."),
    ];

    private WikiChatIngestService Sut(FakeLlm llm) =>
        new(new WikiSourceCapture(_notes, _projects, _sessions, _config, _keeper),
            new WikiIngestService(llm, new NoSpheres(), _apply, _config),
            _sessions, _keeper, _config);

    private string PathOf(string pageKey) => SafePath.JoinBelowRoot(Root, "pages/" + pageKey);

    // ── c1: ветка с разобранным родителем — префикс не во входе модели, не разбирается заново ──

    [Fact]
    public async Task Ветка_ПрефиксНеВоВходеМодели_РодительНеПереразбирается()
    {
        AddSession(ParentId);
        AddSession(BranchId, branchedFrom: ParentId);

        var parentHistory = PrefixHistory();
        // История ветки — копия префикса плюс плашка последней записью (SessionManager.cs:3453-3461)
        // и собственный хвост за точкой ветвления.
        var branchHistory = new List<StoredMessage>(parentHistory)
        {
            new StoredBranchedFromMessage
            {
                SourceSessionId = ParentId,
                SourceName = "Чат про сборку",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            new StoredUserMessage($"{BranchClaim}. Ветка начала отсюда."),
            new StoredTextMessage("Правлю Dockerfile."),
        };
        SetHistory(ParentId, parentHistory);
        SetHistory(BranchId, branchHistory);

        var llm = new FakeLlm(
            ("A", Claims(PrefixClaim)), ("B", Plan1),
            ("A", Claims(BranchClaim)), ("B", Plan1));
        var sut = Sut(llm);

        var parent = await sut.AnalyzeAsync(User, ParentId);
        parent.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, parent.Reason);
        // Родитель отвечает за префикс: его модель его видела. Без этой проверки отсутствие
        // префикса во втором промпте было бы проверкой пустоты, а не границы.
        llm.PromptOf("A", 1).Should().Contain(PrefixMarker);

        var branch = await sut.AnalyzeAsync(User, BranchId);
        branch.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, branch.Reason);

        // Вход модели для ветки — только хвост за точкой ветвления: ни маркера префикса,
        // ни последней реплики родителя в нём нет. Мутация «убрать подъём start за плашку»
        // (WikiChatSlicer.cs:94-95) краснит этот шаг.
        var prompt = llm.PromptOf("A", 2);
        prompt.Should().Contain(BranchClaim, "своё содержимое ветки разбирается");
        prompt.Should().NotContain(PrefixMarker, "общий префикс не идёт во вход модели ветки");
        prompt.Should().NotContain(PrefixTail, "последняя реплика родителя — тоже префикс");

        // Курсоры: родитель — на конце своей истории, ветка — на конце своей. Дельта ветки
        // не считала префикс: один проход A на чат, префикс не разобран вторично.
        var entries = WikiChatTriageStore.Get(Root).Entries;
        entries.Single(e => e.SessionId == ParentId).Cursor.Should().Be(parentHistory.Count);
        entries.Single(e => e.SessionId == BranchId).Cursor.Should().Be(branchHistory.Count);
        llm.CountOf("A").Should().Be(2, "по одному проходу A на чат: префикс не разобран заново");
    }

    // ── c2: продолжение — записка-передачи нет во входе модели; хроника ссылается на прежнюю ──

    [Fact]
    public async Task Продолжение_ЗапискаНеВоВходеМодели_ХроникаСсылаетсяНаПрежнюю()
    {
        AddSession(ParentId);
        AddSession(ContId, continuedFrom: ParentId);

        SetHistory(ParentId,
        [
            new StoredSessionStartedMessage("claude", "default"),
            new StoredUserMessage($"{PrefixClaim}. {PrefixMarker}."),
            new StoredTextMessage("Принято, готовлю план."),
        ]);
        // История продолжения: m2 — записка-передачи (первое пользовательское сообщение
        // с серверной меткой «Предыдущий чат:», форма ContinueAsync), m3…m4 — своё.
        SetHistory(ContId,
        [
            new StoredSessionStartedMessage("claude", "default"),
            new StoredUserMessage(HandoffText),
            new StoredUserMessage(ContClaim),
            new StoredTextMessage("Продолжаю по договорённому."),
        ]);

        var llm = new FakeLlm(
            ("A", Claims(PrefixClaim)), ("B", Plan1),
            ("A", Claims(ContClaim)), ("B", Plan1));
        var sut = Sut(llm);

        var parent = await sut.AnalyzeAsync(User, ParentId);
        parent.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, parent.Reason);

        var cont = await sut.AnalyzeAsync(User, ContId);
        cont.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, cont.Reason);

        // Записка-передачи — вывод модели, код её вырезает (§12б этап 4): ни метки, ни её
        // текста во входе нет; своё содержимое — есть. Мутация «убрать пропуск записки»
        // (WikiChatSlicer.cs:112) краснит этот шаг.
        var prompt = llm.PromptOf("A", 2);
        prompt.Should().Contain(ContClaim, "своё содержимое продолжения разбирается");
        prompt.Should().NotContain(HandoffText, "записка-передачи вырезана из входа модели");

        // Хроника продолжения ссылается на хронику прежнего чата (§12б этап 3). Путь к обеим
        // — только ChroniclePath (тест берёт его оттуда же, как и список чатов S7b0a).
        // Мутация «не добавлять ссылку» (WikiChatIngestService.ContinuationLinkLine → null)
        // краснит проверку содержит.
        var parentKey = WikiChatIngestService.ChroniclePath(Section, ParentId);
        var contKey = WikiChatIngestService.ChroniclePath(Section, ContId);
        parentKey.Should().NotBeNull();
        contKey.Should().NotBeNull();
        var parentPage = WikiPage.Parse(File.ReadAllText(PathOf(parentKey!), Encoding.UTF8));
        parentPage.Model.Should().NotBeNull("хроника прежнего чата создана его разбором");
        var contText = File.ReadAllText(PathOf(contKey!), Encoding.UTF8);
        contText.Should().Contain($"[[{parentPage.Model!.Title}]]",
            "хроника продолжения ссылается на хронику прежнего чата");
        contText.Should().Contain("Продолжение чата:", "ссылка подписана для человека");
    }

    // ── c3: прежний чат другого владельца — ссылки на его хронику нет (хвост ревью S6e-3, п.1) ──

    [Fact]
    public async Task Продолжение_ПрежнийЧатЧужогоВладельца_СсылкиНаЕгоХроникуНет()
    {
        AddSession(ParentId);
        AddSession(ContId, continuedFrom: ParentId);

        SetHistory(ParentId,
        [
            new StoredSessionStartedMessage("claude", "default"),
            new StoredUserMessage($"{PrefixClaim}. {PrefixMarker}."),
            new StoredTextMessage("Принято, готовлю план."),
        ]);
        SetHistory(ContId,
        [
            new StoredSessionStartedMessage("claude", "default"),
            new StoredUserMessage(HandoffText),
            new StoredUserMessage(ContClaim),
            new StoredTextMessage("Продолжаю по договорённому."),
        ]);

        var llm = new FakeLlm(
            ("A", Claims(PrefixClaim)), ("B", Plan1),
            ("A", Claims(ContClaim)), ("B", Plan1));
        var sut = Sut(llm);

        // Пока прежний чат принадлежит u1, его разбор пишет хронику на диск u1.
        var parent = await sut.AnalyzeAsync(User, ParentId);
        parent.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, parent.Reason);

        // Прежний чат становится чатом u2: хроника остаётся на диске u1 — ровно то состояние,
        // где единственный рубеж ссылки — проверка владельца (WikiChatIngestService.cs:530).
        // Без неё ссылка бы образовалась (дом тот, файл на диске, тип хроники) — это и краснеет
        // в мутации «снять ResolveOwnerId(prev) != ownerId».
        _sessions.GetById(ParentId)!.OwnerId = OtherOwner;

        var cont = await sut.AnalyzeAsync(User, ContId);
        cont.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, cont.Reason);

        // Прежняя хроника на диске и читается: отсутствие ссылки — не из-за битой цели.
        var parentKey = WikiChatIngestService.ChroniclePath(Section, ParentId);
        parentKey.Should().NotBeNull();
        var parentPage = WikiPage.Parse(File.ReadAllText(PathOf(parentKey!), Encoding.UTF8));
        parentPage.Model.Should().NotBeNull("хроника прежнего чата осталась на диске u1");

        // Чужой чат не раскрывается ссылкой: ни подписанной строки, ни самой цели.
        var contKey = WikiChatIngestService.ChroniclePath(Section, ContId);
        contKey.Should().NotBeNull();
        var contText = File.ReadAllText(PathOf(contKey!), Encoding.UTF8);
        contText.Should().NotContain("Продолжение чата:",
            "прежний чат другого владельца — ссылка не формируется");
        contText.Should().NotContain($"[[{parentPage.Model!.Title}]]",
            "хроника чужого чата не раскрывается ссылкой");
    }

    // ── подменённая модель и подставные сессии (тот же состав, что в WikiChatIngestTests) ──

    private sealed class FakeLlm : ICheapTextRunner
    {
        public List<(string ActionKey, string Prompt, string Pass)> Calls { get; } = [];

        private readonly Dictionary<string, Queue<string>> _answers = new();

        public FakeLlm(params (string Pass, string Answer)[] answers)
        {
            foreach (var (pass, answer) in answers)
                (_answers.TryGetValue(pass, out var q) ? q : _answers[pass] = new Queue<string>()).Enqueue(answer);
        }

        public string PromptOf(string pass, int nth = 1) =>
            Calls.Where(c => c.Pass == pass).ElementAt(nth - 1).Prompt;

        public int CountOf(string pass) => Calls.Count(c => c.Pass == pass);

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(Answer(actionKey, prompt));

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult<string?>(Answer(actionKey, prompt));

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(new OneShotResult(
                Answer(actionKey, prompt), new OneShotUsage(10, 0, 0, 5, 0.0, "fake"), 1));

        private string Answer(string actionKey, string prompt)
        {
            // Проход — по директиве своего промпта (порядок вызовов не угадывается).
            var pass = prompt.Contains("YOUR PREVIOUS PLAN WAS REJECTED") ? "R"
                : prompt.Contains("extract durable") ? "A"
                : prompt.Contains("turn the claims") ? "B"
                : "?";
            Calls.Add((actionKey, prompt, pass));
            actionKey.Should().Be(LocalActionCatalog.WikiIngest, "место разбора — wiki-ingest");
            var queue = _answers.TryGetValue(pass, out var q) ? q : null;
            return queue is { Count: > 0 } ? queue.Dequeue() : "нет ответа для прохода " + pass;
        }
    }

    private sealed class FakeSessions : ISessionDirectory
    {
        public readonly List<Session> All = [];
        public readonly Dictionary<string, List<StoredMessage>> History = [];

        public Session? GetById(string id) => All.FirstOrDefault(s => s.Id == id);
        public IReadOnlyCollection<Session> GetAll() => All;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>(
                History.TryGetValue(sessionId, out var h) ? h : new List<StoredMessage>());
        public bool SetWikiMark(string sessionId, string? mark) => true;
    }

    private sealed class WikiFlagOn : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }

    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }
}
