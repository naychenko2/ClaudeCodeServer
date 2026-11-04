using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
// nothingNewStreak=1, хроники нет; c3c (доработка S6f, круг 2) — применённый разбор
// снимает свой устаревший nothing_new: zone/reason обнулены, список контроллера
// показывает зону ступени 1, а не «нечего добавить»; c4 — ступень 0 (исполнение задач / нет реплик
// человека / WikiMark=skip) → отказ с причиной, 0 вызовов модели. Плюс уроки ревью
// S6b-3: гонка двух заявок одного чата — один разбор (настоящий параллельный, через
// TaskCompletionSource); отмена ct заявителя не гасит чужую заявку; сбой посреди окон
// не двигает курсор и не оставляет половины хроники. Плюс доработка S4c (ревью d857da6c):
// этот путь не решает «само / Входящие» сам — он получает настройки владельца через
// тот же WikiIngestService: выключенный класс оставляет пакет в «Входящих».
//
// Плюс хвосты ревью W3-6: чужой и несуществующий чат — один message на двоих и он же в
// last (F1/F3); исключение вне защищённого прогона — in-flight снимается, заявка живая,
// не вечный 202 (F2); две заявки одного чата — один прогон (мера — чтения истории на
// уровне чата: модель одна и без single-flight'а чата, её экономит in-flight очереди).
//
// Плюс N7a: место разбора (actionKey) доходит до модели: заявка ночи зовёт wiki-nightly,
// без actionKey — прежнее место wiki-ingest (guard в FakeLlm.Answer на каждом тесте);
// две заявки одного чата с разными местами — один прогон, движок того, кто первым.
// Плюс доработка R-N7a M1: ключ вне каталога LocalActionCatalog — отказ с причиной на
// входе очереди заявок, модель не зовётся (неведомое место не уходит в облако).
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
        new(new WikiSourceCapture(_notes, _projects, _sessions, _config, _keeper),
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

    // Цепка «захват → разбор → применение»: зелёна, когда захват регистрирует карточку в
    // манифесте раздела (дефект d144bb0f решён — см. WikiSourceCaptureTests).
    [Fact]
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

    [Fact]
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

    // ── Ф8 (дефект 603ae88a): версия окна чата — другой источник, заявки разведены ──
    // Подмена записи N (тест выше) даёт ДРУГУЮ версию того же окна: карточка новой версии
    // иная — её имя функция (ref, version) (WikiSourceCapture.cs:309). Пока очередь заявок
    // опознавала источник по одной ссылке Ref, заявка новой версии получала чужой прогон:
    // пакет тот же, модель под неё не звалась ни разу. В C2 этот путь был окном между концом
    // прогона и снятием записи (снятие — продолжение на пуле, WikiIngestService.cs:143): под
    // параллелью в завершённый прогон попадала заявка переписанного окна — отсюда флак.
    // Здесь те же две версии одного окна: первая стоит в проходе A, вторая приходит, когда её
    // заявка уже стоит в очереди. Гейт для этого асинхронный (GateAAsync, см. FakeLlm): с
    // блокирующим GateA префикс прогона идёт на потоке вызова внутри lock очереди
    // (WikiIngestService.cs:131), записи в очереди ещё нет, и исход теста зависел бы от гонки
    // за этот lock — тот же флак, перенесённый в сам тест. Мутация «вернуть ключ без версии»
    // краснит строку PackageId всегда, а не под нагрузкой.
    [Fact]
    public async Task ОдноОкноДвухВерсий_ЗаявкиНеСливаются_КаждаяРазбираетСвоёСодержание()
    {
        AddSession();
        SetShortHistory();
        var capture = new WikiSourceCapture(_notes, _projects, _sessions, _config, _keeper);
        var v1 = await capture.CaptureSession(User, Section, ChatId, 1, 5);
        v1.Ok.Should().BeTrue(v1.Reason);

        const string secondTail = "Готово по-другому.";
        _sessions.History[ChatId][4] = new StoredTextMessage(secondTail);
        var v2 = await capture.CaptureSession(User, Section, ChatId, 1, 5);
        v2.Ok.Should().BeTrue(v2.Reason);
        v2.CardRelPath.Should().NotBe(v1.CardRelPath, "версия источника — в имени его карточки");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llm = new FakeLlm(
            ("A", Claims(Claim1)), ("B", Plan1),
            ("A", Claims(Claim1)), ("B", Plan1), ("R", NothingJson))
        { GateAAsync = gate };
        var ingest = new WikiIngestService(llm, new NoSpheres(), _apply, _config);
        var req1 = new WikiIngestService.Request(WikiGate.OriginSession, "session:" + ChatId + "#m1-m5",
            v1.CardRelPath!, "Пользователь:\n" + Claim1 + "\n\nАссистент:\nГотово.\n", Section, null);
        var req2 = new WikiIngestService.Request(WikiGate.OriginSession, "session:" + ChatId + "#m1-m5",
            v2.CardRelPath!, "Пользователь:\n" + Claim1 + "\n\nАссистент:\n" + secondTail + "\n", Section, null);

        // Первая версия стоит в проходе A; вторая приходит в середину её прогона.
        var t1 = Task.Run(() => ingest.IngestAsync(User, req1));
        SpinWait.SpinUntil(() => llm.Calls.Count > 0, 5000).Should().BeTrue("первая заявка в прогоне");
        var t2 = Task.Run(() => ingest.IngestAsync(User, req2));
        gate.SetResult();
        var first = await t1;
        var again = await t2;

        first.Outcome.Should().Be(WikiIngestService.OutcomeApplied, first.Reason);
        // Мутация «вернуть ключ без версии»: again — тот же прогон (тот же пакет), один
        // проход A на обе версии: красное здесь, без нагрузки и без повторов.
        again.PackageId.Should().NotBe(first.PackageId, "версия окна — своя заявка, а не чужой прогон");
        llm.CountOf("A").Should().Be(2, "каждая версия разбрана своим проходом A");
        llm.PromptOf("A", 2).Should().Contain(secondTail, "вторая заявка разобрала своё содержание, а не чужое");
    }

    // ── хэш курсора — по неизменяемому содержимому реплики (хвост ревью S6e-2, п.2) ──

    // Живой чат разбирают и посреди хода: реплика человека уже записана, а снимок промпта
    // к ней дописывается позже (TurnAccumulator.cs:97). Курсор встаёт на этой реплике, и
    // хэш, покрывающий PromptSnapshotId, изменился бы без всякой правки истории: следующий
    // разбор видел бы «историю переписали» — курсор в 0 и модель заново на весь чат.
    // Мутация «хэш включает PromptSnapshotId» (вернуть сериализацию записи целиком) краснит
    // этот тест; подмена ТЕКСТА записи N остаётся сбросом курсора (тест C2 выше).
    [Fact]
    public async Task Курсор_СлужебныеПоляЗаписиЕгоНеСбрасывают_ПереразбораНет()
    {
        AddSession();
        // История оборвана на реплике человека — ровно то состояние, в котором чат
        // разбирают посреди хода: курсор встаёт на НЕЙ, а не на ответе агента.
        SetHistory(ChatId, [
            new StoredSessionStartedMessage("claude", "default"),
            new StoredUserMessage($"{Claim1}. Хронология: {Marker1}."),
            new StoredTextMessage("Принято, готовлю план."),
            new StoredUserMessage("Давай."),
        ]);

        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1));
        var sut = Sut(llm);

        var first = await sut.AnalyzeAsync(User, ChatId);
        first.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, first.Reason);
        var entry = WikiChatTriageStore.Get(Root).Entries.Single();
        entry.Cursor.Should().Be(4, "курсор на последней записи — реплике человека");

        // Снимок промпта дописан ПОСЛЕ создания реплики: служебное состояние записи, а не
        // то, что в ней сказано. Хэш курсора обязан этого не увидеть.
        ((StoredUserMessage)_sessions.History[ChatId][3]).PromptSnapshotId = "1700000000000-abcd";

        var again = await sut.AnalyzeAsync(User, ChatId);
        again.Outcome.Should().Be(WikiChatIngestService.OutcomeUpToDate, again.Reason);
        again.Reason.Should().Contain("новых сообщений нет", "хэш тот же — курсор на месте, чат не переразобрывается");
        llm.Calls.Should().HaveCount(2, "модель заново не звалась");
    }

    // Хвост живого чата — часто карточка tool_use: ранняя карточка из стрима (пустой Input)
    // легла в history.json снапшотом посреди хода (SaveSnapshotAsync, TurnAccumulator.cs:731),
    // а Input дописывает только финальный OnToolUse (дедуп по id, там же :138). Курсор стоит
    // на этой записи: хэш не обязан видеть дописывание Input — иначе следующий разбор видел бы
    // «историю переписали» и звал бы модель заново на весь чат (хвост ревью S6e-2, доработка 2,
    // п.1). Мутация «Input не в ServiceFieldsByKind["tool_use"]» (убрать его из словаря) краснит
    // этот тест: хэш меняется, курсор падает в 0, модель зовётся ещё дважды.
    [Fact]
    public async Task Курсор_ToolUseInputДописанПослеСнапшота_ПереразбораНет()
    {
        AddSession();
        SetHistory(ChatId, [
            new StoredSessionStartedMessage("claude", "default"),
            new StoredUserMessage($"{Claim1}. Хронология: {Marker1}."),
            new StoredTextMessage("Принято, готовлю план."),
            new StoredToolUseMessage { Id = "tool-1", Name = "Bash" },
        ]);

        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1));
        var sut = Sut(llm);

        var first = await sut.AnalyzeAsync(User, ChatId);
        first.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, first.Reason);
        var entry = WikiChatTriageStore.Get(Root).Entries.Single();
        entry.Cursor.Should().Be(4, "курсор на последней записи — карточке tool_use");

        // Финальный OnToolUse: Input дописан ПОСЛЕ создания записи и после снапшота, лёгшего
        // в историю. Это служебное состояние записи, а не то, что в ней сказано.
        ((StoredToolUseMessage)_sessions.History[ChatId][3]).Input = new { command = "dotnet build" };

        var again = await sut.AnalyzeAsync(User, ChatId);
        again.Outcome.Should().Be(WikiChatIngestService.OutcomeUpToDate, again.Reason);
        again.Reason.Should().Contain("новых сообщений нет", "хэш тот же — курсор на месте, чат не переразобрывается");
        llm.Calls.Should().HaveCount(2, "модель заново не звалась");
    }

    // ── c3a: сбой при применении — курсор на месте, хроники нет ──

    [Fact]
    public async Task C3a_СбойПриПрименении_КурсорНаМесте_БезХроники()
    {
        AddSession();
        SetShortHistory();

        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1));
        // Сбой при применении: карточка источника исчезает с диска во время прохода B.
        // Карточку при захвате регистрирует Хранитель (дефект d144bb0f), поэтому её удаление —
        // расхождение диска с манифестом: применение стопорит шаг 2 (CheckSectionAgainstDisk,
        // WikiApplyService.cs:628-629) раньше рендера — «файл из манифеста отсутствует на диске».
        llm.BeforeNextCall(() =>
        {
            var dir = SafePath.JoinBelowRoot(Root, "pages/Проекты/p1/" + WikiSourceCapture.SourceDirName);
            foreach (var f in Directory.EnumerateFiles(dir, "*.md")) File.Delete(f);
        });

        var res = await Sut(llm).AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected, res.Reason);
        res.Reason.Should().Contain("из манифеста раздела отсутствует на диске", "причина применения доходит до вызывающего");
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

        // Курсор действительно сдвинулся: повторная заявка на том же чате получает up_to_date
        // («разобран до конца», S7b0a) — а не разбирает его заново и не считается отказом.
        var again = await sut.AnalyzeAsync(User, ChatId);
        again.Outcome.Should().Be(WikiChatIngestService.OutcomeUpToDate);
        again.Reason.Should().Contain("новых сообщений нет");
        llm.Calls.Should().HaveCount(2, "пустая дельта — без обращения к модели");
    }

    // ── c3c (доработка S6f, круг 2): применённый разбор снимает свой nothing_new ──
    // Инвариант: зона отражает последний исход разбора. Применённый разбор означает
    // «новые утверждения были» — его собственный устаревший nothing_new не переживает
    // свой же исход: применённый путь пишет null/null, а не прежнюю зону/причину.

    [Fact]
    public async Task C3c_НечегоДобавить_ЗатемПрименённыйРазбор_ЗонаИПричинаСняты_ПоказанаЗонаСтупени1()
    {
        AddSession();
        SetShortHistory();

        // Шаг 1. Исход «нечего добавить»: зона и причина разбора в сторе, streak 1 —
        // пока они не сняты, список показывает именно их (существующее поведение).
        var llmNothing = new FakeLlm(("A", Claims(Claim1)), ("B", NothingJson));
        var sut = Sut(llmNothing);
        var first = await sut.AnalyzeAsync(User, ChatId);
        first.Outcome.Should().Be(WikiChatIngestService.OutcomeNothingNew, first.Reason);

        var shownBefore = ((WikiChatsListDto)((OkObjectResult)Controller().List()).Value!).Items.Single();
        shownBefore.Zone.Should().Be(WikiChatIngestService.ZoneNothingNew, "исход разбора ещё не снят — он и показывается");
        shownBefore.Reason.Should().Be("не о чём писать");

        // Шаг 2. Чат продолжается: дельта за курсором, следующий разбор её применяет.
        _sessions.History[ChatId].Add(new StoredUserMessage($"{Claim2}. Хронология: {Marker2}."));
        _sessions.History[ChatId].Add(new StoredTextMessage("Готово."));

        var llmApplied = new FakeLlm(("A", Claims(Claim2)), ("B", Plan1));
        var res = await Sut(llmApplied).AnalyzeAsync(User, ChatId);
        res.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, "отказ разбора: " + res.Reason);

        // Шаг 3. Стор: применённый исход обнулил чужой устаревший nothing_new —
        // зона и причина пусты, streak 0, курсор на конец применённой дельты.
        var after = WikiChatTriageStore.Get(Root).Entries.Single();
        after.Cursor.Should().Be(7, "курсор — на конец применённой дельты");
        after.CursorHash.Should().NotBeNullOrEmpty();
        after.Zone.Should().BeNull("зона отражает последний исход разбора: применённый снимает nothing_new");
        after.Reason.Should().BeNull("причина снятой зоны не остаётся");
        after.NothingNewStreak.Should().Be(0);

        // Шаг 4. Список контроллера: чат больше не «нечего добавить» — его зона ступени 1
        // (её поставил пересчёт), счётчики — по показанной зоне.
        WikiChatTriageStore.UpdateScoring(Root, ChatId, 20,
            [new WikiTriageSignal(WikiChatScorer.SignalDecisionWords, 20)],
            WikiChatScorer.ZoneTriage, "балл 20: формулировки решений").Ok.Should().BeTrue();

        var list = (WikiChatsListDto)((OkObjectResult)Controller().List()).Value!;
        var item = list.Items.Single(i => i.SessionId == ChatId);
        item.Zone.Should().Be(WikiChatScorer.ZoneTriage, "показана зона ступени 1, а не снятый nothing_new");
        item.Reason.Should().Be("балл 20: формулировки решений", "причина показанной зоны — причина ступени 1");
        list.Items.Should().NotContain(i => i.Zone == WikiChatIngestService.ZoneNothingNew);
        list.Counts.Should().NotContainKey(WikiChatIngestService.ZoneNothingNew);
        list.Counts[WikiChatScorer.ZoneTriage].Should().Be(1, "счётчик зон — по показанной зоне, не по старой");
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

    // ── S7b0a: машинные коды отказа (вызывающий различает их по коду, не подстрокой) ──

    [Fact]
    public async Task КодыОтказа_Ступень0_Чат_И_НезаведённаяВики()
    {
        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1));
        var sut = Sut(llm);

        AddSession("s-task", taskExecution: true);
        SetHistory("s-task", [new StoredUserMessage("что-то")]);
        var exec = await sut.AnalyzeAsync(User, "s-task");
        exec.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        exec.Code.Should().Be(WikiChatIngestService.CodeTaskExecution, "отказ ступени 0 различается кодом");

        AddSession("s-skip", wikiMark: WikiMarks.Skip);
        SetHistory("s-skip", [new StoredUserMessage("реплика есть, но чат помечен")]);
        var skip = await sut.AnalyzeAsync(User, "s-skip");
        skip.Code.Should().Be(WikiChatIngestService.CodeMarkedSkip);

        AddSession("s-personal", projectId: null);
        var personal = await sut.AnalyzeAsync(User, "s-personal");
        personal.Code.Should().Be(WikiChatIngestService.CodePersonalSection);

        // Чужой и несуществующий чат — один код на двоих: per-owner, без утечки существования.
        var none = await sut.AnalyzeAsync(User, "no-such-chat");
        none.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        none.Code.Should().Be(WikiChatIngestService.CodeNotFound);

        AddSession("s-foreign");
        _sessions.All.Single(s => s.Id == "s-foreign").OwnerId = "u2";
        SetHistory("s-foreign", [new StoredUserMessage("чужой чат")]);
        var foreign = await sut.AnalyzeAsync(User, "s-foreign");
        foreign.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        foreign.Code.Should().Be(WikiChatIngestService.CodeNotFound,
            "для вызывающего чужой и несуществующий неразличимы");

        // Свой чат владельца с незаведённой вики — отказ в начале прогона.
        AddSession("s-nowiki", projectId: "p1");
        _sessions.All.Single(s => s.Id == "s-nowiki").OwnerId = "u3";
        var nowiki = await sut.AnalyzeAsync("u3", "s-nowiki");
        nowiki.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        nowiki.Code.Should().Be(WikiChatIngestService.CodeWikiNotInitialized);

        // Отказ, не названный контрактом, остаётся без кода: service кодов не выдумывает
        // (мутация «выдумать код у отказа без истории» — красное на этом же утверждении).
        AddSession("s-nohistory", projectId: "p1");
        var nohistory = await sut.AnalyzeAsync(User, "s-nohistory");
        nohistory.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        nohistory.Reason.Should().Be("у чата нет истории");
        nohistory.Code.Should().BeNull();

        llm.Calls.Should().BeEmpty("все отказы — до обращения к модели");
    }

    // ── S7b0a: up_to_date — курсор на конце истории это состояние, а не отказ ──

    [Fact]
    public async Task UpToDate_РазобранныйДоКонца_НеОтказ_И_БезВызоваМодели()
    {
        AddSession();
        SetShortHistory();

        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1));
        var sut = Sut(llm);

        var first = await sut.AnalyzeAsync(User, ChatId);
        first.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, first.Reason);

        var again = await sut.AnalyzeAsync(User, ChatId);
        again.Outcome.Should().Be(WikiChatIngestService.OutcomeUpToDate,
            "курсор на конце истории: разбирать нечего — состояние, а не отказ");
        again.Reason.Should().Contain("новых сообщений нет");
        again.Code.Should().BeNull("up_to_date — не отказ, кода у него нет");
        llm.Calls.Should().HaveCount(2, "повторная заявка по закрытому курсору модель не зовёт");
    }

    // ── S7b0a: IsRunning и последний исход (чтение analyze-status) ──

    [Fact]
    public async Task IsRunning_ТолькоВоВремяПрогона_И_ТолькоСвоегоВладельца()
    {
        AddSession();
        SetShortHistory();

        var gate = new TaskCompletionSource();
        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1)) { GateA = gate };
        var sut = Sut(llm);

        var t = Task.Run(() => sut.AnalyzeAsync(User, ChatId));
        SpinWait.SpinUntil(() => llm.Calls.Count > 0, 5000).Should().BeTrue("заявка реально в прогоне");
        sut.IsRunning(User, ChatId).Should().BeTrue("во время прогона — true");
        sut.IsRunning("u2", ChatId).Should().BeFalse("чужой владелец не видит чужой чат даже по признаку разбора");
        sut.GetLast(User, ChatId).Should().BeNull("пока прогон идёт, исхода ещё нет");

        gate.SetResult();
        var res = await t;
        res.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, res.Reason);

        // Исход записан до снятия in-flight: «не идёт» означает «last свежий», а не «исход потерян».
        sut.IsRunning(User, ChatId).Should().BeFalse("после окончания — не идёт");
        var last = sut.GetLast(User, ChatId);
        last.Should().NotBeNull("законченный разбор зафиксирован");
        last!.Result.Outcome.Should().Be(res.Outcome);
        last.Result.PackageIds.Should().Equal(res.PackageIds);
        last.FinishedAt.Should().NotBe(default);
        sut.GetLast("u2", ChatId).Should().BeNull("ключ last — с владельцем");
    }

    // Last перечитывается следующим исходом: статус показывает последний, а не первый.
    [Fact]
    public async Task Last_ПеречитываетсяСледующимИсходом()
    {
        AddSession();
        SetShortHistory();

        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1));
        var sut = Sut(llm);

        var first = await sut.AnalyzeAsync(User, ChatId);
        first.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, first.Reason);
        sut.GetLast(User, ChatId)!.Result.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied);

        var again = await sut.AnalyzeAsync(User, ChatId);
        again.Outcome.Should().Be(WikiChatIngestService.OutcomeUpToDate);
        var last = sut.GetLast(User, ChatId)!;
        last.Result.Outcome.Should().Be(WikiChatIngestService.OutcomeUpToDate,
            "последний исход — последний по времени, applied первого разбора не остаётся");
    }

    // Потолок 200 записей: при переполнении снимается самый старый по времени окончания.
    [Fact]
    public async Task Last_Потолок200_СнимаетсяСтарейший()
    {
        var llm = new FakeLlm(); // ответов нет ни для какого отказа — и не нужно
        var sut = Sut(llm);

        // 201 свой чат без истории: каждый даёт быстрый отказ и запись last.
        for (var i = 0; i < 201; i++)
            AddSession($"s-{i:D3}", projectId: "p1");
        for (var i = 0; i < 201; i++)
        {
            var res = await sut.AnalyzeAsync(User, $"s-{i:D3}");
            res.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        }

        sut.GetLast(User, "s-000").Should().BeNull("самый старый вытеснен");
        sut.GetLast(User, "s-001").Should().NotBeNull("держатся newest 200");
        sut.GetLast(User, "s-200").Should().NotBeNull("последний — точно есть");
    }

    // ── гонка (хвост ревью S6b-3): две заявки одного чата — один разбор ──

    [Fact]
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

    // S7b0a: та же гонка строгче — вторая заявка получает ТУ ЖЕ задачу (присоединение по
    // in-flight под lock), а не начинает свою. Мутация «убрать TryGetValue» в AnalyzeAsync:
    // инстансы разные и прогон второй (A+B дважды) — тест красный.
    [Fact]
    public async Task Analyze_Parallel_RunsOnce()
    {
        AddSession();
        SetShortHistory();

        var gate = new TaskCompletionSource();
        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1)) { GateA = gate };
        var sut = Sut(llm);

        var t1 = sut.AnalyzeAsync(User, ChatId);
        SpinWait.SpinUntil(() => llm.Calls.Count > 0, 5000).Should().BeTrue("первая заявка в прогоне");
        var t2 = sut.AnalyzeAsync(User, ChatId);
        ReferenceEquals(t1, t2).Should().BeTrue("та же задача, не второй разбор");

        gate.SetResult();
        var r1 = await t1;
        var r2 = await t2;
        r1.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, r1.Reason);
        r2.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, r2.Reason);
        llm.Calls.Should().HaveCount(2, "один разбор: проходы модели A+B ровно по одному разу");
        r1.PackageIds.Should().Equal(r2.PackageIds);
    }

    // ── N7a: место разбора (actionKey) доходит до модели; ключ single-flight его не содержит ──

    [Fact]
    public async Task Analyze_СActionKeyНочи_МодельЗванаНаМестеНочи()
    {
        // Заявка ночи: оба прохода — на названном месте wiki-nightly. Без actionKey
        // место прежнее (wiki-ingest) — это guard в FakeLlm.Answer, живой на каждом
        // тесте файла (прежний тест :1087).
        AddSession("chat-night");
        SetShortHistory("chat-night");

        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1))
        {
            ExpectedActionKey = LocalActionCatalog.WikiNightly,
        };
        var sut = Sut(llm);

        var res = await sut.AnalyzeAsync(User, "chat-night", actionKey: LocalActionCatalog.WikiNightly);
        res.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, res.Reason);
        llm.Calls.Should().HaveCount(2, "один разбор: проходы A+B ровно по одному разу");
        llm.Calls.Should().OnlyContain(c => c.ActionKey == LocalActionCatalog.WikiNightly,
            "разбор ночи — на названном месте");
    }

    // ── решение Андрея 02.11: ночь не уходит в облако, кнопка — прежняя цепочка ──

    [Fact]
    public async Task Analyze_КлючНочи_ВызовБезОблака()
    {
        // Ночь идёт через RunLocalOrChosenAsync (без финального claude-шага); кнопка «Разобрать»
        // (actionKey не задан) — через RunDetailedAsync, как раньше (следующий тест).
        // Мутации: ночь на RunDetailedAsync (убрать запрет) → этот тест красный;
        // кнопка на RunLocalOrChosenAsync (выставить запрет) → следующий тест красный.
        AddSession("chat-night");
        SetShortHistory("chat-night");
        var night = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1))
        {
            ExpectedActionKey = LocalActionCatalog.WikiNightly,
        };
        var nres = await Sut(night).AnalyzeAsync(User, "chat-night", actionKey: LocalActionCatalog.WikiNightly);
        nres.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, nres.Reason);
        night.NoCloudKeys.Should().HaveCount(2, "оба прохода ночи — запросом без облака");
        night.CloudKeys.Should().BeEmpty("облачная цепочка ночью не зовётся");
    }

    [Fact]
    public async Task Analyze_КнопкаБезКлюча_ПрежняяЦепочкаСОблаком()
    {
        AddSession();
        SetShortHistory();
        var button = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1));
        var bres = await Sut(button).AnalyzeAsync(User, ChatId);
        bres.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, bres.Reason);
        button.CloudKeys.Should().HaveCount(2, "кнопка — прежняя цепочка с облаком");
        button.NoCloudKeys.Should().BeEmpty("запрет облака кнопке не выставляется");
    }

    [Fact]
    public async Task Analyze_НочьЛокальныйРаннерНедоступен_ОтказБезОблака_КурсорНеДвинут()
    {
        // Локаль упала посреди вызова: отказ разбора (Rejected), облако не позвано,
        // исключения наружу нет, курсор чата не двинут.
        AddSession("chat-night");
        SetShortHistory("chat-night");
        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1))
        {
            ExpectedActionKey = LocalActionCatalog.WikiNightly,
            LocalThrow = new InvalidOperationException("локальная модель недоступна"),
        };

        var res = await Sut(llm).AnalyzeAsync(User, "chat-night", actionKey: LocalActionCatalog.WikiNightly);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        res.Reason.Should().Contain("модель не ответила").And.Contain("недоступна");
        llm.CloudKeys.Should().BeEmpty("облако при отказе локали не зовётся");
        llm.NoCloudKeys.Should().ContainSingle("после отказа второй проход не идёт");
        WikiChatTriageStore.Get(Root).Entries
            .Where(e => e.SessionId == "chat-night")
            .Should().NotContain(e => e.Cursor > 0, "курсор не двинут");
    }

    [Fact]
    public async Task Гонка_ДвеЗаявкиРазнымиActionKey_ОдинПрогон()
    {
        // Кнопка начала разбор (wiki-ingest); в середине её прогона приходит ночь
        // (wiki-nightly). In-flight-ключ owner|chat|home места не содержит (шапка
        // сервиса): одна задача на двоих, движок — того, кто пришёл первым.
        // Мутация «игнорировать actionKey» (не доносить его до CallModelAsync): первый
        // тест красный — модель зовётся не на месте заявки.
        AddSession();
        SetShortHistory();

        var gate = new TaskCompletionSource();
        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1)) { GateA = gate };
        var sut = Sut(llm);

        var t1 = sut.AnalyzeAsync(User, ChatId);
        SpinWait.SpinUntil(() => llm.Calls.Count > 0, 5000).Should().BeTrue("первая заявка в прогоне");
        var t2 = sut.AnalyzeAsync(User, ChatId, actionKey: LocalActionCatalog.WikiNightly);
        ReferenceEquals(t1, t2).Should().BeTrue("один чат — та же задача: место в ключ не входит");

        gate.SetResult();
        var r1 = await t1;
        var r2 = await t2;
        r1.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, r1.Reason);
        r2.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, r2.Reason);
        llm.Calls.Should().HaveCount(2, "один прогон: проходы A+B ровно по одному разу, своей модели у ночи нет");
        llm.Calls.Should().OnlyContain(c => c.ActionKey == LocalActionCatalog.WikiIngest,
            "присоединившаяся заявка получает движок того, кто пришёл первым");
    }

    // R-N7a M1: неведомое место разбора — отказ, а не молчаливый уход в облако. Ключ вне
    // каталога (здесь — опечатка «wiki-nighly») у LocalActionRouter.Resolve берёт
    // Tier/Medium, т.е. облако вместо локального места. Guard — на fail-closed входе
    // IngestAsync (WikiIngestService.cs): опоздавшая заявка с таким ключом не присоединяется
    // к чужому прогону и не доходит до модели. Мутация «убрать проверку каталога»: модель
    // зовётся с неведомым ключом — вызов записан в llm.Calls (guard FakeLlm.Answer бьёт
    // внутри вызова и гасится CallModelAsync в причину отказа) — тест красный на assert
    // «модель не зовётся».
    [Fact]
    public async Task Analyze_НеизвестныйActionKey_ОтказБезВызоваМодели()
    {
        AddSession("chat-badkey");
        SetShortHistory("chat-badkey");

        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1));
        var res = await Sut(llm).AnalyzeAsync(User, "chat-badkey", actionKey: "wiki-nighly");

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        res.Reason.Should().Contain("wiki-nighly", "отказ называет неведомое место");
        llm.Calls.Should().BeEmpty("заявка с неведомым местом не доходит до модели");
    }

    // ── W3-6 F1: чужой и несуществующий чат — один message на двоих ──

    [Fact]
    public async Task Analyze_ЧужойИНесуществующийЧат_ОдинаковыйMessage()
    {
        var llm = new FakeLlm();
        var sut = Sut(llm);

        var missing = await sut.AnalyzeAsync(User, "no-such-chat");
        missing.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);

        AddSession("s-foreign");
        _sessions.All.Single(s => s.Id == "s-foreign").OwnerId = "u2";
        var foreign = await sut.AnalyzeAsync(User, "s-foreign");

        // Тот же код и тот же текст: «принадлежит другому владельцу» выдавало бы
        // существование чужого чата — per-owner утечку, которой здесь нет.
        foreign.Code.Should().Be(WikiChatIngestService.CodeNotFound).And.Be(missing.Code);
        foreign.Reason.Should().Be("чат не существует").And.Be(missing.Reason);

        // F3: предпрогонный отказ записан в last как реговой — analyze-status показывает
        // последний исход для обоих.
        sut.GetLast(User, "no-such-chat")!.Result.Reason.Should().Be("чат не существует");
        sut.GetLast(User, "s-foreign")!.Result.Reason.Should().Be("чат не существует");

        llm.Calls.Should().BeEmpty("оба отказа — до обращения к модели");
    }

    // ── W3-6 F3: предпрогонный отказ — последний исход, он записан как реговой ──

    [Fact]
    public async Task Last_ПредпрогонныйОтказЗаписан_КакИРеговой()
    {
        var llm = new FakeLlm();
        var sut = Sut(llm);

        AddSession("s-task", taskExecution: true);
        SetHistory("s-task", [new StoredUserMessage("что-то")]);
        var exec = await sut.AnalyzeAsync(User, "s-task");
        exec.Code.Should().Be(WikiChatIngestService.CodeTaskExecution);
        var lastExec = sut.GetLast(User, "s-task");
        lastExec.Should().NotBeNull("отказ ступени 0 — последний исход этого чата (мутация: снять RecordLast)");
        lastExec!.Result.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        lastExec.Result.Code.Should().Be(WikiChatIngestService.CodeTaskExecution);

        // Реговой отказ (wiki_not_initialized) — то же правило, он и прежде был таким:
        // различия «один пишется, другой нет» больше нет.
        AddSession("s-nowiki", projectId: "p1");
        _sessions.All.Single(s => s.Id == "s-nowiki").OwnerId = "u3";
        var nowiki = await sut.AnalyzeAsync("u3", "s-nowiki");
        nowiki.Code.Should().Be(WikiChatIngestService.CodeWikiNotInitialized);
        sut.GetLast("u3", "s-nowiki")!.Result.Code.Should().Be(WikiChatIngestService.CodeWikiNotInitialized);

        // Без владельца ключа нет: входной отказ не записывается (его и спросить нечем).
        (await sut.AnalyzeAsync("", "s-task")).Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        sut.GetLast("", "s-task").Should().BeNull();

        llm.Calls.Should().BeEmpty();
    }

    // ── W3-6 F2: исключение вне защищённого прогона — заявка живая, не вечный 202 ──

    [Fact]
    public async Task Analyze_ИсключениеПослеЗапуска_СледующаяЗаявкаНеВисиР2()
    {
        AddSession();
        SetShortHistory();
        // Модель не дошла: чтение истории кидает исключение, причина которого сама
        // не читается — guard RunProtectedAsync спотыкается на e.Message и исключение
        // выходит за защищённый прогон (тот самый интервал F2).
        _sessions.ThrowOnRead = n => n == 1 ? new BrokenMessageException() : null;

        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1));
        var sut = Sut(llm);

        // Мутация «убрать finally» (снять сняние и выдачу из finally): запись остаётся,
        // первая заявка не выдаётся никогда — обе строки ниже краснеют по таймауту.
        var first = await sut.AnalyzeAsync(User, ChatId).WaitAsync(TimeSpan.FromSeconds(10));
        first.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected, first.Reason);
        first.Reason.Should().Be("разбор чата не завершился", "сбой guard'а — отказ, а не висящая заявка");
        sut.IsRunning(User, ChatId).Should().BeFalse("неудачный прогон снимает in-flight: чат не «разбирается» вечно");
        sut.GetLast(User, ChatId)!.Result.Reason.Should().Be("разбор чата не завершился",
            "неудачный прогон — тоже последний исход (F3)");

        var second = await sut.AnalyzeAsync(User, ChatId).WaitAsync(TimeSpan.FromSeconds(10));
        second.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied,
            "следующая заявка начинает свой разбор, а не виснет на мёртвой");

        // Неудачный разбор не двинул курсор: второй прогон видит весь чат и доходит до модели.
        llm.Calls.Should().HaveCount(2, "первая заявка не дошла до модели; второй разбор — A+B");
        llm.PromptOf("A", 1).Should().Contain(Marker1, "дельта от нуля: курсор после сбоя не двигался");

        sut.GetLast(User, ChatId)!.Result.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied,
            "последний исход — успешный второй разбор, неудачный его перечитывает");
    }

    // ── W3-6, конкурентность: две заявки одного чата — один прогон модели (мера — число вызовов) ──

    [Fact]
    public async Task Analyze_ДваПараллельныхЗапроса_ОдинПрогонМодели()
    {
        AddSession();
        SetShortHistory();

        var gate = new TaskCompletionSource();
        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1)) { GateA = gate };
        var sut = Sut(llm);

        var t1 = sut.AnalyzeAsync(User, ChatId);
        SpinWait.SpinUntil(() => llm.Calls.Count > 0, 5000).Should().BeTrue("первая заявка реально в прогоне");
        var t2 = sut.AnalyzeAsync(User, ChatId);
        gate.SetResult();
        var results = await Task.WhenAll(t1, t2);

        // Инвариант — одна прогон модели на две заявки. Мера single-flight'а чата —
        // чтения истории: без него (мутация «снять ветку TryGetValue» в AnalyzeAsync)
        // вторая заявка начинает СВОЙ разбор: свой ReadAsync + свой CaptureSession,
        // итого 4 вместо 2 — красное. Модель при этом может остаться вызванной один раз
        // (in-flight очереди заявок S6b-3 склеивает её внутренние заявки по тому же ref):
        // именно поэтому мера — уровень чата, а не только число вызовов модели.
        llm.Calls.Should().HaveCount(2, "две заявки — один разбор: модель A и B ровно по одному разу");
        _sessions.HistoryReads.Should().Be(2, "один разбор: история прочитана его ReadAsync и захватом; вторая заявка своего разбора не начинает");
        results.Should().OnlyContain(r => r.Outcome == WikiChatIngestService.OutcomeApplied);
        results[0].PackageIds.Should().Equal(results[1].PackageIds);

        // И хроннику пишет один разбор: второй (свой) дописал бы те же утверждения вторично.
        var chronicles = ChronicleFiles();
        chronicles.Should().HaveCount(1);
        StatementLines(File.ReadAllText(chronicles[0], System.Text.Encoding.UTF8), Claim1)
            .Should().Be(1, "хроннику дописывает один разбор, не каждая присоединившаяся заявка");
    }

    // ── отмена заявителя не гасит чужую заявку (хвост ревью S6b-3) ──

    [Fact]
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

    [Fact]
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

    // ── доработка S4c (ревью d857da6c): чат-разбор — та же проводка настроек владельца ──
    // Отдельного решения «само / Входящие» у этого пути нет: он зовёт WikiIngestService
    // (WikiChatIngestService.cs:238), а тот читает settings.json владельца (мера 7).
    // Тест подтверждает: выключенный класс доходит и до чат-разбора — пакет ждёт человека.

    [Fact]
    public async Task S4c_ЧатРазбор_КлассВыключен_ПакетЖдётЧеловека_СтраницаНеТронута()
    {
        AddSession();
        SetShortHistory();
        WikiSettingsStore.Mutate(_dir, User, s => s with
        {
            Classes = [new WikiSettingsStore.ClassState(
                WikiPackage.ClassKey("session", WikiPackage.OpAppend), true,
                "класс выключен человеком", DateTimeOffset.UtcNow, null)],
        });

        var llm = new FakeLlm(("A", Claims(Claim1)), ("B", Plan1));
        var res = await Sut(llm).AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeAwaitingHuman, res.Reason);
        var pkg = WikiPackageStore.List(Root).Packages.Single();
        pkg.Status.Should().Be(WikiPackage.StatusProposed);
        pkg.Operations.Should().NotBeEmpty();
        pkg.Operations.Should().OnlyContain(o => o.Decision == WikiPackage.DecisionInbox);
        foreach (var op in pkg.Operations)
            op.Reason.Should().Contain("выключен", "Входящие приходят с объяснением");
        File.ReadAllText(PathOf(PagePath)).Should().NotContain(Claim1,
            "выключенный класс на страницу не пишется: решает человек");
    }

    // ── подменённый ICheapTextRunner: проходы по промптам, gate и побочные эффекты ──

    // Контроллер списка отбора — на тех же подставных сессиях и конфиге: проверка показа
    // (ShownZone) после применённого разбора — без своей копии цепки разбора. Сервис
    // разбора — отдельный свежий экземпляр (S7b0a: конструктор контроллера его требует):
    // разбора он не начинал, его running/last пусты — список показывает состояние стора.
    // ── B6 (находка В1): сводка ночи считается по настоящему пакету разбора чата ──────────────

    private const string HumanCardTitle = "Моя правка";
    private const string AiCardTitle = "Старый чат";
    private const string HumanOld1 = "Тесты на фронте гоняем вручную перед релизом";
    private const string HumanOld2 = "Деплой по пятницам запрещён";
    private const string AiOld1 = "Сборка занимает около десяти минут";
    private const string AiOld2 = "Релиз выходит раз в месяц";
    private const string New1 = "Решено: тесты на фронте гоняет CI на каждый пуш";
    private const string New2 = "Договорились: деплой по пятницам допустим с флагом";
    private const string New3 = "Выяснили: сборка занимает около четырёх минут";
    private const string New4 = "Решено: релиз выходит раз в две недели";

    private static string SeedCard(string title, string origin, string refValue, string extra) => $$"""
        ---
        type: source
        origin: {{origin}}
        ref: {{refValue}}
        hash: 0123456789012345678901234567890123456789012345678901234567890123
        captured: 2026-10-01
        {{extra}}aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{title}}
        """;

    private static string SeedPage(string title, string claim, string cardTitle) => $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{title}}

        ## Сводка
        {{claim}} [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[{{cardTitle}}]] — «{{claim}}»
        """;

    private async Task SeedHumanAndAiTargets()
    {
        var dir = "Проекты/p1/" + WikiSourceCapture.SourceDirName + "/";
        var humanCard = SeedCard(HumanCardTitle, "human", "human:edit-1", "");
        // Карточка человека — прямой записью с записью в манифест (карточку human кладёт только пакет human).
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf(dir + HumanCardTitle + ".md"))!);
        File.WriteAllText(PathOf(dir + HumanCardTitle + ".md"), humanCard, new System.Text.UTF8Encoding(false));
        WikiManifest.RecordApplied(Root, Section,
            new Dictionary<string, string> { [dir + HumanCardTitle + ".md"] = WikiManifest.Sha256Hex(humanCard) });
        var aiCard = SeedCard(AiCardTitle, "session", "session:old#m1-9", "written: 2026-10-01T00:00:00.0000000+00:00\n");
        (await _keeper.WritePageAsync(User, Section, dir + AiCardTitle + ".md", aiCard)).Status.Should().Be(WikiOpStatus.Applied);
        foreach (var (title, claim, card) in new[]
        {
            ("Один", HumanOld1, HumanCardTitle), ("Два", HumanOld2, HumanCardTitle),
            ("Три", AiOld1, AiCardTitle), ("Четыре", AiOld2, AiCardTitle),
        })
            (await _keeper.WritePageAsync(User, Section, $"Проекты/p1/{title}.md", SeedPage(title, claim, card)))
                .Status.Should().Be(WikiOpStatus.Applied);
    }

    [Fact]
    public async Task СводкаНочи_НастоящийРазбор_СчитаетТолькоАвтозаменыИОспариванияУтвержденийЧеловека()
    {
        // Четыре операции в одном пакете: supersede по утверждению человека (считается как замена),
        // conflict по утверждению человека (считается как «отметил противоречие»), conflict и
        // supersede по утверждениям ИИ (не считаются). Мутации по одной: «conflict@human как
        // замена» (в NightSummary), «снять фильтр supersede@human», «ветка Valid в разборе чата
        // всегда continue» — каждая краснит тест.
        await SeedHumanAndAiTargets();
        AddSession();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SetHistory(ChatId,
        [
            new StoredSessionStartedMessage("claude", "default"),
            new StoredUserMessage($"{New1}. {New2}. {New3}. {New4}.", timestamp: nowMs),
            new StoredTextMessage("Принято.", timestamp: nowMs),
            new StoredUserMessage("Давай.", timestamp: nowMs),
            new StoredTextMessage("Готово.", timestamp: nowMs),
        ]);
        static string C(string t) => $$"""{"text":"{{t}}","quote":"{{t}}"}""";
        var claimsJson = $$"""{"claims":[{{C(New1)}},{{C(New2)}},{{C(New3)}},{{C(New4)}}]}""";
        var plan = $$"""
            {"chronicle":{"about":0,"decided":[0],"learned":[],"open_questions":[]},
            "operations":[
            {"op":"supersede","page":"Один","claim":0,"target":"{{HumanOld1}}"},
            {"op":"conflict","page":"Два","claim":1,"target":"{{HumanOld2}}"},
            {"op":"conflict","page":"Три","claim":2,"target":"{{AiOld1}}"},
            {"op":"supersede","page":"Четыре","claim":3,"target":"{{AiOld2}}"}]}
            """;

        var result = await Sut(new FakeLlm(("A", claimsJson), ("B", plan))).AnalyzeAsync(User, ChatId);

        result.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, result.Reason);
        File.ReadAllText(PathOf("Проекты/p1/Один.md")).Should().Contain(New1, "замена по человеку применилась сама");
        result.OverrodeHuman.Should().Be(1, "только supersede@human, применённый ИИ сам");
        result.DisputedPages.Should().Equal(["Проекты/p1/Два.md"]);
    }

    private WikiChatsController Controller()
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, User)], "test"));
        return new WikiChatsController(_sessions, Sut(new FakeLlm()), new WikiChatRecalc(_sessions, _config), _keeper, _config, new WikiFlagOn())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private sealed class FakeLlm : ICheapTextRunner
    {
        public List<(string ActionKey, string Prompt, string Pass)> Calls { get; } = [];
        // Ожидаемое место разбора (N7a): по умолчанию прежнее — guard в Answer проверяет
        // им каждый вызов каждого теста файла; тест ночи выставляет своё.
        public string ExpectedActionKey = LocalActionCatalog.WikiIngest;
        public TaskCompletionSource? GateA; // первый проход A ждёт, пока gate не открыт (на потоке вызова)
        // Асинхронный вариант того же гейта: проход A отдаёт управление, прогон остаётся «в полёте».
        // Нужен там, где проверяется очередь заявок: её вызывающий регистрирует заявку только
        // после первого настоящего await прогона, а с блокирующим GateA весь префикс прогона
        // идёт на потоке вызова внутри lock очереди — записи там нет, и приход второй заявки
        // решался бы гонкой за этот lock.
        public TaskCompletionSource? GateAAsync;
        // Решение 02.11: какими входами пришли вызовы. Без облака — RunLocalOrChosenAsync,
        // с облачной цепочкой — RunDetailedAsync/RunAsync.
        public List<string> NoCloudKeys { get; } = [];
        public List<string> CloudKeys { get; } = [];
        // Локальный раннер «недоступен»: RunLocalOrChosenAsync бросает; облачные входы при этом
        // ответили бы (Answer) — так видно, что облако не позвано.
        public Exception? LocalThrow;

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
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            CloudKeys.Add(actionKey);
            return Task.FromResult(Answer(actionKey, prompt, ct));
        }

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            NoCloudKeys.Add(actionKey);
            if (LocalThrow is not null) throw LocalThrow;
            return Task.FromResult<string?>(Answer(actionKey, prompt, ct));
        }

        public async Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            CloudKeys.Add(actionKey);
            var text = Answer(actionKey, prompt, ct);
            // Первый проход A стоит на асинхронном гейте: вызывающий (await CallModelAsync)
            // отдаёт управление, и к этому моменту заявка уже стоит в очереди.
            if (GateAAsync is not null && Calls[^1].Pass == "A" && CountOf("A") == 1)
                await GateAAsync.Task.ConfigureAwait(false);
            return new OneShotResult(text, new OneShotUsage(10, 0, 0, 5, 0.0, "fake"), 1);
        }

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
            actionKey.Should().Be(ExpectedActionKey, "место разбора — " + ExpectedActionKey);
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
        // Тестовый шов F2: исключение на N-м чтении (1-based) — сбой внутри защищённого
        // прогона, до обращения к модели.
        public Func<int, Exception?>? ThrowOnRead;

        public Session? GetById(string id) => All.FirstOrDefault(s => s.Id == id);
        public IReadOnlyCollection<Session> GetAll() => All;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId)
        {
            var n = Interlocked.Increment(ref HistoryReads);
            if (ThrowOnRead?.Invoke(n) is { } err) throw err;
            return Task.FromResult<IReadOnlyList<StoredMessage>>(
                History.TryGetValue(sessionId, out var h) ? h : new List<StoredMessage>());
        }
        public bool SetWikiMark(string sessionId, string? mark) => true;
    }

    // Исключение, причина которого сама не читается: guard RunProtectedAsync спотыкается
    // на e.Message и исключение выходит за защищённый прогон — ровно тот случай, который
    // держит finally в AnalyzeAsync (F2).
    private sealed class BrokenMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("причина не читается");
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
