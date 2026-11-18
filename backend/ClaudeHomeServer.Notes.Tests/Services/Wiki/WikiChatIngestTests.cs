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
// при неприменённом не двигает курсор и не оставляет половины хроники; при применённом —
// частичный исход (WC-3): отказ позднего окна не перечёркивает применённое, Partial с
// причиной отказа, курсор на конце последнего успешного окна. Плюс доработка S4c (ревью d857da6c):
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
// Утверждение модели в фейке совпадает с цитатой реплики (claim == quote): в пакете
// op.Text = Claim (синтез, S2-8b), дедup меры 2
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

    // Ответ модели в формате синтеза: append на PageTitle, утверждение и цитата — одна и та же
    // реплика (дедуп меры 2 опознаёт повтор по тексту утверждения на странице).
    private static string Synth(string claim) => WikiSynthJson.Append(PageTitle, claim, claim);

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
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, null, flags: new WikiFlagOn());
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
            _sessions, _keeper, new NoSpheres(), _config);

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
            ("S", Synth(Claim1)),
            ("S", Synth(Claim2)));
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
        var promptA2 = llm.PromptOf("S", 2);
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

    // Дефект 2d9df07a и возврат Глеба 3b72f9c9: «Что выяснили» несёт УТВЕРЖДЕНИЕ (Claim, до 600
    // символов), а сноска — дословную цитату 8–300 (ADR-035:276, WikiPlanValidator.RenderApply).
    // Мутации: «строка = цитата вместо claim» и «сноска = claim» (AppendChronicleAsync) — красное здесь.
    [Fact]
    public async Task C1b_ДлинныйClaim_СтрокаНесётУтверждение_СноскаЦитату_КонтрактГейтаСоблюдён()
    {
        AddSession();
        SetShortHistory();
        var longClaim = Claim1 + " — " + new string('в', 350);
        longClaim.Length.Should().BeGreaterThan(WikiPage.MaxQuoteLength);

        var llm = new FakeLlm(
            ("S", WikiSynthJson.Append(PageTitle, longClaim, Claim1)),
            ("S", Synth(Claim2)));
        var sut = Sut(llm);
        var res = await sut.AnalyzeAsync(User, ChatId);
        res.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, res.Reason);

        var chronicles = ChronicleFiles();
        chronicles.Should().HaveCount(1);
        var text = File.ReadAllText(chronicles[0], System.Text.Encoding.UTF8);
        StatementLines(text, longClaim).Should().Be(1, "строка «Что выяснили» — утверждение, а не цитата");
        var parsed = WikiPage.Parse(text).Model!;
        parsed.Footnotes.Should().ContainSingle().Which.Quote.Should().Be(Claim1, "сноска — дословная цитата");
        parsed.Footnotes.Should().OnlyContain(f => !QuoteMatcher.MeasureQuoteLength(f.Quote).OutOfContract);

        // c1: после длинного claim хроника не ломает следующий разбор того же чата.
        _sessions.History[ChatId].Add(new StoredUserMessage($"{Claim2}. Детали: {Marker2}."));
        _sessions.History[ChatId].Add(new StoredTextMessage("Принято."));
        _sessions.History[ChatId].Add(new StoredTextMessage("Готово."));
        var second = await sut.AnalyzeAsync(User, ChatId);
        second.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, second.Reason);
        var text2 = File.ReadAllText(chronicles[0], System.Text.Encoding.UTF8);
        StatementLines(text2, longClaim).Should().Be(1);
        StatementLines(text2, Claim2).Should().Be(1, "второй разбор дописал хронику");
    }

    // Пакет старой формы (без Quotes): цитата — сам Text. Длиннее 300 — сноску не пишем, а
    // утверждение без сноски гейт отвергает, поэтому строка не пишется вовсе: хроника в контракте.
    [Fact]
    public async Task C1c_ПакетБезQuotes_ДлинныйText_СноскаНеЗаписана_ХроникаВКонтракте()
    {
        AddSession();
        SetShortHistory();
        var longText = Claim1 + " — " + new string('в', 340);
        longText.Length.Should().BeGreaterThan(WikiPage.MaxQuoteLength);
        var shortText = Claim2;

        WikiPackage.Operation Legacy(string t) => new()
        {
            Op = WikiPackage.OpAppend, Page = PagePath, PageSection = "Сводка", Text = t,
            Class = WikiPackage.ClassKey(WikiGate.OriginSession, WikiPackage.OpAppend),
            Decision = WikiPackage.DecisionAuto,
            BaseHashes = new Dictionary<string, string> { [PagePath] = new string('a', 64) },
        };
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(), Section = Section,
            Sources = [new WikiPackage.SourceRef("session", ChatId)],
            Operations = [Legacy(longText), Legacy(shortText)],
            Status = WikiPackage.StatusChecking,
        };
        var created = WikiPackageStore.Create(Root, pkg);
        created.Ok.Should().BeTrue(created.Reason);

        var (ok, reason) = await Sut(new FakeLlm()).AppendChronicleAsync(
            User, WikiChatIngestService.HomeOf(_sessions.All[0]), _sessions.All[0], ChatId, [(pkg.Id, "Карточка")]);
        ok.Should().BeTrue(reason);

        var text = File.ReadAllText(ChronicleFiles().Single(), System.Text.Encoding.UTF8);
        text.Should().NotContain(longText, "утверждение без годной сноски в хронику не пишется");
        StatementLines(text, shortText).Should().Be(1);
        var parsed = WikiPage.Parse(text).Model!;
        parsed.Footnotes.Should().ContainSingle().Which.Quote.Should().Be(shortText);
        parsed.Footnotes.Should().OnlyContain(f => !QuoteMatcher.MeasureQuoteLength(f.Quote).OutOfContract);
    }

    // Q1-S4b (риск 8 разреза): после Q1-S4 в claim остаются уцелевшие [[…]], разрешённые
    // в разделе пакета. При записи хроники каждая прогоняется тем же резолвером
    // (WikiGate.StripUnresolvedLinks — второй копии правил нет) уже для раздела дома
    // хроники и его цепочки вверх: боковая или несуществующая ссылка заменяется своим
    // видимым текстом — на диск в таком виде она не попадает ни в каком виде. Мутация
    // «убрать резолв в хронике» (строка statement = WikiGate.StripUnresolvedLinks(...)
    // в AppendChronicleAsync) — красное здесь, и только здесь.
    [Fact]
    public async Task Hronika_SsilkaNaChuzhoyRazdel_SnimaetsyaDoTeksta_BezSkobok()
    {
        AddSession();

        // Страница чужого раздела: пакет стоит в projects/p2, дом хроники — projects/p1,
        // цепочка вверх при NoSpheres — только common. Ни голая, ни префиксная ссылка
        // для дома не резолвится (WikiGate.ResolveLink).
        const string OtherTitle = "Чужое знание";
        var otherKey = "Проекты/p2/" + OtherTitle + ".md";
        var otherText = $$"""
            ---
            type: topic
            aliases: []
            owns: []
            sources: []
            status: active
            ---

            # {{OtherTitle}}

            ## Сводка

            ## Хронология

            ## Противоречия

            ## История
            """;
        (await _keeper.WritePageAsync(User, "projects/p2", otherKey, otherText))
            .Status.Should().Be(WikiOpStatus.Applied, "страница чужого раздела на диске");

        var opBare = new WikiPackage.Operation
        {
            Op = WikiPackage.OpAppend, Page = otherKey, Text = Claim1,
            Claim = $"{Claim1}. См. [[{OtherTitle}]]", Quotes = [Claim1],
            Class = WikiPackage.ClassKey(WikiGate.OriginSession, WikiPackage.OpAppend),
            Decision = WikiPackage.DecisionAuto,
        };
        var opPrefixed = new WikiPackage.Operation
        {
            Op = WikiPackage.OpAppend, Page = otherKey, Text = Claim2,
            Claim = $"{Claim2}. См. [[Проекты/p2/{OtherTitle}]]", Quotes = [Claim2],
            Class = WikiPackage.ClassKey(WikiGate.OriginSession, WikiPackage.OpAppend),
            Decision = WikiPackage.DecisionAuto,
        };
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(), Section = "projects/p2",
            Sources = [new WikiPackage.SourceRef("session", ChatId)],
            Operations = [opBare, opPrefixed], Status = WikiPackage.StatusChecking,
        };
        var created = WikiPackageStore.Create(Root, pkg);
        created.Ok.Should().BeTrue(created.Reason);

        var (ok, reason) = await Sut(new FakeLlm()).AppendChronicleAsync(
            User, WikiChatIngestService.HomeOf(_sessions.All[0]), _sessions.All[0], ChatId, [(pkg.Id, "Карточка")]);
        ok.Should().BeTrue(reason);

        var text = File.ReadAllText(ChronicleFiles().Single(), System.Text.Encoding.UTF8);
        text.Should().NotContain("[[" + OtherTitle + "]]", "голая боковая ссылка не попадает на диск");
        text.Should().NotContain("[[Проекты/p2/" + OtherTitle + "]]", "префиксная ссылка в чужой раздел — тоже");
        StatementLines(text, $"{Claim1}. См. {OtherTitle}").Should().Be(1, "ссылка заменена своим видимым текстом");
        StatementLines(text, $"{Claim2}. См. {OtherTitle}").Should().Be(1, "заголовок без префикса раздела");
    }

    // Q1-S4b, вторая половина инварианта: разрешённая для дома ссылка остаётся как
    // написана — страница дома и страница вверх по цепочке (common) не снимаются.
    // Мутация «снимать всегда» (PlainLinkText без резолва в StripUnresolvedLinks) —
    // красное здесь.
    [Fact]
    public async Task Hronika_SsilkaNaStranicuDomaIVerkhOstaetsyaSsilkoj()
    {
        AddSession();

        // Страница common-раздела — вверх по цепочке дома (projects/p1 → common,
        // WikiSections.UpChain при NoSpheres).
        const string CommonTitle = "Общий порядок";
        var commonKey = "Общее/" + CommonTitle + ".md";
        var commonText = $$"""
            ---
            type: topic
            aliases: []
            owns: []
            sources: []
            status: active
            ---

            # {{CommonTitle}}

            ## Сводка

            ## Хронология

            ## Противоречия

            ## История
            """;
        (await _keeper.WritePageAsync(User, WikiSections.Common, commonKey, commonText))
            .Status.Should().Be(WikiOpStatus.Applied, "страница общего раздела на диске");

        var opHome = new WikiPackage.Operation
        {
            Op = WikiPackage.OpAppend, Page = PagePath, Text = Claim1,
            Claim = $"{Claim1}. См. [[{PageTitle}]]", Quotes = [Claim1],
            Class = WikiPackage.ClassKey(WikiGate.OriginSession, WikiPackage.OpAppend),
            Decision = WikiPackage.DecisionAuto,
        };
        var opUp = new WikiPackage.Operation
        {
            Op = WikiPackage.OpAppend, Page = PagePath, Text = Claim2,
            Claim = $"{Claim2}. См. [[Общее/{CommonTitle}]]", Quotes = [Claim2],
            Class = WikiPackage.ClassKey(WikiGate.OriginSession, WikiPackage.OpAppend),
            Decision = WikiPackage.DecisionAuto,
        };
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(), Section = Section,
            Sources = [new WikiPackage.SourceRef("session", ChatId)],
            Operations = [opHome, opUp], Status = WikiPackage.StatusChecking,
        };
        var created = WikiPackageStore.Create(Root, pkg);
        created.Ok.Should().BeTrue(created.Reason);

        var (ok, reason) = await Sut(new FakeLlm()).AppendChronicleAsync(
            User, WikiChatIngestService.HomeOf(_sessions.All[0]), _sessions.All[0], ChatId, [(pkg.Id, "Карточка")]);
        ok.Should().BeTrue(reason);

        var text = File.ReadAllText(ChronicleFiles().Single(), System.Text.Encoding.UTF8);
        text.Should().Contain("[[" + PageTitle + "]]", "ссылка на страницу дома не снимается");
        text.Should().Contain("[[Общее/" + CommonTitle + "]]", "ссылка вверх по цепочке — тоже");
        StatementLines(text, $"{Claim1}. См. [[{PageTitle}]]").Should().Be(1, "строка записана как написана");
        StatementLines(text, $"{Claim2}. См. [[Общее/{CommonTitle}]]").Should().Be(1);
    }

    // ── «О чём» не трогается при дописывании; новая хроника — без строки «Продолжение чата» ──
    //
    // Доработка B-2d по ревью a6e7a3bc (п. «обязательно до мержа»): правило AppendChronicleAsync
    // («О чём» остаётся как есть — WikiChatIngestService.cs:685-687) и снятое в 1d51512
    // «хроника больше не пишет ссылку на прежний чат» не были закреплены ни одним тестом:
    // мутация Глеба «О чём → пустой список» давала зелёный прогон всего файла (1693/1693).
    // Мутация здесь: «стирать „О чём“ при дописывании» (sections.Add(s) → new() { Name = s.Name,
    // Lines = s.Name == "О чём" ? [] : s.Lines }) — краснеет «легаси-строка осталась».
    // Вторая пара держит снятое: ни один путь разбора больше не пишет «Продолжение чата: [[…]]».
    [Fact]
    public async Task Hronika_NovayaBezSsilkiOChem_LegasiiStrokaPriDopisivaniiSohranyaetsya()
    {
        const string PrevChat = "chat-0";
        const string PrevTitle = "Прежний чат 2026-10-30";

        // Прежний чат: его хроника уже на диске — та же WritePageAsync и та же форма страницы,
        // что у разбора (приём WikiRetractChronicleTests). Она — цель легаси-строки ниже:
        // висящую ссылку гейт применения отверг бы как ссылку на несуществующую страницу
        // (цель проверки — всё состояние раздела: working строится из него целиком,
        // WikiApplyService.cs:656, ссылки проверяет WikiGate.CheckLinks), и тест краснел бы
        // не по проверяемому правилу.
        var prevKey = WikiChatIngestService.ChroniclePath(Section, PrevChat)!;
        var prev = new WikiPage
        {
            Title = PrevTitle,
            Frontmatter = new WikiFrontmatter { Type = WikiFrontmatter.TypeChronicle },
            Sections =
            [
                new() { Name = "О чём", Lines = [] },
                new() { Name = "Что решили", Lines = [] },
                new() { Name = "Что выяснили", Lines = [] },
                new() { Name = "Открытые вопросы", Lines = [] },
            ],
        };
        (await _keeper.WritePageAsync(User, Section, prevKey, prev.Render())).Status
            .Should().Be(WikiOpStatus.Applied, "прежняя хроника на диске");

        AddSession();
        SetShortHistory();

        var llm = new FakeLlm(
            ("S", Synth(Claim1)),
            ("S", Synth(Claim2)));
        var sut = Sut(llm);

        var first = await sut.AnalyzeAsync(User, ChatId);
        first.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, first.Reason);

        var key = WikiChatIngestService.ChroniclePath(Section, ChatId)!;
        var textNew = File.ReadAllText(PathOf(key), System.Text.Encoding.UTF8);

        // Новая хроника — пустое «О чём» и ни одной строки-ссылки: механика «Продолжить в
        // новом чате» убрана из продукта (1d51512), ContinuationLinkLine больше нет.
        textNew.Should().NotContain("Продолжение чата", "новая хроника не пишет ссылку на прежний чат");
        var newPage = WikiPage.Parse(textNew).Model!;
        newPage.Sections.Single(s => s.Name == "О чём").Lines.Should().BeEmpty();

        // Легаси-состояние (так писал старый код до 1d51512): строку дозаписываем руками,
        // в одной копии формата — ContinuationLinkPrefix.
        var legacy = WikiRetractService.ContinuationLinkPrefix + "[[" + PrevTitle + "]]";
        var withLegacy = new WikiPage
        {
            Title = newPage.Title,
            Frontmatter = newPage.Frontmatter,
            Sections = [.. newPage.Sections.Select(s => s.Name == "О чём"
                ? new WikiPage.Section { Name = s.Name, Lines = [.. s.Lines, legacy] }
                : s)],
            Footnotes = newPage.Footnotes,
        };
        (await _keeper.WritePageAsync(User, Section, key, withLegacy.Render())).Status
            .Should().Be(WikiOpStatus.Applied, "легаси-хроника остаётся валидной страницей");

        // Дельта и повторный разбор того же чата (тот же приём, что в C1).
        _sessions.History[ChatId].Add(new StoredUserMessage($"{Claim2}. Детали: {Marker2}."));
        _sessions.History[ChatId].Add(new StoredTextMessage("Принято."));
        _sessions.History[ChatId].Add(new StoredTextMessage("Готово."));

        var second = await sut.AnalyzeAsync(User, ChatId);
        second.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, second.Reason);

        // «О чём» не трогается: написанное раньше — накопленное знание, его снимает откат
        // по источнику (WikiRetractService.RemoveContinuationLinks), а не дописывание.
        var text2 = File.ReadAllText(PathOf(key), System.Text.Encoding.UTF8);
        var page2 = WikiPage.Parse(text2).Model!;
        page2.Sections.Single(s => s.Name == "О чём").Lines.Should().Equal(legacy);
        CountOf(text2, legacy).Should().Be(1, "строка остаётся одна, дописывание её не дублирует");

        // Само дописывание работает: «Что выяснили» выросла обоими разборами.
        StatementLines(text2, Claim1).Should().Be(1);
        StatementLines(text2, Claim2).Should().Be(1, "повторный разбор дописывает «Что выяснили»");
    }

    // ── c2: подмена записи N — курсор сброшен в 0, повторные утверждения не дублируются ──

    [Fact]
    public async Task C2_ПодменаЗаписиN_КурсорСброшен_УтвержденияНеДублируются()
    {
        AddSession();
        SetShortHistory();

        var llm = new FakeLlm(
            ("S", Synth(Claim1)),
            // Второй разбор: история переписана → дельта заново тот же текст → тот же
            // план; дедup (мера 2) отвергает его как повтор, ремонт модели отвечает
            // «нечего добавить».
            ("S", Synth(Claim1)), ("R", NothingJson));
        var sut = Sut(llm);

        var first = await sut.AnalyzeAsync(User, ChatId);
        first.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, first.Reason);

        // Подмена записи N (курсор стоит на 5) — история переписана (§12б этап 1).
        _sessions.History[ChatId][4] = new StoredTextMessage("Готово по-другому.");

        var second = await sut.AnalyzeAsync(User, ChatId);

        // Сброс курсора доказан входом модели: второй проход A видит весь чат,
        // включая маркер закрытой раньше части.
        llm.PromptOf("S", 2).Should().Contain(Marker1, "хэш записи N не совпал — курсор сброшен в 0");

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
            ("S", Synth(Claim1)),
            ("S", Synth(Claim1)), ("R", NothingJson))
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
        llm.CountOf("S").Should().Be(2, "каждая версия разбрана своим проходом A");
        llm.PromptOf("S", 2).Should().Contain(secondTail, "вторая заявка разобрала своё содержание, а не чужое");
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

        var llm = new FakeLlm(("S", Synth(Claim1)));
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
        llm.Calls.Should().HaveCount(1, "модель заново не звалась");
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

        var llm = new FakeLlm(("S", Synth(Claim1)));
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
        llm.Calls.Should().HaveCount(1, "модель заново не звалась");
    }

    // ── c3a: сбой при применении — курсор на месте, хроники нет ──

    [Fact]
    public async Task C3a_СбойПриПрименении_КурсорНаМесте_БезХроники()
    {
        AddSession();
        SetShortHistory();

        var llm = new FakeLlm(("S", Synth(Claim1)));
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
        var failed = WikiChatTriageStore.Get(Root).Entries.Single();
        failed.Cursor.Should().Be(0, "сбой — курсор не двигается");
        failed.RejectStreak.Should().Be(1, "отказ после ответа модели наращивает серию");
        ChronicleFiles().Should().BeEmpty("сбой не оставляет половины хроники");
    }

    // ── c3b: «нечего добавить» — курсор сдвинут, zone и streak записаны, хроники нет ──

    [Fact]
    public async Task C3b_НечегоДобавить_КурсорСдвинут_ЗонаИStreak_ХроникиНет()
    {
        AddSession();
        SetShortHistory();

        var llm = new FakeLlm(("S", NothingJson));
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
        llm.Calls.Should().HaveCount(1, "пустая дельта — без обращения к модели");
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
        var llmNothing = new FakeLlm(("S", NothingJson));
        var sut = Sut(llmNothing);
        var first = await sut.AnalyzeAsync(User, ChatId);
        first.Outcome.Should().Be(WikiChatIngestService.OutcomeNothingNew, first.Reason);

        var shownBefore = ((WikiChatsListDto)((OkObjectResult)Controller().List()).Value!).Items.Single();
        shownBefore.Zone.Should().Be(WikiChatIngestService.ZoneNothingNew, "исход разбора ещё не снят — он и показывается");
        shownBefore.Reason.Should().Be("не о чём писать");

        // Шаг 2. Чат продолжается: дельта за курсором, следующий разбор её применяет.
        _sessions.History[ChatId].Add(new StoredUserMessage($"{Claim2}. Хронология: {Marker2}."));
        _sessions.History[ChatId].Add(new StoredTextMessage("Готово."));

        var llmApplied = new FakeLlm(("S", Synth(Claim2)));
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
        var llm = new FakeLlm(("S", Synth(Claim1)));
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
        var llm = new FakeLlm(("S", Synth(Claim1)));
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

        var llm = new FakeLlm(("S", Synth(Claim1)));
        var sut = Sut(llm);

        var first = await sut.AnalyzeAsync(User, ChatId);
        first.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, first.Reason);

        var again = await sut.AnalyzeAsync(User, ChatId);
        again.Outcome.Should().Be(WikiChatIngestService.OutcomeUpToDate,
            "курсор на конце истории: разбирать нечего — состояние, а не отказ");
        again.Reason.Should().Contain("новых сообщений нет");
        again.Code.Should().BeNull("up_to_date — не отказ, кода у него нет");
        llm.Calls.Should().HaveCount(1, "повторная заявка по закрытому курсору модель не зовёт");
    }

    // ── S7b0a: IsRunning и последний исход (чтение analyze-status) ──

    [Fact]
    public async Task IsRunning_ТолькоВоВремяПрогона_И_ТолькоСвоегоВладельца()
    {
        AddSession();
        SetShortHistory();

        var gate = new TaskCompletionSource();
        var llm = new FakeLlm(("S", Synth(Claim1))) { GateA = gate };
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

        var llm = new FakeLlm(("S", Synth(Claim1)));
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
        var llm = new FakeLlm(("S", Synth(Claim1))) { GateA = gate };
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
        llm.Calls.Should().HaveCount(1, "одна заявка — один прогон; вторая получает её результат");
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
        var llm = new FakeLlm(("S", Synth(Claim1))) { GateA = gate };
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
        llm.Calls.Should().HaveCount(1, "один разбор: модель ровно один раз");
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

        var llm = new FakeLlm(("S", Synth(Claim1)))
        {
            ExpectedActionKey = LocalActionCatalog.WikiNightly,
        };
        var sut = Sut(llm);

        var res = await sut.AnalyzeAsync(User, "chat-night", actionKey: LocalActionCatalog.WikiNightly);
        res.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, res.Reason);
        llm.Calls.Should().HaveCount(1, "один разбор: модель ровно один раз");
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
        var night = new FakeLlm(("S", Synth(Claim1)))
        {
            ExpectedActionKey = LocalActionCatalog.WikiNightly,
        };
        var nres = await Sut(night).AnalyzeAsync(User, "chat-night", actionKey: LocalActionCatalog.WikiNightly);
        nres.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, nres.Reason);
        night.NoCloudKeys.Should().HaveCount(1, "вызов ночи — запросом без облака");
        night.CloudKeys.Should().BeEmpty("облачная цепочка ночью не зовётся");
    }

    [Fact]
    public async Task Analyze_КнопкаБезКлюча_ПрежняяЦепочкаСОблаком()
    {
        AddSession();
        SetShortHistory();
        var button = new FakeLlm(("S", Synth(Claim1)));
        var bres = await Sut(button).AnalyzeAsync(User, ChatId);
        bres.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, bres.Reason);
        button.CloudKeys.Should().HaveCount(1, "кнопка — прежняя цепочка с облаком");
        button.NoCloudKeys.Should().BeEmpty("запрет облака кнопке не выставляется");
    }

    [Fact]
    public async Task Analyze_НочьЛокальныйРаннерНедоступен_ОтказБезОблака_КурсорНеДвинут()
    {
        // Локаль упала посреди вызова: отказ разбора (Rejected), облако не позвано,
        // исключения наружу нет, курсор чата не двинут.
        AddSession("chat-night");
        SetShortHistory("chat-night");
        var llm = new FakeLlm(("S", Synth(Claim1)))
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
        var llm = new FakeLlm(("S", Synth(Claim1))) { GateA = gate };
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
        llm.Calls.Should().HaveCount(1, "один прогон: модель ровно один раз, своей модели у ночи нет");
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

        var llm = new FakeLlm(("S", Synth(Claim1)));
        var res = await Sut(llm).AnalyzeAsync(User, "chat-badkey", actionKey: "wiki-nighly");

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected);
        res.Reason.Should().Contain("wiki-nighly", "отказ называет неведомое место");
        llm.Calls.Should().BeEmpty("заявка с неведомым местом не доходит до модели");
    }

    // ── W3 (И3 путь 2 + одно решение): исполнитель назван аргументом ────────────────────

    [Fact]
    public async Task Analyze_ИсполнительНеОтветил_ОтказСПричинойМаршрута_КурсорНеДвинут()
    {
        // engine проброшен по цепочке до CallModelAsync: null от RunOnLocalEngineDetailedAsync
        // — отказ окна с причиной маршрута (ReasonLocalUnavailable + InputTokens == 0: по ним
        // W5 отличает отказ движка от отказа плана), путь с выбором исполнителя и облако не
        // званы, курсор чата не двинут.
        // Мутация «engine никуда не доходит» (убрать проброс): заявка пошла бы путём ночи
        // (RunLocalOrChosenAsync) и дала applied — EngineCalls пуст и исход красный.
        AddSession("chat-engine");
        SetShortHistory("chat-engine");
        var llm = new FakeLlm(("S", Synth(Claim1)))
        {
            ExpectedActionKey = LocalActionCatalog.WikiNightly,
            EngineDown = true,
        };

        var res = await Sut(llm).AnalyzeAsync(User, "chat-engine",
            actionKey: LocalActionCatalog.WikiNightly, engine: "gemma");

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected, res.Reason);
        res.Reason.Should().Contain(WikiIngestService.ReasonLocalUnavailable)
            .And.NotContain("gemma", "имени движка в причине нет: их несколько (J-W2)");
        res.InputTokens.Should().Be(0, "модель не ответила — понесённого расхода нет");
        llm.EngineCalls.Should().ContainSingle(c => c.Engine == "gemma",
            "путь к движку был, ответа не было — это отказ маршрута, не ответ модели");
        llm.NoCloudKeys.Should().BeEmpty("пути с выбором исполнителя маршрутом здесь нет");
        llm.CloudKeys.Should().BeEmpty("и в облако отказ не уходит");
        WikiChatTriageStore.Get(Root).Entries
            .Where(e => e.SessionId == "chat-engine")
            .Should().NotContain(e => e.Cursor > 0, "курсор не двинут");
    }

    [Fact]
    public async Task Гонка_ДвеЗаявкиРазнымиEngine_ОдинПрогон()
    {
        // Кнопка начала разбор (своего движка у неё нет); в середине её прогона приходит ночь
        // с названным исполнителем. In-flight-ключ owner|chat|home ни места, ни исполнителя не
        // содержит (шапка сервиса): одна задача на двоих, движок — того, кто пришёл первым.
        // Мутация «engine входит в in-flight-ключ» → ReferenceEquals краснеет: тот же чат
        // получил бы второй разбор и вторую модель.
        AddSession();
        SetShortHistory();

        var gate = new TaskCompletionSource();
        var llm = new FakeLlm(("S", Synth(Claim1))) { GateA = gate };
        var sut = Sut(llm);

        var t1 = sut.AnalyzeAsync(User, ChatId);
        SpinWait.SpinUntil(() => llm.Calls.Count > 0, 5000).Should().BeTrue("первая заявка в прогоне");
        var t2 = sut.AnalyzeAsync(User, ChatId, actionKey: LocalActionCatalog.WikiNightly, engine: "gemma");
        ReferenceEquals(t1, t2).Should().BeTrue("один чат — та же задача: исполнитель в ключ не входит");

        gate.SetResult();
        var r1 = await t1;
        var r2 = await t2;
        r1.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, r1.Reason);
        r2.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, r2.Reason);
        llm.Calls.Should().HaveCount(1, "один прогон: модель ровно один раз");
        llm.EngineCalls.Should().BeEmpty("присоединившаяся заявка своей моделью не оборачивается");
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

        var llm = new FakeLlm(("S", Synth(Claim1)));
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
        llm.Calls.Should().HaveCount(1, "первая заявка не дошла до модели; второй разбор — один вызов");
        llm.PromptOf("S", 1).Should().Contain(Marker1, "дельта от нуля: курсор после сбоя не двигался");

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
        var llm = new FakeLlm(("S", Synth(Claim1))) { GateA = gate };
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
        llm.Calls.Should().HaveCount(1, "две заявки — один разбор: модель ровно один раз");
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
        var llm = new FakeLlm(("S", Synth(Claim1))) { GateA = gate };
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
        llm.Calls.Should().HaveCount(1);
    }

    // ── сбой посреди окон и частичный исход (урок S6e-2; WC-3) ──────────────────────

    // Дельта длиннее окна (35k токенов; мера — байты/3, WikiChatSlicer.cs:57):
    // 18 ответов по 6k латинских символов = 36k токенов → ровно два окна S6j.
    // Латинский и 6k, а не кириллица и 12k: кириллица в два байта делает реплику
    // 24 КБ — первая реплика второго окна не полезла бы в выдержку карточки (8 КБ,
    // целыми репликами) и второе окно отбилось бы захватом, а не моделью. Последняя
    // реплика — человеческая: в диапазоне второго окна обязана быть реплика человека
    // (§12а: агентскую цитирует только человеческая того же диапазона).
    private List<StoredMessage> TwoWindowHistory()
    {
        var history = new List<StoredMessage>
        {
            new StoredSessionStartedMessage("claude", "default"),
            new StoredUserMessage($"{Claim1}. Хронология: {Marker1}."),
        };
        for (var i = 0; i < 18; i++) history.Add(new StoredTextMessage(new string('x', 6_000)));
        history.Add(new StoredUserMessage("Хватит. Итога нет."));
        return history;
    }

    // Тот же слайсинг, что делает сервис (от 1 до конца истории): конец первого окна —
    // позиция курсора частичного исхода; два окна — сам сценарий теста.
    private int EndOfFirstWindow(List<StoredMessage> history)
    {
        var windows = WikiChatSlicer.Windows(history, 1, history.Count);
        windows.Should().HaveCount(2, "дельта — ровно два окна");
        return windows[0].Lines[^1].Index;
    }

    // Дельта из пяти окон: та же фикстура, что TwoWindowHistory, только длиннее — 63
    // служебные реплики по 6000 знаков. Границы окон считает слайсер (WindowTokens 35k
    // с перекрытием в 2 реплики), тест лишь проверяет число окон и берёт конец первого
    // из них — как EndOfFirstWindow для двух.
    private List<StoredMessage> FiveWindowHistory()
    {
        var history = new List<StoredMessage>
        {
            new StoredSessionStartedMessage("claude", "default"),
            new StoredUserMessage($"{Claim1}. Хронология: {Marker1}."),
        };
        for (var i = 0; i < 63; i++) history.Add(new StoredTextMessage(new string('x', 6_000)));
        history.Add(new StoredUserMessage("Хватит. Итога нет."));
        return history;
    }

    // Вне шага WC-3: только «нечего добавить» до отказа — по-старому отказ всего разбора:
    // курсор на месте, половины хроники нет.
    [Fact]
    public async Task СбойПосредиОкон_НичегоНеПрименено_КурсорНаМесте_БезПоловиныХроники()
    {
        AddSession();
        SetHistory(ChatId, TwoWindowHistory());

        var llm = new FakeLlm(
            ("S", NothingJson),
            ("S", "модель ответила прозой вместо JSON"));
        var res = await Sut(llm).AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected, res.Reason);
        res.Reason.Should().Contain("окна m", "названо окно, на котором разбор оборвался");
        llm.CountOf("S").Should().Be(2, "первое окно разобрано («нечего добавить»), второе — отказано");
        var refusedEntry = WikiChatTriageStore.Get(Root).Entries.Single();
        refusedEntry.Cursor.Should().Be(0, "отказ при неприменённом курсор не двигает");
        refusedEntry.RejectStreak.Should().Be(1, "отказ после ответа модели наращивает серию");
        ChronicleFiles().Should().BeEmpty("хроника не начинается половиной разбора");
    }

    // c1 (WC-3): окно 1 применил пакет, окно 2 отказано — частичный исход: applied с
    // Partial и причиной отказа, хроника по применённому, курсор на конце окна 1 —
    // не назад (0) и не за отказанное окно (history.Count).
    [Fact]
    public async Task ДваОкна_ВтороеОтказано_ЧастичныйИсход()
    {
        AddSession();
        var history = TwoWindowHistory();
        SetHistory(ChatId, history);
        var endOfFirst = EndOfFirstWindow(history);

        var llm = new FakeLlm(
            ("S", Synth(Claim1)),
            ("S", "модель ответила прозой вместо JSON"));
        var res = await Sut(llm).AnalyzeAsync(User, ChatId);

        // Мутация «вернуть return Refused в default»: исход rejected, Partial нет,
        // курсора и хроники нет — красное здесь.
        res.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, res.Reason);
        res.Partial.Should().BeTrue("разбор оборвался на позднем окне — применённое осталось");
        res.Reason.Should().Contain("окна m", "причина отказанного окна остаётся в результате");
        res.PackageIds.Should().HaveCount(1, "пакет применённого окна не отменён отказом позднего");

        var entry = WikiChatTriageStore.Get(Root).Entries.Single();
        entry.Cursor.Should().Be(endOfFirst, "курсор на конце последнего успешного окна");
        entry.Cursor.Should().NotBe(history.Count, "курсор не прыгает за отказанное окно");
        entry.CursorHash.Should().NotBeNullOrEmpty("хэш записи, на которой стоит курсор");

        // Хроника — по применённым; страница знаний пополнена применённым окном.
        var chronicles = ChronicleFiles();
        chronicles.Should().HaveCount(1);
        StatementLines(File.ReadAllText(chronicles[0], System.Text.Encoding.UTF8), Claim1)
            .Should().Be(1, "хроника по применённому написана");
        File.ReadAllText(PathOf(PagePath)).Should().Contain(Claim1, "применённое окно осталось на странице");
        llm.CountOf("S").Should().Be(2, "первое окно разобрано, второе отказано, дальше цикл не идёт");
    }

    // J-P1c (разрез §5): чат из пяти окон, вызов модели второго окна отказан — так выглядит
    // «входной бюджет кончился» на этом пути (движок отказывает вызов, а не окно как таковое).
    // Первое окно применено. Курсор — на конце последнего применённого окна: не на 0
    // (применённое окно не стирается) и не на конце чата (отказанное окно и три за ним не
    // разбирались). Тот же механизм, что у двухоконного WC-3, но на длинной дельте:
    // проверка именно в том, что курсор не прыгает к history.Count.
    [Fact]
    public async Task ПятьОкон_БюджетОборвалсяНаВтором_КурсорНаКонцеПервого()
    {
        AddSession();
        var history = FiveWindowHistory();
        SetHistory(ChatId, history);
        var windows = WikiChatSlicer.Windows(history, 1, history.Count);
        windows.Should().HaveCount(5, "дельта — ровно пять окон");
        var endOfFirst = windows[0].Lines[^1].Index;

        var llm = new FakeLlm(
            ("S", Synth(Claim1)),
            ("S", "модель ответила прозой вместо JSON"));
        var res = await Sut(llm).AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, res.Reason);
        res.Partial.Should().BeTrue("разбор оборвался на втором окне — применённое осталось");
        res.Reason.Should().Contain("окна m", "причина отказанного окна остаётся в результате");

        var entry = WikiChatTriageStore.Get(Root).Entries.Single();
        entry.Cursor.Should().Be(endOfFirst, "курсор на конце последнего применённого окна");
        entry.Cursor.Should().NotBe(0, "применённое окно не возвращает курсор в 0");
        entry.Cursor.Should().NotBe(history.Count, "курсор не прыгает за отказанное окно к концу чата");
        entry.CursorHash.Should().NotBeNullOrEmpty("хэш записи, на которой стоит курсор");

        llm.CountOf("S").Should().Be(2, "окна 3—5 после отказа не разбираются");
    }

    // c3 (WC-3): хроника частичного исхода не записана — исход refused, курсор на месте
    // (его Upsert идёт только после хроники).
    [Fact]
    public async Task ХроникаНеЗаписана_ЧастичныйИсход_КурсорНаМесте()
    {
        AddSession();
        SetHistory(ChatId, TwoWindowHistory());
        // На пути хроники — страница, которая хроникой не читается: дописать её нечем,
        // тот же сбой хроники, что у полного разбора. Пишется Хранителем (не прямым файлом):
        // страница обязана быть в манифесте раздела — иначе её видит не хроника, а захват
        // окна («чужие файлы вне манифеста»).
        var chronicleKey = WikiChatIngestService.ChroniclePath(Section, ChatId)!;
        (await _keeper.WritePageAsync(User, Section, chronicleKey, PageBase.Replace(PageTitle, ChatId)))
            .Status.Should().Be(WikiOpStatus.Applied);

        var llm = new FakeLlm(
            ("S", Synth(Claim1)),
            ("S", "модель ответила прозой вместо JSON"));
        var res = await Sut(llm).AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected, res.Reason);
        res.Reason.Should().Contain("не читается как хроника", "сбой хроники назван причиной отказа");
        llm.CountOf("S").Should().Be(2, "первое окно применилось, второе отказано");
        WikiChatTriageStore.Get(Root).Entries.Should().BeEmpty("сбой хроники курсор не двигает");
    }

    // c2 (WC-3), гонка: две заявки одного чата при частичном исходе — один разбор:
    // один пакет, одна хроника, один курсор. Мутация «снять проверку _inFlight» в
    // AnalyzeAsync: вторая заявка начинает свой разбор — свои ReadAsync и захваты
    // (HistoryReads больше 3) и свой вызов модели (CountOf("S") больше 2) — красное.
    [Fact]
    public async Task ДваРазбораОдногоЧата_ЧастичныйИсход_ОдинПакетОднаХроника()
    {
        AddSession();
        var history = TwoWindowHistory();
        SetHistory(ChatId, history);
        var endOfFirst = EndOfFirstWindow(history);

        var gate = new TaskCompletionSource();
        var llm = new FakeLlm(
            ("S", Synth(Claim1)),
            ("S", "модель ответила прозой вместо JSON")) { GateA = gate };
        var sut = Sut(llm);

        var t1 = sut.AnalyzeAsync(User, ChatId);
        SpinWait.SpinUntil(() => llm.Calls.Count > 0, 5000).Should().BeTrue("первая заявка реально в прогоне");
        var t2 = sut.AnalyzeAsync(User, ChatId);
        gate.SetResult();
        var results = await Task.WhenAll(t1, t2);

        results.Should().OnlyContain(r => r.Outcome == WikiChatIngestService.OutcomeApplied && r.Partial,
            "обе заявки получают один и тот же разбор");
        results[0].PackageIds.Should().Equal(results[1].PackageIds);
        results[0].PackageIds.Should().HaveCount(1);
        _sessions.HistoryReads.Should().Be(3, "один разбор: его ReadAsync и захваты двух окон; вторая заявка своего разбора не начинает");
        llm.CountOf("S").Should().Be(2, "два окна одного разбора: первое применено, второе отказано");

        WikiPackageStore.List(Root).Packages.Should().HaveCount(1, "пакет один, не на каждую заявку");
        var chronicles = ChronicleFiles();
        chronicles.Should().HaveCount(1);
        StatementLines(File.ReadAllText(chronicles[0], System.Text.Encoding.UTF8), Claim1)
            .Should().Be(1, "хронику пишет один разбор, не каждая присоединившаяся заявка");

        var entry = WikiChatTriageStore.Get(Root).Entries.Single();
        entry.Cursor.Should().Be(endOfFirst, "курсор — конец последнего успешного окна одного разбора");
    }

    // ── WC-4a: фактические входные токены разбора доходят до его результата ────────────

    // c1: два окна — сумма входных токенов ВСЕХ вызовов модели обоих окон, на том же
    // сценарии частичного исхода WC-3: окно 2 отказано ПОСЛЕ ответившего вызова, и его
    // расход понесён — он остаётся в исходе. Мутации: «не прибавлять расход окна»
    // (снять inputTokens += ing.InputTokens) → 0; «прибавлять только успешные окна»
    // (перенести прибавление в ветки applied/awaiting/nothing_new) → 100 вместо 300.
    [Fact]
    public async Task ДваОкна_InputTokensСуммируются()
    {
        AddSession();
        SetHistory(ChatId, TwoWindowHistory());

        var llm = new FakeLlm(
            ("S", Synth(Claim1)),
            ("S", "модель ответила прозой вместо JSON"))
        { InputTokensByCall = [100, 200] };
        var res = await Sut(llm).AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, res.Reason);
        res.Partial.Should().BeTrue("тот же сценарий, что у частичного исхода WC-3");
        llm.CountOf("S").Should().Be(2, "оба окна дошли до модели");
        res.InputTokens.Should().Be(300,
            "100 первого окна и 200 ответившего, но отказанного второго: расход не зависит от исхода окна");
    }

    // c2: штатный «нечего добавить» после ответившего вызова — расход не ноль: он был,
    // даже с тем, что писать нечего. Мутация «nothing_new обнуляет расход» → 0.
    [Fact]
    public async Task ОкноНичегоНового_InputTokensНеНоль()
    {
        AddSession();
        SetShortHistory();

        var llm = new FakeLlm(("S", NothingJson))
        { InputTokensByCall = [100] };
        var res = await Sut(llm).AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeNothingNew, res.Reason);
        res.InputTokens.Should().Be(100, "«нечего добавить» не отменяет понесённого расхода");
    }

    // ── C2R-Sh2: курсор при awaiting_human, RejectStreak, пропуск окна после 3 отказов ──

    private void DisableAppendClass() => WikiSettingsStore.Mutate(_dir, User, s => s with
    {
        Classes = [new WikiSettingsStore.ClassState(
            WikiPackage.ClassKey("session", WikiPackage.OpAppend), true,
            "класс выключен человеком", DateTimeOffset.UtcNow, null)],
    });

    // Окно отдано во «Входящие» (одни awaiting_human): курсор встаёт на конец истории,
    // хроники нет, повторный разбор — up_to_date и не плодит второй пакет.
    // Мутация «не двигать курсор при applied.Count == 0» → второй разбор повторяет окно.
    [Fact]
    public async Task C2R_AwaitingHuman_КурсорДвигается_ПовторНеПлодитПакет()
    {
        AddSession();
        SetShortHistory();
        DisableAppendClass();

        var res = await Sut(new FakeLlm(("S", Synth(Claim1)))).AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeAwaitingHuman, res.Reason);
        var entry = WikiChatTriageStore.Get(Root).Entries.Single();
        entry.Cursor.Should().Be(5, "окно разобрано: курсор на конце истории");
        entry.CursorHash.Should().NotBeNullOrEmpty();
        ChronicleFiles().Should().BeEmpty("хроника — только из применённых пакетов");

        var againLlm = new FakeLlm();
        var again = await Sut(againLlm).AnalyzeAsync(User, ChatId);
        againLlm.Calls.Should().BeEmpty("повторный разбор без новых реплик модель не зовёт");
        again.Outcome.Should().Be(WikiChatIngestService.OutcomeUpToDate, again.Reason);
        WikiPackageStore.List(Root).Packages.Should().HaveCount(1, "дубль-пакета нет");
    }

    // Три отказа подряд ПОСЛЕ ответа модели: на третьем курсор переходит через окно,
    // зона rejected_skipped с причиной последнего отказа, серия обнулена.
    // Мутация «порог 3 → 4» или «не наращивать серию» → красное на втором/третьем прогоне.
    [Fact]
    public async Task C2R_ТриОтказаПослеМодели_КурсорПереходитЧерезОкно()
    {
        AddSession();
        SetShortHistory();

        for (var i = 1; i <= 2; i++)
        {
            var r = await Sut(new FakeLlm(("S", "модель ответила прозой вместо JSON"))).AnalyzeAsync(User, ChatId);
            r.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected, r.Reason);
            var e = WikiChatTriageStore.Get(Root).Entries.Single();
            e.RejectStreak.Should().Be(i);
            e.Cursor.Should().Be(0, "до третьего отказа окно не пропускается");
        }

        var third = await Sut(new FakeLlm(("S", "модель ответила прозой вместо JSON"))).AnalyzeAsync(User, ChatId);
        third.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected, third.Reason);
        third.Reason.Should().Contain("пропущено");
        var skipped = WikiChatTriageStore.Get(Root).Entries.Single();
        skipped.Cursor.Should().Be(5);
        skipped.Zone.Should().Be(WikiChatIngestService.ZoneRejectedSkipped);
        skipped.Reason.Should().Contain("не удался", "причина последнего отказа");
        skipped.RejectStreak.Should().Be(0);
    }

    // Отказ ДО модели (InputTokens == 0) серию не наращивает; разобранный исход её обнуляет.
    [Fact]
    public async Task C2R_ОтказДоМодели_СерияНеРастёт_РазобранныйИсходОбнуляет()
    {
        AddSession();
        SetShortHistory();

        for (var i = 0; i < 4; i++)
        {
            var llm = new FakeLlm(("S", "ответ без JSON")) { InputTokensByCall = [0] };
            var r = await Sut(llm).AnalyzeAsync(User, ChatId);
            r.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected, r.Reason);
        }
        WikiChatTriageStore.Get(Root).Entries.Should().BeEmpty("отказы до модели стор не трогают");

        (await Sut(new FakeLlm(("S", "ответ без JSON"))).AnalyzeAsync(User, ChatId)).Outcome
            .Should().Be(WikiChatIngestService.OutcomeRejected);
        WikiChatTriageStore.Get(Root).Entries.Single().RejectStreak.Should().Be(1);

        var ok = await Sut(new FakeLlm(("S", NothingJson))).AnalyzeAsync(User, ChatId);
        ok.Outcome.Should().Be(WikiChatIngestService.OutcomeNothingNew, ok.Reason);
        WikiChatTriageStore.Get(Root).Entries.Single().RejectStreak.Should().Be(0);
    }

    // Доработка C2R-Sh2 (ревью 59d1dfee, M1): применённый разбор обнуляет серию отказов —
    // писатель WikiChatIngestService.cs:543 пишет RejectStreak: 0 поверх прежней серии.
    // Мутация «писатель сохраняет cur.RejectStreak» → красное: серия остаётся 2.
    [Fact]
    public async Task C2R_ПрименённыйРазбор_ОбнуляетСериюОтказов()
    {
        AddSession();
        SetShortHistory();
        WikiChatTriageStore.Upsert(Root, new WikiChatTriageEntry(
            ChatId, 0, null, null, null, 0, default, RejectStreak: 2)).Ok.Should().BeTrue();

        var res = await Sut(new FakeLlm(("S", Synth(Claim1)))).AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, res.Reason);
        var entry = WikiChatTriageStore.Get(Root).Entries.Single();
        entry.RejectStreak.Should().Be(0, "разбор с применённым пакетом не продолжает ряд отказов");
    }

    // Доработка C2R-Sh2 (ревью 59d1dfee, M1): и исход «Входящих» обнуляет серию — писатель
    // WikiChatIngestService.cs:516. Мутация «писатель сохраняет cur.RejectStreak» → красное.
    [Fact]
    public async Task C2R_ИсходAwaitingHuman_ОбнуляетСериюОтказов()
    {
        AddSession();
        SetShortHistory();
        DisableAppendClass();
        WikiChatTriageStore.Upsert(Root, new WikiChatTriageEntry(
            ChatId, 0, null, null, null, 0, default, RejectStreak: 2)).Ok.Should().BeTrue();

        var res = await Sut(new FakeLlm(("S", Synth(Claim1)))).AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeAwaitingHuman, res.Reason);
        WikiChatTriageStore.Get(Root).Entries.Single().RejectStreak
            .Should().Be(0, "окно отдано человеку разобранным: серия не переносится в следующую ночь");
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

        var llm = new FakeLlm(("S", Synth(Claim1)));
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
        // Две операции в одном пакете: conflict по утверждению человека (считается как «отметил
        // противоречие»), conflict по утверждению ИИ (не считается). Мутации по одной:
        // «conflict@human как замена» (в NightSummary), «ветка Valid в разборе чата всегда
        // continue» — каждая краснит тест.
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
        // Синтез supersede не порождает (форма ValidateSynth: create/append/conflict/link; замена —
        // отдельное решение С4), поэтому в бою замена человека недостижима: считается только
        // conflict по утверждению человека. Счёт замен — в NightSummary (WikiNightlyServiceTests).
        static Dictionary<string, object> Op(string page, string claim, string target) =>
            WikiSynthJson.Op("conflict", page, claim + " — настоящее противоречие", [claim], target: target);
        var synth = WikiSynthJson.Ok(
            Op("Два", New2, HumanOld2),
            Op("Три", New3, AiOld1));

        var result = await Sut(new FakeLlm(("S", synth))).AnalyzeAsync(User, ChatId);

        result.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, result.Reason);
        result.OverrodeHuman.Should().Be(0, "синтез не заменяет утверждений человека");
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
        // Входные токены вызовов по их порядку (WC-4a): тест на сумму результата ставит свои
        // значения — тогда «не прибавить окно» видно числом, а не только фактом «не ноль».
        // Не задан — прежние 10 на каждый вызов (существующие тесты метрик не проверяют).
        public List<long>? InputTokensByCall;
        // W3 (И3 путь 2): исполнитель назван аргументом — путь только на нём, без выбора
        // маршрута и без облака. EngineCalls — что ушло на движок; EngineDown — движок не
        // ответил (null без исключения): вызов раннером зарегистрирован, модели нет.
        public List<(string Engine, string ActionKey, string Prompt)> EngineCalls { get; } = [];
        public bool EngineDown;

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

        public bool UsesLocal(string actionKey) => true;
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
            if (GateAAsync is not null && Calls[^1].Pass == "S" && CountOf("S") == 1)
                await GateAAsync.Task.ConfigureAwait(false);
            // Answer уже записал этот вызов в Calls: его позиция — индекс метрик теста (WC-4a).
            var input = InputTokensByCall is { } t && Calls.Count <= t.Count ? t[Calls.Count - 1] : 10L;
            return new OneShotResult(text, new OneShotUsage(input, 0, 0, 5, 0.0, "fake"), 1);
        }

        public Task<OneShotResult?> RunOnLocalEngineDetailedAsync(string engine, string actionKey,
            string prompt, string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            EngineCalls.Add((engine, actionKey, prompt));
            if (EngineDown)
                return Task.FromResult<OneShotResult?>(null);
            // Ответ и usage — как у локального шага цепочки (вход 1 000, CostUsd 0): расход
            // новой ветки считается так же (WC-4c).
            return Task.FromResult<OneShotResult?>(new OneShotResult(
                Answer(actionKey, prompt, ct), new OneShotUsage(1_000, 0, 0, 50, 0.0, engine), 1));
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
                : prompt.Contains("single pass") ? "S"
                : "?";
            Calls.Add((actionKey, prompt, pass));
            actionKey.Should().Be(ExpectedActionKey, "место разбора — " + ExpectedActionKey);
            if (pass == "S" && GateA is not null && CountOf("S") == 1)
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

    // C2R-Sh2b: исход awaiting_human пишет зону awaiting_human и причину с числом операций,
    // а не пустую зону. Мутация «зона null» → красное.
    [Fact]
    public async Task C2R_AwaitingHuman_ПишетЗонуИПричинуСЧисломОпераций()
    {
        AddSession();
        SetShortHistory();
        DisableAppendClass();

        var res = await Sut(new FakeLlm(("S", Synth(Claim1)))).AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeAwaitingHuman, res.Reason);
        var entry = WikiChatTriageStore.Get(Root).Entries.Single();
        entry.Zone.Should().Be(WikiChatIngestService.ZoneAwaitingHuman);
        var ops = WikiPackageStore.List(Root).Packages.Single().Operations.Count;
        entry.Reason.Should().Be($"{ops} {WikiChatIngestService.OperationsWord(ops)} на решении");
        ops.Should().BeGreaterThan(0);
    }

    // C2R-Sh2b: склонение причины. Мутация «всегда „операций“» → красное.
    [Theory]
    [InlineData(1, "операция")]
    [InlineData(2, "операции")]
    [InlineData(4, "операции")]
    [InlineData(5, "операций")]
    [InlineData(11, "операций")]
    [InlineData(12, "операций")]
    [InlineData(21, "операция")]
    [InlineData(22, "операции")]
    [InlineData(25, "операций")]
    public void OperationsWord_СклонениеПоЧислу(int n, string word) =>
        WikiChatIngestService.OperationsWord(n).Should().Be(word);

    // Цитаты нет ни в одном окне: операция с ней отклоняется кодом и уходит в Dropped окна.
    private const string NoQuote = "Цитаты в источнике нет вообще";

    // 20 открытых пакетов на всю вики (лимит ADR-035, решение 4): следующий пакет, который
    // просит окно разбора, хранилище не примет — так ставится сбой записи пакета.
    private void FillOpenPackages()
    {
        for (var i = 0; i < WikiPackage.MaxOpenPackages; i++)
        {
            var created = WikiPackageStore.Create(Root, new WikiPackage
            {
                Id = WikiPackageStore.NewId(),
                Section = Section,
                Sources = [new WikiPackage.SourceRef(WikiGate.OriginSession, "session:other-" + i + "#m1-1")],
                Operations =
                [
                    new WikiPackage.Operation
                    {
                        Op = WikiPackage.OpAppend,
                        Page = PagePath,
                        Text = "утверждение " + i,
                        Class = WikiPackage.ClassKey(WikiGate.OriginSession, WikiPackage.OpAppend),
                        Decision = WikiPackage.DecisionAuto,
                    },
                ],
                Status = WikiPackage.StatusChecking,
            });
            created.Ok.Should().BeTrue($"пакет для лимита обязан быть поставлен: {created.Reason ?? created.Code}");
        }
        WikiPackageStore.CountOpen(Root).Should().Be(WikiPackage.MaxOpenPackages);
    }

    // Минор ревью C2R-Sh1, п.1: отброшенные поштучным приёмом суммируются по всем окнам,
    // первая причина — из первого окна с отказами. Мутации: «убрать dropped += ing.Dropped»
    // → 0 вместо 3 (сумма не набирается вовсе; сверено мутацией 11.11); «брать причину
    // последнего окна» → чужая причина. Обе красные.
    [Fact]
    public async Task ДваОкна_DroppedСуммируются_ПерваяПричинаИзПервогоОкна()
    {
        AddSession();
        var history = TwoWindowHistory();
        history.Add(new StoredUserMessage($"{Claim2}. Детали: {Marker2}."));
        SetHistory(ChatId, history);
        EndOfFirstWindow(history);

        var llm = new FakeLlm(
            ("S", WikiSynthJson.Ok(
                WikiSynthJson.Op("append", PageTitle, Claim1, [Claim1]),
                WikiSynthJson.Op("append", PageTitle, "Отброленное первого окна", [NoQuote]))),
            ("S", WikiSynthJson.Ok(
                WikiSynthJson.Op("append", PageTitle, Claim2, [Claim2]),
                WikiSynthJson.Op("append", PageTitle, "Отброленное второго окна, первое", [NoQuote]),
                WikiSynthJson.Op("append", PageTitle, "Отброленное второго окна, второе", [NoQuote]))));
        var res = await Sut(llm).AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeApplied, res.Reason);
        res.Dropped.Should().Be(3, "1 из первого окна и 2 из второго: сумма по всем разобранным окнам");
        res.FirstDropReason.Should().NotBeNull().And.Contain("отсутствует в источнике дословно",
            "первая причина принадлежит первому окну с отказами");
    }

    // Миноры ревью C2R-Sh1, п.2 + п.3 на живом пути: окно, чей пакет не принят хранилищем,
    // отказывает весь разбор — и отброшенное при сборке этого пакета доходит до результата.
    // Мутации: «RejectedUsed без WithSectionIssues» или «не нести dropped/firstDropReason в
    // Refused» → Dropped 0 и пустая причина. Обе красные.
    [Fact]
    public async Task МагазинНеПринялПакетОкна_ОтказРазбораНесётDroppedИПервуюПричину()
    {
        AddSession();
        SetShortHistory();
        FillOpenPackages();

        var llm = new FakeLlm(("S", WikiSynthJson.Ok(
            WikiSynthJson.Op("append", PageTitle, Claim1, [Claim1]),
            WikiSynthJson.Op("append", PageTitle, "Отброленное первое", [NoQuote]),
            WikiSynthJson.Op("append", PageTitle, "Отброленное второе", [NoQuote]))));
        var res = await Sut(llm).AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected, res.Reason);
        res.Reason.Should().Contain("магазин пакетов не принял пакет");
        res.Dropped.Should().Be(2, "отказанное окно не стирает отброшенное: оно было понесено");
        res.FirstDropReason.Should().NotBeNull().And.Contain("отсутствует в источнике дословно",
            "причина отброшенной операции доходит вместе с числом");
        llm.CountOf("S").Should().Be(1, "разбор оборвался на первом окне");
    }

    // T-C1 (хвост ffb0cdf3): сбой записи стора разбора после применённого окна — отказ
    // несёт уже понесённое: Dropped и первую причину, как пути WindowFailure/RecordRejection.
    // Стор ставится нечитаемым перед вызовом модели: файла до первого разбора нет — Get в
    // начале прогона видит «нет файла» как норму, а Update в конце ловит Invalid.
    // Мутация «не нести dropped/firstDropReason в Refused на пути upApplied» → Dropped 0 и
    // пустая причина: красен ровно этот тест.
    [Fact]
    public async Task СбойЗаписиСтораПослеПрименённогоОкна_ОтказНесётDroppedИПервуюПричину()
    {
        AddSession();
        SetShortHistory();

        var llm = new FakeLlm(("S", WikiSynthJson.Ok(
            WikiSynthJson.Op("append", PageTitle, Claim1, [Claim1]),
            WikiSynthJson.Op("append", PageTitle, "Отброленное первое", [NoQuote]),
            WikiSynthJson.Op("append", PageTitle, "Отброленное второе", [NoQuote]))));
        llm.BeforeNextCall(() => File.WriteAllText(
            Path.Combine(Root, "keeper", "chat-triage.json"), "не JSON"));

        var res = await Sut(llm).AnalyzeAsync(User, ChatId);

        res.Outcome.Should().Be(WikiChatIngestService.OutcomeRejected, res.Reason);
        res.Reason.Should().Contain("курсор разбора не записан", "сбой записи стора назван причиной отказа");
        res.Dropped.Should().Be(2, "отказ стора не стирает отброшенное: оно было понесено");
        res.FirstDropReason.Should().NotBeNull().And.Contain("отсутствует в источнике дословно",
            "причина отброшенной операции доходит вместе с числом");
    }
}
