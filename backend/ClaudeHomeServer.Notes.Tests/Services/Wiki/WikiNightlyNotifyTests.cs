using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Утреннее уведомление ночного разбора (Ф2, N7c). Отделён от WikiNightlyServiceTests (там идёт
// доработка N7b): фикстуры те же, проверяется только конец прохода — сообщение владельцу.
//
// Контракт: ровно одно уведомление за проход с человеческим текстом. Заголовок (сводка J-J4) —
// «Ночной разбор: применено N; судья принял K, отклонил L; ждут M; <цена>»; цена — форма витрины
// (доработка QA, пункт 2): два знака после точки, непустая сумма меньше цента — «<$0.01», ноль —
// «$0.00» (точной цены в уведомлении нет: она остаётся в строке журнала); в теле — сколько пакетов
// ещё ждёт решения человека («Во «Входящих» …», перенесено из заголовка), почему судья не ответил,
// если не ответил («судья недоступен — ждут решения: N»), сколько операций закрыл отказом сам код
// («отклонено кодом: N», в заголовке их нет), и названием пути «Вики → Журнал» (Url ведёт во
// вкладку «Вики», обещания перехода в тексте нет); при исходе, отличном от обычного, — причина
// простым языком. Молчание: analyzed=0, completed, судья модель не звал, ничего не отложил И код
// ничего не отказал (вынесенные вердикты, отложенные операции и отказы кода — не молчание, хвост 1,
// хвост 4 и доработка QA J-J4); флаг/Enabled
// выключен (там и итога нет). Сбой
// доставки: предупреждение в журнале, итог ночи уже записан, исключение не выходит из службы.
//
// С W6a — итог по исполнителям: счётчики по движкам в файле итога (Executors) и в строке
// журнала («исполнители local 3/3 выбыл 04:00; gemma 4/0»), и одно предложение о выбывшем в
// том же теле уведомления — второго сообщения на проход по-прежнему нет.
public class WikiNightlyNotifyTests : IDisposable
{
    private const string User = "u1";
    private const string Section = "projects/p1";
    private const string PagePath = "Проекты/p1/Сущность.md";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly Sessions _sessions = new();
    private readonly Llm _llm = new();
    private readonly Flag _flag = new();
    private DateTimeOffset _now = At(4);
    // Гейт входа чата в разбор (тот же, что у WikiNightlyParallelTests): при двух исполнителях
    // порядок выдачи держится на том, какой слот сейчас в полёте.
    private readonly Dictionary<string, TaskCompletionSource> _entered = new(StringComparer.Ordinal);

    public WikiNightlyNotifyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_nightly_notify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(projects, _config, NullLogger<NotesService>.Instance, null, flags: _flag);
        _keeper = new WikiKeeper(_notes, _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        _keeper.WritePageAsync(User, Section, PagePath, "---\ntype: topic\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n# Сущность\n\n## Сводка\n\n## Хронология\n\n## Противоречия\n\n## История\n")
            .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static DateTimeOffset At(int hour, int day = 2) => new(2026, 11, day, hour, 0, 0, TimeSpan.Zero);
    private string Root(string owner = User) => WikiPaths.Root(_dir, owner);
    private string LastFile(string owner = User) => Path.Combine(Root(owner), "keeper", "nightly-last.json");

    // ─── фикстуры (те же, что у WikiNightlyServiceTests) ────────────────────

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

    private void AddChat(string id, int humans = 4, string? mark = WikiMarks.Important, string project = "p1",
        int ageMinutes = 0)
    {
        var s = new Session
        {
            Id = id,
            OwnerId = User,
            ProjectId = project,
            Name = id,
            WikiMark = mark,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-ageMinutes),
        };
        _sessions.All.Add(s);
        _sessions.History[id] = Hist(humans);
    }

    // log обязателен: строка итога — часть контракта конца прохода (W6a), а собирающий лог и
    // NullLogger для службы — один и тот же NullLogger, существующие тесты не меняются.
    private WikiNightlyService Sut(
        Func<string, string, string?, CancellationToken, Task<WikiChatIngestService.ChatResult>>? analyze,
        INotificationSender? notifier, RecordingLog log,
        Func<string, string, IReadOnlySet<string>?, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>>? judge = null) =>
        new(_sessions, new WikiChatRecalc(_sessions, _config), new WikiChatStage2(_llm, _sessions, _config),
            analyze ?? Done(), _llm, _config, log, _flag, () => _now,
            notifier, _keeper, judge);

    // Фейк прохода судьи (J-J4): сводка в уведомлении проверяется без облачной модели.
    // deferredCause (S4) — категория причины отложенных: по умолчанию обычный отказ.
    private static Func<string, string, IReadOnlySet<string>?, CancellationToken, Task<WikiJudgeSweep.JudgeSweepResult>> Judge(
        int applied = 0, int rejected = 0, int deferred = 0, int codeRejected = 0, int calls = 0, decimal cost = 0m,
        WikiJudgeSweep.DeferredCause deferredCause = WikiJudgeSweep.DeferredCause.None) =>
        (owner, root, _, ct) => Task.FromResult(new WikiJudgeSweep.JudgeSweepResult(
            calls, cost, applied, rejected, deferred, codeRejected, null, deferredCause));

    private static Func<string, string, string?, CancellationToken, Task<WikiChatIngestService.ChatResult>> Done() =>
        (owner, chat, _, ct) => Task.FromResult(new WikiChatIngestService.ChatResult(
            WikiChatIngestService.OutcomeNothingNew, null, []));

    private Task Tick(WikiNightlyService sut) => sut.RunNightTickAsync(_now);

    private WikiNightlyService.NightResult Last() => WikiNightlyService.ReadLast(Root()).Should().NotBeNull().And.Subject
        as WikiNightlyService.NightResult ?? throw new InvalidOperationException();

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
    }

    // ─── c1: применено 3, во «Входящих» 1 → ровно одно уведомление ──────────

    [Fact]
    public async Task ПроходПрименено3ИОдноВоВходящих_РовноОдноУведомлениеСЧислами()
    {
        Configure();
        AddChat("c1", ageMinutes: 0);
        AddChat("c2", ageMinutes: 1);
        var notifier = new Notifier();
        Task<WikiChatIngestService.ChatResult> Analyze(string owner, string chat, string? engine, CancellationToken ct) => chat switch
        {
            "c1" => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeApplied, null, ["pkg-1", "pkg-2", "pkg-3"])),
            "c2" => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeAwaitingHuman, null, [])),
            _ => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeNothingNew, null, [])),
        };

        await Tick(Sut(Analyze, notifier, new RecordingLog()));

        var last = Last();
        last.Applied.Should().Be(3);
        last.ToInbox.Should().Be(1);
        notifier.Sent.Should().HaveCount(1, "уведомление — одно на проход");
        var n = notifier.Sent[0];
        n.Title.Should().Contain("применено 3");
        // J-J4: «во «Входящих»» из заголовка перенесено в тело — заголовок несёт сводку судьи
        // одной строкой чисел, и её нулевые значения тоже называются. Нулевая цена — «$0.00»
        // (форма витрины, доработка QA, пункт 2), а не «$0».
        n.Title.Should().Contain("судья принял 0, отклонил 0; ждут 0; $0.00");
        n.Body.Should().Contain("Во «Входящих» 1 пакет ждёт решения.");
        // Путь назван текстом, а не обещан переходом: Url ведёт во вкладку «Вики» (подкладка
        // «Журнал» deep-link не имеет), текст — «во вкладке «Вики» → «Журнал»».
        n.Url.Should().Be("#/wiki");
        n.Body.Should().Contain("во вкладке «Вики» → «Журнал»");
        n.Body.Should().NotContain("в «Журнале»", "текст не обещает перехода, которого Url не делает");
        n.Kind.Should().Be("info", "обычный исход — не предупреждение");
    }

    // ─── J-J4 (c2): сводка судьи — заголовок; «судья недоступен» — тело ─────────

    [Fact]
    public async Task СудьяПринял1Отклонил1За004_ЗаголовокРавенСводке_СтрокиОСудьеВТелеНет()
    {
        // Мутации: «судью — в тело, а не в заголовок» → Be() красный; «не печатать стоимость» →
        // Be() красный; «дописывать строку о судье при нуле» → NotContain красный.
        Configure();
        AddChat("c1", ageMinutes: 0);
        AddChat("c2", ageMinutes: 1);
        var notifier = new Notifier();
        Task<WikiChatIngestService.ChatResult> Analyze(string owner, string chat, string? engine, CancellationToken ct) => chat switch
        {
            "c1" => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeApplied, null, ["pkg-1", "pkg-2"])),
            _ => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeNothingNew, null, [])),
        };

        await Tick(Sut(Analyze, notifier, new RecordingLog(), judge: Judge(applied: 1, rejected: 1, calls: 2, cost: 0.04m)));

        var n = notifier.Sent[0];
        n.Title.Should().Be("Ночной разбор: применено 2; судья принял 1, отклонил 1; ждут 0; $0.04");
        n.Body.Should().NotContain("судья недоступен", "отложенных операций нет — строки о них нет");
        n.Body.Should().NotContain("отклонено кодом", "отказов кода нет — нулевой строки в теле нет");
        n.Kind.Should().Be("info", "судья отложил вердикты, а не отказ — это не предупреждение");
    }

    [Theory]
    [InlineData(0.0067d, "<$0.01")]
    [InlineData(0.04d, "$0.04")]
    [InlineData(0.0d, "$0.00")]
    public async Task ЦенаНочиВУведомлении_ДваЗнака_МеньшеЦентаСоЗнакомМеньше(double cost, string expected)
    {
        // Доработка QA (пункт 2, правило Софьи): цена в заголовке — форма витрины: два знака
        // после точки; непустая сумма меньше цента — «<$0.01» (вызов модели
        // был, а «$0.00» об этом врёт); ноль — «$0.00». Старой инвариантной записи («$0.0067»)
        // в уведомлении быть не должно: она остаётся в строке журнала. Мутация «CostText вместо
        // NoticeCostText» — строки 0.0067 и 0.0 краснеют (0.04 даёт ту же запись, она форму не
        // различает).
        Configure();
        AddChat("c1", ageMinutes: 0);
        var notifier = new Notifier();
        Task<WikiChatIngestService.ChatResult> Analyze(string owner, string chat, string? engine, CancellationToken ct) =>
            Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeApplied, null, ["pkg-1"]));

        await Tick(Sut(Analyze, notifier, new RecordingLog(), judge: Judge(applied: 1, calls: 1, cost: (decimal)cost)));

        notifier.Sent.Should().HaveCount(1);
        notifier.Sent[0].Title.Should().Be(
            $"Ночной разбор: применено 1; судья принял 1, отклонил 0; ждут 0; {expected}");
    }

    [Fact]
    public async Task СудьяНеВынесВердикитаПоТрёмОперациям_ТелоНазываетЭтоПрямо()
    {
        // Хвост 2 (решение Софья, 12.11): форма без согласования с числом — «судья недоступен —
        // ждут решения: 3»; старой «3 операций ждут» в теле быть не должно.
        Configure();
        AddChat("c1", ageMinutes: 0);
        var notifier = new Notifier();

        await Tick(Sut(null, notifier, new RecordingLog(), judge: Judge(deferred: 3, calls: 1)));

        var n = notifier.Sent[0];
        n.Title.Should().Contain("ждут 3");
        n.Body.Should().Contain("судья недоступен — ждут решения: 3");
        n.Body.Should().NotContain("операций ждут", "старой формы с согласованием числа в тексте нет");
    }

    // ─── S4: заморозка среды — итог ночи называет причину и число отложенных ────────────

    [Fact]
    public async Task ЗаморозкаСреды_ИтогНочиНазываетПричинуИЧислоОтложенных()
    {
        // Инцидент tb21: ночь с замороженным срезом изоляции откладывала решения судьи, а
        // владелец читал «судья недоступен» — прямую неправду. Категория доехала из прохода
        // судьи в итог: тело называет причину («среда исполнения была заморожена») и
        // существующее число JudgeDeferred; прежней строки «судья недоступен» в этом случае
        // нет. Мутация «NotificationBody игнорирует execution_frozen» (всегда прежняя
        // строка) краснит оба assert о теле.
        Configure();
        AddChat("c1", humans: 2);
        var notifier = new Notifier();

        await Tick(Sut(null, notifier, new RecordingLog(),
            judge: Judge(deferred: 5, calls: 2, deferredCause: WikiJudgeSweep.DeferredCause.ExecutionFrozen)));

        var last = Last();
        last.Analyzed.Should().Be(0);
        last.JudgeDeferred.Should().Be(5);
        last.JudgeDeferredCause.Should().Be(WikiJudgeSweep.DeferredCauseExecutionFrozen,
            "категория записана в файл итога ночи");
        notifier.Sent.Should().HaveCount(1, "отложенные операции — не молчание");
        var body = notifier.Sent[0].Body;
        body.Should().Contain("среда исполнения была заморожена — ждут решения: 5");
        body.Should().NotContain("судья недоступен", "причина названа точно: это не молчаливая недоступность судьи");
    }

    [Fact]
    public async Task КодОтклонилДвеОперацииБезСудьи_ТелоНазываетОтказы()
    {
        // Хвост 3: форма заголовка задана c2 и отказов кода не несёт — без строки в теле владелец
        // не видит, что часть пакета закрыта не судьёй. Нулевой случай (строки нет) проверяет
        // NotContain в тесте «СудьяПринял1Отклонил1…».
        Configure();
        AddChat("c1", ageMinutes: 0);
        var notifier = new Notifier();

        await Tick(Sut(null, notifier, new RecordingLog(), judge: Judge(codeRejected: 2, calls: 1)));

        var n = notifier.Sent[0];
        Last().CodeRejected.Should().Be(2);
        n.Body.Should().Contain("отклонено кодом: 2");
    }

    // ─── B4b (ADR-038, предохранитель 6): сводные строки о человеке — в то же уведомление ───

    [Fact]
    public async Task ЗамениИИФормулировкиЧеловекаВДвухЧатах_ВУведомленииОднаСводнаяСтрока_СЧислом()
    {
        // Мутация «строка на каждый чат» (дописывать строку внутри цикла разборов): две строки
        // «ИИ заменил…» — Be(1) по числу вхождений краснеет. Мутация «не суммировать» (брать
        // последний чат): «5 ваших формулировок» краснеет. Страница b оспорена в обоих чатах — это
        // одна страница, не две: «на 2 страницах».
        Configure();
        AddChat("c1", ageMinutes: 0);
        AddChat("c2", ageMinutes: 1);
        var notifier = new Notifier();
        Task<WikiChatIngestService.ChatResult> Analyze(string owner, string chat, string? engine, CancellationToken ct) => chat switch
        {
            "c1" => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeApplied, null, ["pkg-1"], OverrodeHuman: 2, DisputedPages: ["a.md", "b.md"])),
            "c2" => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeApplied, null, ["pkg-2"], OverrodeHuman: 3, DisputedPages: ["b.md"])),
            _ => Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeNothingNew, null, [])),
        };

        await Tick(Sut(Analyze, notifier, new RecordingLog()));

        notifier.Sent.Should().HaveCount(1, "второго уведомления нет");
        var body = notifier.Sent[0].Body;
        body.Should().Contain("ИИ заменил 5 ваших формулировок.");
        body.Should().Contain("ИИ отметил противоречие на 2 страницах.");
        System.Text.RegularExpressions.Regex.Matches(body, "ИИ заменил").Count.Should().Be(1, "одна сводная строка за ночь");
        Last().OverrodeHuman.Should().Be(5);
        Last().DisputedPages.Should().Be(2);
    }

    [Fact]
    public async Task ЧеловекаНеТрогали_СтрокиОНёмВУведомленииНет()
    {
        Configure();
        AddChat("c1");
        var notifier = new Notifier();
        Task<WikiChatIngestService.ChatResult> Analyze(string owner, string chat, string? engine, CancellationToken ct) =>
            Task.FromResult(new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeApplied, null, ["pkg-1"]));

        await Tick(Sut(Analyze, notifier, new RecordingLog()));

        notifier.Sent[0].Body.Should().NotContain("ИИ заменил").And.NotContain("противоречие");
    }

    [Theory]
    [InlineData(1, "1 вашу формулировку", "1 странице")]
    [InlineData(2, "2 ваши формулировки", "2 страницах")]
    [InlineData(5, "5 ваших формулировок", "5 страницах")]
    [InlineData(11, "11 ваших формулировок", "11 страницах")]
    [InlineData(21, "21 вашу формулировку", "21 странице")]
    public void NotificationBody_ЧислоСклоняется(int n, string replaced, string pages)
    {
        var body = WikiNightlyService.NotificationBody(null, n, n, 0, 0, 0);

        body.Should().Contain($"ИИ заменил {replaced}.");
        body.Should().Contain($"ИИ отметил противоречие на {pages}.");
    }

    // ─── c2: молчание ────────────────────────────────────────────────────────

    [Fact]
    public async Task НичегоНеРазобраноИНочьОбычная_УведомленийНет()
    {
        // Дельта 2 реплики — чат не отобран: проход обычный, analyzed=0. Мутация «слать всегда»
        // (убрать проверку Analyzed==0 && Completed) — этот тест краснеет.
        Configure();
        AddChat("c1", humans: 2);
        var notifier = new Notifier();

        await Tick(Sut(null, notifier, new RecordingLog()));

        Last().Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
        Last().Analyzed.Should().Be(0);
        notifier.Sent.Should().BeEmpty("нечего сказать: ни разборов, ни отказа");
    }

    [Fact]
    public async Task НичегоНеРазобраноСудьяОтложилДве_УведомлениеНеМолчит()
    {
        // Хвост 1: та же ночь с analyzed=0 и обычным исходом, но судья отложил спорные операции
        // дневной заявки — пакет застрял, и владелец должен это видеть. Мутация «вернуть старое
        // условие молчания» (без result.JudgeDeferred == 0) — этот тест краснеет: Sent пусто.
        Configure();
        AddChat("c1", humans: 2);
        var notifier = new Notifier();

        await Tick(Sut(null, notifier, new RecordingLog(), judge: Judge(deferred: 2, calls: 1)));

        var last = Last();
        last.Analyzed.Should().Be(0);
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
        last.JudgeDeferred.Should().Be(2);
        notifier.Sent.Should().HaveCount(1, "отложенные судьёй операции — не молчание");
        notifier.Sent[0].Body.Should().Contain("судья недоступен — ждут решения: 2");
    }

    [Fact]
    public async Task НичегоНеРазобраноКодОтклонилДвеБезСудьи_УведомлениеНеМолчит()
    {
        // Хвост 4 (решение Софья, 12.11, пункт Глеба к fee95fb44): та же ночь с analyzed=0 и
        // обычным исходом, но судья модель не звал, а код сам отказал операции дневной заявки
        // (CodeRejected > 0, JudgeDeferred = 0). Пакет закрыт не судьёй — владелец обязан это
        // увидеть. Мутация «вернуть старое условие молчания» (без result.CodeRejected == 0) —
        // этот тест краснеет: Sent пусто.
        Configure();
        AddChat("c1", humans: 2);
        var notifier = new Notifier();

        await Tick(Sut(null, notifier, new RecordingLog(), judge: Judge(codeRejected: 2, calls: 1)));

        var last = Last();
        last.Analyzed.Should().Be(0);
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
        last.JudgeDeferred.Should().Be(0);
        last.CodeRejected.Should().Be(2);
        notifier.Sent.Should().HaveCount(1, "отказы, вынесенные кодом, — не молчание");
        notifier.Sent[0].Body.Should().Contain("отклонено кодом: 2");
    }

    [Fact]
    public async Task НичегоНеРазобраноСудьяВынесДваВердикита_УведомлениеНеМолчит()
    {
        // Доработка QA (пункт 1, замечание Веры b0065470): та же ночь с analyzed=0 и обычным
        // исходом, но судья звал модель и вынес вердикты по спорным операциям дневной заявки
        // (ничего не отложено, код ничего не отказал) — раньше такая ночь молчала: ни вердиктов,
        // ни их цены владелец не видел. Мутация «вернуть старое условие молчания» (без
        // result.JudgeCalls == 0) — этот тест краснеет: Sent пусто.
        Configure();
        AddChat("c1", humans: 2);
        var notifier = new Notifier();

        await Tick(Sut(null, notifier, new RecordingLog(), judge: Judge(rejected: 2, calls: 2, cost: 0.04m)));

        var last = Last();
        last.Analyzed.Should().Be(0);
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
        last.JudgeCalls.Should().Be(2);
        last.JudgeRejected.Should().Be(2);
        last.JudgeDeferred.Should().Be(0);
        last.CodeRejected.Should().Be(0);
        notifier.Sent.Should().HaveCount(1, "вынесенные вердикты судьи — не молчание");
        notifier.Sent[0].Title.Should().Contain("судья принял 0, отклонил 2");
    }

    [Fact]
    public async Task ФлагВыключен_НиУведомленияНиИтога()
    {
        Configure();
        AddChat("c1");
        _flag.On = false;
        var notifier = new Notifier();

        await Tick(Sut(null, notifier, new RecordingLog()));

        notifier.Sent.Should().BeEmpty();
        File.Exists(LastFile()).Should().BeFalse("пропуск без записи — и без сообщения");
    }

    // ─── исход, отличный от обычного: причина простым языком ────────────────

    [Fact]
    public async Task УпорВЛимитОткрытых_УведомлениеСЧеловеческойПричиной()
    {
        Configure();
        AddChat("c1");
        AddOpenPackages(WikiPackage.MaxOpenPackages);
        var notifier = new Notifier();

        await Tick(Sut(null, notifier, new RecordingLog()));

        Last().Outcome.Should().Be(WikiNightlyService.OutcomeOpenLimit);
        notifier.Sent.Should().HaveCount(1);
        var n = notifier.Sent[0];
        n.Kind.Should().Be("warning", "прерванная ночь — предупреждение");
        n.Body.Should().Contain("лимит открытых пакетов");
        n.Body.Should().Contain("«Входящие»", "причина называет, что делать");
    }

    [Fact]
    public async Task ОшибкаПрохода_ПричинаГоворитВСледующуюНочь_БезВнутреннегоЖаргона()
    {
        // Маршрута места нет → outcome=error. Текст причины — человеческий: «в следующую ночь»,
        // а не «в следующий тик» (тик — внутренний жаргон службы).
        Configure();
        AddChat("c1");
        _llm.Route = false;
        var notifier = new Notifier();

        await Tick(Sut(null, notifier, new RecordingLog()));

        Last().Outcome.Should().Be(WikiNightlyService.OutcomeError);
        notifier.Sent.Should().HaveCount(1);
        var n = notifier.Sent[0];
        n.Kind.Should().Be("warning");
        n.Body.Should().Contain("в следующую ночь");
        n.Body.Should().NotContain("тик", "тик — внутренний жаргон, в тексте его быть не должно");
    }

    // ─── c4: сбой доставки не рвёт службу и не отменяет итог ────────────────

    [Fact]
    public async Task ОтправительБросает_СлужбаЖива_ИтогЗаписан_ВЖурналеПредупреждение()
    {
        Configure();
        AddChat("c1");
        var log = new RecordingLog();

        var act = async () => await Tick(Sut(null, new ThrowingNotifier(), log));

        await act.Should().NotThrowAsync("исключение отправителя не выходит из службы");

        Last().Outcome.Should().Be(WikiNightlyService.OutcomeCompleted,
            "итог записан до попытки доставки: сбой сообщения его не отменяет");
        log.Rows.Should().Contain(r => r.Level == LogLevel.Warning && r.Message.Contains("не доставлено"),
            "сбой доставки назван в журнале — молчания нет");
    }

    // ─── W6a: итог по исполнителям — строка журнала, файл итога, уведомление ───────

    // Время прохода с МЕСТНЫМ смещением машины: «выбыл HH:mm» — wall-clock того, что назвал
    // тесту часовой. At() — UTC-anchored: на машине не UTC его wall-clock был бы другим, и
    // строка журнала проверялась бы по-разному дома и на CI.
    private static DateTimeOffset AtLocal(int hour, int minute = 0) =>
        new(2026, 11, 2, hour, minute, 0, DateTimeOffset.Now.Offset);

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource EnteredOf(string chat)
    {
        lock (_entered)
        {
            if (!_entered.TryGetValue(chat, out var t)) _entered[chat] = t = NewGate();
            return t;
        }
    }

    private static async Task GateOpened(TaskCompletionSource entered) =>
        (await Task.WhenAny(entered.Task, Task.Delay(5000))).Should().BeSameAs(entered.Task, "чат не вошёл в разбор");

    /// <summary>Образец прохода с двумя исполнителями (тот же, что ThreeRejections_… в
    /// WikiNightlyParallelTests): «local» мёртв — три отказа подряд, он выбыл; «gemma» держит
    /// свой чат в полёте, пока local сжигает очередь, и доигрывает её: возвращённые чаты
    /// (c1, c3, c4) берутся на живом. Порядок выдачи в этой конфигурации детерминирован и
    /// зафиксирован прогоном тех тестов.</summary>
    private void TwoExecutorsDeadLocal()
    {
        _llm.Executors = [
            new LocalEngineExecutor("local", "Qwen 32B", 1),
            new LocalEngineExecutor("gemma", "Gemma", 1),
        ];
        AddChat("c1", ageMinutes: 10);
        AddChat("c2", ageMinutes: 20, project: "p2");
        AddChat("c3", ageMinutes: 30, project: "p3");
        AddChat("c4", ageMinutes: 40, project: "p4");
    }

    private Func<string, string, string?, CancellationToken, Task<WikiChatIngestService.ChatResult>>
        DeadLocalLiveGemma(TaskCompletionSource holdGemma) =>
        async (owner, chat, engine, ct) =>
        {
            EnteredOf(chat).TrySetResult();
            if (chat == "c2") await holdGemma.Task.ConfigureAwait(false); // gemma держит свой чат: новые берутся на local
            return engine == "local"
                ? new WikiChatIngestService.ChatResult(
                    WikiChatIngestService.OutcomeRejected,
                    "разбор окна m1-m8 не удался: синтез: " + WikiIngestService.ReasonLocalUnavailable, [],
                    InputTokens: 0)
                : new WikiChatIngestService.ChatResult(
                    WikiChatIngestService.OutcomeApplied, null, ["pkg-" + chat], InputTokens: 300);
        };

    private string SummaryLine(RecordingLog log) =>
        log.Rows.Single(r => r.Message.StartsWith("Ночь вики")).Message;

    [Fact]
    public async Task Summary_OneLine_WithExecutors()
    {
        // Инвариант: одну строку итога пишет координатор после WhenAll. Мутация «слот пишет свою
        // строку итога» (LogNightSummary из RunSlotAsync после apply): при двух исполнителях в
        // журнале две строки итога — HaveCount(1) краснеет.
        Configure();
        _now = AtLocal(4);
        TwoExecutorsDeadLocal();
        var g2 = NewGate();
        var log = new RecordingLog();
        var tick = Tick(Sut(DeadLocalLiveGemma(g2), new Notifier(), log));
        await GateOpened(EnteredOf("c4")); // local отказал c1, c3, c4 — выбыл; чат gemma ещё в полёте
        g2.SetResult();
        await tick;

        log.Rows.Where(r => r.Message.StartsWith("Ночь вики")).Should().HaveCount(1,
            "одна строка итога на проход: её пишет координатор, а не слоты");
        SummaryLine(log).Should().Contain("исполнители local 3/3 выбыл 04:00; gemma 4/0",
            "по движку — его разборы и его отказы; при выбывании — время");

        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted, "живой исполнитель доиграл проход");
        last.Executors.Should().NotBeNull();
        last.Executors!.Select(e => e.Engine).Should().Equal(new[] { "local", "gemma" },
            "порядок — как список исполнителей стора");
        last.Executors![0].Analyzed.Should().Be(3);
        last.Executors![0].Rejected.Should().Be(3);
        last.Executors![0].InputTokens.Should().Be(0, "модель не звалась ни разу");
        last.Executors![0].WithdrawnAt.Should().NotBeNull();
        last.Executors![1].Analyzed.Should().Be(4);
        last.Executors![1].Rejected.Should().Be(0);
        last.Executors![1].InputTokens.Should().Be(1200);
        last.Executors![1].WithdrawnAt.Should().BeNull("живой исполнитель дожил до конца прохода");
    }

    [Fact]
    public async Task Summary_NoKnowledgeText()
    {
        // Инвариант строки итога и уведомления — он же для части «исполнители» (W6a): имена
        // движков и числа, текста знаний нет ни в маркере причины, ни в счётчиках.
        Configure();
        _llm.Executors = [new LocalEngineExecutor("local", "Qwen 32B", 1)];
        AddChat("c1", ageMinutes: 0);
        var log = new RecordingLog();
        var notifier = new Notifier();
        Task<WikiChatIngestService.ChatResult> Analyze(string owner, string chat, string? engine, CancellationToken ct) =>
            Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeRejected,
                "в разделе есть нарушения: Проекты/p1/Сущность.md: утверждение «Сущность — узел графа» без сноски", [],
                InputTokens: 40));

        await Tick(Sut(Analyze, notifier, log));

        var line = SummaryLine(log);
        line.Should().Contain("исполнители local 1/1");
        line.Should().NotContain("Сущность", "текста знаний в строке итога нет");
        line.Should().NotContain("без сноски", "текста знаний в строке итога нет");
        var body = notifier.Sent.Single().Body;
        body.Should().NotContain("Сущность").And.NotContain("без сноски", "текста знаний в уведомлении нет");
    }

    [Fact]
    public async Task ПутьБезИсполнителей_ExecutorsПуст_ЧастиОбИсполнителяхВСтрокеНет()
    {
        // Путь без названных исполнителей (пустой список стора — прежний маршрут места): имени
        // движка там нет (J-W2) — Executors пуст, части «исполнители …» в строке нет вовсе.
        Configure();
        AddChat("c1", ageMinutes: 0);
        var log = new RecordingLog();
        Task<WikiChatIngestService.ChatResult> Analyze(string owner, string chat, string? engine, CancellationToken ct) =>
            Task.FromResult(new WikiChatIngestService.ChatResult(
                WikiChatIngestService.OutcomeApplied, null, ["pkg-1"], InputTokens: 40));

        await Tick(Sut(Analyze, null, log));

        Last().Executors.Should().BeEmpty("исполнителей не было — пустой список, не запись пути места");
        SummaryLine(log).Should().NotContain("исполнители", "имени движка в этом пути нет");
    }

    [Fact]
    public async Task Notify_WithdrawnSentence()
    {
        // Одно предложение о выбывшем исполнителе — с числом попыток (хвост R-W6a): «Модель
        // {Title} сделала N попыток, выбыла в HH:mm, разбор продолжила {Title}» — Title из
        // настроек места («Qwen 32B»), технического ключа «local» в тексте владельцу нет.
        // Попытки названы явно: analyzed по движку с повтором — попытки, а не чаты.
        // Мутация «не передавать Executors в NotificationBody» — оба assert про предложение
        // красные; «не называть попытки» — первый assert красный.
        Configure();
        _now = AtLocal(4);
        TwoExecutorsDeadLocal();
        var g2 = NewGate();
        var notifier = new Notifier();
        var tick = Tick(Sut(DeadLocalLiveGemma(g2), notifier, new RecordingLog()));
        await GateOpened(EnteredOf("c4"));
        g2.SetResult();
        await tick;

        var last = Last();
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
        last.Executors!.Should().HaveCount(2);
        last.Executors![0].WithdrawnAt.Should().NotBeNull();

        notifier.Sent.Should().HaveCount(1, "уведомление одно на проход: фраза о выбывшем — внутри него");
        var body = notifier.Sent[0].Body;
        body.Should().Contain("Модель Qwen 32B сделала 3 попытки, выбыла в 04:00, разбор продолжила Gemma.",
            "имена — подписи движков из настроек места; число попыток названо явно");
        body.Should().NotContain("local", "технического ключа движка в тексте владельцу нет");
        System.Text.RegularExpressions.Regex.Matches(body, "выбыла в").Count.Should().Be(1,
            "одно предложение на каждого выбывшего, а не на каждый отказ");
    }

    [Fact]
    public void ReadLast_СтарыйФормат_БезExecutors_ЧитаетсяNull()
    {
        // Аддитивность формата nightly-last.json (W6a): итог, записанный до появления поля,
        // читается: Executors — null («не измерено»), а не пустой список и не отказ чтения.
        // Файл пишется через JsonOpts (camelCase) — старый итог в нём такой же формы, без поля.
        var json = """
            {"date":"2026-11-01","selected":2,"analyzed":2,"applied":1,"toInbox":0,"estTokens":120,
             "outcome":"completed","overrodeHuman":0,"disputedPages":0,"inputTokens":80,"rejected":1,
             "firstRejectReason":"плохой план","manual":false,"dropped":0,"firstDropReason":null}
            """;
        Directory.CreateDirectory(Path.GetDirectoryName(LastFile())!);
        File.WriteAllText(LastFile(), json);

        var last = WikiNightlyService.ReadLast(Root()).Should().NotBeNull().And.Subject
            as WikiNightlyService.NightResult;
        last!.Date.Should().Be("2026-11-01");
        last.Outcome.Should().Be(WikiNightlyService.OutcomeCompleted);
        last.Analyzed.Should().Be(2);
        last.Rejected.Should().Be(1);
        last.Executors.Should().BeNull("старого поля в файле нет: «не измерено», а не «пустой список»");
        last.JudgeDeferredCause.Should().BeNull("старого поля в файле нет: обычный отказ без категории (S4)");
    }

    // ─── подменные швы (те же, что у WikiNightlyServiceTests) ────────────────

    private static readonly string NothingJson =
        $$"""{"outcome":"{{WikiKeeperPrompts.NothingToAdd}}","reason":"не о чём писать"}""";

    private sealed class Llm : ICheapTextRunner
    {
        public readonly List<(string ActionKey, string Prompt)> Calls = [];
        public bool Route = true;
        // Исполнители места (W6a): что вернул бы шов после урезания по maxTotal — сюда его
        // кладет тест. Пусто — прежний путь без исполнителей (существующие тесты этого файла).
        public IReadOnlyList<LocalEngineExecutor> Executors = [];

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => Route;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add((actionKey, prompt));
            return Task.FromResult(NothingJson);
        }

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add((actionKey, prompt));
            return Task.FromResult<string?>(NothingJson);
        }

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null, object? jsonFormat = null,
            CancellationToken ct = default)
        {
            lock (Calls) Calls.Add((actionKey, prompt));
            return Task.FromResult(new OneShotResult(NothingJson, new OneShotUsage(10, 0, 0, 5, 0.0, "fake"), 1));
        }

        public IReadOnlyList<LocalEngineExecutor> LocalExecutorsFor(string actionKey) =>
            actionKey == LocalActionCatalog.WikiNightly ? Executors : [];

        // Живой движок без ReadyUrl считается готовым: «решит сам вызов» (его отказ и ряд
        // выбывания проверяются ответом разбора, не пробой).
        public Task<bool> IsLocalEngineReadyAsync(string engine, CancellationToken ct = default) =>
            Task.FromResult(true);
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
        public bool On = true;
        public bool IsEnabled(string userId, string key) => On && key == FeatureFlagKeys.LlmWiki;
    }

    private sealed class Notifier : INotificationSender
    {
        public List<CreateNotificationRequest> Sent { get; } = [];

        public Task<string> SendAsync(string userId, CreateNotificationRequest req, bool sendPush = false)
        {
            Sent.Add(req);
            return Task.FromResult("n-" + Sent.Count);
        }
    }

    private sealed class ThrowingNotifier : INotificationSender
    {
        public Task<string> SendAsync(string userId, CreateNotificationRequest req, bool sendPush = false) =>
            throw new InvalidOperationException("доставка отказала");
    }

    private sealed class RecordingLog : ILogger<WikiNightlyService>
    {
        public List<(LogLevel Level, string Message)> Rows { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Rows.Add((logLevel, formatter(state, exception)));
    }
}
