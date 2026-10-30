using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
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
    // С4b-3: конструктор контроллера теперь требует сервис отката. Эти тесты откат не вызывают —
    // экземпляр нужен только чтобы резолвился конструктор; свой сценарий отката — в WikiRevertTests.
    private readonly WikiPackageRevert _revert;

    public WikiPackagesControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_packages_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        var notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance);
        _revert = new WikiPackageRevert(notes, new WikiKeeper(notes, _config), _config);
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
    public void РешениеПоAuto_409_ПоInbox_200_ИСтатусПакетаМеняется()
    {
        var id = NewPackage(WikiPackage.StatusProposed, "common",
            Op(WikiPackage.OpAppend, WikiPackage.DecisionAuto),
            Op(WikiPackage.OpSupersede, WikiPackage.DecisionInbox));

        // Неспорную операцию человек не решает.
        AssertCode(Controller().Decide(id, new WikiDecideRequest(0, true, null)), 409, "wiki_pkg_auto");
        // Тело без index/accept — 400, а не додумывание индекса.
        AssertCode(Controller().Decide(id, new WikiDecideRequest(null, true, null)), 400, "wiki_pkg_request");
        AssertCode(Controller().Decide(id, null), 400, "wiki_pkg_request");

        // Отклонить без причины — нельзя.
        AssertCode(Controller().Decide(id, new WikiDecideRequest(1, false, null)), 409, "wiki_pkg_reason_required");

        // Принять спорную — 200, карточка с новым статусом, и на диске тот же статус.
        var res = Controller().Decide(id, new WikiDecideRequest(1, true, "подтверждено источником"));
        var dto = ((OkObjectResult)res).Value.Should().BeAssignableTo<WikiPackageDto>().Which;
        dto.Status.Should().Be(WikiPackage.StatusAccepted);
        dto.Operations[1].HumanDecision.Should().Be(WikiPackage.HumanAccepted);
        StatusOnDisk(id).Should().Be(WikiPackage.StatusAccepted);
    }

    // ─── 3. «Принять всё спорное» ───────────────────────────────────────────

    [Fact]
    public void ПринятьВсёСпорное_200Accepted_БезСпорных_409()
    {
        var id = NewPackage(WikiPackage.StatusProposed, "common",
            Op(WikiPackage.OpAppend, WikiPackage.DecisionAuto),
            Op(WikiPackage.OpConflict, WikiPackage.DecisionInbox),
            Op(WikiPackage.OpSupersede, WikiPackage.DecisionInbox, "Общее/Z.md"));
        ((OkObjectResult)Controller().AcceptAll(id)).Value
            .Should().BeAssignableTo<WikiPackageDto>().Which.Status.Should().Be(WikiPackage.StatusAccepted);
        StatusOnDisk(id).Should().Be(WikiPackage.StatusAccepted);

        // В пакете без спорных решать нечего: неспорные применяет политика.
        var noContested = NewPackage(WikiPackage.StatusProposed, "common", Op(WikiPackage.OpLink, WikiPackage.DecisionAuto));
        AssertCode(Controller().AcceptAll(noContested), 409, "wiki_pkg_nothing_to_decide");
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
    public void НетПакета_404_НечитаемыйФайл_409_ИРешениеНаНём_409()
    {
        var missing = WikiPackageStore.NewId();
        ((IStatusCodeActionResult)Controller().Get(missing)).StatusCode.Should().Be(404);
        // Решать по несуществующему пакету нечем — тот же 404, что и для чтения.
        ((IStatusCodeActionResult)Controller().Decide(missing, new WikiDecideRequest(0, true, null)))
            .StatusCode.Should().Be(404);

        var brokenId = WikiPackageStore.NewId();
        Directory.CreateDirectory(Path.Combine(WikiPaths.Keeper(_dir, User), "packages"));
        File.WriteAllText(Path.Combine(WikiPaths.Keeper(_dir, User), "packages", brokenId + ".json"), "   ");
        AssertCode(Controller().Get(brokenId), 409, "wiki_pkg_invalid");
        AssertCode(Controller().AcceptAll(brokenId), 409, "wiki_pkg_invalid");
    }

    // ─── Помощники ─────────────────────────────────────────────────────────

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
        return new WikiPackagesController(_projects, _config, _revert)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private WikiPackagesController AnonymousController()
    {
        var http = new DefaultHttpContext(); // User без идентичности — путь боя без токена
        return new WikiPackagesController(_projects, _config, _revert)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }
}
