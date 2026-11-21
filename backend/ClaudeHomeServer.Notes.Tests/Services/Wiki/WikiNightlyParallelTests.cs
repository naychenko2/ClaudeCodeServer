using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Координатор ночи (W4a2, разрез «ночь вики на нескольких локальных исполнителях»): слоты
// исполнителей, сродство с разделом, счётчики и резерв бюджета на координаторе, WhenAll по
// слотам. Шов разбора подменён фейком с гейтами: чат «вошёл в разбор» (entered) и «держится в
// полёте» (его гейт не открыт). Параллельность доказывается самим протоколом: оба entered
// наступили до первого освобождения — значит оба разбора были в полёте одновременно; часы
// прохода заморожены, поэтому мерить пересечение интервалов нельзя — меряется факт полёта.
// Остальной смысл ночи (окно, лимиты, инварианты) — в WikiNightlyServiceTests на
// последовательном пути; здесь — поведение при исполнителях и путь без них.
public class WikiNightlyParallelTests : IDisposable
{
    private const string User = "u1";
    private const string SectionA = "projects/p1";
    private const string SectionB = "projects/p2";
    private const string SectionC = "projects/p3";
    private const string SectionD = "projects/p4";
    private const string SectionE = "projects/p5";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _apply;
    private readonly Sessions _sessions = new();
    private readonly Llm _llm = new();
    private readonly Flag _flag = new();
    // Журнал входов в разбор: чат и названный исполнитель (null — прежний маршрут места).
    private readonly List<(string Chat, string? Engine)> _calls = [];
    private readonly Dictionary<string, TaskCompletionSource> _entered = new(StringComparer.Ordinal);
    // Гейт выхода чата из разбора: ставится перед возвратом фейка — «этого слота в полёте нет».
    private readonly Dictionary<string, TaskCompletionSource> _exited = new(StringComparer.Ordinal);
    // Вызовы шва судьи (а): момент вызова — сколько слотов ещё в полёте, и названный корень.
    private readonly List<(int OpenSlots, string Root)> _judgeCalls = [];
    private DateTimeOffset _now = At(4);

    public WikiNightlyParallelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_nightly_par_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, null, flags: _flag);
        _keeper = new WikiKeeper(_notes, _config);
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static DateTimeOffset At(int hour, int day = 2) => new(2026, 11, day, hour, 0, 0, TimeSpan.Zero);
    private string Root(string owner = User) => WikiPaths.Root(_dir, owner);

    // ─── фикстуры (образец WikiNightlyServiceTests) ─────────────────────────

    private void Configure(bool enabled = true, int start = 3, int end = 6, int perNight = 20, int perSection = 6,
        int chatsPerSection = 10, int budget = 700_000) =>
        WikiSettingsStore.Mutate(_dir, User, s => s with
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

    // ageMinutes — возраст UpdatedAt: очередь строится по нему убыв., тест задаёт порядок
    // очереди явными возрастами (меньший возраст — раньше в очереди). important=false и
    // своя history — серый чат (его судит ступень 2 на подходе очереди).
    private void AddChat(string id, string project, int ageMinutes, int humans = 4, int len = 100,
        bool important = true, List<StoredMessage>? history = null)
    {
        var s = new Session
        {
            Id = id,
            OwnerId = User,
            ProjectId = project,
            Name = id,
            WikiMark = important ? WikiMarks.Important : null,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-ageMinutes),
        };
        _sessions.All.Add(s);
        _sessions.History[id] = history ?? Hist(humans, len);
    }

    private WikiNightlyService Sut(
        Func<string, string, string?, CancellationToken, Task<WikiChatIngestService.ChatResult>> analyze,
        Func<string, string, IReadOnlySet<string>?, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>>? judge = null) =>
        new(_sessions, new WikiChatRecalc(_sessions, _config), new WikiChatStage2(_llm, _sessions, _config),
            analyze, _llm, _config, NullLogger<WikiNightlyService>.Instance, _flag,
            () => _now, null, _keeper, judge);

    private Task Tick(WikiNightlyService sut) => sut.RunNightTickAsync(_now);

    private WikiNightlyService.NightResult Last() => WikiNightlyService.ReadLast(Root()).Should().NotBeNull().And.Subject
        as WikiNightlyService.NightResult ?? throw new InvalidOperationException();

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Гейт входа чата в разбор: тест берёт его ДО прохода, фейк ставит результат при входе в шов.
    private TaskCompletionSource EnteredOf(string chat)
    {
        lock (_entered)
        {
            if (!_entered.TryGetValue(chat, out var t)) _entered[chat] = t = NewGate();
            return t;
        }
    }

    // Гейт открылся за предел: чат вошёл в разбор. Предел — не пауза в норме, а явный провал
    // там, где выдача не случилась (иначе тест висел бы до таймаута коллекции).
    private static async Task GateOpened(TaskCompletionSource entered) =>
        (await Task.WhenAny(entered.Task, Task.Delay(5000))).Should().BeSameAs(entered.Task, "чат не вошёл в разбор");

    // Гейт выхода чата из разбора: тест берёт его ДО прохода, фейк поднимает перед возвратом.
    private TaskCompletionSource ExitedOf(string chat)
    {
        lock (_exited)
        {
            if (!_exited.TryGetValue(chat, out var t)) _exited[chat] = t = NewGate();
            return t;
        }
    }

    // Сколько слотов ещё в полёте: их вход записан, а выхода не было. Меряется в момент вызова
    // шва судьи (а): «судья после всех слотов» = «судья не видит ни одного полёта».
    private int OpenSlots()
    {
        lock (_exited) return _exited.Values.Count(t => !t.Task.IsCompleted);
    }

    // Фейк шва судьи (а): фиксирует момент вызова (полёт слотов) и названный корень.
    private Func<string, string, IReadOnlySet<string>?, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>> JudgeProbe() =>
        (owner, root, _, ct) =>
        {
            lock (_judgeCalls) _judgeCalls.Add((OpenSlots(), root));
            return Task.FromResult(new WikiJudgeSweep.JudgeSweepResult(0, 0m, 0, 0, 0, 0, null));
        };

    // Фейк разбора: вход записан (и signal entered поднят), выход — после открытия гейта.
    // gateFor(null) — выходит сразу (когда полёт не нужен).
    private Func<string, string, string?, CancellationToken, Task<WikiChatIngestService.ChatResult>> GateDone(
        Func<string, TaskCompletionSource?>? gateFor) =>
        async (owner, chat, engine, ct) =>
        {
            lock (_calls) _calls.Add((chat, engine));
            EnteredOf(chat).TrySetResult();
            ExitedOf(chat); // с этой минуты чат «в полёте»: его выхода ещё не было
            if (gateFor?.Invoke(chat) is { } gate) await gate.Task.ConfigureAwait(false);
            ExitedOf(chat).TrySetResult();
            return new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []);
        };

    // ─── сродство с разделом и параллельность разделов ──────────────────────

    [Fact]
    public async Task SameSection_NeverConcurrent()
    {
        Configure();
        // Два слота одного исполнителя свободны — и всё равно второй чат того же раздела
        // не стартует, пока первый в полёте.
        _llm.Executors = [new LocalEngineExecutor("local", "local", 2)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionA, ageMinutes: 20);
        var e1 = EnteredOf("c1");
        var g1 = NewGate();
        var g2 = NewGate();
        var sut = Sut(GateDone(chat => chat == "c1" ? g1 : g2));

        var tick = Tick(sut);
        await GateOpened(e1); // c1 в полёте — c2 не выдан: его раздел занят, хоть слот и свободен
        _calls.Select(c => c.Chat).Should().Equal(new[] { "c1" }, "второй чат того же раздела не выходит, пока первый в полёте");

        g2.SetResult(); // c2 выйдет сразу, как его отдадут
        g1.SetResult();
        await tick;

        _calls.Select(c => c.Chat).Should().Equal("c1", "c2");
        _calls.Select(c => c.Engine).Should().Equal("local", "local");
        Last().Analyzed.Should().Be(2);
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
    }

    [Fact]
    public async Task TwoSections_RunConcurrently()
    {
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 2)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);
        var e1 = EnteredOf("c1");
        var e2 = EnteredOf("c2");
        var g1 = NewGate();
        var g2 = NewGate();
        var sut = Sut(GateDone(chat => chat == "c1" ? g1 : g2));

        var tick = Tick(sut);
        // Оба входа состоялись до первого освобождения: оба разбора в полёте одновременно.
        await GateOpened(e1);
        await GateOpened(e2);
        _calls.Count.Should().Be(2);

        g1.SetResult();
        g2.SetResult();
        await tick;

        Last().Analyzed.Should().Be(2);
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
    }

    [Fact]
    public async Task ThreeChatsTwoSlots_SecondOfSectionWaits()
    {
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 2)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionA, ageMinutes: 20);
        AddChat("c3", SectionB, ageMinutes: 30);
        var e1 = EnteredOf("c1");
        var e3 = EnteredOf("c3");
        var g1 = NewGate();
        var g3 = NewGate();
        var sut = Sut(GateDone(chat => chat == "c1" ? g1 : chat == "c3" ? g3 : null));

        var tick = Tick(sut);
        await GateOpened(e1);
        await GateOpened(e3);
        // Слот два: не c2 (его раздел занят c1), а c3 — следующий чат со свободным разделом.
        _calls.Select(c => c.Chat).Should().Equal("c1", "c3");

        g1.SetResult(); // освободились раздел A и слот — только теперь берётся c2
        g3.SetResult();
        await tick;

        _calls.Select(c => c.Chat).Should().Equal("c1", "c3", "c2");
        Last().Analyzed.Should().Be(3);
    }

    // ─── резерв бюджета до выдачи ───────────────────────────────────────────

    [Fact]
    public async Task BudgetReservedBeforeIssue()
    {
        // Оценка чата резервируется ДО выдачи: при двух свободных слотах второй чат, не влезающий
        // в остаток с учётом оценки первого (в полёте), не выходит вовсе. При одном слоте эта
        // разница не видна — там резерв до выдачи и после завершения совпадают.
        Configure(budget: 300); // бюджет чатов = 210: оценка каждого 110, остатка после первого не хватает
        _llm.Executors = [new LocalEngineExecutor("local", "local", 2)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);
        var e1 = EnteredOf("c1");
        var g1 = NewGate();
        var sut = Sut(GateDone(chat => chat == "c1" ? g1 : null));

        var tick = Tick(sut);
        await GateOpened(e1);
        _calls.Select(c => c.Chat).Should().Equal(new[] { "c1" }, "второй чат не проходит по остатку бюджета и не выдаётся");

        g1.SetResult();
        await tick;

        Last().Outcome.Should().Be(WikiNightlyService.OutcomeBudget);
        Last().Selected.Should().Be(2); // второй чат был выбран до упора — как в последовательном пути
        Last().Analyzed.Should().Be(1);
    }

    // ─── путь без исполнителей: он прежний ──────────────────────────────────

    [Fact]
    public async Task EmptyExecutors_SequentialWithNullEngine()
    {
        // Исполнителей нет (пустой список стора) — прежний последовательный проход: engine=null,
        // порядок очереди сохранён, один разбор в полёте, итог тот же, что был до шага.
        Configure();
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionA, ageMinutes: 20);
        AddChat("c3", SectionB, ageMinutes: 30);
        var e1 = EnteredOf("c1");
        var e2 = EnteredOf("c2");
        var g1 = NewGate();
        // c1 держится в полёте, пока его гейт закрыт: при пустом списке исполнителей c2 не может
        // войти, пока c1 не вышел, — путь строго последовательный.
        var sut = Sut(GateDone(chat => chat == "c1" ? g1 : null));

        var tick = Tick(sut);
        await GateOpened(e1);
        _calls.Select(c => c.Chat).Should().Equal(new[] { "c1" }, "без исполнителей второй чат не стартует рядом с первым");
        g1.SetResult();
        await GateOpened(e2);
        await tick;

        _calls.Select(c => c.Chat).Should().Equal("c1", "c2", "c3");
        _calls.Select(c => c.Engine).Should().Equal(null, null, null);
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
        last.Selected.Should().Be(3);
        last.Analyzed.Should().Be(3);
    }

    // ─── сбой слота не рвёт проход ──────────────────────────────────────────

    [Fact]
    public async Task SlotError_KillsNotPass()
    {
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 2)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);
        var e2 = EnteredOf("c2");
        var g2 = NewGate();
        // c1 падает исключением внутри слота: это отказ его разбора (тот же смысл, что прежний
        // catch) — координатор и соседний слот живы: c2 дорабатывается, в analyzed только он.
        var sut = Sut(async (owner, chat, engine, ct) =>
        {
            if (chat == "c1") throw new InvalidOperationException("движок не принял вызов");
            lock (_calls) _calls.Add((chat, engine));
            EnteredOf(chat).TrySetResult();
            await g2.Task.ConfigureAwait(false);
            return new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []);
        });

        var tick = Tick(sut);
        await GateOpened(e2); // соседний слот жив: c2 в полёте, хотя слот c1 уже отказал
        g2.SetResult();
        await tick;

        _calls.Select(c => c.Chat).Should().Equal("c2");
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
        last.Selected.Should().Be(2); // c1 был выбран, но не разобран — как прежний catch
        last.Analyzed.Should().Be(1);
    }

    // ─── одно решение: судья (а) — строго после всех слотов ───────────────────

    [Fact]
    public async Task JudgeA_AfterAllSlots()
    {
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 2)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);
        var e1 = EnteredOf("c1");
        var e2 = EnteredOf("c2");
        var g1 = NewGate();
        var g2 = NewGate();
        var sut = Sut(GateDone(chat => chat == "c1" ? g1 : g2), JudgeProbe());

        var tick = Tick(sut);
        await GateOpened(e1);
        await GateOpened(e2);
        // Оба слота в полёте: шов судьи (а) не звала ни разу — она после WhenAll, не рядом с ними.
        _judgeCalls.Should().BeEmpty("пока слот в полёте, прохода судьи нет");

        g1.SetResult();
        await GateOpened(ExitedOf("c1"));
        _judgeCalls.Should().BeEmpty("первый слот вышел, второй ещё в полёте — судья ждёт завершения всех");

        g2.SetResult();
        await tick;

        _judgeCalls.Should().HaveCount(1, "ровно один проход судьи на проход ночи");
        _judgeCalls[0].OpenSlots.Should().Be(0, "в момент вызова судья не видит ни одного слота в полёте");
        _judgeCalls[0].Root.Should().Be(Root(), "проход судьи — по корню этого владельца");
        var last = Last();
        last.Analyzed.Should().Be(2);
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
    }

    // ─── отказ подготовки не оставляет слота в полёте за координатором ────────

    [Fact]
    public async Task PrepareError_SlotsDoNotOutliveThePass()
    {
        // Ревью W4a2 (minor 1): исключение prepare/apply уходит в общий catch прохода. Без
        // ожидания inflight на этом выходе чат в полёте доигрывал ПОСЛЕ возврата координатора —
        // его _analyze шёл бы рядом с судьёй (а) или со следующим проходом. Мутация «убрать
        // finally-ожидание» → красный: проход вернулся бы, пока c1 держат его гейт.
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 2)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);
        var e1 = EnteredOf("c1");
        var g1 = NewGate();
        // Отказ подготовки c2: его история бросает, только когда первый чат уже в полёте
        // (пересчёт отбора читаает истории раньше — ему отказ не ставится).
        var threw = NewGate();
        _sessions.ThrowHistoryWhen = id => id == "c2" && _calls.Count > 0;
        _sessions.HistoryThrew = threw;
        var sut = Sut(GateDone(chat => chat == "c1" ? g1 : null), JudgeProbe());

        var tick = Tick(sut);
        await GateOpened(e1);
        await GateOpened(threw); // prepare(c2) бросил: координатор уходит исключением
        // Пока c1 держит свой гейт, проход не может вернуться: его ожидание — в finally.
        var winner = await Task.WhenAny(tick, Task.Delay(500));
        winner.Should().NotBeSameAs(tick, "проход вернулся, пока слот c1 ещё в полёте");

        g1.SetResult();
        await tick;

        await ExitedOf("c1").Task; // дождан до возврата координатора
        _judgeCalls.Should().HaveCount(1, "судья (а) — после того, как полёт погашен");
        _judgeCalls[0].OpenSlots.Should().Be(0);
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeError, "отказ подготовки — отказ прохода");
        last.Selected.Should().Be(1, "c1 был выбран до отказа следующего чата");
        last.Analyzed.Should().Be(0, "исходы дожданных слотов проходу уже не нужны");
    }

    // ─── один чат не бывает в двух слотах ─────────────────────────────────────

    [Fact]
    public async Task SameChat_NeverInTwoSlots()
    {
        // Два свободных слота и два чата одного раздела: второй слот не получает ничего
        // законного — ни повтор c1 (его уже забрали из очереди), ни c2 (раздел занят).
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 2)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionA, ageMinutes: 20);
        var e1 = EnteredOf("c1");
        var g1 = NewGate();
        var g2 = NewGate();
        var sut = Sut(GateDone(chat => chat == "c1" ? g1 : g2));

        var tick = Tick(sut);
        await GateOpened(e1);
        _calls.Select(c => c.Chat).Should().Equal(new[] { "c1" },
            "свободный слот не берёт повторно чат, который уже в полёте");

        g1.SetResult();
        g2.SetResult();
        await tick;

        _calls.Select(c => c.Chat).Should().Equal("c1", "c2");
        _calls.Select(c => c.Chat).Distinct().Should().HaveCount(2, "каждый чат — ровно один раз за проход");
        Last().Analyzed.Should().Be(2);
    }

    // ─── путь 8: «Разобрать сейчас» идёт тем же координатором ────────────────

    [Fact]
    public async Task Path8_ManualPass_GoesThroughTheSameCoordinator()
    {
        // Ручной проход — тот же RunOwnerPassAsync и тот же координатор с исполнителями:
        // параллельность и имена движков здесь ровно те же, что у ночного тика.
        Configure();
        _llm.Executors = [new LocalEngineExecutor("gemma", "Gemma", 2)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);
        var e1 = EnteredOf("c1");
        var e2 = EnteredOf("c2");
        var g1 = NewGate();
        var g2 = NewGate();
        var sut = Sut(GateDone(chat => chat == "c1" ? g1 : g2));

        var start = sut.TryStartManual(User, out var pass);
        start.Should().Be(WikiNightlyService.ManualStart.Started);
        WikiNightlyService.IsRunning(Root()).Should().BeTrue("проход владельца занят");
        // Ручной тик того же владельца рядом не идёт: один проход на владельца.
        sut.TryStartManual(User).Should().Be(WikiNightlyService.ManualStart.AlreadyRunning);

        await GateOpened(e1);
        await GateOpened(e2);
        // Оба входа до первого освобождения: ручной проход ведёт чаты параллельно и называет
        // исполнителя — то есть идёт координатором, а не прежним последовательным циклом.
        _calls.Select(c => c.Chat).Should().Equal("c1", "c2");
        _calls.Select(c => c.Engine).Should().Equal("gemma", "gemma");

        g1.SetResult();
        g2.SetResult();
        await pass;

        var last = Last();
        last.Manual.Should().BeTrue();
        last.Analyzed.Should().Be(2);
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
        WikiNightlyService.IsRunning(Root()).Should().BeFalse("замок снят по конце прохода");
    }

    // ─── путь 10: судья (а) — по месту wiki-judge, не через исполнителя ночи ──

    [Fact]
    public async Task Path10_JudgeIsCalledByPlaceNotByExecutor()
    {
        // Живой проход судьи (WikiJudgeSweep) на том же шве, что и ночь с исполнителями:
        // её вызов — RunDetailedAsync места wiki-judge (его выбирает админ), а не путь
        // названного движка. Мутация «пустить исполнителя в судью» → красный второй assert.
        Configure();
        _llm.Executors = [new LocalEngineExecutor("gemma", "Gemma", 1)];
        AddChat("c1", SectionA, ageMinutes: 10);
        OpenDisputedPackage();
        var judge = new WikiJudgeSweep(_llm, _apply, _config);
        var sut = Sut(GateDone(null), (owner, root, only, ct) => judge.RunAsync(owner, root, only, ct));

        await Tick(sut);

        _llm.ModelCalls.Should().Contain(c => c.Method == "RunDetailed" && c.ActionKey == LocalActionCatalog.WikiJudge,
            "судья зовёт модель по своему месту обычным путём места");
        _llm.ModelCalls.Should().NotContain(c => c.Method == "RunOnLocalEngine" && c.ActionKey == LocalActionCatalog.WikiJudge,
            "судья не идёт через исполнителя ночи: её движок выбирает админ места wiki-judge");
        var last = Last();
        last.Analyzed.Should().Be(1);
        last.JudgeCalls.Should().Be(1, "один живой проход судьи на конец прохода");
    }

    // Пакет со спорной операцией, допущенной к судье (тот же приём, что в WikiJudgeSweepTests):
    // путь 10 меряется на живом проходе — без допущенной операции судья моделью не зовётся вовсе.
    private const string CommonPage = "Общее/Сущность.md";
    private const string OldClaim = "Прежний план был остаться на Windows";

    private static readonly string CommonPageFile = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Сущность

        ## Сводка
        {{OldClaim}}

        ## Хронология

        ## Противоречия

        ## История
        """;

    private void OpenDisputedPackage()
    {
        var wrote = _keeper.WritePageAsync(User, "common", CommonPage, CommonPageFile).GetAwaiter().GetResult();
        wrote.Status.Should().Be(WikiOpStatus.Applied, wrote.Reason);
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = "common",
            Sources = [new WikiPackage.SourceRef("session", "session:c0#m1-9")],
            Operations =
            [
                new WikiPackage.Operation
                {
                    Op = WikiPackage.OpSupersede,
                    Page = CommonPage,
                    Text = "Новый план: перенести сборку на Linux",
                    Target = OldClaim,
                    Class = WikiPackage.ClassKey("session", WikiPackage.OpSupersede),
                    Decision = WikiPackage.DecisionInbox,
                    Reason = "целевая страница в статусе «disputed»: там уже нужен человек",
                    ReasonCode = WikiApplyPolicy.CodeTargetDisputed,
                },
            ],
            Status = WikiPackage.StatusChecking,
        };
        WikiPackageStore.Create(Root(), pkg).Ok.Should().BeTrue();
        WikiPackageStore.Transition(Root(), pkg.Id, WikiPackage.StatusProposed).Ok.Should().BeTrue();
    }

    // ─── отказ исполнителя (W5): выбывание, проба готовности, повтор чата ──────────
    //
    // Ряд отказов «модель недоступна» считается по исполнителю: три его отказа выводят его из
    // прохода, живой добивает очередь — в том числе чат мёртвого (один повтор на чат, другому
    // исполнителю). Неготовый исполнитель чатов не получает и не выбывает. Выбыли или не готовы
    // все — проход стоит. Границы ряда («подряд идущие», обнуление состоявшимся разбором)
    // держат тесты последовательного пути (WikiNightlyServiceTests) — здесь только то, что
    // появляется вместе с исполнителями.

    /// <summary>Фейк разбора по исполнителям: namedDown — движок, который не отвечает (тот же
    /// признак отказа, что у IsLocalDownRejection: формулировка маршрута + InputTokens 0).
    /// Всё возвращается сразу: порядок здесь держат очередь и слоты, а не гейты полёта — для
    /// них тесты выше.</summary>
    private Func<string, string, string?, CancellationToken, Task<WikiChatIngestService.ChatResult>> DownFor(
        params string[] namedDown) =>
        DownForChat(null, namedDown);

    /// <summary>То же, но downChats добавляет пары «chat|engine», которые отказывают и для
    /// живого движка: так видно, что повтор чата — один, а не «пока хоть один движок жив».</summary>
    private Func<string, string, string?, CancellationToken, Task<WikiChatIngestService.ChatResult>> DownForChat(
        string[]? downChats, params string[] namedDown) =>
        (owner, chat, engine, ct) =>
        {
            lock (_calls) _calls.Add((chat, engine));
            var down = engine is not null
                && (Array.IndexOf(namedDown, engine) >= 0
                    || Array.IndexOf(downChats ?? [], chat + "|" + engine) >= 0);
            return Task.FromResult(down
                ? new WikiChatIngestService.ChatResult(
                    WikiChatIngestService.OutcomeRejected,
                    "разбор окна m1-m8 не удался: синтез: " + WikiIngestService.ReasonLocalUnavailable, [],
                    InputTokens: 0)
                : new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []));
        };

    [Fact]
    public async Task DeadExecutor_ChatRetriedOnOther_Once()
    {
        // Три исполнителя, «local» мёртв. Его чат c1 возвращается в голову очереди и отдан
        // другому — ровно один раз: вторая попытка (gemma, тоже отказала c1) расходует лимит, и
        // третий живой исполнитель c1 уже не берёт. Чат c2 при этом добирается нормально: лимит
        // одного чата не мешает остальным. Мутации: «повтор без лимита» (retried не спрашивается)
        // → c1 зовётся трижды, на всех трёх движках (первый assert красный); «не возвращать чат
        // в очередь» → c1 на другом не идёт вовсе (второй assert красный). Selected — три чата,
        // а не четыре попытки: повтор не резервирует оценку чата второй раз.
        Configure();
        _llm.Executors = [
            new LocalEngineExecutor("local", "local", 1),
            new LocalEngineExecutor("gemma", "Gemma", 1),
            new LocalEngineExecutor("llama", "Llama", 1),
        ];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);
        AddChat("c3", SectionC, ageMinutes: 30);

        await Tick(Sut(DownForChat(["c1|gemma"], "local")));

        _calls.Count(c => c.Chat == "c1").Should().Be(2, "повтор чата — один на проход, даже когда живой движок ещё есть");
        _calls.Select(c => c.Engine).Should().Contain(new[] { "gemma" }, "чат мёртвого добрал другой");
        _calls.Should().NotContain(c => c.Chat == "c1" && c.Engine == "llama",
            "после израсходованного повтора чат остаётся за этой ночью, а не ходит по всем движкам");
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted, "живые исполнители доиграли очередь");
        last.Selected.Should().Be(3, "повтор — не новый выбранный чат и не вторая резервация его оценки");
        last.Rejected.Should().Be(2);
    }

    [Fact]
    public async Task PlanRejection_NotRetried()
    {
        // Отказ с текстом мёртвого движка, но с nonzero входом: модель в ранних окнах чата
        // отвечала (плохой план, пустой ответ — что угодно), и только последнее окно упёрлось в
        // мёртвый движок. Такой чат не повторяется: он уже частично разобран, его остаток ждёт
        // следующей ночи, а повтор гонял бы его по кругу. По нему и ряд исполнителя не растёт.
        // Мутация «IsLocalDownRejection без проверки InputTokens == 0» → c1 возвращается в
        // очередь и добран живым gemma (первый assert красный).
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 1), new LocalEngineExecutor("gemma", "Gemma", 1)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);

        await Tick(Sut((owner, chat, engine, ct) =>
        {
            lock (_calls) _calls.Add((chat, engine));
            var down = engine == "local";
            return Task.FromResult(down
                ? new WikiChatIngestService.ChatResult(
                    WikiChatIngestService.OutcomeRejected,
                    "разбор окна m1-m8 не удался: синтез: " + WikiIngestService.ReasonLocalUnavailable, [],
                    InputTokens: 500) // модель отвечала: признак «ни одного вызова» здесь не выполнен
                : new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []));
        }));

        _calls.Select(c => c.Chat).Should().Equal(new[] { "c1", "c2" },
            "каждый чат — одна попытка: отказ состоявшегося разбора не повторяется");
        _calls.Count(c => c.Chat == "c1").Should().Be(1, "чаты частично разобранного отказа не возвращаются в очередь");
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted, "ряд по таким отказам не растёт: движок не признан мёртвым");
        last.Analyzed.Should().Be(2);
        last.Rejected.Should().Be(1);
        last.InputTokens.Should().Be(500, "понесённый вход отказа не отменяет");
    }

    [Fact]
    public async Task NotReady_SkippedNotWithdrawn()
    {
        // «local» не готов (проба отказывает): он не получает чатов и при этом не сжигает их
        // отказом и не выбывает — их берёт готовый «gemma». Мутация «негативная проба = отказ
        // чата» → c2 не выходит вовсе и ряд local растёт (второй assert красный); «негативная
        // проба = выбывание» или «неготового всё равно дать» → c2 идёт на local (первый assert
        // красный).
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 1), new LocalEngineExecutor("gemma", "Gemma", 1)];
        _llm.ReadyEngines = new HashSet<string>(StringComparer.Ordinal) { "gemma" };
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);
        var g1 = NewGate();
        var sut = Sut(async (owner, chat, engine, ct) =>
        {
            lock (_calls) _calls.Add((chat, engine));
            EnteredOf(chat).TrySetResult();
            if (chat == "c1") await g1.Task.ConfigureAwait(false);
            return new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []);
        });

        var tick = Tick(sut);
        await GateOpened(EnteredOf("c1")); // c1 держат в полёте: пока он в нём, c2 не выходит
        _calls.Select(c => c.Chat).Should().Equal(new[] { "c1" });
        _llm.ReadyChecks.Should().Contain("local", "неготового спрашивают и пропускают");

        g1.SetResult();
        await tick;

        _calls.Select(c => c.Chat).Should().Equal("c1", "c2");
        _calls.Select(c => c.Engine).Should().Equal("gemma", "gemma");
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
        last.Analyzed.Should().Be(2);
        last.Rejected.Should().Be(0, "неготовый исполнитель не сжегла ни одного чата");
    }

    [Fact]
    public async Task AllDead_Error()
    {
        // Ни один исполнитель не готов (никто не звался, ни одного чата не сожжено) и в полёте
        // никого: продолжать нечем — итог error, дату он не закрывает. Мутация «дать всё равно
        // неготовому» → error нет, чаты ушли бы в несуществующий движок (второй assert
        // красный); «пропустить проверку» → то же.
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 1), new LocalEngineExecutor("gemma", "Gemma", 1)];
        _llm.ReadyEngines = new HashSet<string>(StringComparer.Ordinal); // ни один не готов
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);

        await Tick(Sut(DownFor()));

        _calls.Should().BeEmpty("ни один вызов не состоялся: ни одного исполнителя не было готово");
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeError, "никого живого и готового — ночь не удалась");
        last.Selected.Should().Be(0);
        last.Analyzed.Should().Be(0);
    }

    [Fact]
    public async Task ThreeRejections_ExecutorWithdraws_PassFinishesOnLive()
    {
        // Три отказа «модель недоступна» подряд у «local» — он выбыл до конца прохода: дальше ни
        // одного его вызова, очередь доиграна на «gemma», итог completed. Мутация «не помнить
        // выбывание» → local зовётся и дальше (счёт его вызовов красный); «ряд по месту, а не по
        // исполнителю» → local выбывает на первом же отказе c1, отданном gemma (красный).
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 1), new LocalEngineExecutor("gemma", "Gemma", 1)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);
        AddChat("c3", SectionC, ageMinutes: 30);
        AddChat("c4", SectionD, ageMinutes: 40);
        var g2 = NewGate();
        var sut = Sut(async (owner, chat, engine, ct) =>
        {
            lock (_calls) _calls.Add((chat, engine));
            EnteredOf(chat).TrySetResult();
            var dead = engine == "local";
            if (chat == "c2") await g2.Task.ConfigureAwait(false); // gemma держит свой чат: новые берутся на local
            return dead
                ? new WikiChatIngestService.ChatResult(
                    WikiChatIngestService.OutcomeRejected,
                    "разбор окна m1-m8 не удался: синтез: " + WikiIngestService.ReasonLocalUnavailable, [],
                    InputTokens: 0)
                : new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []);
        });

        var tick = Tick(sut);
        // Пока gemma держит свой чат, очередь идёт на свободный local: он успевает отказать
        // c1, c3 и c4 — три отказа подряд. Гейт открывают после третьего: до этого момента
        // проходу ждать нечего, и он честно стоит на живом слоте.
        await GateOpened(EnteredOf("c4"));
        g2.SetResult();
        await tick;

        _calls.Count(c => c.Engine == "local").Should().Be(3,
            "три отказа подряд — исполнитель выбыл: его слоты чатов больше не получают");
        _calls.Count(c => c.Chat == "c1").Should().Be(2,
            "повтор одного чата — один на проход: мёртвый раз, живой раз");
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted, "живой исполнитель доиграл проход");
        last.Selected.Should().Be(4);
        last.Analyzed.Should().Be(7);
        last.Rejected.Should().Be(3);
    }

    [Fact]
    public async Task LimitNamedFirst_WinsOverExecutorDeath()
    {
        // Хвост ревью R-W4a2 (3): «смерть движка» больше не называет итог из ApplyResult — она
        // названа в CheckStop («нет живого исполнителя») и в ветке «никого живого и готового»,
        // и обе доступны только пока упора нет (условие цикла выдачи — stop is null). Здесь
        // порядок обратный прежнему: три отказа local (выбывание) — первыми, limit пакетов
        // (пакет c2) — вторым. Итог — limit (budget), дату он закрывает; error его не подменяет.
        // Мутация «вернуть OutcomeError из ApplyResult при выбывании» (прежнее поведение) →
        // красный последний assert: итог error вместо budget.
        Configure(perNight: 1);
        _llm.Executors = [new LocalEngineExecutor("local", "local", 1), new LocalEngineExecutor("gemma", "Gemma", 1)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);
        AddChat("c3", SectionC, ageMinutes: 30);
        AddChat("c4", SectionD, ageMinutes: 40);
        var g2 = NewGate();
        var sut = Sut(async (owner, chat, engine, ct) =>
        {
            lock (_calls) _calls.Add((chat, engine));
            EnteredOf(chat).TrySetResult();
            if (chat == "c2") await g2.Task.ConfigureAwait(false); // gemma держит свой Applied-чат: новые берутся на local
            return engine == "local"
                ? new WikiChatIngestService.ChatResult(
                    WikiChatIngestService.OutcomeRejected,
                    "разбор окна m1-m8 не удался: синтез: " + WikiIngestService.ReasonLocalUnavailable, [],
                    InputTokens: 0)
                : new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeApplied, null, ["p1"]);
        });

        var tick = Tick(sut);
        await GateOpened(EnteredOf("c4")); // local отказал c1, c3, c4 — выбыл; пакет c2 ещё в полёте
        g2.SetResult();
        await tick;

        var last = Last();
        _calls.Count(c => c.Engine == "local").Should().Be(3);
        last.Rejected.Should().Be(3, "ряд мёртвого исполнителя дошёл до лимита");
        last.Applied.Should().Be(1, "пакет живого исполнителя применён");
        last.Outcome.Should().Be(WikiNightlyService.OutcomeBudget, "первый названный упор держит итог: limit пакетов назван раньше");
    }

    // ─── ряд отказов «подряд» (доработка W5 по ревью R-W5, п.1) ──────────────

    /// <summary>Образец для обоих тестов ряда: один исполнитель, пять чатов. Порядок, в котором
    /// координатор отдаёт им чаты, детерминирован в этой конфигурации и зафиксирован прогоном
    /// (одинаков в исправленном и мутантном коде: он зависит только от ротации очереди и
    /// возвратов отказанных чатов — тот же исполнитель их не берёт): c1, c5, c2, c3, c4 —
    /// после каждого отказа обход продолжается с первым из оставшихся, сдвинутые в конце
    /// обхода не оказываются. Таблица ответов в тестах построена под этот порядок: на
    /// «local» идут [down, план-отказ, down, down], щуп c4 (состоявшийся) — пятым:
    /// «local» берёт его только пока жив; при мутации «не обнулять ряд» три down
    /// складываются до щупа, единственный исполнитель выбывает — щуп не разобран вовсе
    /// (анализ 4, не 5), итог error, не completed.</summary>
    private async Task RunStreakSampleAsync(Func<string, WikiChatIngestService.ChatResult> localAnswer)
    {
        var sut = Sut((owner, chat, engine, ct) =>
        {
            lock (_calls) _calls.Add((chat, engine));
            return Task.FromResult(localAnswer(chat));
        });
        await Tick(sut);
    }

    private static WikiChatIngestService.ChatResult DownReject() => new(
        WikiChatIngestService.OutcomeRejected,
        "разбор окна m1-m8 не удался: синтез: " + WikiIngestService.ReasonLocalUnavailable, [], InputTokens: 0);

    private static WikiChatIngestService.ChatResult PlanReject() => new(
        WikiChatIngestService.OutcomeRejected, "разбор окна m1-m8 не удался: план не разобран", [],
        InputTokens: 500); // модель отвечала: это не отказ «модели нет»

    private static WikiChatIngestService.ChatResult SlotBusyReject() => new(
        WikiChatIngestService.OutcomeRejected,
        "разбор окна m1-m8 не удался: " + new LocalEngineSlotRefusalException("local", LocalTurnRefusal.QueueFull, 4, 4).Message,
        [], InputTokens: 0); // движок занят, а не мёртв: в ряд выбывания не входит (IsLocalDownRejection — false)

    [Fact]
    public async Task DownStreak_PlanRejectResets_NotWithdrawn()
    {
        // Порядок координатора c1, c5, c2, c3, c4 (см. RunStreakSampleAsync): на «local»
        // идут [down, план-отказ, down, down] и затем щуп c4 (состоявшийся). Ряд «модель
        // недоступна» считает только подряд идущие отказы этого вида: отказ состоявшегося
        // плана (модель отвечала, InputTokens > 0) обнуляет его, как в master — три down
        // суммарно, но не подряд, исполнитель жив и добирает щуп. Три down ПОДРЯД на том же
        // исполнителе — выбыл: это ThreeRejections_ExecutorWithdraws_PassFinishesOnLive.
        // Мутация «не обнулять ряд при не-down отказе» (NoteLive только в ветке не-Rejected):
        // ряд растёт через план-отказ (c1=1, c2=2, c3=3) и исполнитель выбывает до щупа →
        // первый assert красный: c4 не зван вовсе, анализ 4, итог error.
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 1)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);
        AddChat("c3", SectionC, ageMinutes: 30);
        AddChat("c4", SectionD, ageMinutes: 40);
        AddChat("c5", SectionE, ageMinutes: 50);

        await RunStreakSampleAsync(chat => chat switch
        {
            "c1" or "c2" or "c3" => DownReject(),
            "c5" => PlanReject(),
            _ => new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []),
        });

        _calls.Select(c => c.Chat).Should().Equal(new[] { "c1", "c5", "c2", "c3", "c4" },
            "после план-отказа ряд начался заново: три down не подряд — исполнитель жив и сам берёт щуп");
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted, "ряд не дошёл до лимита: исполнитель не выбыл");
        last.Analyzed.Should().Be(5);
        last.Rejected.Should().Be(4);
        last.InputTokens.Should().Be(500, "понесённый вход план-отказа не отменяет");
    }

    [Fact]
    public async Task DownStreak_SlotBusyRejectResets_NotWithdrawn()
    {
        // Тот же порядок и тот же инвариант, средний отказ — «слот движка занят»: движок
        // занят, а не мёртв, его отказ с нулевым входом в ряд не входит (IsLocalDownRejection
        // — false по формулировке причины) и обнуляет его. Щуп c4 принят «local» —
        // исполнитель жив.
        // Мутация та же, что у предыдущего теста: ряд растёт через отказ слота и исполнитель
        // выбывает до щупа → первый assert красный: c4 не зван вовсе, анализ 4, итог error.
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 1)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);
        AddChat("c3", SectionC, ageMinutes: 30);
        AddChat("c4", SectionD, ageMinutes: 40);
        AddChat("c5", SectionE, ageMinutes: 50);

        await RunStreakSampleAsync(chat => chat switch
        {
            "c1" or "c2" or "c3" => DownReject(),
            "c5" => SlotBusyReject(),
            _ => new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []),
        });

        _calls.Select(c => c.Chat).Should().Equal(new[] { "c1", "c5", "c2", "c3", "c4" },
            "отказ занятого слота ряд обнуляет: три down не подряд — исполнитель жив и сам берёт щуп");
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted, "ряд не дошёл до лимита: исполнитель не выбыл");
        last.Analyzed.Should().Be(5);
        last.Rejected.Should().Be(4);
    }

    [Fact]
    public async Task ReadyProbeThrows_EngineNotReady_PassNotBroken()
    {
        // Исключение пробы готовности (R-W5, п.2): «local» считается не готовым — ровно как
        // негативная проба (NotReady_SkippedNotWithdrawn): его не спрашивают повторно, чаты
        // берёт готовый «gemma», итог completed; проход не прерван исключением (без try/catch
        // RunPassAsync ловил бы его в общий catch → error). Мутация «убрать try/catch пробы» →
        // последний assert красный: outcome=error.
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 1), new LocalEngineExecutor("gemma", "Gemma", 1)];
        _llm.ReadyThrows = new HashSet<string>(StringComparer.Ordinal) { "local" };
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);

        await Tick(Sut(DownFor()));

        _llm.ReadyChecks.Should().Contain("local", "сбой пробы не снимает исполнителя из обхода: его спрашивают, как любого");
        _calls.Should().NotContain(c => c.Engine == "local", "сбой пробы = не готов: его слоты чатов не получают");
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted, "исключение пробы наружу не летит: живой доиграл проход");
        last.Analyzed.Should().Be(2);
        last.Rejected.Should().Be(0, "неготовый не сжёг ни одного чата");
    }

    // ─── доработка W5 №2 (сверка Александра): конец выключателя, серый повтор, «слот занят» ──

    [Fact]
    public async Task ThreeRejectionsLastInQueue_AppliedBefore_Error()
    {
        // П.1, путь по умолчанию (пустой список — регрессия 2e13d45c): третий отказ подряд
        // «модель недоступна» пришёлся последним чатом очереди — следующей выдачи нет, и
        // CheckStop своего error не называет. До правки: итог completed с применённым
        // пакетом — дата закрылась при мёртвом маршруте. Теперь: живых исполнителей к концу
        // прохода нет — error, дату не закрывает. Мутация «убрать пост-проверку живых
        // исполнителей» → последний assert красный.
        Configure();
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);
        AddChat("c3", SectionC, ageMinutes: 30);
        AddChat("c4", SectionD, ageMinutes: 40);

        await Tick(Sut((owner, chat, engine, ct) =>
        {
            lock (_calls) _calls.Add((chat, engine));
            return chat == "c1"
                ? Task.FromResult(new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeApplied, null, ["p1"]))
                : Task.FromResult(DownReject());
        }));

        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeError, "третий отказ — последний чат: живых исполнителей к концу прохода нет");
        last.Applied.Should().Be(1, "применённый раньше пакет не делает мёртвую ночь completed");
        last.Analyzed.Should().Be(4);
        last.Rejected.Should().Be(3);
    }

    [Fact]
    public async Task AllExecutorsDieOnLastChat_CoordinatorError()
    {
        // П.1, та же дыра в координаторе (stop ?? Completed): третий отказ подряд каждого
        // исполнителя пришёлся на последний чат — очередь пуста, и следующей выдачи, которая
        // назвала бы error, нет. Порядок держится гейтами: «gemma» держит свой Applied-чат,
        // пока «local» сжигает очередь (c2, c3, c4 — три отказа, выбывание), и только потом
        // берёт возвращённых сам — и тоже выбывает на последнем. Мутация «убрать пост-проверку
        // живых исполнителей» → первый assert красный: completed при двух мёртвых движках.
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 1), new LocalEngineExecutor("gemma", "Gemma", 1)];
        AddChat("c0", SectionA, ageMinutes: 5);
        AddChat("c1", SectionB, ageMinutes: 10);
        AddChat("c2", SectionC, ageMinutes: 20);
        AddChat("c3", SectionD, ageMinutes: 30);
        AddChat("c4", SectionE, ageMinutes: 40);
        var g1 = NewGate();

        var tick = Tick(Sut(async (owner, chat, engine, ct) =>
        {
            lock (_calls) _calls.Add((chat, engine));
            EnteredOf(chat).TrySetResult();
            if (chat == "c1" && engine == "gemma") await g1.Task.ConfigureAwait(false);
            return engine == "local" && chat == "c0"
                ? new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeApplied, null, ["p0"])
                : engine == "gemma" && chat == "c1"
                    ? new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeApplied, null, ["p1"])
                    : new WikiChatIngestService.ChatResult(
                        WikiChatIngestService.OutcomeRejected,
                        "разбор окна m1-m8 не удался: синтез: " + WikiIngestService.ReasonLocalUnavailable, [],
                        InputTokens: 0);
        }));

        await GateOpened(EnteredOf("c3")); // «local» отказал c2 и c3; c4 добьёт его до выбывания
        g1.SetResult(); // «gemma» доигрывает свой и берёт возвращённых: оба движка мертвоют на последнем чате
        await tick;

        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeError, "к концу прохода живых исполнителей нет: дату не закрывает");
        last.Applied.Should().Be(2, "применённое раньше — не причина звать мёртвую ночь completed");
        last.Rejected.Should().Be(6, "c2, c3, c4 — по одному повтору на другом исполнителе");
    }

    // Чат с баллом в серой зоне (та же фикстура, что серые тесты WikiNightlyServiceTests):
    // ступень 2 спрашивается на подходе очереди, вердикт пишется в стор — но снимок entries
    // взят до прохода и его не видит.
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

    [Fact]
    public async Task GrayChatRetriedOnOther_Stage2AskedOnce()
    {
        // П.2: серый чат судит ступень 2 («да»), «local» его отказывает, повтор идёт на
        // «gemma». Без проверки repeat'а ступень 2 читала бы снимок entries (вердикта в нём
        // нет) и звала бы модель второй раз — второе решение по одному чату и удвоенная
        // оценка ступени 2 в est. Один чат в фикстуре: с двумя первый слот local успевал
        // занять другой чат, и повтор строился бы не на сером. Мутации: «ступень 2 и при
        // repeat» → второй assert красный (модель звана дважды), пятый красный (est удвоена).
        Configure();
        _llm.AnswerFor = a => a == LocalActionCatalog.WikiTriage
            ? """{"hasKnowledge":true,"about":"a","reason":"r"}"""
            : "{}";
        _llm.Executors = [new LocalEngineExecutor("local", "local", 1), new LocalEngineExecutor("gemma", "Gemma", 1)];
        AddChat("g1", SectionA, ageMinutes: 10, important: false, history: Gray());

        await Tick(Sut(DownForChat(["g1|local"])));

        _calls.Count(c => c.Chat == "g1").Should().Be(2, "испытание строит повтор: без него тест пустой");
        _calls.Should().Contain(c => c.Chat == "g1" && c.Engine == "gemma");
        _llm.ModelCalls.Count(c => c.ActionKey == LocalActionCatalog.WikiTriage)
            .Should().Be(1, "повторный чат ступень 2 второй раз не спрашивает");
        // Оценка ночи = оценка выбранного чата + одна оценка ступени 2. При повторном вызове
        // ступени 2 (мутация) сумма была бы больше ровно на stage2Est.
        static long chatEst(List<StoredMessage> h) =>
            (WikiChatSlicer.Lines(h, 1, h.Count).Sum(l => (long)l.Text.Length) + 3) / 4;
        var stage2Est = (WikiChatStage2.MaxInputChars + WikiChatStage2.PromptHeader.Length) / 4;
        var last = Last();
        last.EstTokens.Should().Be(chatEst(Gray()) + stage2Est, "оценка ступени 2 посчитана один раз");
        last.Selected.Should().Be(1);
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
    }

    [Fact]
    public async Task NoPlaceRouteWithNamedExecutors_NightGoesOnList()
    {
        // П.4: маршрут места не настроен (HasRoute — false), но список исполнителей задан:
        // ночь идёт по списку — отсутствие маршрута места не гасит живой именованный движок.
        // До правки CheckStop требовал HasRoute при любом списке → error при живом движке,
        // хотя UI говорит «ночь идёт по списку исполнителей». Мутация «проверять маршрут
        // независимо от списка» → последний assert красный. Путь без исполнителей и без
        // маршрута остаётся error — это существующий тест ОшибкаПрохода… в WikiNightlyNotifyTests.
        Configure();
        _llm.Route = false;
        _llm.Executors = [new LocalEngineExecutor("gemma", "Gemma", 1)];
        AddChat("c1", SectionA, ageMinutes: 10);

        await Tick(Sut(DownFor()));

        _calls.Should().ContainSingle(c => c.Chat == "c1" && c.Engine == "gemma");
        Last().Outcome.Should().Be(WikiNightlyService.OutcomeCompleted, "живой именованный исполнитель, список задан — ночь идёт");
    }

    [Fact]
    public async Task SlotRefusal_RetriedOnOtherOnce_ExecutorNotWithdrawn()
    {
        // П.3: «слот движка занят» (нулевой вход, причина LocalEngineSlotRefusalException):
        // чат не сгорает на занятом движке — он отдаётся ровно один раз другому; отказавший
        // не выбывает (ряда нет) и свой чат второй раз не берёт; повтор больше одного не
        // повторяется. В итоге это обычный отказ. Мутации: «не повторять при отказе слота» →
        // первый assert красный (c1 не добран); «включить в ряд выбывания» → предпоследний
        // assert красный (error вместо completed).
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 1), new LocalEngineExecutor("gemma", "Gemma", 1)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);

        await Tick(Sut(async (owner, chat, engine, ct) =>
        {
            lock (_calls) _calls.Add((chat, engine));
            return engine == "local" && chat == "c1"
                ? new WikiChatIngestService.ChatResult(
                    WikiChatIngestService.OutcomeRejected,
                    "разбор окна m1-m8 не удался: " + new LocalEngineSlotRefusalException("local", LocalTurnRefusal.QueueFull, 4, 4).Message,
                    [], InputTokens: 0)
                : new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []);
        }));

        _calls.Count(c => c.Chat == "c1").Should().Be(2, "чат занятого слота добирается другим — ровно один раз");
        _calls.Should().Contain(c => c.Chat == "c1" && c.Engine == "gemma");
        _calls.Count(c => c.Engine == "local").Should().Be(1, "отказавший исполнитель возвращённый чат не берёт");
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted, "занятость — не смерть: ряд не растёт, ночь доиграна");
        last.Rejected.Should().Be(1, "отказ занятости — обычный отказ строки итога");
    }

    [Fact]
    public async Task SlotRefusal_RecognizedBySharedMarker_BothRefusalReasons()
    {
        // Опознание отказа держится общей метки LocalEngineSlotRefusalException.ReasonMarker, а
        // не текста теста: причина здесь собрана из самого исключения при обеих причинах отказа
        // («очередь полна» и «вышло время ожидания») — их сообщения различаются всем, кроме этой
        // метки, поэтому опознание не привязано к формулировке и правка её в Core не выключает
        // единственный повтор. Мутация «сравнивать IsSlotRefusalRejection по другой строке» →
        // оба первых assert красные: ни один чат не добран, итог error, а не completed.
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "local", 2), new LocalEngineExecutor("gemma", "Gemma", 1)];
        AddChat("c1", SectionA, ageMinutes: 10);
        AddChat("c2", SectionB, ageMinutes: 20);

        await Tick(Sut(async (owner, chat, engine, ct) =>
        {
            lock (_calls) _calls.Add((chat, engine));
            return engine == "local"
                ? new WikiChatIngestService.ChatResult(
                    WikiChatIngestService.OutcomeRejected,
                    "разбор окна m1-m8 не удался: " + new LocalEngineSlotRefusalException(
                        "local", chat == "c1" ? LocalTurnRefusal.QueueFull : LocalTurnRefusal.Timeout, 4, 4).Message,
                    [], InputTokens: 0)
                : new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []);
        }));

        _calls.Count(c => c.Chat == "c1").Should().Be(2, "отказ «очередь полна» опознан: чат добирает другой исполнитель");
        _calls.Count(c => c.Chat == "c2").Should().Be(2, "отказ «вышло время ожидания» опознан тем же признаком");
        _calls.Should().Contain(c => c.Chat == "c1" && c.Engine == "gemma");
        _calls.Should().Contain(c => c.Chat == "c2" && c.Engine == "gemma");
        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted, "занятость — не смерть: ряд не растёт, ночь доиграна");
        last.Rejected.Should().Be(2, "оба отказа — обычные отказы строки итога");
    }

    // ─── подмены ─────────────────────────────────────────────────────────────

    private sealed class Llm : ICheapTextRunner
    {
        public bool Route = true;
        // Список исполнителей места (W4a2): что вернул бы шов после урезания по maxTotal —
        // сюда его кладет тест. Пусто — прежний путь без исполнителей.
        public IReadOnlyList<LocalEngineExecutor> Executors = [];

        // Каждый вызов модели: метод шва, место, исполнитель (null — не путь названного движка),
        // промпт. На этом журнале держится путь 10: судья обязана зваться методом места, а не
        // методом исполнителя.
        public List<(string Method, string ActionKey, string? Engine, string Prompt)> ModelCalls { get; } = [];

        // Ответ судье: пустой список вердиктов — вызов состоялся, вердиктов нет (их разбор —
        // не предмет этого теста).
        public string JudgeAnswer = """{"verdicts":[]}""";
        // Ответ по месту (серый тест ступени 2): null — прежний "{}"; иначе — что вернуть
        // вызову этого actionKey.
        public Func<string, string>? AnswerFor;

        public bool UsesLocal(string actionKey) => true;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => Route;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        private void Note(string method, string actionKey, string? engine, string prompt)
        {
            lock (ModelCalls) ModelCalls.Add((method, actionKey, engine, prompt));
        }

        private string Answer(string actionKey) =>
            actionKey == LocalActionCatalog.WikiJudge ? JudgeAnswer : AnswerFor?.Invoke(actionKey) ?? "{}";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Note("RunAsync", actionKey, null, prompt);
            return Task.FromResult(Answer(actionKey));
        }

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            Note("RunLocalOrChosen", actionKey, null, prompt);
            return Task.FromResult<string?>(Answer(actionKey));
        }

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null, object? jsonFormat = null,
            CancellationToken ct = default)
        {
            Note("RunDetailed", actionKey, null, prompt);
            return Task.FromResult(new OneShotResult(Answer(actionKey), null, 0));
        }

        public Task<OneShotResult?> RunLocalOrChosenDetailedAsync(string actionKey, string prompt,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Note("RunLocalOrChosenDetailed", actionKey, null, prompt);
            return Task.FromResult<OneShotResult?>(new OneShotResult(Answer(actionKey), null, 0));
        }

        // Путь названного движка (W3/И3): здесь он регистрируется, чтобы путь 10 мог сказать,
        // что судья в него не попала. Ответ — тот же пустой JSON: эти тесты не судят содержание.
        public Task<OneShotResult?> RunOnLocalEngineDetailedAsync(string engine, string actionKey,
            string prompt, string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Note("RunOnLocalEngine", actionKey, engine, prompt);
            return Task.FromResult<OneShotResult?>(new OneShotResult(Answer(actionKey), null, 0));
        }

        public IReadOnlyList<LocalEngineExecutor> LocalExecutorsFor(string actionKey) =>
            actionKey == LocalActionCatalog.WikiNightly ? Executors : [];

        // Готовность движка (W5): null — готовы все (живой движок без ReadyUrl: «считаем
        // готовым, решит сам вызов»); непустое — только названные, пустое — никто не готов.
        // Проба вызывается перед каждой выдачей чата, поэтому её журнал нужен тесту:
        // «неготового пропускают» проверяется тем, что его спросили и чата ему не дали.
        public HashSet<string>? ReadyEngines;
        public List<string> ReadyChecks { get; } = [];
        // Проба по названным движкам бросает вместо ответа (R-W5, п.2): «проба не удалась».
        public HashSet<string>? ReadyThrows;

        public Task<bool> IsLocalEngineReadyAsync(string engine, CancellationToken ct = default)
        {
            lock (ReadyChecks) ReadyChecks.Add(engine);
            if (ReadyThrows?.Contains(engine) == true)
                throw new InvalidOperationException($"проба готовности движка «{engine}» не удалась");
            return Task.FromResult(ReadyEngines is null || ReadyEngines.Contains(engine));
        }
    }

    private sealed class Sessions : ISessionDirectory
    {
        public readonly List<Session> All = [];
        public readonly Dictionary<string, List<StoredMessage>> History = [];

        // История чата бросает, если предикат сказал «да»: так тест ставит отказ подготовки
        // (prepare) в нужный момент — предикат знает состояние теста (например, «первый чат уже
        // в полёте»). Пересчёт отбора тоже читает истории: он обязан отказа не получить.
        public Func<string, bool>? ThrowHistoryWhen;
        public TaskCompletionSource? HistoryThrew;

        public Session? GetById(string id) => All.FirstOrDefault(s => s.Id == id);
        public IReadOnlyCollection<Session> GetAll() => All;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId)
        {
            if (ThrowHistoryWhen?.Invoke(sessionId) == true)
            {
                HistoryThrew?.TrySetResult();
                throw new InvalidOperationException("история чата не читается");
            }
            return Task.FromResult<IReadOnlyList<StoredMessage>>(History.TryGetValue(sessionId, out var h) ? h : []);
        }
        public bool SetWikiMark(string sessionId, string? mark) => true;
    }

    private sealed class NoSpheres : ClaudeHomeServer.Services.Spheres.ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    private sealed class Flag : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool On = true;

        public bool IsEnabled(string userId, string key) => On && key == FeatureFlagKeys.LlmWiki;
    }
}
