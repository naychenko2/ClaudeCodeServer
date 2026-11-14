using System.Reflection;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Ночной разбор чатов (Ф2, N7b). Разбор чата в большинстве тестов подменён делегатом (считает
// вызовы, возвращает заданный исход); цепочка «настоящий WikiChatIngestService + подменённый
// ICheapTextRunner» — в тестах места модели, курсора и single-flight. Стор, настройки и итог ночи —
// настоящие, на диске.
//
// Критерии карточки: c1 — окно, бюджет, лимиты пакетов, упор в открытые; c2 — дельта 2/3 реплики
// человека; c3 — пути инвариантов (исполнение задач, skip, серая без «да», сбой не двигает курсор,
// флаг/Enabled выкл → 0 новых файлов); c4 — отказ одного владельца не срывает остальных, один проход
// на владельца.
public class WikiNightlyServiceTests : IDisposable
{
    private const string User = "u1";
    private const string Other = "u2";
    private const string Section = "projects/p1";
    private const string PagePath = "Проекты/p1/Сущность.md";

    // Знание фикстуры (J-O1, тест журнала ночи): утверждение своими словами + дословная цитата
    // из реплики чата — то, что реально доезжает до страницы и чего в строке журнала быть не
    // должно. Та же пара, что в тестах разбора (WikiIngestTests).
    private const string ClaimText = "Сборку решено перенести на Linux";
    private const string ClaimQuote = "Решение: перенести сборку на Linux";

    private static readonly string NothingJson =
        $$"""{"outcome":"{{WikiKeeperPrompts.NothingToAdd}}","reason":"не о чём писать"}""";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _apply;
    private readonly Sessions _sessions = new();
    private readonly Llm _llm = new();
    private readonly Flag _flag = new();
    private readonly List<string> _analyzed = [];
    private DateTimeOffset _now = At(4);

    public WikiNightlyServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_nightly_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, null, flags: _flag);
        _keeper = new WikiKeeper(_notes, _config);
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config);
        foreach (var owner in new[] { User, Other })
            _keeper.EnsureInitializedAsync(owner).GetAwaiter().GetResult();
        _keeper.WritePageAsync(User, Section, PagePath, "---\ntype: topic\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n# Сущность\n\n## Сводка\n\n## Хронология\n\n## Противоречия\n\n## История\n")
            .GetAwaiter().GetResult();
        // Сигнал о нечитаемых настройках пишется в _health.md «Общего»: файл должен существовать.
        var health = SafePath.Join(Root(User), "pages/" + WikiSections.Folder(WikiSections.Common) + WikiKeeper.HealthFileName);
        if (!File.Exists(health))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(health)!);
            File.WriteAllText(health, "# Здоровье\n");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static DateTimeOffset At(int hour, int day = 2) => new(2026, 11, day, hour, 0, 0, TimeSpan.Zero);
    private string Root(string owner = User) => WikiPaths.Root(_dir, owner);
    private string LastFile(string owner = User) => Path.Combine(Root(owner), "keeper", "nightly-last.json");
    private string TriageFile() => Path.Combine(Root(), "keeper", "chat-triage.json");

    // ─── фикстуры ──────────────────────────────────────────────────────────

    private void Configure(bool enabled = true, int start = 3, int end = 6, int perNight = 20, int perSection = 6,
        int chatsPerSection = 10, int budget = 700_000, string owner = User) =>
        WikiSettingsStore.Mutate(_dir, owner, s => s with
        {
            Nightly = new WikiSettingsStore.NightlyConfig(enabled, start, end, perNight, perSection, chatsPerSection, budget),
        });

    private static List<StoredMessage> Hist(int humans, int len = 100)
    {
        var h = new List<StoredMessage>();
        for (var i = 0; i < humans; i++)
        {
            h.Add(new StoredUserMessage($"Реплика {i} " + new string('а', len)));
            h.Add(new StoredTextMessage("Принято."));
        }
        return h;
    }

    private Session AddChat(string id, int humans = 4, string? mark = WikiMarks.Important, string project = "p1",
        bool taskExecution = false, string owner = User, int len = 100, int ageMinutes = 0, List<StoredMessage>? history = null)
    {
        var s = new Session
        {
            Id = id,
            OwnerId = owner,
            ProjectId = project,
            Name = id,
            WikiMark = mark,
            TaskExecution = taskExecution,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-ageMinutes),
        };
        _sessions.All.Add(s);
        _sessions.History[id] = history ?? Hist(humans, len);
        return s;
    }

    // log — захват журнала для тестов строки итога (J-O1); без него — тихий SUT, как раньше.
    // judge — фейк прохода судьи (J-J4): без него судья не зовётся, её поля остаются нулями.
    private WikiNightlyService Sut(Func<string, string, string?, CancellationToken, Task<WikiChatIngestService.ChatResult>>? analyze = null,
        ClaudeHomeServer.Services.Composition.IFeatureFlagGate? flags = null,
        ClaudeHomeServer.Services.Composition.INotificationSender? notifier = null,
        ILogger<WikiNightlyService>? log = null,
        Func<string, string, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>>? judge = null) =>
        new(_sessions, new WikiChatRecalc(_sessions, _config), new WikiChatStage2(_llm, _sessions, _config),
            analyze ?? Done(), _llm, _config, log ?? NullLogger<WikiNightlyService>.Instance, flags ?? _flag,
            () => _now, notifier, _keeper, judge);

    // Фейк прохода судьи (J-J4): точка запуска и перенос её счётчиков проверяются без облачной
    // модели — числа фикстуры приходят в итог ровно как их вернул проход.
    private static Func<string, string, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>> Judge(
        int applied = 0, int rejected = 0, int deferred = 0, int codeRejected = 0, int calls = 0,
        decimal cost = 0m, string? firstFailure = null) =>
        (owner, root, ct) => Task.FromResult(new WikiJudgeSweep.JudgeSweepResult(
            calls, cost, applied, rejected, deferred, codeRejected, firstFailure));

    private Func<string, string, string?, CancellationToken, Task<WikiChatIngestService.ChatResult>> Done(
        string outcome = WikiChatIngestService.OutcomeNothingNew, int packages = 0) =>
        (owner, chat, _, ct) =>
        {
            lock (_analyzed) _analyzed.Add(chat);
            return Task.FromResult(new WikiChatIngestService.ChatResult(
                outcome, null, [.. Enumerable.Range(0, packages).Select(i => $"pkg-{chat}-{i}")]));
        };

    private Task Tick(WikiNightlyService? sut = null) => (sut ?? Sut()).RunNightTickAsync(_now);

    private WikiNightlyService.NightResult Last() => WikiNightlyService.ReadLast(Root()).Should().NotBeNull().And.Subject
        as WikiNightlyService.NightResult ?? throw new InvalidOperationException();

    // midJudge — фейк точки (б) внутри настоящего WikiIngestService (доработка финала J-J4):
    // считает, звался ли судья посреди ночи. null — точки (б) нет, как в цепочке без судьи.
    private WikiChatIngestService RealIngest(
        Func<string, string, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>>? midJudge = null) =>
        new(new WikiSourceCapture(_notes, _projects, _sessions, _config, _keeper),
            new WikiIngestService(_llm, new NoSpheres(), _apply, _config, null, midJudge), _sessions, _keeper, _config);

    // log — захват журнала для тестов строки итога (J-O1); без него — тихий SUT, как раньше.
    // judge — фейк точки (а) (проход судьи в конце), midJudge — фейк точки (б) в настоящей цепочке.
    private WikiNightlyService RealSut(ILogger<WikiNightlyService>? log = null,
        Func<string, string, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>>? judge = null,
        Func<string, string, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>>? midJudge = null) =>
        new(_sessions, new WikiChatRecalc(_sessions, _config), new WikiChatStage2(_llm, _sessions, _config),
            (o, c, _, ct) => RealIngest(midJudge).AnalyzeAsync(o, c, ct, LocalActionCatalog.WikiNightly),
            _llm, _config, log ?? NullLogger<WikiNightlyService>.Instance, _flag, () => _now,
            keeper: _keeper, judge: judge);

    // Строка журнала владельца (тест доработки финала J-J4): карантин N7d — страница,
    // откатанная человеком, делает операции новых пакетов спорными («Входящие»).
    private void LogLine(string ts, string op, string actor, params string[] pages)
    {
        var path = Path.Combine(Root(), "keeper", "log.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var arr = string.Join(",", pages.Select(p => "\"" + p + "\""));
        File.AppendAllText(path,
            $$"""{"ts":"{{ts}}","op":"{{op}}","section":"{{Section}}","pages":[{{arr}}],"actor":"{{actor}}"}""" + "\n");
    }

    private List<string> KeeperFiles() =>
        Directory.Exists(Path.Combine(Root(), "keeper"))
            ? [.. Directory.EnumerateFiles(Path.Combine(Root(), "keeper"), "*", SearchOption.AllDirectories).Order()]
            : [];

    private void AddOpenPackages(int n)
    {
        for (var i = 0; i < n; i++)
        {
            var r = WikiPackageStore.Create(Root(), new WikiPackage
            {
                Id = WikiPackageStore.NewId(),
                Section = Section,
                Sources = [new WikiPackage.SourceRef("note", "note:x")],
                Operations = [new WikiPackage.Operation
                {
                    Op = WikiPackage.OpAppend,
                    Page = PagePath,
                    Text = "утверждение " + i,
                    Class = WikiPackage.ClassKey("note", WikiPackage.OpAppend),
                    Decision = WikiPackage.DecisionInbox,
                    Reason = "спорное",
                    BaseHashes = new Dictionary<string, string>(),
                }],
                Status = WikiPackage.StatusChecking,
            });
            r.Ok.Should().BeTrue(r.Reason);
        }
        WikiPackageStore.CountOpen(Root()).Should().Be(n);
    }

    // ─── окно и часовой пояс ───────────────────────────────────────────────

    [Theory]
    [InlineData(21, 21, 2, true)]
    [InlineData(23, 21, 2, true)]
    [InlineData(0, 21, 2, true)]
    [InlineData(1, 21, 2, true)]
    [InlineData(2, 21, 2, false)]   // конец окна не входит: [from, to)
    [InlineData(20, 21, 2, false)]
    [InlineData(3, 3, 6, true)]
    [InlineData(6, 3, 6, false)]
    [InlineData(4, 4, 4, false)]
    public void Окно_ПолуоткрытоеИЧерезПолночь(int hour, int start, int end, bool expected) =>
        WikiNightlyService.InWindow(hour, start, end).Should().Be(expected);

    [Fact]
    public void ДатаПрохода_ЧастьОкнаПослеПолуночи_ЭтоДатаНачалаОкна()
    {
        WikiNightlyService.WindowDate(At(23, day: 2), 21, 2).Should().Be(new DateOnly(2026, 11, 2));
        WikiNightlyService.WindowDate(At(1, day: 3), 21, 2).Should().Be(new DateOnly(2026, 11, 2));
        WikiNightlyService.WindowDate(At(4, day: 3), 3, 6).Should().Be(new DateOnly(2026, 11, 3));
    }

    [Fact]
    public async Task ОкноЧерезПолночь_ОдинПроходНаНочь_ИДваДняДваПрохода()
    {
        Configure(start: 21, end: 2);
        AddChat("c1");
        _now = At(23, day: 2);
        await Tick();
        _analyzed.Should().Equal("c1");
        Last().Date.Should().Be("2026-11-02");

        // 01:00 следующих суток — та же ночь: повтора нет.
        _now = At(1, day: 3);
        await Tick(Sut());
        _analyzed.Should().Equal("c1");

        // 22:00 следующего дня — новая ночь, но дельты нет (чат тот же, курсор делегатом не двигается):
        // проход состоялся — разбор вызван заново.
        _now = At(22, day: 3);
        await Tick(Sut());
        _analyzed.Should().Equal("c1", "c1");
        Last().Date.Should().Be("2026-11-03");
    }

    [Fact]
    public async Task ВнеОкна_НольВызововИНольФайлов()
    {
        Configure();
        AddChat("c1");
        var before = KeeperFiles();
        _now = At(12);

        await Tick();

        _analyzed.Should().BeEmpty();
        _llm.Calls.Should().BeEmpty();
        KeeperFiles().Should().Equal(before);
    }

    // ─── пропуск без записи ────────────────────────────────────────────────

    [Fact]
    public async Task ФлагLlmWikiВыкл_ПропускБезЕдинойЗаписи()
    {
        Configure(enabled: true);
        AddChat("c1");
        _flag.On = false;
        var before = KeeperFiles();

        await Tick();

        _analyzed.Should().BeEmpty();
        _llm.Calls.Should().BeEmpty();
        KeeperFiles().Should().Equal(before, "в keeper/ ни nightly-last.json, ни chat-triage.json, ни других новых файлов");
    }

    [Fact]
    public async Task ТумблерВыкл_ПоУмолчанию_ПропускБезЕдинойЗаписи()
    {
        // Настроек ночи нет вовсе: блок читается как умолчания, а умолчание — выключено.
        AddChat("c1");
        var before = KeeperFiles();

        await Tick();

        _analyzed.Should().BeEmpty();
        KeeperFiles().Should().Equal(before);
        Configure(enabled: false);
        before = KeeperFiles();
        await Tick();
        _analyzed.Should().BeEmpty();
        KeeperFiles().Should().Equal(before);
    }

    [Fact]
    public async Task НечитаемыеНастройки_ОчередиНет_СтрокаВHealth_ОднаВСутки()
    {
        File.WriteAllText(Path.Combine(Root(), "keeper", "settings.json"), "{ не json");
        AddChat("c1");
        var health = Path.Combine(Root(), "pages", WikiSections.Folder(WikiSections.Common)!, WikiKeeper.HealthFileName);
        var sut = Sut();

        await Tick(sut);
        await Tick(sut);

        _analyzed.Should().BeEmpty("умолчания — ночь выключена");
        File.Exists(LastFile()).Should().BeFalse();
        File.ReadAllText(health).Split("нечитаемы").Length.Should().Be(2, "ровно одна строка за сутки, а не по строке на тик");
    }

    // ─── LI-1: ночь сама заводит вики при включённом тумблере ──────────────

    private const string Fresh = "u3"; // владелец без pages/_schema.md

    private string FreshSchema() => Path.Combine(Root(Fresh), "pages", WikiPaths.SchemaFileName);
    private string FreshPages() => Path.Combine(Root(Fresh), "pages");

    [Fact]
    public async Task ТумблерВключён_ВикиНеЗаведена_НочьЗаводитПроходитИУведомляет()
    {
        Configure(owner: Fresh);
        AddChat("c1", owner: Fresh);
        File.Exists(FreshSchema()).Should().BeFalse();
        var notifier = new Notifier();

        await Tick(Sut(Done(WikiChatIngestService.OutcomeApplied, packages: 1), notifier: notifier));

        File.Exists(FreshSchema()).Should().BeTrue("ночь завела вики через EnsureInitializedAsync");
        _analyzed.Should().Equal(new[] { "c1" }, "проход выполнен");
        WikiNightlyService.ReadLast(Root(Fresh)).Should().NotBeNull();
        notifier.Sent.Should().ContainSingle("итог ночи доставлен владельцу");
    }

    [Fact]
    public async Task НеЗаводит_ВнеОкна()
    {
        Configure(owner: Fresh);
        AddChat("c1", owner: Fresh);
        _now = At(12);

        await Tick();

        Directory.Exists(FreshPages()).Should().BeFalse();
        _analyzed.Should().BeEmpty();
    }

    [Fact]
    public async Task НеЗаводит_ТумблерВыключен()
    {
        Configure(enabled: false, owner: Fresh);
        AddChat("c1", owner: Fresh);

        await Tick();

        Directory.Exists(FreshPages()).Should().BeFalse();
        _analyzed.Should().BeEmpty();
    }

    [Fact]
    public async Task НеЗаводит_ФлагLlmWikiВыключен()
    {
        Configure(owner: Fresh);
        AddChat("c1", owner: Fresh);
        _flag.On = false;

        await Tick();

        Directory.Exists(FreshPages()).Should().BeFalse();
        _analyzed.Should().BeEmpty();
    }

    [Fact]
    public async Task НеЗаводит_НевалидныеНастройки_БезСигналаИЗаписи()
    {
        Directory.CreateDirectory(Path.Combine(Root(Fresh), "keeper"));
        File.WriteAllText(Path.Combine(Root(Fresh), "keeper", "settings.json"), "{ не json");
        AddChat("c1", owner: Fresh);

        await Tick();

        Directory.Exists(FreshPages()).Should().BeFalse("_health.md создал бы pages/ мимо единственного метода заведения");
        _analyzed.Should().BeEmpty();
    }

    [Fact]
    public async Task Гонка_НочнойТикИЗаведениеИзВне_ОдинШаблон_ПравкаЧеловекаНеЗатёрта()
    {
        Configure(owner: Fresh);
        AddChat("c1", owner: Fresh);
        var sut = Sut();
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Оба заведения стартуют по одному сигналу — ночь и «PUT settings/analyze».
        var tick = Task.Run(async () => { await go.Task; await sut.RunNightTickAsync(_now); });
        var direct = Task.Run(async () => { await go.Task; await _keeper.EnsureInitializedAsync(Fresh); });
        go.SetResult();
        await Task.WhenAll(tick, direct);

        File.Exists(FreshSchema()).Should().BeTrue();
        const string edited = "# Правка человека\n";
        File.WriteAllText(FreshSchema(), edited);

        _now = At(4, day: 3); // следующая ночь: заведение идёт повторно и параллельно с прямым вызовом
        AddChat("c2", owner: Fresh);
        var go2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tick2 = Task.Run(async () => { await go2.Task; await sut.RunNightTickAsync(_now); });
        var direct2 = Task.Run(async () => { await go2.Task; await _keeper.EnsureInitializedAsync(Fresh); });
        go2.SetResult();
        await Task.WhenAll(tick2, direct2);

        File.ReadAllText(FreshSchema()).Should().Be(edited, "заведение идемпотентно: шаблон поверх правки человека не пишется");
    }

    // ─── c1: бюджет, лимиты пакетов и открытых ────────────────────────────

    [Fact]
    public async Task Бюджет_ЧатамНеБольше70ПроцентовИОценкаВИтоге()
    {
        // Бюджет 7 000 (в 100 раз меньше 700k ради скорости): потолок чатов — 4 900 токенов;
        // дельта чата — 3 реплики × 2 000 знаков ≈ 1 500 токенов, помещается три, четвёртый — упор.
        Configure(budget: 7_000);
        for (var i = 0; i < 5; i++)
            AddChat($"c{i}", humans: 3, len: 2_000, ageMinutes: i);

        await Tick();

        _analyzed.Should().HaveCount(3);
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeBudget);
        last.EstTokens.Should().BeInRange(1, 4_900);
        last.Analyzed.Should().Be(3);
    }

    [Fact]
    public async Task ЛимитПакетовЗаНочь_21йПакетНеСоздаётся()
    {
        Configure(perNight: 20, perSection: 20, chatsPerSection: 20);
        for (var i = 0; i < 25; i++)
            AddChat($"c{i}", project: $"p{i}", ageMinutes: i); // 25 разделов — упор именно в лимит ночи

        await Tick(Sut(Done(WikiChatIngestService.OutcomeApplied, packages: 1)));

        _analyzed.Should().HaveCount(20);
        var last = Last();
        last.Applied.Should().Be(20);
        last.Outcome.Should().Be(WikiNightlyService.OutcomeBudget);
    }

    [Fact]
    public async Task ЛимитПакетовНаРаздел_ЧужойРазделПродолжаетРазбираться()
    {
        Configure(perSection: 6);
        for (var i = 0; i < 9; i++)
            AddChat($"a{i}", project: "p1", ageMinutes: i);
        AddChat("b0", project: "p2", ageMinutes: 100);

        await Tick(Sut(Done(WikiChatIngestService.OutcomeApplied, packages: 1)));

        _analyzed.Count(c => c.StartsWith('a')).Should().Be(6);
        _analyzed.Should().Contain("b0");
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeCompleted, "упор раздела не рвёт ночь");
    }

    [Fact]
    public async Task ЛимитПакетовЗаНочь_ЧатСПакетамиСверхОстатка_ОстанавливаетПроход()
    {
        // Пакеты чата прибавляются пачкой: при лимите 2 чат c1 даёт 3 — превышение допустимо на сам
        // этот чат (пакеты уже в Хранителе, их не откатывать), но дальше ночь не идёт: c2 не
        // разбирается вовсе, итог — budget. Лимит раздела здесь ни при чём: он закрывает только
        // свой раздел (тест ниже).
        Configure(perNight: 2, perSection: 20, chatsPerSection: 20);
        AddChat("c1", project: "p1", ageMinutes: 0);
        AddChat("c2", project: "p2", ageMinutes: 1);

        await Tick(Sut(Done(WikiChatIngestService.OutcomeApplied, packages: 3)));

        _analyzed.Should().Equal(new[] { "c1" }, "после превышения следующий чат не берётся");
        var last = Last();
        last.Applied.Should().Be(3, "пакеты состоявшегося чата не откатываются");
        last.Outcome.Should().Be(WikiNightlyService.OutcomeBudget);
    }

    [Fact]
    public async Task ЛимитПакетовНаРаздел_ЧатСПакетамиСверхОстатка_ЗакрываетТолькоРаздел()
    {
        // Тот же сценарий на лимите раздела (решение 02.11): a1 даёт 3 пакета при лимите 2 — раздел
        // закрыт, a2 из него не разбирается; b1 из другого раздела разбирается, итог — completed
        // (ночь отработала по плану). Мутация «вернуть для раздела budget+break»: b1 не зовётся,
        // итог — budget, тест краснеет.
        Configure(perNight: 20, perSection: 2);
        AddChat("a1", project: "p1", ageMinutes: 0);
        AddChat("a2", project: "p1", ageMinutes: 1);
        AddChat("b1", project: "p2", ageMinutes: 2);

        await Tick(Sut(Done(WikiChatIngestService.OutcomeApplied, packages: 3)));

        _analyzed.Should().Equal(new[] { "a1", "b1" }, "раздел закрыт, чужой раздел продолжает");
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
        last.Applied.Should().Be(6, "пакеты состоявшихся чатов (a1 и b1) не откатываются");
    }

    [Fact]
    public async Task ЛимитЧатовНаРаздел_Десять()
    {
        Configure(chatsPerSection: 10);
        for (var i = 0; i < 14; i++)
            AddChat($"c{i}", ageMinutes: i);

        await Tick();

        _analyzed.Should().HaveCount(10);
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
    }

    [Fact]
    public async Task УпорВОткрытыеПакеты_ИтогOpenLimit_НольВызовов()
    {
        Configure();
        AddChat("c1");
        AddOpenPackages(WikiPackage.MaxOpenPackages);

        await Tick();

        _analyzed.Should().BeEmpty();
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeOpenLimit);
    }

    [Fact]
    public async Task УпорВОткрытыеПакетыПосредиНочи_ПоследующиеЧатыНеЗовутся()
    {
        Configure();
        AddChat("c1", ageMinutes: 0);
        AddChat("c2", ageMinutes: 1);
        AddChat("c3", ageMinutes: 2);
        var calls = 0;
        Task<WikiChatIngestService.ChatResult> Analyze(string o, string c, string? engine, CancellationToken ct)
        {
            _analyzed.Add(c);
            if (++calls == 1) AddOpenPackages(WikiPackage.MaxOpenPackages);
            return Task.FromResult(new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeAwaitingHuman, null, []));
        }

        await Tick(Sut(Analyze));

        _analyzed.Should().Equal("c1");
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeOpenLimit);
        Last().ToInbox.Should().Be(1);
    }

    // ─── c2: дельта реплик человека ────────────────────────────────────────

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public async Task Дельта_ДвеРепликиЖдут_ТриРазбираются(int humans, bool analyzed)
    {
        Configure();
        AddChat("c1", humans: humans);

        await Tick();

        _analyzed.Should().Equal(analyzed ? ["c1"] : []);
    }

    [Fact]
    public async Task Дельта_СчитаетсяПослеКурсора()
    {
        Configure();
        AddChat("c1", humans: 5); // записи: 10, реплики человека — на нечётных
        // Курсор после третьей реплики человека (запись 6): в дельте остаются две реплики.
        WikiChatTriageStore.Upsert(Root(), new WikiChatTriageEntry("c1", 6, "h", null, null, 0, default)).Ok.Should().BeTrue();

        await Tick();

        _analyzed.Should().BeEmpty("после курсора 2 реплики человека — копим дальше");
    }

    // ─── c3: пути инвариантов ──────────────────────────────────────────────

    [Fact]
    public async Task ЧатИсполненияЗадач_ВажныйСБаллом_НольВызововМоделиОбоихМест()
    {
        Configure();
        AddChat("t1", humans: 6, taskExecution: true, mark: WikiMarks.Important);
        // Подмена разбора считает вызовы: настоящий AnalyzeAsync отказал бы сам, но здесь проверяется
        // именно очередь ночи — до разбора и до ступени 2 такой чат не должен дойти.
        await Tick();

        _llm.Calls.Should().BeEmpty("ни ступень 2, ни разбор модель не зовут");
        _analyzed.Should().BeEmpty();
    }

    [Fact]
    public async Task ПометкаSkip_ПобеждаетБалл_НольВызовов()
    {
        Configure();
        // Богатый чат (балл заведомо выше порога), но человек сказал «не разбирать».
        AddChat("s1", mark: WikiMarks.Skip, history: Rich());

        await Tick();

        _llm.Calls.Should().BeEmpty();
        _analyzed.Should().BeEmpty();
    }

    [Fact]
    public async Task СерыйЧат_БезДаСтупени2_НеБерётсяИСледующаяНочьСпрашиваетСнова()
    {
        Configure();
        AddChat("g1", mark: null, history: Gray());
        WikiChatScorer.Score(ScorerInput(_sessions.GetById("g1")!)).Zone.Should().Be(WikiChatScorer.ZoneGray, "фикстура серого чата");
        _llm.Reply = a => a == LocalActionCatalog.WikiTriage ? "это не json" : NothingJson;

        await Tick();

        _llm.Calls.Count(c => c.ActionKey == LocalActionCatalog.WikiTriage).Should().Be(1);
        _llm.Calls.Should().NotContain(c => c.ActionKey == LocalActionCatalog.WikiNightly);
        _analyzed.Should().BeEmpty();
        var e = WikiChatTriageStore.Get(Root()).Entries.Single(x => x.SessionId == "g1");
        e.Stage2Zone.Should().BeNull("отказ ступени 2 — серая зона, вердикт не пишется");

        _now = At(4, day: 3); // следующая ночь
        await Tick(Sut());
        _llm.Calls.Count(c => c.ActionKey == LocalActionCatalog.WikiTriage).Should().Be(2, "спросили снова");
    }

    [Fact]
    public async Task СерыйЧат_СДаСтупени2_Разбирается_СНетВердиктНеМеняетЗону()
    {
        Configure();
        AddChat("g1", mark: null, history: Gray());
        AddChat("g2", mark: null, history: Gray(), ageMinutes: 5);
        _llm.Reply = a => a == LocalActionCatalog.WikiTriage
            ? """{"hasKnowledge":true,"about":"a","reason":"r"}"""
            : NothingJson;

        await Tick();
        _analyzed.Should().BeEquivalentTo(["g1", "g2"]);

        // Нет: вердикт «знания нет» записан, чат не берётся.
        _analyzed.Clear();
        _llm.Reply = a => """{"hasKnowledge":false,"about":"a","reason":"r"}""";
        WikiChatTriageStore.UpdateStage2(Root(), "g1", WikiChatScorer.ZoneSkipped, "нет", 3).Ok.Should().BeTrue();
        _now = At(4, day: 3);
        await Tick(Sut());
        _analyzed.Should().NotContain("g1", "ступень 2 сказала «нет», реплик стало не на 3 больше");
    }

    [Fact]
    public async Task СерыйЧат_УстаревшееДаСтупени2_ПереспрашиваетсяСимметричноНет()
    {
        Configure();
        AddChat("g1", mark: null, history: GrayStale());
        // Ночь 1: ступень 2 сказала «да» (historyCount=3) — вердикт записан, чат разобран.
        _llm.Reply = a => a == LocalActionCatalog.WikiTriage
            ? """{"hasKnowledge":true,"about":"a","reason":"r"}"""
            : NothingJson;
        await Tick();
        _analyzed.Should().Equal("g1");
        var verdict = WikiChatTriageStore.Get(Root()).Entries.Single(x => x.SessionId == "g1");
        verdict.Stage2Zone.Should().Be(WikiChatScorer.ZoneTriage);
        verdict.Stage2HistoryCount.Should().Be(3);

        // Следующая ночь, реплик столько же: свежее «да» не переспрашивается, чат разобран.
        _analyzed.Clear();
        _llm.Calls.Clear();
        _now = At(4, day: 3);
        await Tick(Sut());
        _llm.Calls.Should().NotContain(c => c.ActionKey == LocalActionCatalog.WikiTriage,
            "свежий вердикт не тратит второй вызов модели");
        _analyzed.Should().Equal("g1");

        // +3 реплики человека (короткие: зона ступени 1 не меняется) — «да» устарело: ступень 2
        // зовётся снова, как устаревшее «нет»; новый отказ откладывает чат до следующей ночи.
        // Мутация «не переспрашивать «да»»: модель не позвана, _analyzed = [g1] — тест краснеет.
        _sessions.History["g1"] = [.. GrayStale(), .. ShortHumans(3)];
        WikiChatScorer.Score(ScorerInput(_sessions.GetById("g1")!)).Zone.Should().Be(WikiChatScorer.ZoneGray,
            "фикстура остаётся серой: переспрос проверяется на сером чате");
        _analyzed.Clear();
        _llm.Calls.Clear();
        _llm.Reply = _ => """{"hasKnowledge":false,"about":"a","reason":"r"}""";
        _now = At(4, day: 4);
        await Tick(Sut());
        _llm.Calls.Should().Contain(c => c.ActionKey == LocalActionCatalog.WikiTriage,
            "устаревшее «да» переспрашивается симметрично устаревшему «нет»");
        _analyzed.Should().BeEmpty("новый отказ — чат отложен");
        var after = WikiChatTriageStore.Get(Root()).Entries.Single(x => x.SessionId == "g1");
        after.Stage2Zone.Should().Be(WikiChatScorer.ZoneSkipped);
        after.Stage2HistoryCount.Should().Be(6, "переспрос меряет историю заново");
    }

    [Fact]
    public async Task СбойРазбора_ИсключениеИлиОтказ_КурсорНеМеняется_НочьИдётДальше()
    {
        Configure();
        AddChat("c1", ageMinutes: 0);
        AddChat("c2", ageMinutes: 1);
        WikiChatTriageStore.Upsert(Root(), new WikiChatTriageEntry("c1", 0, null, null, null, 0, default)).Ok.Should().BeTrue();
        var before = WikiChatTriageStore.Get(Root()).Entries.Single(e => e.SessionId == "c1");
        Task<WikiChatIngestService.ChatResult> Analyze(string o, string c, string? engine, CancellationToken ct)
        {
            _analyzed.Add(c);
            if (c == "c1") throw new InvalidOperationException("разбор упал");
            return Task.FromResult(new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeRejected, "сбой окна", []));
        }

        await Tick(Sut(Analyze));

        _analyzed.Should().Equal("c1", "c2");
        var after = WikiChatTriageStore.Get(Root()).Entries.Single(e => e.SessionId == "c1");
        after.Cursor.Should().Be(before.Cursor);
        after.CursorHash.Should().Be(before.CursorHash);
        WikiChatTriageStore.Get(Root()).Entries.Single(e => e.SessionId == "c2").Cursor.Should().Be(0);
        // С 2.11 (WN-2) итог этой ночи — error, не completed: единственный состоявшийся разбор
        // отказан с нулевым входом — весь выход ночи нулевой (инвариант «нулевой вход при всех
        // отказах ≠ completed»). Смысл теста не изменился: исключение не роняет ночь (она дошла до
        // c2), курсоры не тронуты; изменилась только честная отметка итога.
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeError);
        Last().Rejected.Should().Be(1, "сбой исключением в rejected не идёт, отказ c2 — идёт");
        Last().FirstRejectReason.Should().Be("сбой окна");
    }

    [Fact]
    public async Task НастоящийРазбор_ОтказМодели_КурсорНаМесте()
    {
        Configure();
        AddChat("c1");
        _llm.Reply = _ => "это не контракт";

        await Tick(RealSut());

        _llm.Calls.Should().NotBeEmpty();
        WikiChatTriageStore.Get(Root()).Entries.Single(e => e.SessionId == "c1").Cursor.Should().Be(0, "сбой применения не двигает курсор");
    }

    // ─── место модели и single-flight ──────────────────────────────────────

    [Fact]
    public async Task НастоящийРазбор_НочьЗовётМестоWikiNightly_ИДвигаетКурсорПоНечегоДобавить()
    {
        Configure();
        AddChat("c1");

        await Tick(RealSut());

        _llm.Calls.Should().NotBeEmpty().And.OnlyContain(c => c.ActionKey == LocalActionCatalog.WikiNightly);
        var e = WikiChatTriageStore.Get(Root()).Entries.Single(x => x.SessionId == "c1");
        e.Cursor.Should().Be(8, "курсор сдвинул AnalyzeAsync по штатному исходу nothing_new");
        e.Zone.Should().Be(WikiChatIngestService.ZoneNothingNew);
        Last().Analyzed.Should().Be(1);
    }

    [Fact]
    public async Task КнопкаИТик_ПоОдномуЧату_ОдинРазбор()
    {
        Configure();
        AddChat("c1");
        var ingest = RealIngest();
        _llm.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tickCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = new WikiNightlyService(_sessions, new WikiChatRecalc(_sessions, _config), new WikiChatStage2(_llm, _sessions, _config),
            (o, c, _, ct) =>
            {
                var run = ingest.AnalyzeAsync(o, c, ct, LocalActionCatalog.WikiNightly);
                tickCalled.TrySetResult(); // после вызова: заявка тика уже села на чужой прогон (или начала свой)
                return run;
            },
            _llm, _config, NullLogger<WikiNightlyService>.Instance, _flag, () => _now);

        var button = ingest.AnalyzeAsync(User, "c1"); // кнопка пришла первой
        var tick = sut.RunNightTickAsync(_now);
        // Ждём события, а не таймер: модель вызвана кнопкой и тик уже обратился к разбору того же чата.
        // Таймаут вместо зависания, если событие не наступит.
        var both = Task.WhenAll(_llm.Entered.Task, tickCalled.Task);
        var reached = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(5)));
        reached.Should().BeSameAs(both, "и кнопка дошла до модели, и тик обратился к разбору");
        _llm.Gate.SetResult();
        await Task.WhenAll(button, tick);

        _llm.Calls.Should().ContainSingle("один прогон на двоих");
        _llm.Calls[0].ActionKey.Should().Be(LocalActionCatalog.WikiIngest, "движок того, кто пришёл первым");
    }

    [Fact]
    public async Task ЛокальныйДвижокНедоступен_ОблакоМолчаНеПодставляется()
    {
        Configure();
        AddChat("c1");
        _llm.Route = false;

        await Tick(RealSut());

        _llm.Calls.Should().BeEmpty();
        _analyzed.Should().BeEmpty();
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeError);
    }

    [Fact]
    public async Task ПубличныйКонструкторПробрасываетИсполнителя_РазборЗовётНазванный_МестомНочи()
    {
        // W4a1: шов _analyze принимает engine и отдаёт его AnalyzeAsync рядом с константой места.
        // Проверяется сам шов в той точке, где он собирается по-настоящему (публичный конструктор),
        // на настоящей цепочке разбора: названного движка под тестом нет — вызов зарегистрирован
        // и ответил null, то есть отказ окна по причине маршрута.
        // Мутация «публичный конструктор теряет engine» (AnalyzeAsync без него): заявка ушла бы
        // через RunLocalOrChosenDetailedAsync — EngineCalls пуст и отказа по причине маршрута нет,
        // оба утверждения краснеют.
        Configure();
        AddChat("c-engine");
        var sut = new WikiNightlyService(_sessions, new WikiChatRecalc(_sessions, _config),
            new WikiChatStage2(_llm, _sessions, _config), RealIngest(), _keeper,
            new WikiJudgeSweep(_llm, _apply, _config), _llm, _config,
            NullLogger<WikiNightlyService>.Instance, _flag);
        var analyze = (Func<string, string, string?, CancellationToken, Task<WikiChatIngestService.ChatResult>>)
            typeof(WikiNightlyService).GetField("_analyze", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(sut)!;

        var res = await analyze(User, "c-engine", "gemma", CancellationToken.None);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected, res.Reason);
        res.Reason.Should().Contain(WikiIngestService.ReasonLocalUnavailable);
        _llm.EngineCalls.Should().ContainSingle("путь был только на названном исполнителе")
            .Which.Engine.Should().Be("gemma", "имя дошло до вызова модели как есть");
        _llm.EngineCalls[0].ActionKey.Should().Be(LocalActionCatalog.WikiNightly,
            "место заявки — константа ночи, её не берут из вызова");
    }

    // ─── PUT enabled=false и закрытие окна посреди ночи ────────────────────

    [Fact]
    public async Task ТумблерВыключенПосредиНочи_СтопПередСледующимЧатом()
    {
        Configure();
        AddChat("c1", ageMinutes: 0);
        AddChat("c2", ageMinutes: 1);
        Task<WikiChatIngestService.ChatResult> Analyze(string o, string c, string? engine, CancellationToken ct)
        {
            _analyzed.Add(c);
            Configure(enabled: false); // PUT enabled=false посреди ночи
            return Task.FromResult(new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []));
        }

        await Tick(Sut(Analyze));

        _analyzed.Should().Equal("c1");
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeDisabledMidway);
    }

    [Fact]
    public async Task ОкноЗакрылосьПосредиНочи_ИтогWindowClosed()
    {
        Configure(start: 3, end: 6);
        AddChat("c1", ageMinutes: 0);
        AddChat("c2", ageMinutes: 1);
        Task<WikiChatIngestService.ChatResult> Analyze(string o, string c, string? engine, CancellationToken ct)
        {
            _analyzed.Add(c);
            _now = At(6); // конец окна не входит
            return Task.FromResult(new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []));
        }

        await Tick(Sut(Analyze));

        _analyzed.Should().Equal("c1");
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeWindowClosed);
    }

    // ─── дата прохода, рестарт, атомарность ────────────────────────────────

    [Fact]
    public async Task ЗавершённыйПроход_НеПовторяетсяПослеРестарта_ОшибкаПробуетсяСнова()
    {
        Configure();
        AddChat("c1");
        File.WriteAllText(TriageFile(), "{ битый стор"); // пересчёт отказывает → проход error
        await Tick();
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeError);
        _analyzed.Should().BeEmpty();

        File.Delete(TriageFile());
        await Tick(Sut()); // «рестарт»: новый экземпляр службы
        _analyzed.Should().Equal("c1");
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);

        await Tick(Sut()); // ещё один «рестарт» в ту же ночь
        _analyzed.Should().Equal("c1");
    }

    [Fact]
    public async Task ДваТикаБезМаршрута_ОдноУведомлениеНаДату_ДругаяДатаУведомляетСнова()
    {
        // Локальная модель недоступна всю ночь: каждый тик окна проходит её заново (error дату
        // не закрывает), но предупреждение владелец получает один раз на дату. Мутация «убрать
        // условие» (зовёт TryNotifyAsync без охраны errorAlreadyNotified) — второй тик шлёт
        // второе уведомление, тест краснеет.
        Configure();
        AddChat("c1");
        _llm.Route = false;
        var notifier = new Notifier();
        var sut = Sut(notifier: notifier);

        await Tick(sut);
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeError);
        notifier.Sent.Should().HaveCount(1, "первая неудача ночи названа");

        await Tick(sut); // следующий тик того же окна: дата по-прежнему не закрыта, уведомление — нет
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeError, "error дату не закрывает");
        notifier.Sent.Should().HaveCount(1, "уведомление об error — одно на дату");

        // Следующая ночь: записанный error — другой (вчерашней) даты — уведомление уходит снова.
        _now = At(4, day: 3);
        await Tick(sut);
        Last().Date.Should().Be("2026-11-03");
        notifier.Sent.Should().HaveCount(2, "error другой даты назван снова");
    }

    [Fact]
    public async Task ОшибкаПотомУспехВТуЖеДату_УспехСообщается()
    {
        // Мутация «убрать проверку исхода из охраны» (молчать при любом исходе после error этой
        // даты) — второй тик не шлёт уведомление об успехе, тест краснеет.
        Configure();
        AddChat("c1");
        File.WriteAllText(TriageFile(), "{ битый стор"); // пересчёт отказывает → проход error
        await Tick();
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeError);

        File.Delete(TriageFile());
        var notifier = new Notifier();
        await Tick(Sut(notifier: notifier));

        Last().Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
        notifier.Sent.Should().HaveCount(1, "успех после error той же даты сообщается");
    }

    [Fact]
    public async Task ПрерванныйПроход_ДатуНеЗакрывает()
    {
        Configure();
        AddChat("c1", ageMinutes: 0);
        AddChat("c2", ageMinutes: 1);
        using var cts = new CancellationTokenSource();
        Task<WikiChatIngestService.ChatResult> Analyze(string o, string c, string? engine, CancellationToken ct)
        {
            _analyzed.Add(c);
            cts.Cancel(); // остановка хоста посреди прохода
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []));
        }

        await Sut(Analyze).RunNightTickAsync(_now, cts.Token);

        File.Exists(LastFile()).Should().BeFalse("дата ставится после успешного прохода, а не до него");
        _analyzed.Clear();
        await Tick(Sut());
        _analyzed.Should().NotBeEmpty("следующий запуск проходит ночь заново");
    }

    [Fact]
    public async Task ИтогНочи_ПишетсяАтомарно_БезОстаточныхTmp()
    {
        Configure();
        AddChat("c1");
        await Tick(Sut(Done(WikiChatIngestService.OutcomeApplied, packages: 2)));

        Directory.EnumerateFiles(Path.Combine(Root(), "keeper"), "*.tmp").Should().BeEmpty();
        var last = Last();
        last.Selected.Should().Be(1);
        last.Analyzed.Should().Be(1);
        last.Applied.Should().Be(2);
        last.ToInbox.Should().Be(0);
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
        last.Date.Should().Be("2026-11-02");
        File.ReadAllText(LastFile()).Should().Contain("\"estTokens\"").And.Contain("\"toInbox\"");
    }

    // ─── WC-3: частичный исход считается в applied ─────────────────────────
    // Частичный исход разбора (Partial, отказ позднего окна) — Outcome applied:
    // применённые пакеты чата считаются в итог ночи так же, как при полном разборе.

    [Fact]
    public async Task ЧастичныйИсход_СчитаетсяВApplied()
    {
        Configure();
        AddChat("c1");
        AddChat("c2");
        await Tick(Sut((owner, chat, _, ct) =>
        {
            lock (_analyzed) _analyzed.Add(chat);
            return Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeApplied, "разбор окна m8-m10 не удался", ["pkg-" + chat],
                Partial: true));
        }));

        var last = Last();
        last.Analyzed.Should().Be(2);
        last.Applied.Should().Be(2, "пакеты частичного исхода — в applied, отказ позднего окна их не перечёркивает");
        last.ToInbox.Should().Be(0);
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
    }

    // ─── WC-4b: фактические входные токены в итоге ночи ─────────────────────
    // Итог несёт сумму фактического входа разобранных чатов (ChatResult.InputTokens, WC-4a) рядом с
    // оценкой: EstTokens решает бюджет заранее, InputTokens — что реально ушло модели.

    [Fact]
    public async Task ИтогНочи_InputTokensРавенСуммеФактических()
    {
        // Мутации: «не прибавлять r.InputTokens» → 0; «суммировать только applied» → 0 (исход здесь
        // nothing_new). Сбой разбора (c3) до analyzed++ не доходит — в сумме его нет.
        Configure();
        AddChat("c1");
        AddChat("c2");
        AddChat("c3");
        await Tick(Sut((owner, chat, _, ct) =>
        {
            lock (_analyzed) _analyzed.Add(chat);
            if (chat == "c3") throw new InvalidOperationException("разбор не вернулся");
            return Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeNothingNew, null, [],
                InputTokens: chat == "c1" ? 1_000 : 2_500));
        }));

        var last = Last();
        last.Analyzed.Should().Be(2, "сбой разбора в итог не входит");
        last.InputTokens.Should().Be(3_500, "сумма фактического входа разобранных чатов");
        last.EstTokens.Should().BeGreaterThan(0, "оценка остаётся: по ней бюджет решает заранее");
        File.ReadAllText(LastFile()).Should().Contain("\"inputTokens\"");
    }

    [Fact]
    public async Task ИтогНочи_InputTokensВключаетВходСтупени2()
    {
        // Мутация «не прибавлять judged.InputTokens к inputTokens в RunPassAsync» → 1 000
        // (красный): фактический вход ступени 2 выпадает из суммы.
        Configure();
        AddChat("g1", mark: null, history: Gray());
        WikiChatScorer.Score(ScorerInput(_sessions.GetById("g1")!)).Zone.Should().Be(WikiChatScorer.ZoneGray,
            "фикстура серого чата: ступень 2 спрашивается");
        _llm.LocalInputTokens = 300;
        _llm.Reply = a => a == LocalActionCatalog.WikiTriage
            ? """{"hasKnowledge":true,"about":"a","reason":"r"}"""
            : NothingJson;

        await Tick(Sut((owner, chat, _, ct) => Task.FromResult(new WikiChatIngestService.ChatResult(
            WikiChatIngestService.OutcomeNothingNew, null, [], InputTokens: 1_000))));

        var last = Last();
        last.Analyzed.Should().Be(1);
        last.InputTokens.Should().Be(1_300, "разбор чата (1 000) плюс фактический вход ступени 2 (300)");
    }

    [Fact]
    public async Task ОтказСтупени2_ЕёВходВИтогеОстаётся_ЧатНеРазобран()
    {
        // Расход считается по факту вызова, а не по исходу вердикта: отказ ступени 2 понесённого
        // расхода не отменяет (тот же смысл, что у ChatResult в WC-4b). Мутация «суммировать
        // только Judged» → 0 (красный).
        Configure();
        AddChat("g1", mark: null, history: Gray());
        _llm.LocalInputTokens = 300;
        _llm.Reply = _ => "это не json";

        await Tick(Sut((owner, chat, _, ct) => Task.FromResult(new WikiChatIngestService.ChatResult(
            WikiChatIngestService.OutcomeNothingNew, null, [], InputTokens: 1_000))));

        _analyzed.Should().BeEmpty("отказ ступени 2 — серая зона, чат не берётся");
        Last().InputTokens.Should().Be(300, "вызов модели состоялся — его вход в итоге");
    }

    // ─── WN-2: отказы считаются, нулевой вход — не completed ─────────────────
    // Ночь 06.11 (отчёт 79e98305, Ш2): 100 чатов отказаны без вызова модели, а итог — completed,
    // повтора нет, в журнале ни строки. Теперь отказы считаются, называются в журнале и при
    // нулевом входе дают error — дату он не закрывает, следующий тик пробует снова.

    [Fact]
    public async Task ВсеРазборыОтказаныНулевойВход_ИтогError_ИПовторВТотуНочь()
    {
        // Мутации: «не считать rejected» → rejected=0 (красный); «не переводить в OutcomeError» →
        // outcome=completed (красный); «error тоже закрывает дату» → второй тик не зовёт _analyze,
        // счётчик остался бы 3 (красный).
        Configure();
        AddChat("c1", ageMinutes: 0);
        AddChat("c2", ageMinutes: 1);
        AddChat("c3", ageMinutes: 2);
        var sut = Sut((owner, chat, _, ct) =>
        {
            lock (_analyzed) _analyzed.Add(chat);
            return Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeRejected, "модель недоступна: " + chat, [], InputTokens: 0));
        });

        await Tick(sut);

        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeError, "нулевой вход при всех отказах — не completed");
        last.Analyzed.Should().Be(3);
        last.Rejected.Should().Be(3);
        last.FirstRejectReason.Should().Be("модель недоступна: c1", "причина первого отказанного чата");
        last.InputTokens.Should().Be(0);
        File.ReadAllText(LastFile()).Should().Contain("\"rejected\"").And.Contain("\"firstRejectReason\"");

        // error дату не закрывает: второй тик той же ночи проходит её заново.
        await Tick(sut);
        _analyzed.Should().HaveCount(6, "error не закрывает дату — следующий тик той же ночи пробует снова");
    }

    [Fact]
    public async Task ЧастьОтказанаОдинРазобранный_ИтогCompleted_ОтказыПосчитаны()
    {
        // Что ловит этот тест на самом деле (R-WN-2, находка 2): замену всего хвоста условия
        // `rejected == analyzed && inputTokens == 0` на `rejected > 0` (перевод в error при любом
        // отказе) — с живой c3 outcome стал бы error, тест красный. Одиночные правки guard'а
        // (:603) здесь не ловятся: `rejected == analyzed → rejected > 0` блокирует ненулевой вход
        // c3 (1000), снятие `inputTokens == 0` — то, что rejected (2) != analyzed (3). Границу
        // «не все отказаны при нулевом входе» защищает ОтказаныНеВсеНулевойВход_…
        Configure();
        AddChat("c1", ageMinutes: 0);
        AddChat("c2", ageMinutes: 1);
        AddChat("c3", ageMinutes: 2);
        await Tick(Sut((owner, chat, _, ct) =>
        {
            lock (_analyzed) _analyzed.Add(chat);
            if (chat == "c3")
                return Task.FromResult(new WikiChatIngestService.ChatResult(
                    WikiChatIngestService.OutcomeNothingNew, null, [], InputTokens: 1_000));
            return Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeRejected, "отказ " + chat, [], InputTokens: 0));
        }));

        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted, "есть несостоявшийся отказом разбор с ненулевым входом");
        last.Analyzed.Should().Be(3);
        last.Rejected.Should().Be(2);
        last.FirstRejectReason.Should().Be("отказ c1", "причина первого отказанного, а не первого вообще");
        last.InputTokens.Should().Be(1_000);
    }

    [Fact]
    public async Task ОтброшенныеОперации_СуммируютсяВИтогеСПервойПричиной()
    {
        Configure();
        AddChat("c1", ageMinutes: 0);
        AddChat("c2", ageMinutes: 1);
        await Tick(Sut((owner, chat, _, ct) => Task.FromResult(new WikiChatIngestService.ChatResult(
            WikiChatIngestService.OutcomeNothingNew, null, [], InputTokens: 10,
            Dropped: chat == "c1" ? 1 : 2, FirstDropReason: "причина " + chat))));

        var last = Last();
        last.Dropped.Should().Be(3);
        last.FirstDropReason.Should().Be("причина c1");
    }

    [Fact]
    public void СтарыйФайлИтога_БезПолейDropped_ЧитаетсяНулём()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LastFile())!);
        File.WriteAllText(LastFile(),
            """{"date":"2026-11-01","selected":1,"analyzed":1,"applied":0,"toInbox":0,"estTokens":0,"outcome":"completed"}""");

        var last = Last();
        last.Dropped.Should().Be(0);
        last.FirstDropReason.Should().BeNull();
    }

    [Fact]
    public async Task ОтказаныНеВсеНулевойВход_ИтогCompleted_НочьНеПовторяется()
    {
        // Граница инварианта «не все разборы отказаны» (R-WN-2, находка 1): конъюнкта
        // `rejected == analyzed` в guard'е (WikiNightlyService.cs:603). 2 отказа с нулевым входом +
        // 1 несостоявшийся отказом разбор с нулевым входом: rejected (2) < analyzed (3) — guard
        // ложен, итог completed: частично отказанная ночь при нулевом входе — не error, дата
        // закрыта, повторного прохода нет. Мутации: «rejected == analyzed → rejected > 0» →
        // 2 > 0 и вход 0 — guard истинен, outcome=error, тест красный; «заменить весь хвост
        // условия на rejected > 0» — тоже красный. Существующий ЧастьОтказанаОдинРазобранный
        // первой мутации не ловит: его вход 1000 блокирует конъюнкту inputTokens == 0.
        Configure();
        AddChat("c1", ageMinutes: 0);
        AddChat("c2", ageMinutes: 1);
        AddChat("c3", ageMinutes: 2);
        var sut = Sut((owner, chat, _, ct) =>
        {
            lock (_analyzed) _analyzed.Add(chat);
            if (chat == "c3")
                return Task.FromResult(new WikiChatIngestService.ChatResult(
                    WikiChatIngestService.OutcomeNothingNew, null, [], InputTokens: 0));
            return Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeRejected, "отказ " + chat, [], InputTokens: 0));
        });

        await Tick(sut);

        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted, "не все разборы отказаны — нулевой вход не делает ночь error");
        last.Analyzed.Should().Be(3);
        last.Rejected.Should().Be(2);
        last.FirstRejectReason.Should().Be("отказ c1");
        last.InputTokens.Should().Be(0);

        // completed закрывает дату: следующий тик той же ночи не переразбирает чаты.
        await Tick(sut);
        _analyzed.Should().HaveCount(3, "completed закрывает дату — частично отказанная ночь не повторяется");
    }

    [Fact]
    public async Task ДвижокЗанятСлотНеДождались_ИтогНазываетЗанятостьANePustoyOtvet()
    {
        // c2 + требование 5 (WN-4): отказ по занятости доезжает до ночи причиной про занятость
        // движка, а не «модель дала пустой ответ» (так ночь 06.11 прятала 108 отказов без
        // единого вызова модели). Ночь считает такие чаты rejected, пишет первую причину и при
        // нулевом входе даёт error — guard WN-2 с ожиданием слота не превращается в
        // бесконечный повтор: отказ назван и в журнале, и в итоге.
        Configure();
        AddChat("c1", ageMinutes: 0);
        _llm.Throw = actionKey => actionKey == LocalActionCatalog.WikiNightly
            ? new LocalEngineSlotRefusalException("local-qwen", LocalTurnRefusal.Timeout, 4, 4)
            : null;

        await Tick(RealSut());

        var last = Last();
        last.Analyzed.Should().Be(1);
        last.Rejected.Should().Be(1, "отказ по занятости — отказ разбора, он считается");
        last.FirstRejectReason.Should().Contain("занят").And.NotContain("пустой ответ");
        last.InputTokens.Should().Be(0, "модель не звалась — понесённого расхода нет");
        last.Outcome.Should().Be(WikiNightlyService.OutcomeError);
    }

    // ─── J-O1 (дефект 2e13d45c): мёртвый локальный маршрут и строка итога ночи ───
    // Ночь 12.11: три прохода по 41 отказу с ложной причиной «пустой ответ» при 0 токенов.
    // Причина теперь названа маршрутом (ReasonLocalUnavailable), три таких отказа подряд
    // обрывают проход, а итог прохода читается одной строкой журнала процесса.

    [Fact]
    public async Task ЛокальныйМаршрутМёртв_ТриОтказаПодряд_ПроходОстановлен_ИтогError()
    {
        // Мутации: «убрать выключатель» (localDownStreak и break в RunPassAsync) → 10 вызовов
        // модели вместо 3 (красный); «не ставить OutcomeError» → completed (красный);
        // «обнулять ряд на каждом отказе» → выключатель не срабатывает (красный).
        Configure();
        for (var i = 1; i <= 10; i++) AddChat("c" + i, ageMinutes: i);
        _llm.LocalDown = true;

        await Tick(RealSut());

        _llm.Calls.Count(c => c.ActionKey == LocalActionCatalog.WikiNightly).Should().Be(3,
            "три отказа локального маршрута подряд — проход остановлен, очередь дальше не зовётся");
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeError, "мёртвый локальный маршрут — не completed");
        last.Analyzed.Should().Be(3);
        last.Rejected.Should().Be(3);
        last.InputTokens.Should().Be(0, "модель не звалась ни разу");
        last.FirstRejectReason.Should().Contain(WikiIngestService.ReasonLocalUnavailable)
            .And.NotContain("пустой ответ", "ложная причина больше не пишется");

        // error дату не закрывает: следующий тик той же ночи пробует снова — и снова гаснет
        // на трёх отказах, а не на сорока одном.
        await Tick(RealSut());
        _llm.Calls.Count(c => c.ActionKey == LocalActionCatalog.WikiNightly).Should().Be(6,
            "повтор есть (error дату не закрывает), но короткий");
    }

    [Fact]
    public async Task ОтказыЛокальногоМаршрутаНеПодряд_НочьИдётДальше_ИтогCompleted()
    {
        // Граница «подряд идущих» (между ними — состоявшийся разбор, ряд обнуляется): три
        // отказа локального маршрута за ночь, но не подряд → проход не останавливается.
        // Мутация «считать отказы всей ночи, а не ряда» → 3 ≥ лимита → error (красный);
        // «не обнулять ряд при успешном разборе» → тоже error (красный).
        Configure();
        AddChat("c1", ageMinutes: 0);
        AddChat("c2", ageMinutes: 1);
        AddChat("c3", ageMinutes: 2);
        AddChat("c4", ageMinutes: 3);
        var localDown = "разбор окна m1-m8 не удался: синтез: " + WikiIngestService.ReasonLocalUnavailable;

        await Tick(Sut((owner, chat, _, ct) =>
        {
            lock (_analyzed) _analyzed.Add(chat);
            return chat == "c2"
                ? Task.FromResult(new WikiChatIngestService.ChatResult(
                    WikiChatIngestService.OutcomeNothingNew, null, [], InputTokens: 1_000))
                : Task.FromResult(new WikiChatIngestService.ChatResult(
                    WikiChatIngestService.OutcomeRejected, localDown, [], InputTokens: 0));
        }));

        var last = Last();
        _analyzed.Should().HaveCount(4, "ряд не дошёл до лимита: очередь прошла целиком");
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted, "отказы не подряд — это не мёртвый движок");
        last.Rejected.Should().Be(3);
        last.FirstRejectReason.Should().Be(localDown);
    }

    [Fact]
    public async Task ИтогНочи_ОднаСтрокаЖурнала_БезТекстаЗнаний()
    {
        // c3 (J-O1): прошедшая ночь проверяется по журналу процесса — одна структурная строка
        // Information на проход владельца, по полям итога. Знаний в ней нет: утверждение и
        // цитата этой фикстуры реально доезжают до страницы (Applied), но в строку не попадают.
        // Мутации: «строка на каждый чат» → счёт красный; «Warning вместо Information» →
        // уровень красный; «добавить в шаблон текст отказа/утверждения» → NotContain красный.
        Configure();
        AddChat("c1", history: KnowledgeHist());
        _llm.Reply = _ => WikiSynthJson.Append("Сущность", ClaimText, ClaimQuote);
        var log = new RecordingLog();

        await Tick(RealSut(log));

        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted, last.FirstRejectReason ?? "");
        last.Applied.Should().Be(1, "фикстура настоящая: утверждение с цитатой применено");
        var rows = log.Rows.Where(r => r.Message.StartsWith("Ночь вики")).ToList();
        rows.Should().ContainSingle("одна строка на проход владельца");
        rows[0].Level.Should().Be(LogLevel.Information);
        rows[0].Message.Should().Contain("Ночь вики " + User).And.Contain(last.Date).And.Contain(last.Outcome)
            .And.Contain("чатов ").And.Contain("отказов ").And.Contain("пакетов 1").And.Contain("ток;")
            // J-J4: сводка судьи — в той же строке, нулевые числа тоже числа (полная форма).
            .And.Contain("на судье 0/0/0").And.Contain("судья 0 выз. $0");
        rows[0].Message.Should().NotContain("(первый:", "отказов нет — недописанного поля в строке быть не должно");
        rows[0].Message.Should().NotContain(ClaimText).And.NotContain(ClaimQuote);
        log.Rows.Should().NotContain(
            r => r.Message.StartsWith("Ночь вики") && r.Level == LogLevel.Warning,
            "обычная ночь не кричит: Warning — только плохой исход");
    }

    [Fact]
    public async Task НочьError_СтрокаЖурналаДополненаWarning_НочьOpenLimitТоже()
    {
        // c3 (J-O1): плохая ночь видна в журнале по уровню: к той же структурной строке
        // добавляется ровно одна Warning (error и open_limit). Мутация «не дублировать
        // Warning» → счёт красный; «Warning при любом исходе» → первый блок красный.
        Configure();
        AddChat("c1");
        _llm.LocalDown = true;
        var log = new RecordingLog();

        await Tick(RealSut(log));

        Last().Outcome.Should().Be(WikiNightlyService.OutcomeError);
        var rows = log.Rows.Where(r => r.Message.StartsWith("Ночь вики")).ToList();
        rows.Should().HaveCount(2, "одна Information и одна Warning");
        rows.Count(r => r.Level == LogLevel.Information).Should().Be(1);
        rows.Count(r => r.Level == LogLevel.Warning).Should().Be(1, "error — виден по уровню, не поиском");

        // Тот же порядок для open_limit: упор в лимит открытых пакетов — плохой исход.
        var log2 = new RecordingLog();
        AddOpenPackages(WikiPackage.MaxOpenPackages);
        await Tick(Sut(log: log2));
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeOpenLimit);
        log2.Rows.Count(r => r.Message.StartsWith("Ночь вики") && r.Level == LogLevel.Warning).Should().Be(1);
    }

    [Fact]
    public async Task СтарыйИтогБезПоля_ЧитаетсяНулём()
    {
        // Аддитивность поля: файл итога, записанный до WC-4b, читается как раньше, нового поля в нём
        // нет → 0. Ломающего изменения формата нет, BackupSchema.Version не тронут.
        File.WriteAllText(LastFile(), """
            {"date":"2026-11-02","selected":1,"analyzed":1,"applied":0,"toInbox":0,
             "estTokens":120,"outcome":"completed","overrodeHuman":0,"disputedPages":0}
            """);

        var last = Last();
        last.InputTokens.Should().Be(0);
        last.EstTokens.Should().Be(120);
        // Тот же файл не содержит и полей WN-2 — читаются нулём без исключения.
        last.Rejected.Should().Be(0);
        last.FirstRejectReason.Should().BeNull();
        // …ни полей J-J4 (судья) и R-J-W0 (ступень 2): старый итог читается нулями/null.
        last.JudgeCalls.Should().Be(0);
        last.JudgeCostUsd.Should().Be(0m);
        last.JudgeApplied.Should().Be(0);
        last.JudgeRejected.Should().Be(0);
        last.JudgeDeferred.Should().Be(0);
        last.CodeRejected.Should().Be(0);
        last.Stage2Refused.Should().Be(0);
        last.FirstStage2Refusal.Should().BeNull();
    }

    // ─── J-J4: точка (а) — проход судьи в конце прохода, его числа в итоге, файле и строке ───

    [Fact]
    public async Task ПроходСудьиВКонцеНочи_ЕгоСчётчикиВИтогеФайлеИСтрокеЖурнала()
    {
        // Мутации: «не звать судью в конце RunPassAsync» → judgeCalls=0 (красный); «не нести её
        // счётчики в Result» → judgeApplied=0 (красный); «не печатать их в строке журнала» →
        // NotContain красный.
        Configure();
        AddChat("c1", ageMinutes: 0);
        var log = new RecordingLog();

        await Tick(Sut((owner, chat, _, ct) => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeAwaitingHuman, "в пакете есть спорные операции", [])),
            log: log, judge: Judge(applied: 1, calls: 1, cost: 0.02m)));

        var last = Last();
        last.ToInbox.Should().Be(1);
        last.JudgeApplied.Should().Be(1, "судья вынес вердикт по одной спорной операции");
        last.JudgeCalls.Should().Be(1);
        last.JudgeCostUsd.Should().Be(0.02m, "стоимость вызовов судьи — единственные деньги в итоге");

        // Файл пишется с отступами (JsonOpts.WriteIndented) — значения в нём с пробелом после «:».
        var json = File.ReadAllText(LastFile());
        json.Should().Contain("\"judgeCalls\": 1").And.Contain("\"judgeApplied\": 1").And.Contain("\"judgeCostUsd\": 0.02");

        var line = log.Rows.Single(r => r.Message.StartsWith("Ночь вики"));
        line.Message.Should().Contain("на судье 1/0/0").And.Contain("судья 1 выз. $0.02");
        line.Level.Should().Be(LogLevel.Information, "отложенных операций нет — обычная ночь");
    }

    [Fact]
    public async Task СудьяОтложилТриОперации_СтрокаИтогаДополненаWarning()
    {
        // J-J4 (c3): «судья недоступен» видно по уровню, а не поиском: к той же строке — ровно
        // одна Warning. Мутация «не дублировать Warning при JudgeDeferred» → счёт красный;
        // «не звать судью вовсе» → строки «судья 1 выз. $0» нет → красный.
        Configure();
        AddChat("c1");
        var log = new RecordingLog();

        await Tick(Sut(log: log, judge: Judge(deferred: 3, calls: 1)));

        Last().JudgeDeferred.Should().Be(3);
        var rows = log.Rows.Where(r => r.Message.StartsWith("Ночь вики")).ToList();
        rows.Should().HaveCount(2, "одна Information и одна Warning");
        rows[0].Message.Should().Contain("на судье 0/0/3").And.Contain("судья 1 выз. $0");
        rows.Count(r => r.Level == LogLevel.Warning).Should().Be(1);
    }

    [Fact]
    public async Task НочьСДвумяСпорнымиОкнами_СудьяОднимПроходомВКонце_ЕгоЧислаВИтоге()
    {
        // Доработка финала J-J4 (пункт 1 «до мержа»): окна ночи идут через тот же IngestAsync,
        // но точка (б) судью для места wiki-nightly не пускает — её полный проход один, в конце
        // RunPassAsync (точка (а)), и его числа идут в итог. Посреди ночи тот проход
        // выбрасывал свой итог: сводка ночи читала бы почти ноль вызовов и цену около нуля.
        // Мутация «вернуть судью в точку (б) для ночи» (убрать guard по actionKey в
        // RunProtectedAsync) → midCalls=2, красный.
        Configure();
        AddChat("c1", history: KnowledgeHist());
        AddChat("c2", history: KnowledgeHist());
        _llm.Reply = _ => WikiSynthJson.Append("Сущность", ClaimText, ClaimQuote);
        // Спорность обоих окон: страница откатана человеком вчера (карантин N7d) — операции
        // каждого пакета идут в «Входящие», каждое окно заканчивается awaiting_human.
        LogLine(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"), "revert", "user", PagePath);

        var midCalls = 0;
        var endCalls = 0;
        var gate = new object();
        Func<string, string, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>> mid =
            (owner, root, ct) =>
            {
                lock (gate) midCalls++;
                return Task.FromResult(new WikiJudgeSweep.JudgeSweepResult(0, 0m, 0, 0, 0, 0, null));
            };
        Func<string, string, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>> end =
            (owner, root, ct) =>
            {
                lock (gate) endCalls++;
                return Task.FromResult(new WikiJudgeSweep.JudgeSweepResult(2, 0.04m, 2, 0, 0, 0, null));
            };

        await Tick(RealSut(judge: end, midJudge: mid));

        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted, last.FirstRejectReason ?? "");
        last.ToInbox.Should().Be(2, "оба окна спорные: каждое — своей записью в «Входящих»");
        midCalls.Should().Be(0, "посреди ночи судья не идёт: у неё один полный проход — в конце");
        endCalls.Should().Be(1, "ровно один проход судьи на ночь");
        last.JudgeCalls.Should().Be(2, "в итог ночи идут числа её единственного прохода, а не ноль");
        last.JudgeCostUsd.Should().Be(0.04m, "цена ночи — цена её единственного прохода");
        last.JudgeApplied.Should().Be(2);
    }

    [Fact]
    public async Task ОтказнаяНочь_ВСтрокеИтогаНетПутиСтраницыИТекстаУтверждения()
    {
        // Хвост ревью R-J-O1 (minor 1 и minor 2): сырая причина отказа окна несёт путь страницы и
        // строку утверждения (WikiGate.cs:241) — в строку итога они не едут; полная причина
        // остаётся в файле. Мутация «вернуть сырую причину» (печатать FirstRejectReason) →
        // красный на обоих NotContain.
        const string PageMarker = "Раздел/стр.md";
        const string ClaimMarker = "Утверждение без сноски на существующей странице";
        var reason = "разбор окна m1-m8 не удался: в разделе есть нарушения: "
            + $"{PageMarker}: утверждение в 'Сущности' без сноски: {ClaimMarker}";
        Configure();
        AddChat("c1", ageMinutes: 0);
        var log = new RecordingLog();

        await Tick(Sut((owner, chat, _, ct) => Task.FromResult(new WikiChatIngestService.ChatResult(
            WikiChatIngestService.OutcomeRejected, reason, [], InputTokens: 5)), log: log));

        var last = Last();
        last.Rejected.Should().Be(1);
        last.FirstRejectReason.Should().Be(reason, "полная причина — в файле итога, её менять нет");

        var rows = log.Rows.Where(r => r.Message.StartsWith("Ночь вики")).ToList();
        rows.Should().ContainSingle("одна строка на проход");
        rows[0].Message.Should().Contain("отказов 1 (первый:")
            .And.NotContain(PageMarker, "пути страницы в строку итога не едут")
            .And.NotContain(ClaimMarker, "текста утверждения — тоже");
    }

    [Fact]
    public async Task ОтказСтупени2_ПричинаВСтрокеИтога_ТекстаЧатаВНейНет()
    {
        // Хвост ревью R-J-W0: ступень 2 на облачном месте молча оставляла чаты серыми весь проход
        // — ни строки о причине. Мутация «не вести stage2Refused» → счётчик и строка красные;
        // «не называть её построчно» → красный на строке про чата.
        Configure();
        AddChat("g1", mark: null, history: Gray());
        _llm.NotLocalKey = LocalActionCatalog.WikiTriage;
        var log = new RecordingLog();

        await Tick(Sut(log: log));

        _analyzed.Should().BeEmpty("отказ ступени 2 — чат не берётся");
        var last = Last();
        last.Stage2Refused.Should().Be(1);
        last.FirstStage2Refusal.Should().Contain("нелокальный маршрут");

        var rows = log.Rows.Where(r => r.Message.StartsWith("Ночь вики")).ToList();
        rows.Should().ContainSingle();
        rows[0].Message.Should().Contain("ступень 2 отказана 1").And.Contain("нелокальный маршрут");
        rows[0].Message.Should().NotContain("Тут решили вариант", "текста чата в строке итога нет");
        log.Rows.Should().Contain(r => r.Message.Contains("чат g1 ступень 2 отказана"),
            "причина названа и построчно, а не только сводкой");
    }

    // ─── WN-5: ручной запуск ───────────────────────────────────────────────

    [Fact]
    public async Task ГонкаДвухРучныхЗапусков_ОдинСтартуетДругойAlreadyRunning()
    {
        // Мутация (c1): убрать проверку `if (!Running.TryAdd(root, 0))` в StartManual — оба
        // вызова возвращают Started, тест красный на AlreadyRunning.
        Configure();
        AddChat("c1");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<WikiChatIngestService.ChatResult> Analyze(string o, string c, string? engine, CancellationToken ct)
        {
            lock (_analyzed) _analyzed.Add(c);
            entered.TrySetResult();
            await gate.Task;
            return new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []);
        }

        // Замок берётся синхронно, до возврата: оба вызова успевают столкнуться с ним, пока
        // первый проход держит его на гейте.
        Task pass1 = null!, pass2 = null!;
        var first = Task.Run(() => Sut(Analyze).TryStartManual(User, out pass1));
        var second = Task.Run(() => Sut(Analyze).TryStartManual(User, out pass2));
        var results = await Task.WhenAll(first, second);

        results.Count(r => r == WikiNightlyService.ManualStart.Started).Should().Be(1);
        results.Count(r => r == WikiNightlyService.ManualStart.AlreadyRunning).Should().Be(1,
            "замок общий на процесс: второй вызов отвергнут, не дожидаясь первого");

        // Таймаут вместо зависания: дошёл ли начатый проход до разбора.
        var reached = await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        reached.Should().BeSameAs(entered.Task, "начатый проход обязан дойти до подменённого разбора");
        gate.SetResult();
        await (results[0] == WikiNightlyService.ManualStart.Started ? pass1 : pass2);
        _analyzed.Should().ContainSingle("проход был один");
        WikiNightlyService.IsRunning(Root()).Should().BeFalse("после прохода замок снят");
    }

    [Fact]
    public async Task РучнойПроходДержитЗамок_ТикВОкнеНеЗовет_ПослеЗавершенияСноваМожно()
    {
        Configure();
        AddChat("c1");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<WikiChatIngestService.ChatResult> Analyze(string o, string c, string? engine, CancellationToken ct)
        {
            lock (_analyzed) _analyzed.Add(c);
            entered.TrySetResult();
            await gate.Task;
            return new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []);
        }
        var sut = Sut(Analyze);

        var start = sut.TryStartManual(User, out var pass);
        start.Should().Be(WikiNightlyService.ManualStart.Started);
        var reached = await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        reached.Should().BeSameAs(entered.Task, "проход обязан дойти до подменённого разбора");
        _analyzed.Should().ContainSingle();

        // Тик в окне, пока владелец занят замком: новых вызовов разбора нет.
        await Tick(sut);
        _analyzed.Should().ContainSingle("тик пропустил занятого владельца");

        gate.SetResult();
        await pass;
        WikiNightlyService.IsRunning(Root()).Should().BeFalse("после прохода замок снят");

        // И отметка «за дату уже пройдено», и то, что она ручная, — новому ручному не преграда.
        var again = Sut(Analyze).TryStartManual(User, out var pass2);
        again.Should().Be(WikiNightlyService.ManualStart.Started);
        await pass2;
        _analyzed.Should().HaveCount(2, "второй ручной проход состоялся");
    }

    [Fact]
    public async Task РучнойПроходВнеОкняЗоветРазборИПишетManual_ТикТойЖеДатыПроходитСнова()
    {
        // Мутации: «CheckStop проверяет окно и при manual» → ручной проход встаёт window_closed,
        // нулевых вызовов — тест красный; «убрать !last.Manual из условия пропуска тика» →
        // тик той же даты молчит, второго вызова нет — тест красный.
        Configure(); // окно 3–6
        AddChat("c1");
        _now = At(1); // вне окна: тик бы не начал
        var start = Sut().TryStartManual(User, out var pass);
        start.Should().Be(WikiNightlyService.ManualStart.Started);
        await pass;

        _analyzed.Should().Equal(new[] { "c1" }, "окно при ручном не проверяется");
        var last = Last();
        last.Manual.Should().BeTrue();
        last.Date.Should().Be("2026-11-02", "дата ручного прохода — сегодняшняя");
        File.ReadAllText(LastFile()).Should().Contain("\"manual\"");

        // Тик в 04:00 той же даты: ручной проход дату НЕ закрыл — ночь проходит заново.
        _now = At(4);
        await Tick(Sut());
        _analyzed.Should().Equal("c1", "c1");

        // Старый файл без поля читается Manual=false (аддитивность, как у Rejected).
        File.WriteAllText(LastFile(), """
            {"date":"2026-11-02","selected":1,"analyzed":1,"applied":0,"toInbox":0,
             "estTokens":120,"outcome":"completed"}
            """);
        Last().Manual.Should().BeFalse();
    }

    [Fact]
    public async Task РучнойПроходСИсключениемРазбора_ЗавершаетсяБезИсключения_ЗамокСнят_СноваМожно()
    {
        Configure();
        AddChat("c1");
        var sut = Sut((o, c, _, ct) => throw new InvalidOperationException("разбор упал"));

        var start = sut.TryStartManual(User, out var pass);
        start.Should().Be(WikiNightlyService.ManualStart.Started);
        await pass; // наружу — без исключения
        WikiNightlyService.IsRunning(Root()).Should().BeFalse("finally снимает замок");

        var again = sut.TryStartManual(User, out var pass2);
        again.Should().Be(WikiNightlyService.ManualStart.Started, "неудачный ручной ничего не закрывает");
        await pass2;
    }

    // ─── c4: изоляция владельцев и один проход ─────────────────────────────

    [Fact]
    public async Task ИсключениеУОдногоВладельца_ВторойОбработан_ТикНеПадает()
    {
        Configure(owner: User);
        Configure(owner: Other);
        AddChat("c-u1", owner: User);
        AddChat("c-u2", owner: Other);
        _flag.ThrowFor = User; // u1 («u1» < «u2») падает первым

        var act = async () => await Tick();

        await act.Should().NotThrowAsync();
        _analyzed.Should().Equal("c-u2");
    }

    [Fact]
    public async Task ДваТикаОдногоВладельца_ОдинПроход()
    {
        Configure();
        AddChat("c1");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<WikiChatIngestService.ChatResult> Analyze(string o, string c, string? engine, CancellationToken ct)
        {
            lock (_analyzed) _analyzed.Add(c);
            entered.TrySetResult();
            await gate.Task;
            return new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []);
        }

        var first = Sut(Analyze).RunNightTickAsync(_now);
        await entered.Task;
        // Второй тик — другой экземпляр службы того же владельца (перекрытие интервала, рестарт).
        // Таймаут вместо зависания: без охраны второй тик встал бы на том же гейте.
        var second = Sut(Analyze).RunNightTickAsync(_now);
        var finished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5)));
        gate.SetResult();
        finished.Should().BeSameAs(second, "второй тик обязан вернуться сразу, не дожидаясь первого");
        await first;

        _analyzed.Should().ContainSingle("второй тик пропустил занятого владельца");
    }

    // ─── порядок очереди ───────────────────────────────────────────────────

    [Fact]
    public async Task Очередь_СначалаВажные_ПотомСвежиеВперёд()
    {
        Configure();
        AddChat("old", ageMinutes: 100, mark: null, history: Rich());
        AddChat("fresh", ageMinutes: 1, mark: null, history: Rich());
        AddChat("imp", ageMinutes: 500, mark: WikiMarks.Important);
        foreach (var id in new[] { "old", "fresh" })
            WikiChatScorer.Score(ScorerInput(_sessions.GetById(id)!)).Zone.Should().Be(WikiChatScorer.ZoneTriage, "фикстура богатого чата");

        await Tick();

        _analyzed.Should().Equal("imp", "fresh", "old");
    }

    // ─── фикстуры чатов под скорер ─────────────────────────────────────────

    // Богатый чат: шесть содержательных реплики человека с оборотом решения — зона triage.
    private static List<StoredMessage> Rich()
    {
        var h = new List<StoredMessage>();
        for (var i = 0; i < 6; i++)
        {
            h.Add(new StoredUserMessage($"Давайте решили вариант {i}: " + new string('б', 400)));
            h.Add(new StoredTextMessage("Принято, делаю."));
        }
        return h;
    }

    // Серый чат: три содержательные реплики с оборотом решения — между порогами 6 и 15.
    private static List<StoredMessage> Gray()
    {
        var h = new List<StoredMessage>();
        for (var i = 0; i < 3; i++)
        {
            h.Add(new StoredUserMessage($"Тут решили вариант {i}: " + new string('в', 600)));
            h.Add(new StoredTextMessage("Принято, делаю."));
        }
        return h;
    }

    // Серый чат под переспрос: балл 11 (3 реплики + 2 содержательные + оборот решения) — серая
    // зона, и после трёх коротких реплик балл 14 < 15: зона не меняется, меняется лишь устаревание
    // вердикта ступени 2.
    private static List<StoredMessage> GrayStale()
    {
        var h = new List<StoredMessage>();
        for (var i = 0; i < 2; i++)
        {
            h.Add(new StoredUserMessage($"Тут решили вариант {i}: " + new string('в', 800)));
            h.Add(new StoredTextMessage("Принято, делаю."));
        }
        h.Add(new StoredUserMessage("Дальше."));
        h.Add(new StoredTextMessage("Принято, делаю."));
        return h;
    }

    // Короткие реплики человека: двигают счётчик устаревания, не балл зоны.
    private static List<StoredMessage> ShortHumans(int n)
    {
        var h = new List<StoredMessage>();
        for (var i = 0; i < n; i++)
        {
            h.Add(new StoredUserMessage("ок"));
            h.Add(new StoredTextMessage("Принято."));
        }
        return h;
    }

    private WikiChatScorer.WikiChatInput ScorerInput(Session s) =>
        new(s.TaskExecution, s.WikiMark, s.IsPinned, false, null, s.CreatedAt, s.UpdatedAt, 0, _sessions.History[s.Id]);

    // Чат с цитируемым знанием (тест журнала J-O1): три реплики человека — нужная дельта
    // ночи; первая содержит дословную цитату фикстуры, чтобы утверждение применилось на
    // страницу. Все реплики короткие: дельта остаётся одним окном разбора.
    private static List<StoredMessage> KnowledgeHist() => [
        new StoredUserMessage(ClaimQuote + "."),
        new StoredTextMessage("Принято."),
        new StoredUserMessage("Прежний план был остаться на Windows."),
        new StoredTextMessage("Принято."),
        new StoredUserMessage("Контенер собран на второй машине."),
        new StoredTextMessage("Принято."),
    ];

    // ─── подмены ───────────────────────────────────────────────────────────

    private sealed class Llm : ICheapTextRunner
    {
        public readonly List<(string ActionKey, string Prompt)> Calls = [];
        public bool Route = true;
        public Func<string, string>? Reply;
        public TaskCompletionSource? Gate;
        // Сколько «отдал» движок на detailed-вызове (полный вход). 0 — метрик не было:
        // вызов успешен, usage=null, итог прибавляет 0 (WC-4c).
        public long LocalInputTokens;
        // Модель вызвана и встала на гейте — событие вместо таймерного ожидания в тестах.
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // «Движок не принял вызов» по actionKey (WN-4): возврат исключением вместо ответа —
        // так же, как ждущий слот путь TrackedLocalLlmClient (LocalEngineSlotRefusalException).
        public Func<string, Exception?>? Throw;

        // «Локальный движок мёртв» (дефект 2e13d45c): цепочка без облачного шага отдаёт null
        // без исключения — движок не настроен/не готов/не ответил. Вызов при этом состоялся
        // (он считается), ответа нет.
        public bool LocalDown;

        // Место с «нелокальным маршрутом» (хвост ревью R-J-W0): ступень 2 отказывает до чтения
        // истории и до вызова — так админ поставил её на облачную модель.
        public string? NotLocalKey;

        // Названный исполнитель заявки (W3, путь «engine аргументом»): под тестом такого движка нет —
        // вызов регистрируется (имя и место заявки — то, что проверяют тесты проброса) и отвечает
        // null, как неготовый движок: подмены другим исполнителем нет.
        public List<(string Engine, string ActionKey)> EngineCalls { get; } = [];

        public bool UsesLocal(string actionKey) => actionKey != NotLocalKey;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => Route;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(Answer(actionKey, prompt));

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult<string?>(Answer(actionKey, prompt));

        public async Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null, object? jsonFormat = null,
            CancellationToken ct = default)
        {
            var text = Answer(actionKey, prompt);
            if (Gate is not null && Calls.Count == 1)
            {
                Entered.TrySetResult();
                await Gate.Task.ConfigureAwait(false);
            }
            return new OneShotResult(text, new OneShotUsage(10, 0, 0, 5, 0.0, "fake"), 1);
        }

        // Ночная ветка (wiki-nightly) и ступень 2 (wiki-triage) зовут detailed-вариант шва
        // (WC-4c): тот же ответ плюс usage «движка».
        public Task<OneShotResult?> RunLocalOrChosenDetailedAsync(string actionKey, string prompt,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            var text = Answer(actionKey, prompt);
            if (Throw?.Invoke(actionKey) is { } ex)
                return Task.FromException<OneShotResult?>(ex);
            if (LocalDown)
                return Task.FromResult<OneShotResult?>(null);
            return Task.FromResult<OneShotResult?>(new OneShotResult(
                text,
                LocalInputTokens == 0
                    ? null
                    : new OneShotUsage(LocalInputTokens, 0, 0, 5, 0.0, "qwen-local"), 1));
        }

        public Task<OneShotResult?> RunOnLocalEngineDetailedAsync(string engine, string actionKey,
            string prompt, string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            lock (EngineCalls) EngineCalls.Add((engine, actionKey));
            return Task.FromResult<OneShotResult?>(null);
        }

        private string Answer(string actionKey, string prompt)
        {
            lock (Calls) Calls.Add((actionKey, prompt));
            return Reply?.Invoke(actionKey)
                ?? (actionKey == LocalActionCatalog.WikiTriage
                    ? """{"hasKnowledge":true,"about":"a","reason":"r"}"""
                    : NothingJson);
        }
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

    private sealed class Flag : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool On = true;
        public string? ThrowFor;
        public bool IsEnabled(string userId, string key)
        {
            if (userId == ThrowFor) throw new InvalidOperationException("шов флага упал");
            return On && key == FeatureFlagKeys.LlmWiki;
        }
    }

    private sealed class Notifier : ClaudeHomeServer.Services.Composition.INotificationSender
    {
        public List<CreateNotificationRequest> Sent { get; } = [];

        public Task<string> SendAsync(string userId, CreateNotificationRequest req, bool sendPush = false)
        {
            Sent.Add(req);
            return Task.FromResult("n-" + Sent.Count);
        }
    }

    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    // Захват журнала службы (тесты строки итога, J-O1): строка как её видит журнал —
    // отформатированная, с подставленными значениями.
    private sealed class RecordingLog : ILogger<WikiNightlyService>
    {
        public List<(LogLevel Level, string Message)> Rows { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Rows.Add((logLevel, formatter(state, exception)));
    }
}
