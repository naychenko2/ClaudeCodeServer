using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// A6 (очередь 2): связность в итоге ночи — K0/K1/K2 по новым страницам и тронутым разделам,
// StrippedLinks, ConnectivityOk. Разбор и судья подменены фейками (образец WikiNightlyServiceTests):
// фейк разбора кладёт страницы в pages/ как это сделал бы Хранитель, замер идёт по настоящему диску.
public class WikiNightlyConnectivityTests : IDisposable
{
    private const string User = "u1";
    private const string Section = "projects/p1";
    private const string Dir = "Проекты/p1/";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly WikiKeeper _keeper;
    private readonly Sessions _sessions = new();
    private readonly Llm _llm = new();
    private readonly Flag _flag = new();
    private readonly List<string> _analyzed = [];
    private DateTimeOffset _now = new(2026, 11, 2, 4, 0, 0, TimeSpan.Zero);

    public WikiNightlyConnectivityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_nightly_conn_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        var notes = new NotesService(projects, _config, NullLogger<NotesService>.Instance, null, flags: _flag);
        _keeper = new WikiKeeper(notes, _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        var health = SafePath.Join(Root(), "pages/" + WikiSections.Folder(WikiSections.Common) + WikiKeeper.HealthFileName);
        if (!File.Exists(health))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(health)!);
            File.WriteAllText(health, "# Здоровье\n");
        }
        WriteOldPage("Сущность");
        WikiSettingsStore.Mutate(_dir, User, s => s with
        {
            Nightly = new WikiSettingsStore.NightlyConfig(true, 3, 6, 20, 6, 10, 700_000),
        });
        _sessions.All.Add(new Session { Id = "c1", OwnerId = User, ProjectId = "p1", Name = "c1", WikiMark = WikiMarks.Important, UpdatedAt = DateTime.UtcNow });
        var history = new List<StoredMessage>();
        for (var i = 0; i < 4; i++)
        {
            history.Add(new StoredUserMessage($"Реплика {i} " + new string('а', 100)));
            history.Add(new StoredTextMessage("Принято."));
        }
        _sessions.History["c1"] = history;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Root() => WikiPaths.Root(_dir, User);

    private string PagePath(string title) => Path.Combine(Root(), "pages", Dir + title.Replace(' ', '-') + ".md");

    private static string Content(string title, string text)
    {
        var sb = new StringBuilder("---\ntype: topic\naliases: []\nowns: []\nsources: []\n---\n# ").Append(title).Append('\n');
        for (var i = 0; i < WikiPage.SectionNames.Count; i++)
        {
            sb.Append("\n## ").Append(WikiPage.SectionNames[i]).Append('\n');
            if (i == 0)
                sb.Append(text).Append('\n');
        }
        return sb.ToString();
    }

    private void WritePage(string title, string text = "Содержимое страницы без упоминаний других.")
    {
        var path = PagePath(title);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Content(title, text));
    }

    private void WriteOldPage(string title) => WritePage(title);

    private string PagesHash()
    {
        var sb = new StringBuilder();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Root(), "pages"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            sb.Append(f).Append(':').Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))).Append('\n');
        return sb.ToString();
    }

    private WikiNightlyService Sut(Func<string, string, string?, CancellationToken, Task<WikiChatIngestService.ChatResult>> analyze,
        ILogger<WikiNightlyService>? log = null,
        Func<string, string, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>>? judge = null) =>
        new(_sessions, new WikiChatRecalc(_sessions, _config), new WikiChatStage2(_llm, _sessions, _config),
            analyze, _llm, _config, log ?? NullLogger<WikiNightlyService>.Instance, _flag,
            () => _now, null, _keeper, judge);

    private Func<string, string, string?, CancellationToken, Task<WikiChatIngestService.ChatResult>> Analyze(
        Action? write = null, int stripped = 0) =>
        (owner, chat, _, ct) =>
        {
            lock (_analyzed) _analyzed.Add(chat);
            write?.Invoke();
            return Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeNothingNew, null, [], StrippedLinks: stripped));
        };

    private WikiNightlyService.NightResult Last() => WikiNightlyService.ReadLast(Root()).Should().NotBeNull().And.Subject
        as WikiNightlyService.NightResult ?? throw new InvalidOperationException();

    [Fact]
    public async Task Итог_НовыеСвязаны_K1Единица_ConnectivityOk_ИСнятыеСсылкиСуммой()
    {
        var log = new RecordingLog();
        await Sut(Analyze(() =>
        {
            WritePage("Порядок работ");
            WritePage("Порядок встреч", "Обсудили Порядок работ и записали решение.");
        }, stripped: 7), log).RunNightTickAsync(_now);

        var r = Last();
        r.NewConnectivity.Should().NotBeNull();
        r.NewConnectivity!.Pages.Should().Be(2);
        r.NewConnectivity.K1.Should().Be(1.0, "B упоминает A, обе страницы связаны «тема ↔ тема»");
        r.NewConnectivity.K0.Should().Be(1.0, "степень каждой — 1 (упоминание)");
        r.NewConnectivity.K2.Should().Be(0);
        r.SectionConnectivity!.Pages.Should().Be(3, "тронутый раздел: сущность фикстуры и две новые");
        r.SectionConnectivity.K1.Should().BeApproximately(2.0 / 3, 1e-9);
        r.ConnectivityOk.Should().BeTrue();
        r.StrippedLinks.Should().Be(7);
        log.Rows.Select(x => x.Message).Should().Contain(m =>
            m.Contains("связность: новые K1 100 %, тело K2 0 %, прежняя степень 1.00; ссылок снято у модели 7"));
    }

    [Fact]
    public async Task Pass_TwoNewPages_OneTopicLinked()
    {
        // Фон из 58 страниц делает N большим, старая страница «Квантовая телепортация фотонов» —
        // сосед «По теме» первой новой (другого источника: карточек нет). Вторая новая изолирована.
        for (var i = 0; i < 58; i++)
            WritePage($"Пустая страница номер {i}");
        WritePage("Квантовая телепортация фотонов");
        var log = new RecordingLog();
        await Sut(Analyze(() =>
        {
            WritePage("Телепортация фотонов квантовая запутанных");
            WritePage("Омега");
        }), log).RunNightTickAsync(_now);

        var r = Last();
        r.NewConnectivity!.Pages.Should().Be(2);
        r.NewConnectivity.K1.Should().Be(0.5);
        log.Rows.Select(x => x.Message).Should().Contain(m => m.Contains("связность: новые K1 50 %"));
    }

    [Fact]
    public async Task Итог_НовыеИзолированы_ConnectivityOkFalse()
    {
        // Живая база: связанный старый корпус (Б упоминает А), K1 корпуса заметно выше допуска.
        WritePage("Старая А");
        WritePage("Старая Б", "Обсудили Старая А.");
        await Sut(Analyze(() =>
        {
            WritePage("Альфа");
            WritePage("Омега");
        })).RunNightTickAsync(_now);

        var r = Last();
        r.NewConnectivity!.K1.Should().Be(0);
        r.CorpusConnectivity!.Pages.Should().Be(3, "корпус — сущность фикстуры и две старые, новые не входят");
        r.CorpusConnectivity.K1.Should().BeApproximately(2.0 / 3, 1e-9);
        r.ConnectivityOk.Should().BeFalse("K1 новых 0 % ниже K1 корпуса минус 10 п. п.");
    }

    [Fact]
    public async Task Итог_БазаПуста_ConnectivityOkNull_НеTrue()
    {
        File.Delete(PagePath("Сущность"));
        var log = new RecordingLog();
        await Sut(Analyze(() => WritePage("Альфа")), log).RunNightTickAsync(_now);

        var r = Last();
        r.NewConnectivity.Should().NotBeNull();
        r.CorpusConnectivity.Should().BeNull("старых страниц знаний нет");
        r.ConnectivityOk.Should().BeNull("«не измерено», а не true");
        log.Rows.Select(x => x.Message).Should().Contain(m => m.Contains("корпус K1 —"));
    }

    [Fact]
    public void MeasureConnectivity_КорпусБезНовых_ОдинЗамерСтрогоПоСтарымСтраницам()
    {
        // Сторож «новые в базу не входят»: Pages корпуса = старые страницы знаний, K1 корпуса
        // равен отдельному Measure по ним на том же снимке.
        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        WritePage("Старая А");
        WritePage("Старая Б", "Обсудили Старая А.");
        foreach (var f in Directory.GetFiles(Path.Combine(Root(), "pages", Dir)))
            before[Dir + Path.GetFileName(f)] = File.ReadAllText(f);
        WritePage("Новая В");
        var after = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.GetFiles(Path.Combine(Root(), "pages", Dir)))
            after[Dir + Path.GetFileName(f)] = File.ReadAllText(f);

        var (nw, _, corpus) = WikiNightlyService.MeasureConnectivity(before, after);

        nw!.Pages.Should().Be(1);
        corpus!.Pages.Should().Be(before.Count, "база = все страницы знаний минус новые");
        var drafts = after.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new WikiGate.Draft(kv.Key, kv.Value)).ToList();
        var direct = WikiConnectivity.Measure(drafts, [.. before.Keys]);
        corpus.K1.Should().Be(direct.K1);
    }

    [Fact]
    public async Task Итог_НовыхСтраницНет_ConnectivityOkNull_СнятыеСсылкиНоль()
    {
        await Sut(Analyze()).RunNightTickAsync(_now);

        var r = Last();
        r.NewConnectivity.Should().BeNull();
        r.SectionConnectivity.Should().BeNull();
        r.ConnectivityOk.Should().BeNull("новых страниц не было");
        r.StrippedLinks.Should().Be(0, "замер состоялся: ноль снятых — это измерено");
    }

    [Fact]
    public void СтарыйИтогБезПолей_ЧитаетсяNull_НеНулём()
    {
        var file = Path.Combine(Root(), "keeper", "nightly-last.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file,
            """{"date":"2026-11-01","selected":1,"analyzed":1,"applied":0,"toInbox":0,"estTokens":0,"outcome":"completed"}""");

        var r = Last();

        r.StrippedLinks.Should().BeNull("«не измерено», а не 0");
        r.NewConnectivity.Should().BeNull();
        r.SectionConnectivity.Should().BeNull();
        r.ConnectivityOk.Should().BeNull();
    }

    [Fact]
    public async Task Замер_ФайлыPagesДоИПослеЗамераРавны()
    {
        // Инвариант «после судьи ночь только читает»: фейк судьи фиксирует хэш в момент вызова
        // (замер идёт сразу за ним), после прохода хэш тот же.
        string? atJudge = null;
        var sut = Sut(Analyze(() =>
        {
            WritePage("Порядок работ");
            WritePage("Порядок встреч", "Обсудили Порядок работ.");
        }), judge: (o, root, ct) =>
        {
            atJudge = PagesHash();
            return Task.FromResult(new WikiJudgeSweep.JudgeSweepResult(0, 0m, 0, 0, 0, 0, null));
        });

        await sut.RunNightTickAsync(_now);

        Last().NewConnectivity.Should().NotBeNull("замер состоялся");
        PagesHash().Should().Be(atJudge);
    }

    [Fact]
    public async Task TwoPasses_SameOwner_OneResult()
    {
        // Мутация: убрать `if (!Running.TryAdd(root, 0))` в StartManual — оба прохода идут,
        // _analyzed получает две записи и тест красный.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<WikiChatIngestService.ChatResult> Slow(string o, string c, string? engine, CancellationToken ct)
        {
            lock (_analyzed) _analyzed.Add(c);
            WritePage("Порядок работ");
            WritePage("Порядок встреч", "Обсудили Порядок работ.");
            entered.TrySetResult();
            await gate.Task;
            return new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, [], StrippedLinks: 3);
        }

        Task pass1 = Task.CompletedTask, pass2 = Task.CompletedTask;
        var first = Task.Run(() => Sut(Slow).TryStartManual(User, out pass1));
        var second = Task.Run(() => Sut(Slow).TryStartManual(User, out pass2));
        var results = await Task.WhenAll(first, second);

        results.Count(x => x == WikiNightlyService.ManualStart.Started).Should().Be(1);
        (await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5)))).Should().BeSameAs(entered.Task);
        gate.SetResult();
        await (results[0] == WikiNightlyService.ManualStart.Started ? pass1 : pass2);

        _analyzed.Should().ContainSingle("проход один");
        var r = Last();
        r.StrippedLinks.Should().Be(3);
        r.NewConnectivity!.Pages.Should().Be(2);
    }

    private sealed class RecordingLog : ILogger<WikiNightlyService>
    {
        public List<(LogLevel Level, string Message)> Rows { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Rows.Add((logLevel, formatter(state, exception)));
    }

    private sealed class Llm : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) => Task.FromResult("{}");

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => Task.FromResult<string?>("{}");

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null, object? jsonFormat = null,
            CancellationToken ct = default) =>
            Task.FromResult(new OneShotResult("{}", new OneShotUsage(10, 0, 0, 5, 0.0, "fake"), 1));

        public IReadOnlyList<LocalEngineExecutor> LocalExecutorsFor(string actionKey) => [];

        public Task<bool> IsLocalEngineReadyAsync(string engine, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class Sessions : ISessionDirectory
    {
        public readonly List<Session> All = [];
        public readonly Dictionary<string, List<StoredMessage>> History = [];

        public Session? GetById(string id) => All.FirstOrDefault(s => s.Id == id);
        public IReadOnlyCollection<Session> GetAll() => All;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>(History.TryGetValue(sessionId, out var h) ? h : []);
        public bool SetWikiMark(string sessionId, string? mark) => true;
    }

    private sealed class Flag : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }
}
