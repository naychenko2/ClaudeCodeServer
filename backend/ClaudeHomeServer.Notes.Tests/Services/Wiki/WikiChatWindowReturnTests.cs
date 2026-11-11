using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// T-A1 (разрез плана 12554ff8, хвост apply_failed — финал 630b8b12): ReturnWindowAsync
// возвращает курсор разбора чата на from-1, чтобы окно from…N разбиралось заново.
// Фикстура — та же, что в WikiChatIngestTests: цепка настоящая на диске, модель — подменённый
// ICheapTextRunner.
//
// Критерии: c1 — курсор возвращается на from-1 с хэшем записи границы (та же функция
// HashOfRecord, что у курсора прогона), зона/причина разбора обнулены, поля скорера и
// ступени 2 целы (их переносит стор); курсор уже под окном — ничего не меняется;
// приватный и удалённый чат — запись не изменилась, исключения нет; гонка с прогоном —
// откат делается после конца прогона и не затёрт его финальным AdvanceCursor.
//
// Курсор 9 в тестах ставится прямым Upsert/UpdateScoring стора: откат смотрит ТОЛЬКО
// cursor/запись чата (стор-флаг и история окна его не интересуют), а хэш границы ReturnWindow
// пишет сам — прежний хэш записи для этого не нужен.
public class WikiChatWindowReturnTests : IDisposable
{
    private const string User = "u1";
    private const string Other = "u2";
    private const string Section = "projects/p1";
    private const string ChatId = "chat-1";
    private const string PageTitle = "Сущность";
    private const string PagePath = "Проекты/p1/Сущность.md";

    private const string Claim1 = "Решение: перенести сборку на Linux";
    private const string Marker1 = "первый сбой был 12 октября";
    // Утверждение из дельты m3…m9: его цитата есть в возвращённом окне (в m2 её нет) —
    // по нему тест проверяет, что следующий разбор видит именно дельту.
    private const string ClaimDelta = "Договорились: тесты гоняем через CI";
    private const string Marker3 = "вторая реплика человека";
    private const string Marker4 = "третья реплика человека";

    private static string Synth(string claim) => WikiSynthJson.Append(PageTitle, claim, claim);

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

    public WikiChatWindowReturnTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_window_return_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, null, flags: new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config);
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        var r = _keeper.WritePageAsync(User, Section, PagePath, PageBase).GetAwaiter().GetResult();
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Root => WikiPaths.Root(_dir, User);

    private void AddSession(string id = ChatId, string? owner = User, string? projectId = "p1",
        bool taskExecution = false, string? wikiMark = null, string? privateTo = null)
    {
        _sessions.All.Add(new Session
        {
            Id = id,
            OwnerId = owner!,
            ProjectId = projectId,
            Name = "Чат про сборку",
            TaskExecution = taskExecution,
            WikiMark = wikiMark,
            PrivateToUserId = privateTo,
        });
    }

    private void SetHistory(string id, List<StoredMessage> history) => _sessions.History[id] = history;

    // Девять записей: m1 служебная, дальше пары «человек/агент» до m9. Маркеры m2/m4/m8 —
    // по ним тест различает, с какой записи окно дошло до модели.
    private List<StoredMessage> NineHistory() =>
    [
        new StoredSessionStartedMessage("claude", "default"),
        new StoredUserMessage($"{Claim1}. Хронология: {Marker1}."),
        new StoredTextMessage("Принято, готовлю план."),
        new StoredUserMessage($"{ClaimDelta}. {Marker3}."),
        new StoredTextMessage("Готовлю."),
        new StoredUserMessage("Дальше."),
        new StoredTextMessage("Дальше."),
        new StoredUserMessage($"Итог: {Marker4}."),
        new StoredTextMessage("Готово."),
    ];

    private WikiChatIngestService Sut(FakeLlm llm) =>
        new(new WikiSourceCapture(_notes, _projects, _sessions, _config, _keeper),
            new WikiIngestService(llm, new NoSpheres(), _apply, _config),
            _sessions, _keeper, _config);

    // Состояние «разбор закрыл окно курсором 9»: прямая запись стора (тот же путь, что у
    // теста C2R в WikiChatIngestTests), плюс поля скорера через UpdateScoring.
    private void SeedTriage(int cursor, string? zone = WikiChatIngestService.ZoneNothingNew,
        int nothingNewStreak = 1)
    {
        var seeded = WikiChatTriageStore.Upsert(Root, new WikiChatTriageEntry(
            ChatId, cursor, "old-hash", zone, "не о чём писать", nothingNewStreak, DateTimeOffset.UtcNow));
        seeded.Ok.Should().BeTrue(seeded.Reason);
        var scored = WikiChatTriageStore.UpdateScoring(Root, ChatId, 7, null, "grey", "серый чат");
        scored.Ok.Should().BeTrue(scored.Reason);
    }

    private WikiChatTriageEntry Entry() => WikiChatTriageStore.Get(Root).Entries.Single();

    // ── c1: курсор возвращается на from-1 с хэшем границы; зона обнулена, скорер цел ──

    [Fact]
    public async Task ВозвратОкна_КурсорНаFromMinus1_ХэшГраницы_ЗонаОбнулена_СкорерЦел()
    {
        AddSession();
        var history = NineHistory();
        SetHistory(ChatId, history);
        SeedTriage(9);

        var ok = await Sut(new FakeLlm()).ReturnWindowAsync(User, ChatId, 3);
        ok.Should().BeTrue("окно возвращено");

        var entry = Entry();
        entry.Cursor.Should().Be(2, "курсор возвращается на запись перед началом окна");
        entry.CursorHash.Should().Be(WikiChatIngestService.HashOfRecord(history[1]),
            "хэш границы — та же функция и та же запись, что у курсора прогона");
        entry.Zone.Should().BeNull("прежний исход nothing_new снимает следующий разбор, не откат");
        entry.Reason.Should().BeNull();
        entry.NothingNewStreak.Should().Be(1, "откат — не исход разбора: streak не трогается");
        entry.RejectStreak.Should().Be(0);
        entry.Score.Should().Be(7, "поля скорера переносятся стором (M2)");
        entry.Signals.Should().BeNull();
        entry.Stage1Zone.Should().Be("grey", "зона ступени 1 — другого писателя, откат её не трогает");
        entry.Stage1Reason.Should().Be("серый чат");
    }

    // Хэш границы при from==1: курсор 0 и null — та же форма, что у «ничего не разобрано».
    [Fact]
    public async Task ВозвратОкна_ОтПервойЗаписи_КурсорНоль_ХэшNull()
    {
        AddSession();
        SetHistory(ChatId, NineHistory());
        SeedTriage(9);

        var ok = await Sut(new FakeLlm()).ReturnWindowAsync(User, ChatId, 1);
        ok.Should().BeTrue();

        var entry = Entry();
        entry.Cursor.Should().Be(0);
        entry.CursorHash.Should().BeNull("записи 0 нет — хэша нет");
        entry.Zone.Should().BeNull();
    }

    // Откат ставит живой хэш границы: следующий разбор берёт дельту ровно от from, не с нуля
    // (хэш не совпал бы — RunAsync сбросил бы курсор в 0 и модель увидела бы Marker1) и не с
    // прежнего курсора (up_to_date — модель не звалась бы вовсе). Мутация «не ставить хэш» →
    // модель видит весь чат (красное); «не двигать курсор» → up_to_date (красное).
    [Fact]
    public async Task ВозвратОкна_СледующийРазборБерётДельтуОтFrom()
    {
        AddSession();
        SetHistory(ChatId, NineHistory());
        SeedTriage(9);

        var sut = Sut(new FakeLlm());
        (await sut.ReturnWindowAsync(User, ChatId, 3)).Should().BeTrue();

        var llm = new FakeLlm(("S", Synth(ClaimDelta)));
        var res = await Sut(llm).AnalyzeAsync(User, ChatId);
        res.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, res.Reason);

        var prompt = llm.PromptOf("S");
        prompt.Should().Contain(Marker3, "дельта начинается с m3 — сразу за возвращённым курсором");
        prompt.Should().Contain(Marker4);
        prompt.Should().NotContain(Marker1, "закрытая курсором часть чата не разбирается заново");
    }

    // ── курсор уже под окном: ничего ──

    [Fact]
    public async Task КурсорПодОкном_НичегоНеМеняется()
    {
        AddSession();
        SetHistory(ChatId, NineHistory());
        SeedTriage(1);

        var before = Entry();
        var ok = await Sut(new FakeLlm()).ReturnWindowAsync(User, ChatId, 3);
        ok.Should().BeFalse();

        var after = Entry();
        after.Cursor.Should().Be(before.Cursor);
        after.CursorHash.Should().Be(before.CursorHash);
        after.Zone.Should().Be(before.Zone);
        after.Reason.Should().Be(before.Reason);
        after.TriagedAt.Should().Be(before.TriagedAt, "пустого обращения к стору не было");
    }

    // ── чат недоступен: запись не изменилась, исключения нет ──

    [Fact]
    public async Task ПриватныйЧат_ЗаписьНеИзменена_ИсключенияНет()
    {
        AddSession(privateTo: User);
        SetHistory(ChatId, NineHistory());
        SeedTriage(9);

        var ok = await Sut(new FakeLlm()).ReturnWindowAsync(User, ChatId, 3);
        ok.Should().BeFalse();

        var entry = Entry();
        entry.Cursor.Should().Be(9);
        entry.Zone.Should().Be(WikiChatIngestService.ZoneNothingNew);
    }

    [Fact]
    public async Task УдалённыйЧат_ЗаписьНеИзменена_ИсключенияНет()
    {
        // Чата нет в директории, запись стора — осталась (чистка стора отдельная операция).
        SetHistory(ChatId, NineHistory());
        SeedTriage(9);

        var ok = await Sut(new FakeLlm()).ReturnWindowAsync(User, ChatId, 3);
        ok.Should().BeFalse();

        Entry().Cursor.Should().Be(9);
    }

    [Fact]
    public async Task ЧатПомеченНеРазбирать_ЗаписьНеИзменена()
    {
        AddSession(wikiMark: WikiMarks.Skip);
        SetHistory(ChatId, NineHistory());
        SeedTriage(9);

        var ok = await Sut(new FakeLlm()).ReturnWindowAsync(User, ChatId, 3);
        ok.Should().BeFalse();

        Entry().Cursor.Should().Be(9);
    }

    // Чужой владелец не возвращает чужой курсор (per-owner, как весь вход класса).
    [Fact]
    public async Task ЧужойВладелец_Ничего()
    {
        AddSession();
        SetHistory(ChatId, NineHistory());
        SeedTriage(9);

        var ok = await Sut(new FakeLlm()).ReturnWindowAsync(Other, ChatId, 3);
        ok.Should().BeFalse();

        Entry().Cursor.Should().Be(9);
    }

    // История короче целевого курсора: откату не на что опереться (хэша записи from-1 нет).
    [Fact]
    public async Task ИсторияКорочеЦелевогоКурсора_НичегоНеМеняется()
    {
        AddSession();
        SetHistory(ChatId,
        [
            new StoredSessionStartedMessage("claude", "default"),
            new StoredUserMessage($"{Claim1}. Хронология: {Marker1}."),
            new StoredTextMessage("Принято."),
        ]);
        SeedTriage(9);

        // from-1 = 4 при истории в 3 записи: хэша границы нет — откат нечего ставить.
        var ok = await Sut(new FakeLlm()).ReturnWindowAsync(User, ChatId, 5);
        ok.Should().BeFalse();

        Entry().Cursor.Should().Be(9);
    }

    // ── гонка: чат в прогоне — откат после его конца, не затёрт финальным AdvanceCursor ──

    [Fact]
    public async Task Гонка_ОткатПослеКонцаПрогона_КурсорFromMinus1()
    {
        AddSession();
        SetHistory(ChatId, NineHistory());

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llm = new FakeLlm(("S", Synth(Claim1))) { GateA = gate };
        var sut = Sut(llm);

        var run = sut.AnalyzeAsync(User, ChatId);
        SpinWait.SpinUntil(() => llm.Calls.Count > 0, 5000).Should().BeTrue("прогон реально в модели");

        // Откат приходит в середину прогона: он не пишет сразу (финальный AdvanceCursor
        // прогона затёр бы его), а ждёт задачу прогона и повторяет один раз.
        var back = sut.ReturnWindowAsync(User, ChatId, 3);
        back.IsCompleted.Should().BeFalse("чат в прогоне — откат отложен");

        gate.SetResult();
        var analyzed = await run;
        analyzed.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, analyzed.Reason);
        (await back).Should().BeTrue();

        // Мутация «не ждать прогон» (писать сразу): финальный AdvanceCursor прогона ставит
        // курсор 9 после отката — здесь 9 вместо 2, красное.
        var entry = Entry();
        entry.Cursor.Should().Be(2, "откат пережил финальный курсор прогона");
        entry.Zone.Should().BeNull();
    }

    // ── фейки — те же, что в WikiChatIngestTests ──

    private sealed class FakeLlm : ICheapTextRunner
    {
        public List<(string ActionKey, string Prompt, string Pass)> Calls { get; } = [];
        public TaskCompletionSource? GateA; // первый проход A ждёт, пока gate не открыт (на потоке вызова)

        private readonly Dictionary<string, Queue<string>> _answers = new();

        public FakeLlm(params (string Pass, string Answer)[] answers)
        {
            foreach (var (pass, answer) in answers)
                (_answers.TryGetValue(pass, out var q) ? q : _answers[pass] = new Queue<string>()).Enqueue(answer);
        }

        public string PromptOf(string pass, int nth = 1) =>
            Calls.Where(c => c.Pass == pass).ElementAt(nth - 1).Prompt;

        public bool UsesLocal(string actionKey) => true;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult(Answer(prompt, ct));

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult<string?>(Answer(prompt, ct));

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            var text = Answer(prompt, ct);
            return Task.FromResult(new OneShotResult(text, new OneShotUsage(10, 0, 0, 5, 0.0, "fake"), 1));
        }

        private string Answer(string prompt, CancellationToken ct)
        {
            var pass = prompt.Contains("YOUR PREVIOUS PLAN WAS REJECTED") ? "R"
                : prompt.Contains("single pass") ? "S"
                : "?";
            Calls.Add((LocalActionCatalog.WikiIngest, prompt, pass));
            if (pass == "S" && GateA is not null && Calls.Count(c => c.Pass == "S") == 1)
                GateA.Task.WaitAsync(ct).GetAwaiter().GetResult();
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

    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    private sealed class WikiFlagOn : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }
}
