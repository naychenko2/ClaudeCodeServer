using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
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
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

// S7b0a: REST-вход к разбору чата по требованию. Контроллер — напрямую (образец
// WikiPackagesControllerTests: тестируется контракт действия, не транспорт); цепка разбора
// настоящая, модель — подменённый ICheapTextRunner (образец WikiChatIngestTests).
// Ожидание passedётся через тестовый шов AnalyzeCore(sessionId, wait): реальные 10 с
// производственного ожидания в тесте не ждём; «медленный» прогон — gate в проходе A.
//
// Контракт: 200 {outcome, reason?, packageIds[]} — applied / awaiting_human / nothing_new /
// up_to_date и rejected без кода; 202 {running:true} — ожидание истекло, разбор продолжается;
// 404 {code:"not_found"} — чужой и несуществующий неразличимы; 409 {code,message} —
// task_execution, marked_skip, personal_section; нечитаемый стор
// списка/пересчёта — wiki_triage_invalid с текстом «файл разбора чатов не читается».
// С LI-2 analyze перед разбором заводит вику заявителя: 409 wiki_not_initialized этой ручки
// больше нет (состояния разбора — прежние); svc и выключенный флаг до заведения не доходят.
// analyze-status: 200 {running, last?{outcome,reason,packageIds,finishedAt}}, чужой чат — 404.
// Список: строка чата несёт running, cursor, chroniclePath (путь — по формуле сервиса,
// и только если файл хроники есть на диске — N1, дефект 58ad40a5: после отката — null)
// и mark (N1b) — пометку с чата.
// mark (N1b): 200 {mark} — пометка на чате (SetWikiMark), null — снять; чужой и
// несуществующий — один 404 с одним текстом (тот же контракт, что у analyze-status);
// неизвестное значение или пустое тело — 400 {code:"wiki_mark_invalid"}; выключенный
// флаг — 409 {code:"wiki_llm_wiki_off"} и пометка не сменилась. Порядок проверок ручки:
// токен → флаг → свой чат → значение.
public class WikiChatsControllerTests : IDisposable
{
    private const string User = "u1";
    private const string Foreign = "u2";
    private const string ChatId = "chat-1";
    private const string PageTitle = "Сущность";
    private const string PagePath = "Проекты/p1/Сущность.md";

    private const string Claim1 = "Решение: перенести сборку на Linux";
    private const string Marker1 = "первый сбой был 12 октября";

    // Ответ модели в формате синтеза: append на PageTitle, утверждение и цитата — одна и та же
    // реплика (дедуп меры 2 опознаёт повтор по тексту утверждения на странице).
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
    private readonly FakeSessions _sessions = new();
    // Модель одна на стенд: скрипт и gate тест выставляет перед своим прогоном.
    private readonly FakeLlm _llm = new();
    private readonly WikiChatIngestService _chat;

    public WikiChatsControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_chats_rest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, null, flags: new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config);
        var apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config);
        _chat = new WikiChatIngestService(
            new WikiSourceCapture(_notes, _projects, _sessions, _config, _keeper),
            new WikiIngestService(_llm, new NoSpheres(), apply, _config),
            _sessions, _keeper, new NoSpheres(), _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
        // Страница знаний, которую план разбора пополняет: без неё применённый исход
        // невозможен (цепка та же, что в WikiChatIngestTests).
        var r = _keeper.WritePageAsync(User, "projects/p1", PagePath, PageBase).GetAwaiter().GetResult();
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── 1. Чужой и несуществующий чат: один 404 на двоих ────────────────────

    [Fact]
    public async Task Analyze_ЧужойИлиНесуществующийЧат_404_ОдинКод()
    {
        AddSession();
        SetShortHistory();
        _sessions.All.Single(s => s.Id == ChatId).OwnerId = Foreign;

        var foreign = await Controller().AnalyzeCore(ChatId, Wait);
        AssertBody(foreign, 404, WikiChatIngestService.CodeNotFound);

        var missing = await Controller().AnalyzeCore("no-such-chat", Wait);
        AssertBody(missing, 404, WikiChatIngestService.CodeNotFound);

        // Мутация «убрать проверку владельца» (ResolveOwnerId в AnalyzeAsync): чужой чат
        // разбирается и доходит до модели — вместо 404 был бы 200, тест красный.
        _llm.Calls.Should().BeEmpty("отказ per-owner — до обращения к модели");
    }

    [Fact]
    public async Task Analyze_ФлагВыключен_409_МодельНеЗовётся()
    {
        AssertBody(await Controller(flagsOn: false).AnalyzeCore(ChatId, Wait), 409, "wiki_llm_wiki_off");
        // Мутация «убрать проверку флага в AnalyzeCore»: разбор доходит до модели — тест красный.
        _llm.Calls.Should().BeEmpty("при выключенном флаге разбор не стартует");

        // LI-2: и при выключенной функции вики не появляется: ни одной точки заведения при
        // выключенном llm-wiki (u2 в этом стенде не заведён).
        AssertBody(await Controller(Foreign, flagsOn: false).AnalyzeCore("s-any", Wait), 409, "wiki_llm_wiki_off");
        WikiPaths.IsInitialized(_dir, Foreign).Should().BeFalse();
    }

    // ─── 2. Ступень 0: 409 с точным кодом ───────────────────────────────────

    [Fact]
    public async Task Analyze_Ступень0_409_СТочнымКодом()
    {
        AddSession("s-task", taskExecution: true);
        AssertBody(await Controller().AnalyzeCore("s-task", Wait), 409, WikiChatIngestService.CodeTaskExecution);

        AddSession("s-skip", wikiMark: WikiMarks.Skip);
        AssertBody(await Controller().AnalyzeCore("s-skip", Wait), 409, WikiChatIngestService.CodeMarkedSkip);

        AddSession("s-personal", projectId: null);
        AssertBody(await Controller().AnalyzeCore("s-personal", Wait), 409, WikiChatIngestService.CodePersonalSection);

        _llm.Calls.Should().BeEmpty("все три отказа — до обращения к модели");
    }

    // ─── 2b. LI-2: вика заявителя заводится сама, до разбора ────────────────

    [Fact]
    public async Task AnalyzeНезаведённойВикиЗаводитЕёИРазборИдётДальше()
    {
        // Мутация (критерий c2): убрать EnsureInitializedAsync в AnalyzeCore — u2 остаётся без
        // pages/_schema.md, сервис отвечает 409 wiki_not_initialized и модель не звала: красный.
        AddSession("s-nowiki", projectId: "p1");
        _sessions.All.Single(s => s.Id == "s-nowiki").OwnerId = Foreign;
        SetShortHistory("s-nowiki");
        _llm.Script(("S", Synth(Claim1)));

        var res = await Controller(Foreign).AnalyzeCore("s-nowiki", Wait);

        var schema = Path.Combine(WikiPaths.Pages(_dir, Foreign), WikiPaths.SchemaFileName);
        File.Exists(schema).Should().BeTrue("«разобрать этот чат» — действие человека: его вика создаётся сама");
        File.ReadAllText(schema).Should().StartWith("# Схема вики", "шаблон пишет только Хранитель");
        _llm.Calls.Should().NotBeEmpty("разбор дошёл до модели: отказа по незаведённой вике здесь нет");
        AssertNotCode(res, WikiChatIngestService.CodeWikiNotInitialized);
    }

    [Fact]
    public async Task ГонкаДвухАнализовНезаведённойВики_ОдинШаблон_ПравкаЧеловекаНеЗатёрта()
    {
        // Два одновременных действия одного владельца (барьер — TaskCompletionSource, не
        // Task.Delay): ни одно не бросает, вика заведена ровно одним шаблоном. Затем правка
        // человека между вызовами: последующие действия проходят по «файл уже есть» и её не
        // затирают. Мутация (критерий c2): снять EnsureInitializedAsync в AnalyzeCore — этот
        // тест красный на первой же проверке (проверено: вики остаётся незаведённой).
        // Сам замок _initLocks здесь не мерится: у Write уникальное имя temp
        // (WikiFileWriter.cs:24), поэтому двойная запись не даёт коллизии; замок держит
        // тест WikiKeeperTests.ДваПараллельныхЗавестиВики (там вместе с именем temp).
        AddSession("s-race-a", projectId: "p1", wikiMark: WikiMarks.Skip);
        AddSession("s-race-b", projectId: "p1", wikiMark: WikiMarks.Skip);
        foreach (var id in new[] { "s-race-a", "s-race-b" })
            _sessions.All.Single(s => s.Id == id).OwnerId = Foreign;

        await RunBoth("s-race-a", "s-race-b"); // ни один из двух не бросил — иначе бросил бы здесь

        var schema = Path.Combine(WikiPaths.Pages(_dir, Foreign), WikiPaths.SchemaFileName);
        File.Exists(schema).Should().BeTrue("гонка не оставила вики незаведённой");
        File.ReadAllText(schema).Should().StartWith("# Схема вики");

        // Человек правил схему (операция schema, решение 6).
        File.WriteAllText(schema, "# Схема вики\n\nправка человека.", new UTF8Encoding(false));

        await RunBoth("s-race-a", "s-race-b");
        File.ReadAllText(schema).Should().Be("# Схема вики\n\nправка человека.",
            "повторное заведение идемпотентно: правку человека никто не затирает");
        _llm.Calls.Should().BeEmpty("заведение — до ступени 0, до модели никто не дошёл");
    }

    /// <summary>Два действия одного владельца, стартованные от одного барьера.</summary>
    private Task RunBoth(string idA, string idB)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Task.Run(async () => { await barrier.Task; return await Controller(Foreign).AnalyzeCore(idA, Wait); });
        var b = Task.Run(async () => { await barrier.Task; return await Controller(Foreign).AnalyzeCore(idB, Wait); });
        barrier.SetResult();
        // Ни один не бросает и не зависает: барьер открыт, потолок — страхование от зависания.
        return Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(20));
    }

    // ─── 3. Применённый и up_to_date: 200 с исходом (не отказ) ───────────────

    [Fact]
    public async Task Analyze_РазобранныйДоКонца_200_up_to_date_АНеОтказ()
    {
        AddSession();
        SetShortHistory();
        _llm.Script(("S", Synth(Claim1)));

        var first = await Controller().AnalyzeCore(ChatId, Wait);
        ((IStatusCodeActionResult)first).StatusCode.Should().Be(200);
        var b1 = Body(first);
        b1.GetProperty("outcome").GetString().Should().Be(WikiChatIngestService.OutcomeApplied);
        b1.GetProperty("packageIds").GetArrayLength().Should().Be(1);

        var again = await Controller().AnalyzeCore(ChatId, Wait);
        ((IStatusCodeActionResult)again).StatusCode.Should().Be(200,
            "закрытый курсор — состояние up_to_date, а не 409 и не 404");
        var b2 = Body(again);
        b2.GetProperty("outcome").GetString().Should().Be(WikiChatIngestService.OutcomeUpToDate);
        b2.GetProperty("reason").GetString().Should().Contain("новых сообщений нет");
        _llm.Calls.Should().HaveCount(1, "вторая заявка модель не звала");
    }

    // ─── 4. Медленный прогон: 202, затем analyze-status от него ──────────────

    [Fact]
    public async Task Analyze_МедленныйПрогон_202_ЗатемStatusRunningПотомИсход()
    {
        AddSession();
        SetShortHistory();
        var gate = new TaskCompletionSource();
        _llm.Script(("S", Synth(Claim1)));
        _llm.GateA = gate;

        // Ожидание короче прогона: 202 с running — и прогон после отказа клиента не гасится
        // (ct заявителя в него не идёт — в AnalyzeCore его нет вовсе).
        var slow = await Controller().AnalyzeCore(ChatId, TimeSpan.FromMilliseconds(200));
        ((IStatusCodeActionResult)slow).StatusCode.Should().Be(202);
        Body(slow).GetProperty("running").GetBoolean().Should().BeTrue();

        var mid = (OkObjectResult)Controller().AnalyzeStatus(ChatId);
        mid.StatusCode.Should().Be(200);
        var bm = Body(mid);
        bm.GetProperty("running").GetBoolean().Should().BeTrue("разбор продолжается на стороне сервиса");
        bm.GetProperty("last").ValueKind.Should().Be(JsonValueKind.Null, "пока идёт — исхода ещё нет");

        gate.SetResult();
        // Ждем окончания прогона по факту записи last (TaskCompletionSource-гейт + опрос
        // признака), не по паузе.
        SpinWait.SpinUntil(() => _chat.GetLast(User, ChatId) is not null, 10_000).Should().BeTrue();

        var done = (OkObjectResult)Controller().AnalyzeStatus(ChatId);
        done.StatusCode.Should().Be(200);
        var bd = Body(done);
        bd.GetProperty("running").GetBoolean().Should().BeFalse();
        var last = bd.GetProperty("last");
        last.GetProperty("outcome").GetString().Should().Be(WikiChatIngestService.OutcomeApplied);
        last.GetProperty("packageIds").GetArrayLength().Should().Be(1);
        last.GetProperty("finishedAt").GetDateTimeOffset().Should().NotBe(default);
    }

    // ─── 5. analyze-status: чужой 404, свой без разбора — пустой статус ──────

    [Fact]
    public void AnalyzeStatus_ЧужойИлиНесуществующий_404_СвойБезРазбора_ПустойСтатус()
    {
        AddSession("s-foreign");
        _sessions.All.Single(s => s.Id == "s-foreign").OwnerId = Foreign;
        AssertBody(Controller().AnalyzeStatus("s-foreign"), 404, WikiChatIngestService.CodeNotFound);
        AssertBody(Controller().AnalyzeStatus("no-such-chat"), 404, WikiChatIngestService.CodeNotFound);

        AddSession();
        var ok = (OkObjectResult)Controller().AnalyzeStatus(ChatId);
        ok.StatusCode.Should().Be(200);
        var body = Body(ok);
        body.GetProperty("running").GetBoolean().Should().BeFalse();
        body.GetProperty("last").ValueKind.Should().Be(JsonValueKind.Null, "разбора не было — last пустой");
    }

    // ─── 6. Список несёт running, cursor, chroniclePath ──────────────────────

    [Fact]
    public async Task Список_Несёт_running_cursor_chroniclePath()
    {
        AddSession();
        AddSession("s-personal", projectId: null);
        // Стор заполняем пересчётом: он не разбирает и курсора не ставит — ровно то,
        // что нужно, чтобы видеть нулевые значения новых полей.
        ((IStatusCodeActionResult)await Controller().Recalculate()).StatusCode.Should().Be(200);
        // Ожидание N1 (дефект 58ad40a5): путь без файла больше не отдаётся — хроника на диске.
        // Ставим её тем же методом, которым её пишет разбор (WritePageAsync — ровно тот, что
        // зовёт AppendChronicleAsync, образец H2-1): тест держит дисковое состояние, не формат.
        var w = await _keeper.WritePageAsync(User, "projects/p1", "Проекты/p1/Хроники/chat-1.md", ChronicleStub());
        w.Status.Should().Be(WikiOpStatus.Applied, w.Reason);

        var list = (WikiChatsListDto)((OkObjectResult)Controller().List()).Value!;

        var own = list.Items.Single(i => i.SessionId == ChatId);
        own.Running.Should().BeFalse("никто не разбирал");
        own.Cursor.Should().Be(0, "пересчёт курсора не касается (разведение владения S6f)");
        own.ChroniclePath.Should().Be("Проекты/p1/Хроники/chat-1.md",
            "путь — по формуле сервиса (AppendChronicle/ChroniclePath), не от контроллера");

        var personal = list.Items.Single(i => i.SessionId == "s-personal");
        personal.ChroniclePath.Should().BeNull("в личном разделе хроник не бывает (личный закрыт до Ф2)");
    }

    // ─── 7. Запись стора есть, файла хроники нет — пути нет (N1) ──────────────

    [Fact]
    public async Task Список_ЧатБезФайлаХроники_ChroniclePathNull()
    {
        AddSession();
        // Запись стора есть (её поставил пересчёт), хроники никогда не было: путь по формуле
        // выводится, но открыть его нечем — «Открыть хронику» вела бы на мёртвую страницу.
        ((IStatusCodeActionResult)await Controller().Recalculate()).StatusCode.Should().Be(200);

        var list = (WikiChatsListDto)((OkObjectResult)Controller().List()).Value!;
        list.Items.Single(i => i.SessionId == ChatId).ChroniclePath.Should().BeNull(
            "файла хроники нет — пути нет (N1: фронт ведёт на «Не найдено»)");

        // Мутация «убрать проверку существования файла» (ChroniclePathIfExists): путь вернулся
        // бы по одной формуле — тест красный.
    }

    // ─── 8. Откат хроники: тот же запрос списка — null (дефект N1) ────────────

    [Fact]
    public async Task Список_ХроникаСнятаОткатом_ChroniclePathNull()
    {
        AddSession();
        SetShortHistory();
        _llm.Script(("S", Synth(Claim1)));
        var first = await Controller().AnalyzeCore(ChatId, Wait);
        ((IStatusCodeActionResult)first).StatusCode.Should().Be(200);
        Body(first).GetProperty("outcome").GetString().Should().Be(WikiChatIngestService.OutcomeApplied);

        // Разбор написал хронику (AppendChronicleAsync) — путь отдаётся.
        var withFile = (WikiChatsListDto)((OkObjectResult)Controller().List()).Value!;
        withFile.Items.Single(i => i.SessionId == ChatId).ChroniclePath
            .Should().Be("Проекты/p1/Хроники/chat-1.md", "хроника на диске — путь есть");

        // Откат по источнику снимает хронику целиком (WikiRetractService, мера 5); запись
        // стора остаётся — список продолжает показывать чат, но без пути.
        var retract = new WikiRetractService(_notes, _keeper, new WikiPackageRevert(_notes, _keeper, _config),
            _sessions, new NoSpheres(), _config);
        var res = await retract.RetractAsync(User, "session:" + ChatId);
        res.Ok.Should().BeTrue(res.Reason);
        res.Failed.Should().BeEmpty();
        res.RemovedChronicles.Should().Contain("Проекты/p1/Хроники/chat-1.md");

        var after = (WikiChatsListDto)((OkObjectResult)Controller().List()).Value!;
        after.Items.Single(i => i.SessionId == ChatId).ChroniclePath.Should().BeNull(
            "файл хроники откатан — тот же запрос даёт null (N1: «Не найдено», а не мёртвый путь)");
    }

    // ─── 9. Нечитаемый стор: 409 с точным кодом и текстом контракта (NA-3) ───

    [Fact]
    public async Task СписокИПересчёт_НечитаемыйСтор_409_СТочнымКодомИТекстом()
    {
        // Фронт подписан на точные литералы (lib/wiki.ts, wikiLabels.ts): контракт 409 —
        // не только статус, тело несёт код и строку для человека. Здесь проверяются
        // литералы контракта, а не константа стора: подмена кода или текста на любой из
        // двух ручек (List / recalculate) видна этим тестом. Контракт самой службы —
        // WikiChatScorerTests.ПересчётСлужбыНаНечитаемомСторе.
        var file = Path.Combine(WikiPaths.Root(_dir, User), "keeper", "chat-triage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ не json");

        var list = Controller().List();
        AssertBody(list, 409, "wiki_triage_invalid");
        Body(list).GetProperty("message").GetString().Should().Be("файл разбора чатов не читается");

        var rec = await Controller().Recalculate();
        AssertBody(rec, 409, "wiki_triage_invalid");
        Body(rec).GetProperty("message").GetString().Should().Be("файл разбора чатов не читается");

        // Мутация «подставить code="wiki_triage_bogus" в ветке отказа recalculate» или
        // «в List» — этот тест красный: проверены литералы контракта, а не константа стора.
    }

    // ─── 10. Пометка чата (N1b): PUT {sessionId}/mark ──────────────────────────

    [Fact]
    public void Пометка_СвойЧат_200_ПометкаСменилась_ИСнимаетсяNull()
    {
        AddSession();
        var on = Controller().Mark(ChatId, new WikiChatMarkRequest { Mark = WikiMarks.Important });
        ((IStatusCodeActionResult)on).StatusCode.Should().Be(200);
        Body(on).GetProperty("mark").GetString().Should().Be(WikiMarks.Important);
        _sessions.All.Single(s => s.Id == ChatId).WikiMark.Should().Be(WikiMarks.Important,
            "пометка живёт на чате (SetWikiMark), а не в сторе отбора");

        // «Поле одно»: следующая пометка замещает прежнюю, а не дополняет её — после skip
        // важный не остаётся (цепочка important → skip → null).
        var skip = Controller().Mark(ChatId, new WikiChatMarkRequest { Mark = WikiMarks.Skip });
        ((IStatusCodeActionResult)skip).StatusCode.Should().Be(200);
        Body(skip).GetProperty("mark").GetString().Should().Be(WikiMarks.Skip);
        _sessions.All.Single(s => s.Id == ChatId).WikiMark.Should().Be(WikiMarks.Skip,
            "skip замещает important: пометка на чате одна");

        var off = Controller().Mark(ChatId, new WikiChatMarkRequest { Mark = null });
        ((IStatusCodeActionResult)off).StatusCode.Should().Be(200);
        Body(off).GetProperty("mark").ValueKind.Should().Be(JsonValueKind.Null);
        _sessions.All.Single(s => s.Id == ChatId).WikiMark.Should().BeNull("null — снять пометку");
    }

    [Fact]
    public void Пометка_ЧужойИлиНесуществующий_404_ОдинКодИОдинТекст()
    {
        AddSession("s-foreign");
        _sessions.All.Single(s => s.Id == "s-foreign").OwnerId = Foreign;

        var foreign = Controller().Mark("s-foreign", new WikiChatMarkRequest { Mark = WikiMarks.Important });
        var missing = Controller().Mark("no-such-chat", new WikiChatMarkRequest { Mark = WikiMarks.Important });
        AssertBody(foreign, 404, WikiChatIngestService.CodeNotFound);
        AssertBody(missing, 404, WikiChatIngestService.CodeNotFound);
        // Один 404 на двоих — и тело то же: по ответу нельзя узнать, что чат существует,
        // но чужой (тот же контракт, что у analyze-status).
        Body(foreign).ToString().Should().Be(Body(missing).ToString());
        _sessions.All.Single(s => s.Id == "s-foreign").WikiMark.Should().BeNull(
            "чужому чату пометка не ставится");
        // Мутация «убрать проверку владельца» (ResolveOwnerId в Mark): чужой чат получил бы
        // пометку и 200 — тест красный (c4).
    }

    [Fact]
    public void Пометка_НеизвестноеЗначениеИлиПустоеТело_400_ПометкаНеСменилась()
    {
        AddSession();
        AssertBody(Controller().Mark(ChatId, new WikiChatMarkRequest { Mark = "foo" }), 400, "wiki_mark_invalid");
        AssertBody(Controller().Mark(ChatId, null), 400, "wiki_mark_invalid");
        _sessions.All.Single(s => s.Id == ChatId).WikiMark.Should().BeNull("отказ — до записи");
    }

    [Fact]
    public void Пометка_ФлагВыключен_409_ИПометкаНеСменилась()
    {
        AddSession();
        AssertBody(Controller(flagsOn: false).Mark(ChatId, new WikiChatMarkRequest { Mark = WikiMarks.Skip }),
            409, "wiki_llm_wiki_off");
        _sessions.All.Single(s => s.Id == ChatId).WikiMark.Should().BeNull(
            "при выключенной функции пометка не ставится: отказ раньше записи");
    }

    [Fact]
    public void Пометка_ФлагВыключенИЧужойИлиНесуществующийЧат_409ПоФлагу_АНе404()
    {
        // Порядок проверок ручки (флаг раньше чата, как в analyze): при выключенном флаге
        // отказ идёт по флагу, даже если чата нет или он чужой — по ответу нельзя узнать,
        // существует ли чат (тот же per-owner контракт, что у analyze).
        AddSession("s-foreign");
        _sessions.All.Single(s => s.Id == "s-foreign").OwnerId = Foreign;

        AssertBody(Controller(flagsOn: false).Mark("s-foreign", new WikiChatMarkRequest { Mark = WikiMarks.Important }),
            409, "wiki_llm_wiki_off");
        AssertBody(Controller(flagsOn: false).Mark("no-such-chat", new WikiChatMarkRequest { Mark = WikiMarks.Important }),
            409, "wiki_llm_wiki_off");
        _sessions.All.Single(s => s.Id == "s-foreign").WikiMark.Should().BeNull(
            "отказ по флагу раньше записи: чужому чату пометка не поставлена");
        // Мутация «переставить проверки в Mark» (чат раньше флага): чужой чат дал бы 404 —
        // тест красный; порядок «флаг → чат» закреплён этим тестом.
    }

    [Fact]
    public async Task ПометкаSkip_ЗатемAnalyze_ОтказMarkedSkip_НольВызововМодели()
    {
        AddSession();
        SetShortHistory();
        ((IStatusCodeActionResult)Controller().Mark(ChatId, new WikiChatMarkRequest { Mark = WikiMarks.Skip }))
            .StatusCode.Should().Be(200);
        AssertBody(await Controller().AnalyzeCore(ChatId, Wait), 409, WikiChatIngestService.CodeMarkedSkip);
        _llm.Calls.Should().BeEmpty("skip отвергает разбор до единого обращения к модели");
    }

    [Fact]
    public void Пометка_НеДвигаетUpdatedAt()
    {
        AddSession();
        var s = _sessions.All.Single(x => x.Id == ChatId);
        var before = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        s.UpdatedAt = before;
        ((IStatusCodeActionResult)Controller().Mark(ChatId, new WikiChatMarkRequest { Mark = WikiMarks.Important }))
            .StatusCode.Should().Be(200);
        s.UpdatedAt.Should().Be(before,
            "пометка — настройка чата, не активность: по UpdatedAt идут сортировка и непрочитанность");
    }

    [Fact]
    public async Task Список_Несёт_mark_по_помеченному_и_непомеченному_чату()
    {
        AddSession();
        AddSession("s-skip");
        // Записи стора ставит пересчёт: список — про посчитанные чаты.
        ((IStatusCodeActionResult)await Controller().Recalculate()).StatusCode.Should().Be(200);
        ((IStatusCodeActionResult)Controller().Mark("s-skip", new WikiChatMarkRequest { Mark = WikiMarks.Skip }))
            .StatusCode.Should().Be(200);

        var list = (WikiChatsListDto)((OkObjectResult)Controller().List()).Value!;
        list.Items.Single(i => i.SessionId == ChatId).Mark.Should().BeNull("у чата пометки нет");
        list.Items.Single(i => i.SessionId == "s-skip").Mark.Should().Be(WikiMarks.Skip,
            "список показывает пометку для кнопок в строке (F3): её читает с чата, не со стора");
    }

    // ─── 11. Сервисный токен (B2b): все мутирующие ручки 403, чтение остаётся 200 ──

    // Мутация (критерий c2): убрать проверку IsServiceToken в Recalculate — этот тест
    // падает (пересчёт дошёл бы до службы и записал стор).
    [Fact]
    public async Task Пересчёт_СервисныйТокен_403_СторНеСоздан()
    {
        AddSession();

        var res = await Controller(svc: true).Recalculate();

        ((IStatusCodeActionResult)res).StatusCode.Should().Be(403);
        File.Exists(Path.Combine(WikiPaths.Root(_dir, User), "keeper", "chat-triage.json"))
            .Should().BeFalse("отказ до обращения к службе: ни байта в стор");
    }

    // Мутация: убрать проверку IsServiceToken в Mark — пометка ставилась бы и 200, тест красный.
    [Fact]
    public void Пометка_СервисныйТокен_403_ПометкаНеСменилась()
    {
        AddSession();

        var res = Controller(svc: true).Mark(ChatId, new WikiChatMarkRequest { Mark = WikiMarks.Important });

        ((IStatusCodeActionResult)res).StatusCode.Should().Be(403);
        _sessions.All.Single(s => s.Id == ChatId).WikiMark.Should().BeNull("отказ — до записи на чате");
    }

    // Мутация: убрать проверку IsServiceToken в AnalyzeCore — разбор дошёл бы до модели,
    // тест красный (по _llm.Calls).
    [Fact]
    public async Task Анализ_СервисныйТокен_403_МодельНеЗовётся()
    {
        AddSession();
        SetShortHistory();

        var res = await Controller(svc: true).AnalyzeCore(ChatId, Wait);

        ((IStatusCodeActionResult)res).StatusCode.Should().Be(403);
        _llm.Calls.Should().BeEmpty("отказ по типу токена — до обращения к модели");

        // LI-2: svc не доходит и до заведения — агент не заводит вику (инвариант ADR-035).
        AddSession("s-svc-niki", projectId: "p1");
        _sessions.All.Single(s => s.Id == "s-svc-niki").OwnerId = Foreign;
        AssertBodyStatus(await Controller(Foreign, svc: true).AnalyzeCore("s-svc-niki", Wait), 403);
        WikiPaths.IsInitialized(_dir, Foreign).Should().BeFalse("403 по токену — ни байта в pages/");
    }

    // Отказ по типу токена не зависит от состояния: при выключенном флаге и у несуществующего
    // чата ответ тот же 403 — по нему нельзя узнать ни состояния функции, ни существования чата.
    [Fact]
    public void Пометка_СервисныйТокенПриВыключенномФлагеИЛИзНесуществующегоЧата_403ПоТокену()
    {
        AssertBodyStatus(Controller(flagsOn: false, svc: true).Mark(ChatId, new WikiChatMarkRequest { Mark = WikiMarks.Skip }), 403);
        AssertBodyStatus(Controller(svc: true).Mark("no-such-chat", new WikiChatMarkRequest { Mark = WikiMarks.Skip }), 403);
    }

    // Чтение под svc остаётся 200: список и analyze-status — не мутирующие (контракт B2b).
    [Fact]
    public async Task СписокИСтатус_СервисныйТокен_200()
    {
        AddSession();
        ((IStatusCodeActionResult)await Controller().Recalculate()).StatusCode.Should().Be(200);

        var list = (WikiChatsListDto)((OkObjectResult)Controller(svc: true).List()).Value!;
        list.Items.Single(i => i.SessionId == ChatId).Should().NotBeNull();

        var status = (OkObjectResult)Controller(svc: true).AnalyzeStatus(ChatId);
        status.StatusCode.Should().Be(200);
    }

    // C2R-Sh2b: awaiting_human стоит первой в списке, причина — «N операция/и/ий на решении»,
    // а после решения всех спорных операций зона снимается (M1). Мутации: убрать фильтр
    // SessionsAwaitingHuman → последний ассерт красный; вернуть зону в конец ZoneOrder →
    // ассерт порядка красный.
    [Fact]
    public async Task Список_AwaitingHuman_ПерваяВСписке_СнимаетсяПослеРешенияПакета()
    {
        AddSession();
        AddSession("s-other");
        SetShortHistory();
        ((IStatusCodeActionResult)await Controller().Recalculate()).StatusCode.Should().Be(200);
        WikiSettingsStore.Mutate(_dir, User, st => st with
        {
            Classes = [new WikiSettingsStore.ClassState(
                WikiPackage.ClassKey("session", WikiPackage.OpAppend), true,
                "класс выключен человеком", DateTimeOffset.UtcNow, null)],
        });
        _llm.Script(("S", Synth(Claim1)));

        var res = await _chat.AnalyzeAsync(User, ChatId);
        res.Outcome.Should().Be(WikiChatIngestService.OutcomeAwaitingHuman, res.Reason);

        var list = (WikiChatsListDto)((OkObjectResult)Controller().List()).Value!;
        list.Items[0].SessionId.Should().Be(ChatId);
        list.Items[0].Zone.Should().Be(WikiChatIngestService.ZoneAwaitingHuman);
        list.Items[0].Reason.Should().Be("1 операция на решении");

        var root = WikiPaths.Root(_dir, User);
        var pkg = WikiPackageStore.List(root).Packages.Single();
        WikiPackageStore.AcceptAllContested(root, pkg.Id).Ok.Should().BeTrue();

        var after = (WikiChatsListDto)((OkObjectResult)Controller().List()).Value!;
        var row = after.Items.Single(i => i.SessionId == ChatId);
        row.Zone.Should().NotBe(WikiChatIngestService.ZoneAwaitingHuman, "все спорные операции решены");
        row.Reason.Should().NotContain("на решении", "причина awaiting_human снята вместе с зоной");
        after.Counts.ContainsKey(WikiChatIngestService.ZoneAwaitingHuman).Should().BeFalse();
    }

    // ─── обвязка ──────────────────────────────────────────────────────────────

    // WaitAsync возвращается сразу по завершению разбора, поэтому большой потолок не удлиняет
    // зелёный прогон — он лишь страхует от зависания. Малый потолок (5 с) под нагрузкой полного
    // прогона отдавал 202 вместо 200. «Медленный» прогон ловит свой короткий wait (тест 4).
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(2);

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

    private void SetShortHistory(string id = ChatId) => _sessions.History[id] =
    [
        new StoredSessionStartedMessage("claude", "default"),
        new StoredUserMessage($"{Claim1}. Хронология: {Marker1}."),
        new StoredTextMessage("Принято, готовлю план."),
        new StoredUserMessage("Давай."),
        new StoredTextMessage("Готово."),
    ];

    // Заглушка хроники на диске — рендер того же кода, что рендерит разбор (WikiPage.Render),
    // без сносок: тесту нужно существование файла, а не содержание утверждений.
    private static string ChronicleStub() => new WikiPage
    {
        Title = "Хроника чата-1",
        Frontmatter = new WikiFrontmatter { Type = WikiFrontmatter.TypeChronicle },
        Sections =
        [
            new() { Name = "О чём", Lines = [] },
            new() { Name = "Что решили", Lines = [] },
            new() { Name = "Что выяснили", Lines = [] },
            new() { Name = "Открытые вопросы", Lines = [] },
        ],
    }.Render();

    private WikiChatsController Controller(string user = User, bool flagsOn = true, bool svc = false)
    {
        var http = new DefaultHttpContext();
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, user)], "test");
        // Сервисный токен хода (typ=svc) — тот же claim-литерал, что выдаёт JwtService;
        // MapInboundClaims=false оставляет raw-имена, как в бою.
        if (svc) identity.AddClaim(new Claim("typ", "svc"));
        http.User = new ClaimsPrincipal(identity);
        return new WikiChatsController(_sessions, _chat, new WikiChatRecalc(_sessions, _config), _keeper, _config, flagsOn ? new WikiFlagOn() : new WikiFlagOff())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static JsonElement Body(IActionResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(((ObjectResult)result).Value!)).RootElement;

    private static void AssertBody(IActionResult result, int status, string code)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>($"{status} с телом отказа").Which;
        obj.StatusCode.Should().Be(status);
        Body(obj).GetProperty("code").GetString().Should().Be(code);
    }

    // Отказ без машинного кода (403 по типу токена — формат {error}, как в образце
    // PluginsController/TasksController): проверяется только статус.
    private static void AssertBodyStatus(IActionResult result, int status) =>
        ((IStatusCodeActionResult)result).StatusCode.Should().Be(status);

    // Этот ответ не является отказом с данным кодом (для точек, где прежний отказ снят:
    // какой исход дал сервис дальше — предмет других тестов).
    private static void AssertNotCode(IActionResult result, string code)
    {
        if (result is ObjectResult { Value: not null } obj && Body(obj).TryGetProperty("code", out var got))
            got.GetString().Should().NotBe(code);
    }

    // Подменённая модель того же образца, что в WikiChatIngestTests: проходы по директивам
    // промпта, gate первого прохода A; скрипт и gate тест выставляет перед своим прогоном.
    private sealed class FakeLlm : ICheapTextRunner
    {
        public List<(string ActionKey, string Prompt, string Pass)> Calls { get; } = [];
        public TaskCompletionSource? GateA;

        private readonly Dictionary<string, Queue<string>> _answers = new();

        public void Script(params (string Pass, string Answer)[] answers)
        {
            _answers.Clear();
            Calls.Clear();
            GateA = null;
            foreach (var (pass, answer) in answers)
                (_answers.TryGetValue(pass, out var q) ? q : _answers[pass] = new Queue<string>()).Enqueue(answer);
        }

        public bool UsesLocal(string actionKey) => true;
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
            var pass = prompt.Contains("YOUR PREVIOUS PLAN WAS REJECTED") ? "R"
                : prompt.Contains("single pass") ? "S"
                : "?";
            Calls.Add((actionKey, prompt, pass));
            actionKey.Should().Be(LocalActionCatalog.WikiIngest, "место разбора — wiki-ingest");
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
        // Копия поведения SessionManager.SetWikiMark (тест держит контракт шва, а не заглушку):
        // валидация значения на входе, пометка на чате, UpdatedAt не трогается (настройка,
        // не активность). false — чата нет или метка неизвестна.
        public bool SetWikiMark(string sessionId, string? mark)
        {
            if (mark is not null && mark != WikiMarks.Important && mark != WikiMarks.Skip) return false;
            var s = All.FirstOrDefault(x => x.Id == sessionId);
            if (s is null) return false;
            s.WikiMark = mark;
            return true;
        }
    }

    private sealed class WikiFlagOn : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }

    // Боевой шлюз флагов не бывает null: «флаг выключен» — это шлюз, отвечающий «нет» на ключ llm-wiki.
    private sealed class WikiFlagOff : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key != FeatureFlagKeys.LlmWiki;
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
