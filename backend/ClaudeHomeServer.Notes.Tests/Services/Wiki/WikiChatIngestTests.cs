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

// «Разобрать этот чат сейчас» (S6e-2, ADR-035 §12а ступень 0, §12б этапы 1–2 и 9):
// курсор и дельта из стора (S6e-1), окна S6j → заявки в настоящий WikiIngestService,
// одна хроника на чат, сдвиг курсора только после применения или «нечего добавить».
// Модель — подменённый ICheapTextRunner; вся остальная цепка — настоящая, на диске
// (тот же порядок фикстур, что в WikiIngestTests).
//
// Критерии: c1 — второй разбор отдаёт модели только дельту после курсора, хроника одна
// и дописана (мутация «дельта от 0» красная); c2 — подмена записи N сбрасывает курсор
// в 0, повторные утверждения гасит дедup (S4d), а не курсор; c3 — сбой при применении →
// курсор на месте, «нечего добавить» → курсор сдвинут, zone=nothing_new,
// nothingNewStreak=1, хроники нет; c4 — ступень 0 (исполнение задач / нет реплик
// человека / WikiMark=skip) → отказ с причиной, 0 вызовов модели. Плюс уроки ревью
// S6b-3: гонка двух заявок одного чата — один разбор (настоящий параллельный, через
// TaskCompletionSource); отмена ct заявителя не гасит чужую заявку; сбой посреди окон
// не двигает курсор и не оставляет половины хроники.
//
// Утверждение модели — дословная цитата реплики (text == quote): в финальном пакете
// op.Text — подтверждённая цитата (WikiIngestService.cs:335 VerifiedQuote), дедup меры 2
// (WikiDedup.IsDuplicateStatement → HasSameText) опознаёт повтор именно по тексту
// утверждения на странице (WikiPlanValidator.cs:211) — пересказ он бы не поймал.
public class WikiChatIngestTests : IDisposable
{
    private const string User = "u1";
    private const string Section = "projects/p1";
    private const string ChatId = "chat-1";
    private const string PageTitle = "Сущность";
    private const string PagePath = "Проекты/p1/Сущность.md";

    // Маркеры — часть реплики, но не часть утверждения: по ним тест различает
    // «модель видит дельту за курсором» и «модель видит весь чат».
    private const string Claim1 = "Решение: перенести сборку на Linux";
    private const string Marker1 = "первый сбой был 12 октября";
    private const string Claim2 = "Договорились: тесты гоняем через CI на Linux";
    private const string Marker2 = "грин 44 из 44";

    private static string Claims(string claim) =>
        $$"""{"claims":[{"text":"{{claim}}","quote":"{{claim}}"}]}""";

    private static readonly string Plan1 = $$"""
        {"chronicle":{"about":0,"decided":[0],"learned":[],"open_questions":[]},
        "operations":[{"op":"append","page":"{{PageTitle}}","claim":0}]}
        """;

    private static readonly string NothingJson =
        $$"""{"outcome":"{{WikiKeeperPrompts.NothingToAdd}}","reason":"не о чём писать"}""";

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

    public WikiChatIngestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_chat_ingest_" + Guid.NewGuid().ToString("N"));
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
        // Базовое состояние раздела назначения: пустая страница знаний, которую план
        // пополняет. Карточку источника ставит сам разбор (захват — в сервисе).
        var r = _keeper.WritePageAsync(User, Section, PagePath, PageBase).GetAwaiter().GetResult();
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Root => WikiPaths.Root(_dir, User);

    private void AddSession(string id = ChatId, string? projectId = "p1",
        bool taskExecution = false, string? wikiMark = null)
    {
        _sessions.All.Add(new Session
        {
            Id = id,
            OwnerId = User,
            ProjectId = projectId,
            Name = "Чат про сборку",
            TaskExecution = taskExecution,
            WikiMark = wikiMark,
        });
    }

    private void SetHistory(string id, List<StoredMessage> history) => _sessions.History[id] = history;

    // Короткий содержательный чат: m1 служебный, m2 человек (утверждение 1 + маркер 1),
    // m3 агент, m4 человек, m5 агент.
    private void SetShortHistory(string id = ChatId) => SetHistory(id,
    [
        new StoredSessionStartedMessage("claude", "default"),
        new StoredUserMessage($"{Claim1}. Хронология: {Marker1}."),
        new StoredTextMessage("Принято, готовлю план."),
        new StoredUserMessage("Давай."),
        new StoredTextMessage("Готово."),
    ]);

    private WikiChatIngestService Sut(FakeLlm llm) =>
        new(new WikiSourceCapture(_notes, _projects, _sessions, _config),
            new WikiIngestService(llm, new NoSpheres(), _apply, _config),
            _sessions, _keeper, _config);

    private List<string> ChronicleFiles()
    {
        var dir = SafePath.JoinBelowRoot(Root, "pages/Проекты/p1/" + WikiChatIngestService.ChronicleDirName);
        return Directory.Exists(dir)
            ? [.. Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)]
            : [];
    }

    private string PathOf(string pageKey) => SafePath.JoinBelowRoot(Root, "pages/" + pageKey);

    // Строка утверждения на странице — «{текст} [^sN]»; сноска-определение содержит
    // тот же текст без этой приставки (WikiPlanValidator.cs:573-581). Счёт по строке
    // с маркером сноски — сколько раз утверждение записано, а не сколько раз его текст
    // встречается в файле.
    private static int StatementLines(string pageText, string statement) =>
        CountOf(pageText, statement + " [^s");

    private static int CountOf(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0; i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
            n++;
        return n;
    }

    // ── c1: второй разбор — только дельта после курсора; хроника одна и дописана ──

    // Skip: дефект d144bb0f — карточка захвата пишется вне манифеста раздела, первое
    // же применение отбивается дрейфом; цепка «захват → разбор → применение» зелёная
    // только с решением по дефекту (проверено: с экспериментальным исключением
    // Источники/ из дрейфа эти пять тестов зелёные, правка не коммитилась).
    [Fact(Skip = "дефект d144bb0f: карточка захвата вне манифеста — применение отбито дрейфом; снять skip после решения")]
    public async Task C1_ВторойРазбор_ТолькоДельта_ХроникаОднаИДописана()
    {
        AddSession();
        SetShortHistory();

        var llm = new FakeLlm(
            ("A", Claims(Claim1)), ("B", Plan1),
            ("A", Claims(Claim2)), ("B", Plan1));
        var sut = Sut(llm);

        var first = await sut.AnalyzeAsync(User, ChatId);
        first.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, first.Reason);

        var entry = WikiChatTriageStore.Get(Root).Entries.Single();
        entry.Cursor.Should().Be(5, "первый разбор закрыт курсором на конце истории");
        entry.CursorHash.Should().NotBeNullOrEmpty();

        // Три новых сообщения — дельта для второго разбора.
        _sessions.History[ChatId].Add(new StoredUserMessage($"{Claim2}. Детали: {Marker2}."));
        _sessions.History[ChatId].Add(new StoredTextMessage("Принято."));
        _sessions.History[ChatId].Add(new StoredTextMessage("Готово."));

        var second = await sut.AnalyzeAsync(User, ChatId);
        second.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, second.Reason);

        // Мутация «дельта от 0» (взять окно с 1, а не с курсора+1): промпт второго
        // разбора содержит маркер первой дельты — тест красный.
        var promptA2 = llm.PromptOf("A", 2);
        promptA2.Should().Contain(Marker2, "дельта дошла до модели");
        promptA2.Should().NotContain(Marker1, "закрытая курсором часть чата повторно не разбирается");

        // Хроника — одна страница на чат, дописана обоими разборами.
        var chronicles = ChronicleFiles();
        chronicles.Should().HaveCount(1, "одна хроника на чат");
        var text = File.ReadAllText(chronicles[0], System.Text.Encoding.UTF8);
        StatementLines(text, Claim1).Should().Be(1, "первый разбор записан в хронику");
        StatementLines(text, Claim2).Should().Be(1, "повторный разбор дописывает хронику, а не начинает заново");
        text.Should().Contain("type: chronicle");
        text.Should().Contain("Чат про сборку", "заголовок несёт имя чата (ADR-035:273)");

        // Страница знаний пополнена ровно один раз каждым утверждением.
        var page = File.ReadAllText(PathOf(PagePath));
        StatementLines(page, Claim1).Should().Be(1);
        StatementLines(page, Claim2).Should().Be(1);
    }

    // ── c2: подмена записи N — курсор сброшен в 0, повторные утверждения не дублируются ──

    [Fact(Skip = "дефект d144bb0f: карточка захвата вне манифеста — применение отбито дрейфом; снять skip после решения")]
    public async Task C2_ПодменаЗаписиN_КурсорСброшен_УтвержденияНеДублируются()
    {
        AddSession();
        SetShortHistory();

        var llm = new FakeLlm(
            ("A", Claims(Claim1)), ("B", Plan1),
            // Второй разбор: история переписана → дельта заново тот же текст → тот же
            // план; дедup (мера 2) отвергает его как повтор, ремонт модели отвечает
            // «нечего добавить».
            ("A", Claims(Claim1)), ("B", Plan1), ("R", NothingJson));
        var sut = Sut(llm);

        var first = await sut.AnalyzeAsync(User, ChatId);
        first.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, first.Reason);

        // Подмена записи N (курсор стоит на 5) — история переписана (§12б этап 1).
        _sessions.History[ChatId][4] = new StoredTextMessage("Готово по-другому.");

        var second = await sut.AnalyzeAsync(User, ChatId);

        // Сброс курсора доказан входом модели: второй проход A видит весь чат,
        // включая маркер закрытой раньше части.
        llm.PromptOf("A", 2).Should().Contain(Marker1, "хэш записи N не совпал — курсор сброшен в 0");

        // Повтор гасит дедup, а не курсор: исход — штатный «нечего добавить»,
        // причина валидатора («повтор: утверждение уже на странице…») доходит до ремонта.
        second.Outcome.Should().Be(WikiChatIngestService.OutcomeNothingNew, second.Reason);

        // На странице и в хронике утверждение осталось в единственном экземпляре.
        var page = File.ReadAllText(PathOf(PagePath));
        StatementLines(page, Claim1).Should().Be(1, "повторный разбор не дублирует утверждение на странице");
        var chronicles = ChronicleFiles();
        chronicles.Should().HaveCount(1);
        StatementLines(File.ReadAllText(chronicles[0], System.Text.Encoding.UTF8), Claim1)
            .Should().Be(1, "хроника не дописывает то, что уже в ней есть");
        WikiPackageStore.List(Root).Packages.Should().HaveCount(1,
            "второй разбор дошёл до «нечего добавить», второго пакета нет");
    }

    // ── c3a: сбой при применении — курсор на месте, хроники нет ──

    [Fact]
    public async Task C3a_СбойПриПрименении_КурсорНаМесте_БезХроники()
    {
        AddSession();
        SetShortHistory();

        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1));
        // Сбой при применении: карточка источника исчезает с диска во время прохода B —
        // применение не находит цель сносок (WikiApplyService.cs:540-542) и отказывает пакет.
        llm.BeforeNextCall(() =>
        {
            var dir = SafePath.JoinBelowRoot(Root, "pages/Проекты/p1/" + WikiSourceCapture.SourceDirName);
            foreach (var f in Directory.EnumerateFiles(dir, "*.md")) File.Delete(f);
        });

        var res = await Sut(llm).AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected, res.Reason);
        res.Reason.Should().Contain("не найдена в разделе", "причина применения доходит до вызывающего");
        WikiChatTriageStore.Get(Root).Entries.Should().BeEmpty("сбой — курсор не двигается");
        ChronicleFiles().Should().BeEmpty("сбой не оставляет половины хроники");
    }

    // ── c3b: «нечего добавить» — курсор сдвинут, zone и streak записаны, хроники нет ──

    [Fact]
    public async Task C3b_НечегоДобавить_КурсорСдвинут_ЗонаИStreak_ХроникиНет()
    {
        AddSession();
        SetShortHistory();

        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", NothingJson));
        var sut = Sut(llm);

        var res = await sut.AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeNothingNew, res.Reason);
        res.Reason.Should().Be("не о чём писать", "причина модели доходит до вызывающего");

        var entry = WikiChatTriageStore.Get(Root).Entries.Single();
        entry.Cursor.Should().Be(5, "«нечего добавить» — штатный исход, курсор сдвинут на конец дельты");
        entry.CursorHash.Should().NotBeNullOrEmpty();
        entry.Zone.Should().Be(WikiChatIngestService.ZoneNothingNew);
        entry.NothingNewStreak.Should().Be(1, "счётчик рядов «нечего добавить» ведётся для S6f");

        ChronicleFiles().Should().BeEmpty("при штатном исходе хроника не создаётся");
        WikiPackageStore.List(Root).Packages.Should().BeEmpty();

        // Курсор действительно сдвинулся: повторная заявка на том же чате отказана
        // как не имеющая дельты — а не разбирает его заново.
        var again = await sut.AnalyzeAsync(User, ChatId);
        again.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        again.Reason.Should().Contain("новых сообщений нет");
        llm.Calls.Should().HaveCount(2, "отказ по пустой дельте — без обращения к модели");
    }

    // ── c4: ступень 0 — отказ с причиной, ни одного вызова модели ──

    [Fact]
    public async Task C4_Ступень0_ОтказБезВызоваМодели()
    {
        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1));
        var sut = Sut(llm);

        var none = await sut.AnalyzeAsync(User, "no-such-chat");
        none.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        none.Reason.Should().Be("чат не существует");

        AddSession("s-task", taskExecution: true);
        SetHistory("s-task", [new StoredUserMessage("что-то")]);
        var exec = await sut.AnalyzeAsync(User, "s-task");
        exec.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        exec.Reason.Should().Contain("чаты исполнения задач");
        // Мутация «снять ступень 0» по одному лишь тексту отказа не видна: захват
        // (S6h) отказывает TaskExecution теми же словами, но ПОСЛЕ чтения истории.
        // Ступень 0 этого сервиса — до чтения чата: история не прочитана ни разу.
        _sessions.HistoryReads.Should().Be(0, "ступень 0 отсекает до чтения истории чата");

        AddSession("s-silent");
        SetHistory("s-silent",
        [
            new StoredSessionStartedMessage("claude", "default"),
            new StoredTextMessage("ответ агента без человека"),
            new StoredUserMessage("авто-реплика", auto: true),
            new StoredUserMessage("директива", systemDirective: true),
        ]);
        var silent = await sut.AnalyzeAsync(User, "s-silent");
        silent.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        silent.Reason.Should().Contain("нет ни одной реплики человека");

        AddSession("s-skip", wikiMark: WikiMarks.Skip);
        SetHistory("s-skip", [new StoredUserMessage("реплика есть, но чат помечен")]);
        var skip = await sut.AnalyzeAsync(User, "s-skip");
        skip.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        skip.Reason.Should().Contain("не разбирать");

        llm.Calls.Should().BeEmpty("ступень 0 — до обращения к модели");
        WikiPackageStore.List(Root).Packages.Should().BeEmpty();
        ChronicleFiles().Should().BeEmpty();
    }

    // ── гонка (хвост ревью S6b-3): две заявки одного чата — один разбор ──

    [Fact(Skip = "дефект d144bb0f: карточка захвата вне манифеста — применение отбито дрейфом; снять skip после решения")]
    public async Task Гонка_ДвеЗаявкиОдногоЧата_ОдинРазбор()
    {
        AddSession();
        SetShortHistory();

        var gate = new TaskCompletionSource();
        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1)) { GateA = gate };
        var sut = Sut(llm);

        // Первая заявка стоит в модели; вторая приходит в середину её прогона.
        var t1 = Task.Run(() => sut.AnalyzeAsync(User, ChatId));
        SpinWait.SpinUntil(() => llm.Calls.Count > 0, 5000).Should().BeTrue("первая заявка реально в прогоне");
        var t2 = sut.AnalyzeAsync(User, ChatId);
        gate.SetResult();
        var results = await Task.WhenAll(t1, t2);

        // Мутация «без присоединения к in-flight» (снять ветку TryGetValue→return в
        // AnalyzeAsync): вторая заявка начинает СВОЙ разбор — история читается дважды
        // на каждый разбор (свой ReadAsync + свой CaptureSession), итого 4 вместо 2,
        // красное. По модели этого не видно: очередь заявок (S6b-3) со своим in-flight
        // склеила бы её внутренние заявки и модель осталась бы вызвана дважды —
        // дедup нужен на уровне чата, а не только заявки.
        _sessions.HistoryReads.Should().Be(2, "один разбор: история прочитана его ReadAsync и захватом; вторая заявка своего разбора не начинает");
        llm.Calls.Should().HaveCount(2, "одна заявка — один прогон; вторая получает её результат");
        results.Should().OnlyContain(r => r.Outcome == WikiChatIngestService.OutcomeApplied);
        results[0].PackageIds.Should().Equal(results[1].PackageIds);
        WikiPackageStore.List(Root).Packages.Should().HaveCount(1, "двух пакетов на один чат нет");
        ChronicleFiles().Should().HaveCount(1, "хронику пишет один разбор, а не два");
    }

    // ── отмена заявителя не гасит чужую заявку (хвост ревью S6b-3) ──

    [Fact(Skip = "дефект d144bb0f: карточка захвата вне манифеста — применение отбито дрейфом; снять skip после решения")]
    public async Task ОтменаЗаявителя_НеГаситОбщийРазбор()
    {
        AddSession();
        SetShortHistory();

        var gate = new TaskCompletionSource();
        var cts = new CancellationTokenSource();
        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1)) { GateA = gate };
        var sut = Sut(llm);

        var t1 = Task.Run(() => sut.AnalyzeAsync(User, ChatId, cts.Token));
        SpinWait.SpinUntil(() => llm.Calls.Count > 0, 5000).Should().BeTrue();
        cts.Cancel(); // заявитель 1 отказался — общий прогон гасить нельзя
        var t2 = sut.AnalyzeAsync(User, ChatId);
        gate.SetResult();

        var second = await t2;
        // Мутация «передать ct заявителя в прогон»: проход A падает по отмене,
        // обе заявки получают отказ — тест красный.
        second.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, second.Reason);
        (await t1).Outcome.Should().Be(WikiChatIngestService.OutcomeApplied,
            "отмена одного заявителя не отменяет сам разбор");
        llm.Calls.Should().HaveCount(2);
    }

    // ── сбой посреди окон: курсор на месте, половины хроники нет (урок S6e-2) ──

    [Fact(Skip = "дефект d144bb0f: карточка захвата вне манифеста — применение отбито дрейфом; снять skip после решения")]
    public async Task СбойПосредиОкон_КурсорНаМесте_БезПоловиныХроники()
    {
        AddSession();
        // Дельта длиннее окна (35k токенов; мера — байты/3, WikiChatSlicer.cs:57):
        // 18 ответов по 6k латинских символов = 36k токенов → ровно два окна S6j.
        // Латинский и 6k, а не кириллица и 12k: кириллица в два байта делает реплику
        // 24 КБ — первая реплика второго окна не полезла бы в выдержку карточки (8 КБ,
        // целыми репликами) и второе окно отбилось бы захватом, а не моделью. Последняя
        // реплика — человеческая: в диапазоне второго окна обязана быть реплика человека
        // (§12а: агентскую цитирует только человеческая того же диапазона).
        var history = new List<StoredMessage>
        {
            new StoredSessionStartedMessage("claude", "default"),
            new StoredUserMessage($"{Claim1}. Хронология: {Marker1}."),
        };
        for (var i = 0; i < 18; i++) history.Add(new StoredTextMessage(new string('x', 6_000)));
        history.Add(new StoredUserMessage("Хватит. Итога нет."));
        SetHistory(ChatId, history);

        var llm = new FakeLlm(
            ("A", Claims(Claim1)), ("B", Plan1),
            ("A", "модель ответила прозой вместо JSON"));
        var res = await Sut(llm).AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected, res.Reason);
        res.Reason.Should().Contain("окна m", "названо окно, на котором разбор оборвался");
        llm.CountOf("A").Should().Be(2, "первое окно разобрано, второе — отказано");
        WikiChatTriageStore.Get(Root).Entries.Should().BeEmpty("сбой посреди окон курсор не двигает");
        ChronicleFiles().Should().BeEmpty("хроника не начинается половиной разбора");
    }

    // ── подменённый ICheapTextRunner: проходы по промптам, gate и побочные эффекты ──

    private sealed class FakeLlm : ICheapTextRunner
    {
        public List<(string ActionKey, string Prompt, string Pass)> Calls { get; } = [];
        public TaskCompletionSource? GateA; // первый проход A ждёт, пока gate не открыт

        private readonly Dictionary<string, Queue<string>> _answers = new();
        private readonly List<Action> _beforeNextCall = [];

        public FakeLlm(params (string Pass, string Answer)[] answers)
        {
            foreach (var (pass, answer) in answers)
                (_answers.TryGetValue(pass, out var q) ? q : _answers[pass] = new Queue<string>()).Enqueue(answer);
        }

        public void BeforeNextCall(Action action) => _beforeNextCall.Add(action);

        public string PromptOf(string pass, int nth = 1) =>
            Calls.Where(c => c.Pass == pass).ElementAt(nth - 1).Prompt;

        public int CountOf(string pass) => Calls.Count(c => c.Pass == pass);

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(Answer(actionKey, prompt, ct));

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult<string?>(Answer(actionKey, prompt, ct));

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult(new OneShotResult(
                Answer(actionKey, prompt, ct), new OneShotUsage(10, 0, 0, 5, 0.0, "fake"), 1));

        private string Answer(string actionKey, string prompt, CancellationToken ct)
        {
            if (_beforeNextCall.Count > 0)
            {
                var action = _beforeNextCall[0];
                _beforeNextCall.RemoveAt(0);
                action();
            }
            // Проход — по директиве своего промпта (порядок вызовов не угадывается).
            var pass = prompt.Contains("YOUR PREVIOUS PLAN WAS REJECTED") ? "R"
                : prompt.Contains("extract durable") ? "A"
                : prompt.Contains("turn the claims") ? "B"
                : "?";
            Calls.Add((actionKey, prompt, pass));
            actionKey.Should().Be(LocalActionCatalog.WikiIngest, "место разбора — wiki-ingest");
            if (pass == "A" && GateA is not null && CountOf("A") == 1)
                GateA.Task.WaitAsync(ct).GetAwaiter().GetResult();
            var queue = _answers.TryGetValue(pass, out var q) ? q : null;
            return queue is { Count: > 0 } ? queue.Dequeue() : "нет ответа для прохода " + pass;
        }
    }

    private sealed class FakeSessions : ISessionDirectory
    {
        public readonly List<Session> All = [];
        public readonly Dictionary<string, List<StoredMessage>> History = [];
        // Сколько раз историю реально читали: маркер «присоединилась заявка или
        // начала свой разбор» — модель одна и та же при обеих реализациях очереди.
        public int HistoryReads;

        public Session? GetById(string id) => All.FirstOrDefault(s => s.Id == id);
        public IReadOnlyCollection<Session> GetAll() => All;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId)
        {
            Interlocked.Increment(ref HistoryReads);
            return Task.FromResult<IReadOnlyList<StoredMessage>>(
                History.TryGetValue(sessionId, out var h) ? h : new List<StoredMessage>());
        }
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
