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

    private WikiNightlyService Sut(Func<string, string, CancellationToken, Task<WikiChatIngestService.ChatResult>>? analyze = null,
        ClaudeHomeServer.Services.Composition.IFeatureFlagGate? flags = null,
        ClaudeHomeServer.Services.Composition.INotificationSender? notifier = null) =>
        new(_sessions, new WikiChatRecalc(_sessions, _config), new WikiChatStage2(_llm, _sessions, _config),
            analyze ?? Done(), _llm, _config, NullLogger<WikiNightlyService>.Instance, flags ?? _flag, () => _now, notifier);

    private Func<string, string, CancellationToken, Task<WikiChatIngestService.ChatResult>> Done(
        string outcome = WikiChatIngestService.OutcomeNothingNew, int packages = 0) =>
        (owner, chat, ct) =>
        {
            lock (_analyzed) _analyzed.Add(chat);
            return Task.FromResult(new WikiChatIngestService.ChatResult(
                outcome, null, [.. Enumerable.Range(0, packages).Select(i => $"pkg-{chat}-{i}")]));
        };

    private Task Tick(WikiNightlyService? sut = null) => (sut ?? Sut()).RunNightTickAsync(_now);

    private WikiNightlyService.NightResult Last() => WikiNightlyService.ReadLast(Root()).Should().NotBeNull().And.Subject
        as WikiNightlyService.NightResult ?? throw new InvalidOperationException();

    private WikiChatIngestService RealIngest() =>
        new(new WikiSourceCapture(_notes, _projects, _sessions, _config, _keeper),
            new WikiIngestService(_llm, new NoSpheres(), _apply, _config), _sessions, _keeper, _config);

    private WikiNightlyService RealSut() =>
        new(_sessions, new WikiChatRecalc(_sessions, _config), new WikiChatStage2(_llm, _sessions, _config),
            (o, c, ct) => RealIngest().AnalyzeAsync(o, c, ct, LocalActionCatalog.WikiNightly),
            _llm, _config, NullLogger<WikiNightlyService>.Instance, _flag, () => _now);

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
        Task<WikiChatIngestService.ChatResult> Analyze(string o, string c, CancellationToken ct)
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
        Task<WikiChatIngestService.ChatResult> Analyze(string o, string c, CancellationToken ct)
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
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
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
            (o, c, ct) =>
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

    // ─── PUT enabled=false и закрытие окна посреди ночи ────────────────────

    [Fact]
    public async Task ТумблерВыключенПосредиНочи_СтопПередСледующимЧатом()
    {
        Configure();
        AddChat("c1", ageMinutes: 0);
        AddChat("c2", ageMinutes: 1);
        Task<WikiChatIngestService.ChatResult> Analyze(string o, string c, CancellationToken ct)
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
        Task<WikiChatIngestService.ChatResult> Analyze(string o, string c, CancellationToken ct)
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
        Task<WikiChatIngestService.ChatResult> Analyze(string o, string c, CancellationToken ct)
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
        async Task<WikiChatIngestService.ChatResult> Analyze(string o, string c, CancellationToken ct)
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
        new(s.TaskExecution, s.WikiMark, s.IsPinned, false, null, null, s.CreatedAt, s.UpdatedAt, 0, _sessions.History[s.Id]);

    // ─── подмены ───────────────────────────────────────────────────────────

    private sealed class Llm : ICheapTextRunner
    {
        public readonly List<(string ActionKey, string Prompt)> Calls = [];
        public bool Route = true;
        public Func<string, string>? Reply;
        public TaskCompletionSource? Gate;
        // Модель вызвана и встала на гейте — событие вместо таймерного ожидания в тестах.
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool UsesLocal(string actionKey) => false;
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
}
