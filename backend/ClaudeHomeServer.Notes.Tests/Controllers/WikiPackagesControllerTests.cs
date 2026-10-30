using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

// S4a: REST «Входящих» пакетов (ADR-035, решения 4 и 12г). Владелец — только из токена;
// раздел пакета, называющий чужой проект, — 403 (тот же контракт, что у WikiController).
// Человек решает только операции с решением политики `inbox`: `auto` через REST не решается
// (409). Пакеты здесь только читаются и решаются: ни создания, ни применения (S4b).
// Образец построения — WikiControllerTests: temp-дир + боевые UserStore/ProjectManager +
// контроллер напрямую (тестируется контракт действия, не транспорт).
public class WikiPackagesControllerTests : IDisposable
{
    private const string User = "u1";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Page = "Общее/X.md";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    // S4b-3: конструктор контроллера требует сервис отката; S4f-2 — сервис отката по источнику
    // и каталог чатов (граница владения sourceRef); S4g — сервис применения (решение человека
    // замыкается применением). Тесты 1–5 эти сервисы не вызывают — экземпляры нужны, чтобы
    // резолвился конструктор; свои сценарии — в тестах 6 (retract) и 7–11 (применение).
    private readonly NotesService _notes;
    private readonly WikiApplyService _apply;
    private readonly WikiPackageRevert _revert;
    private readonly WikiRetractService _retract;
    private readonly SessionsStub _sessions = new();
    private readonly WikiKeeper _keeper;

    public WikiPackagesControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_packages_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        // Флаг llm-wiki: состояние вики читается моделью (BuildFresh) только с ним — тот же
        // приём, что в WikiApplyTests. Без инициализированной вики флаг ничего не меняет:
        // тесты 1–6 вики не читают.
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, null, new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config);
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config);
        _revert = new WikiPackageRevert(_notes, _keeper, _config);
        _retract = new WikiRetractService(_notes, _keeper, _revert, _sessions, new NoSpheres(), _config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── 1. Только своя вики: 403 чужой раздел, 401 без токена ─────────────

    [Fact]
    public void ЧужойРазделПакета_403_ВСпискеЕгоНет_БезТокена_401()
    {
        var foreign = CreateServerProject("Чужой проект", owner: "u2");
        // Пакет лежит в своём корне — но его раздел называет чужой проект: это чужая вики.
        var id = NewPackage(WikiPackage.StatusProposed, "projects/" + foreign.Id,
            Op(WikiPackage.OpAppend, WikiPackage.DecisionInbox, $"Проекты/{foreign.Id}/X.md"));

        AssertCode(Controller().Get(id), 403, "wiki_foreign");
        ListOf(Controller().List(null)).Should().NotContain(p => p.Id == id);

        // Без токена — 401 (контракт), а не NRE на claim.
        ((IStatusCodeActionResult)AnonymousController().List(null)).StatusCode.Should().Be(401);
        ((IStatusCodeActionResult)AnonymousController().Get(id)).StatusCode.Should().Be(401);
    }

    // ─── 2. Решение по операции: auto — 409, inbox — 200 и статус меняется ──

    [Fact]
    public async Task РешениеПоAuto_409_ПоInbox_200_ИСтатусПакетаМеняется()
    {
        var id = NewPackage(WikiPackage.StatusProposed, "common",
            Op(WikiPackage.OpAppend, WikiPackage.DecisionAuto),
            Op(WikiPackage.OpSupersede, WikiPackage.DecisionInbox));

        // Неспорную операцию человек не решает.
        AssertCode(await Controller().Decide(id, new WikiDecideRequest(0, true, null)), 409, "wiki_pkg_auto");
        // Тело без index/accept — 400, а не додумывание индекса.
        AssertCode(await Controller().Decide(id, new WikiDecideRequest(null, true, null)), 400, "wiki_pkg_request");
        AssertCode(await Controller().Decide(id, null), 400, "wiki_pkg_request");

        // Отклонить без причины — нельзя.
        AssertCode(await Controller().Decide(id, new WikiDecideRequest(1, false, null)), 409, "wiki_pkg_reason_required");

        // Принять спорную — 200, карточка с новым статусом, и на диске тот же статус.
        // Применять здесь нечем: вики этого владельца не заведена — отказ применения в теле
        // ответа, а не потеря решения: решение на диске, статус пакета не сдвинулся.
        var dto = ((OkObjectResult)await Controller().Decide(id, new WikiDecideRequest(1, true, "подтверждено источником")))
            .Value.Should().BeAssignableTo<WikiDecisionDto>().Which;
        dto.Package.Status.Should().Be(WikiPackage.StatusAccepted);
        dto.Package.Operations[1].HumanDecision.Should().Be(WikiPackage.HumanAccepted);
        dto.Apply!.Applied.Should().BeFalse();
        dto.Apply.Code.Should().Be(WikiApplyService.CodeOff);
        StatusOnDisk(id).Should().Be(WikiPackage.StatusAccepted);
    }

    // ─── 3. «Принять всё спорное» ───────────────────────────────────────────

    [Fact]
    public async Task ПринятьВсёСпорное_200Accepted_БезСпорных_409()
    {
        var id = NewPackage(WikiPackage.StatusProposed, "common",
            Op(WikiPackage.OpAppend, WikiPackage.DecisionAuto),
            Op(WikiPackage.OpConflict, WikiPackage.DecisionInbox),
            Op(WikiPackage.OpSupersede, WikiPackage.DecisionInbox, "Общее/Z.md"));
        var dto = ((OkObjectResult)await Controller().AcceptAll(id)).Value
            .Should().BeAssignableTo<WikiDecisionDto>().Which;
        dto.Package.Status.Should().Be(WikiPackage.StatusAccepted);
        StatusOnDisk(id).Should().Be(WikiPackage.StatusAccepted);
        // Вики этого владельца не заведена: применение отказано с причиной, решение цело.
        dto.Apply!.Applied.Should().BeFalse();
        dto.Apply.Code.Should().Be(WikiApplyService.CodeOff);

        // В пакете без спорных решать нечего: неспорные применяет политика.
        var noContested = NewPackage(WikiPackage.StatusProposed, "common", Op(WikiPackage.OpLink, WikiPackage.DecisionAuto));
        AssertCode(await Controller().AcceptAll(noContested), 409, "wiki_pkg_nothing_to_decide");
    }

    // ─── 4. Список и «Входящие» ────────────────────────────────────────────

    [Fact]
    public void Список_ФильтрВходящихТолькоProposed_НечитаемыйФайлВиденЧислом()
    {
        var waiting = NewPackage(WikiPackage.StatusProposed, "common", Op(WikiPackage.OpSupersede, WikiPackage.DecisionInbox));
        var applied = NewPackage(WikiPackage.StatusApplied, "common", Op(WikiPackage.OpAppend, WikiPackage.DecisionAuto));
        var brokenId = WikiPackageStore.NewId();
        File.WriteAllText(Path.Combine(WikiPaths.Keeper(_dir, User), "packages", brokenId + ".json"), "{ не json");

        var all = ListOf(Controller().List(null));
        all.Select(p => p.Id).Should().BeEquivalentTo([waiting, applied]);
        // Свежие первыми: порядок ответа не зависит от обхода каталога.
        all[0].Id.Should().Be(applied);
        all.First(p => p.Id == waiting).Operations[0].Class.Should().Be("session×supersede");
        all.First(p => p.Id == applied).Status.Should().Be(WikiPackage.StatusApplied);

        var inbox = ListOf(Controller().List(true));
        inbox.Select(p => p.Id).Should().Equal([waiting]);

        // Нечитаемый файл — числом: он виден как сломанный, но не притворяется пакетом.
        ((OkObjectResult)Controller().List(null)).Value.Should().BeAssignableTo<WikiPackagesDto>().Which
            .InvalidCount.Should().Be(1);

        // Чужого владельца здесь нет вовсе: u2 видит пустой список.
        ListOf(Controller("u2").List(null)).Should().BeEmpty();
    }

    // ─── 5. Нет пакета и нечитаемый файл — разные исходы ────────────────────

    [Fact]
    public async Task НетПакета_404_НечитаемыйФайл_409_ИРешениеНаНём_409()
    {
        var missing = WikiPackageStore.NewId();
        ((IStatusCodeActionResult)Controller().Get(missing)).StatusCode.Should().Be(404);
        // Решать по несуществующему пакету нечем — тот же 404, что и для чтения.
        ((IStatusCodeActionResult)await Controller().Decide(missing, new WikiDecideRequest(0, true, null)))
            .StatusCode.Should().Be(404);

        var brokenId = WikiPackageStore.NewId();
        Directory.CreateDirectory(Path.Combine(WikiPaths.Keeper(_dir, User), "packages"));
        File.WriteAllText(Path.Combine(WikiPaths.Keeper(_dir, User), "packages", brokenId + ".json"), "   ");
        AssertCode(Controller().Get(brokenId), 409, "wiki_pkg_invalid");
        AssertCode(await Controller().AcceptAll(brokenId), 409, "wiki_pkg_invalid");
    }

    // ─── Помощники ─────────────────────────────────────────────────────────

    // ─── 6. S4f-2: «Откатить всё из этого источника» (c3) ────────────────────

    [Fact]
    public async Task Retract_ЧужойИсточник_404_НеверныйSourceRef_400_Свой_200_ИСтрокаВЖурнале()
    {
        // Мутация «без границы владения» (снять проверку ResolveOwnerId): чат-F дал бы 200 с
        // пустым исходом — 404 краснеет. Мутация «без журнала» (снять строку op=retract в
        // сервисе): проверка строки на диске краснеет. Пакет здесь намеренно без прообразов:
        // это путь NoPreimages → снятие сносок — ровно то, для чего REST и поставлен.
        await _keeper.EnsureInitializedAsync(User);
        const string cardTitle = "Карточка чата";
        const string cardPath = "Общее/Карточка-чата.md";
        var card = $$"""
            ---
            type: source
            origin: session
            ref: session:chat-A#m1-9
            hash: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
            captured: 2026-10-30
            aliases: []
            owns: []
            sources: []
            status: active
            ---

            # {{cardTitle}}
            """;
        var page = $$"""
            ---
            type: topic
            aliases: []
            owns: []
            sources: []
            status: active
            ---

            # X

            ## Сводка
            Утверждение из чата [^s1]

            ## Хронология

            ## Противоречия

            ## История

            [^s1]: [[{{cardTitle}}]] — «Утверждение из чата»
            """;
        (await _keeper.WritePageAsync(User, "common", cardPath, card)).Status.Should().Be(WikiOpStatus.Applied);
        (await _keeper.WritePageAsync(User, "common", Page, page)).Status.Should().Be(WikiOpStatus.Applied);
        _sessions.Known["chat-A"] = new Session { Id = "chat-A", OwnerId = User };
        _sessions.Known["chat-F"] = new Session { Id = "chat-F", OwnerId = "u2" };

        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = "common",
            Sources = [new WikiPackage.SourceRef("session", "chat-A")],
            Operations = [Op(WikiPackage.OpAppend, WikiPackage.DecisionAuto)],
            Status = WikiPackage.StatusChecking,
        };
        WikiPackageStore.Create(WikiPaths.Root(_dir, User), pkg).Ok.Should().BeTrue();
        foreach (var step in new[] { WikiPackage.StatusProposed, WikiPackage.StatusApplying, WikiPackage.StatusApplied })
            WikiPackageStore.Transition(WikiPaths.Root(_dir, User), pkg.Id, step).Ok.Should().BeTrue();

        // Чужой чат и несуществующий — один 404: ответ не выдаёт наружу, существует ли источник.
        ((IStatusCodeActionResult)await Controller().Retract(new WikiRetractRequest("session:chat-F")))
            .StatusCode.Should().Be(404);
        ((IStatusCodeActionResult)await Controller().Retract(new WikiRetractRequest("session:chat-none")))
            .StatusCode.Should().Be(404);
        // Неверная форма — 400 (тот же разбор, что у сервиса: без префикса origin, пустое тело).
        AssertCode(await Controller().Retract(new WikiRetractRequest("chat-A")), 400, "wiki_retract_shape");
        AssertCode(await Controller().Retract(null), 400, "wiki_retract_shape");

        // Свой: 200 со списками; страница без сносок; пакет «reverted»; строка op=retract на диске.
        var dto = ((OkObjectResult)await Controller().Retract(new WikiRetractRequest("session:chat-A"))).Value
            .Should().BeAssignableTo<WikiRetractDto>().Which;
        dto.RevertedPackages.Should().BeEmpty("побайтного отката здесь не было — путь снятия сносок");
        dto.UnlinkedPackages.Should().Equal(pkg.Id);
        dto.RemainingPackages.Should().BeEmpty();
        dto.MarkedSkip.Should().BeTrue("с диска снято — своему чату поставлена пометка");
        File.ReadAllText(Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl"))
            .Should().Contain("\"op\":\"retract\"").And.Contain(pkg.Id);
        File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), Page)).Should().NotContain("[^");
        StatusOnDisk(pkg.Id).Should().Be(WikiPackage.StatusReverted);

        // Повтор: обработанный пакет из выбора выпадает — исход пустой, журнал не двоится.
        var again = ((OkObjectResult)await Controller().Retract(new WikiRetractRequest("session:chat-A"))).Value
            .Should().BeAssignableTo<WikiRetractDto>().Which;
        again.UnlinkedPackages.Should().BeEmpty();
        LogLines("retract").Should().HaveCount(1);
    }

    // ─── 7–12. S4g: решение человека замыкается применением (ADR-035 §5) ─────────
    //
    // Боевой фикстур: карточка источника и страница записаны Хранителем (манифест согласован —
    // иначе любое применение отказал бы дрейф), пакет в proposed с спорной supersede-операцией
    // на эту страницу. Текст источника нигде не хранится (в карточку он не пишется), поэтому
    // дословность на этом пути не перепроверяется: она подтверждена при разборе, рендерит тот
    // же Text из Operations пакета (решение Софьи по S4g).

    private const string CardTitle = "Карточка чата";
    private const string CardPageKey = "Общее/Карточка-чата.md";
    private const string Claim = "Решение: перенести сборку на Linux";
    private const string OldClaim = "Прежний план был остаться на Windows";

    private const string SourceCardFile = $$"""
        ---
        type: source
        origin: session
        ref: session:chat-1#m1-9
        hash: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
        captured: 2026-10-30
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{CardTitle}}
        """;

    private const string BasePage = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # X

        ## Сводка
        {{OldClaim}} [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[{{CardTitle}}]] — «{{OldClaim}}»
        """;

    [Fact]
    public async Task ПоследнееРешениеПринято_СтраницаЗаписана_ПакетApplied_ПричинаПуста()
    {
        // Мутация «не вызывать ApplyAsync после решения» (снять вызов в AfterDecision):
        // краснеет Applied/Status/файл страницы/журнал этого теста.
        var id = await NewRealPackage([RealOp()]);

        var dto = ((OkObjectResult)await Controller().Decide(id, new WikiDecideRequest(0, true, "подтверждаю")))
            .Value.Should().BeAssignableTo<WikiDecisionDto>().Which;

        dto.Apply!.Applied.Should().BeTrue("решение, оставившее принятую операцию, применяет пакет тем же запросом");
        dto.Apply.Code.Should().BeNull();
        dto.Apply.Reason.Should().BeNull();
        dto.Apply.Violations.Should().BeEmpty();
        dto.Package.Status.Should().Be(WikiPackage.StatusApplied);
        StatusOnDisk(id).Should().Be(WikiPackage.StatusApplied);

        // Страница записана по-настоящему: новое утверждение в файле, прообраз базы на диске,
        // ровно одна строка журнала — с политикой inbox (человека спросили, это видно).
        File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), Page)).Should().Contain(Claim);
        File.Exists(Path.Combine(WikiPaths.Keeper(_dir, User), "preimages", id, Page)).Should().BeTrue();
        var applyLines = LogLines("apply");
        applyLines.Should().HaveCount(1);
        applyLines[0].Should().Contain("\"policy\":\"inbox\"");
    }

    [Fact]
    public async Task ПоловинаСпорныхРешена_ПакетProposed_НаДискеНичегоНеПрименено()
    {
        // Мутация «применять при нерешённых спорных» (звать ApplyAsync для любого статуса):
        // краснеют Apply.Should().BeNull() и статус proposed на диске этого теста.
        var id = await NewRealPackage([RealOp(), RealOp(page: "Общее/Z.md")]);
        var before = File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), Page));

        var dto = ((OkObjectResult)await Controller().Decide(id, new WikiDecideRequest(0, true, "подтверждаю")))
            .Value.Should().BeAssignableTo<WikiDecisionDto>().Which;

        dto.Package.Status.Should().Be(WikiPackage.StatusProposed);
        dto.Apply.Should().BeNull("осталась нерешённая спорная операция — применять нечего");
        StatusOnDisk(id).Should().Be(WikiPackage.StatusProposed);
        File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), Page)).Should().Be(before);
        LogLines("apply").Should().BeEmpty();
    }

    [Fact]
    public async Task ВсеСпорныеОтклонены_ПакетRejected_НаДискеНичегоНеПрименено()
    {
        var id = await NewRealPackage([RealOp()]);
        var before = File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), Page));

        var dto = ((OkObjectResult)await Controller().Decide(id, new WikiDecideRequest(0, false, "не подтверждается")))
            .Value.Should().BeAssignableTo<WikiDecisionDto>().Which;

        dto.Package.Status.Should().Be(WikiPackage.StatusRejected);
        dto.Apply.Should().BeNull("отклонены все спорные, принятых нет — применять нечего");
        StatusOnDisk(id).Should().Be(WikiPackage.StatusRejected);
        File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), Page)).Should().Be(before);
        LogLines("apply").Should().BeEmpty();
    }

    [Fact]
    public async Task БазаОперацииИсчезла_StaleВОтветеСПричиной_РешениеСохранено()
    {
        // baseHashes называют страницу, которой в манифеста нет: база намерения исчезла,
        // авто-rebase нет (решение 4). Сбой применения не теряет решения человека.
        var id = await NewRealPackage([RealOp(basePage: "Общее/Нет.md")]);
        var before = File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), Page));

        var dto = ((OkObjectResult)await Controller().Decide(id, new WikiDecideRequest(0, true, "подтверждаю")))
            .Value.Should().BeAssignableTo<WikiDecisionDto>().Which;

        dto.Apply!.Applied.Should().BeFalse();
        dto.Apply.Code.Should().Be(WikiApplyService.CodeStale);
        dto.Apply.Reason.Should().NotBeNullOrWhiteSpace("причина — текст для человека");
        dto.Package.Status.Should().Be(WikiPackage.StatusStale);
        StatusOnDisk(id).Should().Be(WikiPackage.StatusStale);
        ReadOp(id).HumanDecision.Should().Be(WikiPackage.HumanAccepted, "решения записаны до применения");
        File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), Page)).Should().Be(before);
        LogLines("apply").Should().BeEmpty();
    }

    [Fact]
    public async Task НетКарточкиИсточника_ApplyFailedСПричиной_РешениеСохранено()
    {
        // Пакет называет источник, карточки которого в его разделе нет: применять нечем —
        // отказ с причиной после заявки, а не «зависший applying» и не исключение наружу.
        var id = await NewRealPackage([RealOp()], sourceRef: "session:chat-9#m1-9");
        var before = File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), Page));

        var dto = ((OkObjectResult)await Controller().Decide(id, new WikiDecideRequest(0, true, "подтверждаю")))
            .Value.Should().BeAssignableTo<WikiDecisionDto>().Which;

        dto.Apply!.Applied.Should().BeFalse();
        dto.Apply.Code.Should().Be(WikiApplyService.CodeSourceUnavailable);
        dto.Apply.Reason.Should().NotBeNullOrWhiteSpace();
        dto.Package.Status.Should().Be(WikiPackage.StatusApplyFailed);
        StatusOnDisk(id).Should().Be(WikiPackage.StatusApplyFailed);
        ReadOp(id).HumanDecision.Should().Be(WikiPackage.HumanAccepted);
        File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), Page)).Should().Be(before);
        LogLines("apply").Should().BeEmpty();
    }

    [Fact]
    public async Task ГонкаДвухРешений_ПрименениеРовноОдно_ВторойОтказПоСтатусу()
    {
        // Мутация «без проверки статуса» (снять в сторе отказ пакета не в proposed): краснеет
        // HaveCount(1) отказа — оба вызова дошли бы до применения.
        var id = await NewRealPackage([RealOp()]);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Task.Run(async () => { await gate.Task; return await Controller().AcceptAll(id); });
        var b = Task.Run(async () => { await gate.Task; return await Controller().AcceptAll(id); });
        gate.SetResult();
        var results = new[] { await a, await b };

        var ok = results.OfType<OkObjectResult>().ToList();
        var refused = results.OfType<ObjectResult>().Where(r => r.StatusCode == 409).ToList();
        ok.Should().HaveCount(1);
        refused.Should().HaveCount(1);
        ((WikiDecisionDto)ok[0].Value!).Apply!.Applied.Should().BeTrue();
        JsonDocument.Parse(JsonSerializer.Serialize(refused[0].Value)).RootElement
            .GetProperty("code").GetString().Should().Be(WikiPackageStore.CodeNotDecidable);

        // Ровно одно применение: одна строка журнала и один прообраз этой страницы — второй
        // записи этого пакета нет ни при каком порядке двух вызовов.
        LogLines("apply").Should().HaveCount(1);
        Directory.GetFiles(Path.Combine(WikiPaths.Keeper(_dir, User), "preimages", id), "*", SearchOption.AllDirectories)
            .Should().HaveCount(1);
        StatusOnDisk(id).Should().Be(WikiPackage.StatusApplied);
    }

    [Fact]
    public async Task ЧужойРазделПакета_Решение_403_ПримененияНет()
    {
        // Та же граница, что у остальных ручек: чужой раздел — 403 без утечки существования,
        // и до применения дело не доходит.
        var foreign = CreateServerProject("Чужой проект 2", owner: "u2");
        var id = NewPackage(WikiPackage.StatusProposed, "projects/" + foreign.Id,
            Op(WikiPackage.OpAppend, WikiPackage.DecisionInbox, $"Проекты/{foreign.Id}/X.md"));

        AssertCode(await Controller().Decide(id, new WikiDecideRequest(0, true, null)), 403, "wiki_foreign");
        AssertCode(await Controller().AcceptAll(id), 403, "wiki_foreign");
        StatusOnDisk(id).Should().Be(WikiPackage.StatusProposed);
        LogLines("apply").Should().BeEmpty();
    }

    // ─── 13–17. Ревью S4g: карточка источника — точная, а не «первая в разделе» ────
    //
    // Путь решения человека (контекста нет) берёт карточку с диска в разделе самого пакета.
    // Здесь проверяется сам выбор: две карточки одного origin+ref — не «первая», а отказ;
    // чужой origin и чужой раздел — не карточка; ровно одна — сноска ведёт именно на неё.

    [Fact]
    public async Task ДвеКарточкиОдногоИсточника_ОтказСПричиной_ЦитатаНеПриписана_РешениеСохранено()
    {
        // В разделе пакета лежат две карточки одного origin+ref — прежняя и новая версия
        // одного источника. Взять первую — отнести сноску к версии, которую человек не
        // выбирал: отказ с причиной, пакет «apply_failed», решения на диске (ревью S4g, MAJOR).
        // Мутация «брать первое совпадение» (вместо проверки dup — matches[0]): краснеют Code
        // и StatusOnDisk этого теста — применение дошло бы до записи страницы.
        var id = await NewRealPackage([RealOp()]);
        var otherVersion = """
            ---
            type: source
            origin: session
            ref: session:chat-1#m1-9
            hash: fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210
            captured: 2026-10-20
            aliases: []
            owns: []
            sources: []
            status: active
            ---

            # Прежняя версия карточки
            """;
        await WriteViaKeeper("Общее/Прежняя-версия-карточки.md", otherVersion);
        var before = File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), Page));

        var dto = ((OkObjectResult)await Controller().Decide(id, new WikiDecideRequest(0, true, "подтверждаю")))
            .Value.Should().BeAssignableTo<WikiDecisionDto>().Which;

        dto.Apply!.Applied.Should().BeFalse();
        dto.Apply.Code.Should().Be(WikiApplyService.CodeSourceUnavailable);
        dto.Apply.Reason.Should().Contain("несколько версий источника, нужна точная карточка");
        dto.Package.Status.Should().Be(WikiPackage.StatusApplyFailed);
        StatusOnDisk(id).Should().Be(WikiPackage.StatusApplyFailed);
        ReadOp(id).HumanDecision.Should().Be(WikiPackage.HumanAccepted, "решения записаны до применения");
        // Ни одной из версий цитата не приписана: страница байт в байт прежняя, журнала нет.
        File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), Page)).Should().Be(before);
        LogLines("apply").Should().BeEmpty();
    }

    [Fact]
    public async Task РовноОднаКарточкаИсточника_СноскаНовогоУтвержденияВедётНаНеё()
    {
        // Рядом в разделе — карточка соседнего источника («chat-10» против названного
        // «chat-1»): при неточной сверке ref она совпала бы тоже, и сноска ушла бы на неё.
        // Ровно одна карточка названа — сноска новой строки обязана вести именно на неё
        // (ревью S4g, п. 2). Мутация «снять границу «#» в RefMatches» (StartsWith(wanted)
        // без «#»): сошлись бы обе — краснеют Applied и строка s2 этого теста.
        var id = await NewRealPackage([RealOp()], sourceRef: "chat-1");
        var neighbour = """
            ---
            type: source
            origin: session
            ref: session:chat-10#m1-9
            hash: fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210
            captured: 2026-10-29
            aliases: []
            owns: []
            sources: []
            status: active
            ---

            # Карточка другого чата
            """;
        await WriteViaKeeper("Общее/Карточка-другого-чата.md", neighbour);

        var dto = ((OkObjectResult)await Controller().Decide(id, new WikiDecideRequest(0, true, "подтверждаю")))
            .Value.Should().BeAssignableTo<WikiDecisionDto>().Which;

        dto.Apply!.Applied.Should().BeTrue("ровно одна карточка названного источника — применять нечем только если её нет");
        var after = File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), Page));
        after.Should().Contain($"{Claim} [^s2]");
        after.Should().Contain($"[^s2]: [[{CardTitle}]]");
        after.Should().NotContain("[^s2]: [[Карточка другого чата]]", "соседний источник — не цель этой сноски");
    }

    [Fact]
    public async Task КарточкаТогоЖеRefДругогоOrigin_НеКарточкаИсточника_ОтказПрименения()
    {
        // В разделе — карточка с той же строкой ref, но другого origin (task вместо session):
        // это не источник пакета, и сноска на карточку чужого происхождения была бы молчаливой
        // подменой источника (ревью S4g, M1). Мутация «снять сверку origin» (cardOrigin !=
        // s.Origin): совпадение по одному ref выиграло бы — краснеют Code и StatusOnDisk.
        var id = await NewRealPackage([RealOp()]);
        var foreignOrigin = """
            ---
            type: source
            origin: task
            ref: session:chat-1#m1-9
            hash: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
            captured: 2026-10-30
            aliases: []
            owns: []
            sources: []
            status: active
            ---

            # Карточка чата
            """;
        // Та же страница карточки, другой origin: в разделе ровно одна type: source, и она
        // — чужого происхождения.
        await WriteViaKeeper(CardPageKey, foreignOrigin);
        var before = File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), Page));

        var dto = ((OkObjectResult)await Controller().Decide(id, new WikiDecideRequest(0, true, "подтверждаю")))
            .Value.Should().BeAssignableTo<WikiDecisionDto>().Which;

        dto.Apply!.Applied.Should().BeFalse();
        dto.Apply.Code.Should().Be(WikiApplyService.CodeSourceUnavailable);
        dto.Apply.Reason.Should().Contain("карточка источника пакета не найдена");
        StatusOnDisk(id).Should().Be(WikiPackage.StatusApplyFailed);
        File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), Page)).Should().Be(before);
        LogLines("apply").Should().BeEmpty();
    }

    [Fact]
    public async Task КарточкаИзДругогоРаздела_ТотЖеOriginИRef_ОтказПрименения()
    {
        // Пакет в разделе своего проекта; карточка того же origin+ref — в «Общем», то есть в
        // цепочке вверх: она в состоянии проверки (право на ссылку у цепочки есть), но не в
        // разделе пакета — этот путь чужих разделов не читает (ADR-036, решение 1; ревью S4g,
        // M1). Мутация «снять сверку раздела» (WikiSections.Parse(d.Path) != section): карточка
        // из «Общего» подошла бы, и краснеют Code и StatusOnDisk этого теста.
        var project = CreateServerProject("Проект карточки");
        var section = "projects/" + project.Id;
        var projectPage = $"Проекты/{project.Id}/X.md";
        await _keeper.EnsureInitializedAsync(User);
        await WriteViaKeeper(CardPageKey, SourceCardFile);
        await WriteViaKeeper(section, projectPage, BasePage);
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = section,
            Sources = [new WikiPackage.SourceRef("session", "session:chat-1#m1-9")],
            Operations = [RealOp(page: projectPage, basePage: projectPage)],
            Status = WikiPackage.StatusChecking,
        };
        WikiPackageStore.Create(WikiPaths.Root(_dir, User), pkg).Ok.Should().BeTrue();
        WikiPackageStore.Transition(WikiPaths.Root(_dir, User), pkg.Id, WikiPackage.StatusProposed).Ok.Should().BeTrue();
        var before = File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), projectPage));

        var dto = ((OkObjectResult)await Controller().Decide(pkg.Id, new WikiDecideRequest(0, true, "подтверждаю")))
            .Value.Should().BeAssignableTo<WikiDecisionDto>().Which;

        dto.Apply!.Applied.Should().BeFalse();
        dto.Apply.Code.Should().Be(WikiApplyService.CodeSourceUnavailable);
        StatusOnDisk(pkg.Id).Should().Be(WikiPackage.StatusApplyFailed);
        File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), projectPage)).Should().Be(before);
        LogLines("apply").Should().BeEmpty();
    }

    // ─── 18–19. Ревью S4g, M2/M3: сбой применения и исчезнувший пакет — ответ, а не 500/404 ─

    [Fact]
    public async Task ИсключениеИзПрименения_Не500_РезультатОтказСПричиной_СтатусНеДвигается()
    {
        // Сбой, вышедший из ApplyAsync наружу (здесь — чужой формат владельца: Root бросает
        // ArgumentException до заявки), не даёт 500: «Входящие» отвечают результатом-отказом с
        // причиной, решения человека целы, пакет не двинут (ревью S4g, M3). Мутация «снять
        // try/catch» в ApplyOutcomeAsync: тест падает с исключением, а не с ответом.
        var id = await NewRealPackage([RealOp()]);
        var root = WikiPaths.Root(_dir, User);
        var decided = WikiPackageStore.DecideOperation(root, id, 0, true, "подтверждаю");
        decided.Ok.Should().BeTrue(decided.Reason);

        var dto = ((OkObjectResult)await Controller().AfterDecision("..", root, decided.Package!))
            .Value.Should().BeAssignableTo<WikiDecisionDto>().Which;

        dto.Apply!.Applied.Should().BeFalse();
        dto.Apply.Code.Should().Be(WikiApplyService.CodeInternal);
        dto.Apply.Reason.Should().Contain("внутренний сбой применения");
        StatusOnDisk(id).Should().Be(WikiPackage.StatusAccepted, "сбой до заявки не ведёт пакет в «applying»");
        LogLines("apply").Should().BeEmpty();
    }

    [Fact]
    public void СбойПослеЗаявки_ПакетЗакрытApplyFailed_АНеЗависаетВApplying()
    {
        // «applying» без закрытия доводит только сторож старта; здесь сбой закрывается
        // переходом сразу (ревью S4g, M3). Мутация «не закрывать переходом» (убрать Transition
        // в CrashedApply): StatusOnDisk краснеет — пакет остался бы в «applying».
        var id = NewPackage(WikiPackage.StatusProposed, "common", Op(WikiPackage.OpAppend, WikiPackage.DecisionAuto));
        var root = WikiPaths.Root(_dir, User);
        WikiPackageStore.Transition(root, id, WikiPackage.StatusApplying).Ok.Should().BeTrue();

        var r = WikiPackagesController.CrashedApply(root, id, new IOException("диск отказал"));

        r.Applied.Should().BeFalse();
        r.Code.Should().Be(WikiApplyService.CodeInternal);
        r.Reason.Should().Contain("внутренний сбой применения");
        StatusOnDisk(id).Should().Be(WikiPackage.StatusApplyFailed);
    }

    [Fact]
    public async Task ПакетИсчезПослеРешения_Не404_ОтветСРезультатомПрименения()
    {
        // Файл пакета исчез между решением и перечитыванием (гонка с удалением): 404 здесь —
        // пустой отказ там, где человек только что вынес решение и где есть его исход. Ответ
        // — 200 с результатом применения и последней прочитанной карточкой (ревью S4g, M2).
        // Мутация «вернуть NotFound()»: краснеет весь тест — тела не будет.
        var id = await NewRealPackage([RealOp()]);
        var root = WikiPaths.Root(_dir, User);
        var decided = WikiPackageStore.DecideOperation(root, id, 0, true, "подтверждаю");
        decided.Ok.Should().BeTrue(decided.Reason);
        File.Delete(Path.Combine(WikiPaths.Keeper(_dir, User), "packages", id + ".json"));

        var dto = ((OkObjectResult)await Controller().AfterDecision(User, root, decided.Package!))
            .Value.Should().BeAssignableTo<WikiDecisionDto>().Which;

        dto.Apply!.Applied.Should().BeFalse();
        dto.Apply.Code.Should().Be(WikiApplyService.CodeClaim, "применять нечем: пакета нет на диске");
        dto.Apply.Reason.Should().NotBeNullOrWhiteSpace("причина — текст для человека");
        dto.Package.Id.Should().Be(id);
        dto.Package.Status.Should().Be(WikiPackage.StatusAccepted, "последняя прочитанная карточка");
        dto.Package.Operations[0].HumanDecision.Should().Be(WikiPackage.HumanAccepted, "решение видно в ответе");
        LogLines("apply").Should().BeEmpty();
    }

    // Пакет боевой пробы: карточка и страница записаны Хранителем (манифест согласован —
    // применения без дрейфа нет), сам в proposed с одной спорной операцией. sourceRef —
    // карточка, которую называет пакет: с другой на диске это «нет карточки источника».
    private async Task<string> NewRealPackage(
        WikiPackage.Operation[] ops, string sourceRef = "session:chat-1#m1-9")
    {
        await _keeper.EnsureInitializedAsync(User);
        await WriteViaKeeper(CardPageKey, SourceCardFile);
        await WriteViaKeeper(Page, BasePage);
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = "common",
            Sources = [new WikiPackage.SourceRef("session", sourceRef)],
            Operations = ops,
            Status = WikiPackage.StatusChecking,
        };
        WikiPackageStore.Create(WikiPaths.Root(_dir, User), pkg).Ok.Should().BeTrue();
        WikiPackageStore.Transition(WikiPaths.Root(_dir, User), pkg.Id, WikiPackage.StatusProposed).Ok.Should().BeTrue();
        return pkg.Id;
    }

    private async Task WriteViaKeeper(string pageKey, string content) => await WriteViaKeeper("common", pageKey, content);

    private async Task WriteViaKeeper(string section, string pageKey, string content)
    {
        var r = await _keeper.WritePageAsync(User, section, pageKey, content);
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    // Спорная supersede на боевую страницу: замещаемое утверждение в ней есть; basePage —
    // страница, названная базой (несуществующая — случай «база исчезла»).
    private static WikiPackage.Operation RealOp(string page = Page, string basePage = Page) => new()
    {
        Op = WikiPackage.OpSupersede,
        Page = page,
        Text = Claim,
        Target = OldClaim,
        Class = WikiPackage.ClassKey("session", WikiPackage.OpSupersede),
        Decision = WikiPackage.DecisionInbox,
        Reason = "замещает действующее утверждение",
        BaseHashes = new Dictionary<string, string> { [basePage] = Hash },
    };

    private WikiPackage.Operation ReadOp(string id)
    {
        var res = WikiPackageStore.TryRead(WikiPaths.Root(_dir, User), id);
        res.State.Should().Be(WikiPackageFileState.Valid);
        return res.Package!.Operations[0];
    }

    private sealed class WikiFlagOn : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }

    private List<string> LogLines(string op)
    {
        var p = Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl");
        return File.Exists(p)
            ? File.ReadAllLines(p).Where(l => l.Contains($"\"op\":\"{op}\"")).ToList()
            : [];
    }

    private Project CreateServerProject(string name, string owner = User)
    {
        var root = Path.Combine(_dir, "repo", owner, name);
        Directory.CreateDirectory(root);
        return _projects.Create(name, root, owner, owner);
    }

    // Пакет в своём корне вики и доведён до статуса разрешёнными переходами стора.
    private string NewPackage(string status, string section, params WikiPackage.Operation[] ops)
    {
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = section,
            Sources = [new WikiPackage.SourceRef("session", "chat-1")],
            Operations = ops.Length == 0 ? [Op(WikiPackage.OpAppend, WikiPackage.DecisionAuto)] : ops,
            Status = WikiPackage.StatusChecking,
        };
        WikiPackageStore.Create(WikiPaths.Root(_dir, User), pkg).Ok.Should().BeTrue();
        var steps = status == WikiPackage.StatusChecking
            ? []
            : status == WikiPackage.StatusApplied
                ? [WikiPackage.StatusProposed, WikiPackage.StatusApplying, WikiPackage.StatusApplied]
                : new[] { status };
        foreach (var step in steps)
            WikiPackageStore.Transition(WikiPaths.Root(_dir, User), pkg.Id, step).Ok.Should().BeTrue($"{status}: {step}");
        return pkg.Id;
    }

    private static WikiPackage.Operation Op(string op, string decision, string page = Page) => new()
    {
        Op = op,
        Page = page,
        Text = "утверждение из чата",
        Target = "прежнее утверждение",
        Class = WikiPackage.ClassKey("session", op),
        Decision = decision,
        Reason = decision == WikiPackage.DecisionInbox ? "замещает действующее утверждение" : null,
        BaseHashes = new Dictionary<string, string> { [page] = Hash },
    };

    private string StatusOnDisk(string id)
    {
        var res = WikiPackageStore.TryRead(WikiPaths.Root(_dir, User), id);
        res.State.Should().Be(WikiPackageFileState.Valid);
        return res.Package!.Status;
    }

    private static IReadOnlyList<WikiPackageDto> ListOf(IActionResult result) =>
        ((OkObjectResult)result).Value.Should().BeAssignableTo<WikiPackagesDto>().Which.Packages;

    private static void AssertCode(IActionResult result, int status, string code)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>($"{status} с телом отказа").Which;
        obj.StatusCode.Should().Be(status);
        JsonDocument.Parse(JsonSerializer.Serialize(obj.Value)).RootElement
            .GetProperty("code").GetString().Should().Be(code);
    }

    private WikiPackagesController Controller(string user = User)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, user)], "test"));
        return new WikiPackagesController(_projects, _config, _apply, _revert, _retract, _sessions)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private WikiPackagesController AnonymousController()
    {
        var http = new DefaultHttpContext(); // User без идентичности — путь боя без токена
        return new WikiPackagesController(_projects, _config, _apply, _revert, _retract, _sessions)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    // ─── Стабы для отката по источнику (S4f-2) ─────────────────────────────

    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    private sealed class SessionsStub : ISessionDirectory
    {
        public readonly Dictionary<string, Session> Known = new();
        public Session? GetById(string id) => Known.TryGetValue(id, out var s) ? s : null;
        public IReadOnlyCollection<Session> GetAll() => Known.Values;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
        public bool SetWikiMark(string sessionId, string? mark) => Known.ContainsKey(sessionId);
    }
}
