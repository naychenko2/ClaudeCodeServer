using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Plugins.Controllers;
using PluginBridge = ClaudeHomeServer.Services.Plugins.PluginBridge;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Plugins;

// REST установки и согласия (ADR-031 §2.3, §4, шаг 1.4) — unit-тесты контроллера
// (контроллер конструируется напрямую, ControllerContext несёт ClaimsPrincipal).
//
// Гейты: Subsystems:plugins:Enabled (SubsystemGate) → 503 (M2: сервис не активен);
// флаг plugins-mvp (IFeatureFlagGate) → 403 (пользовательский гейт).
// C1: source — метка из белого списка (draft:/upload:/catalog:), не путь → 400.
// C2: install — только user JWT (сервисный → 403), granted = [] при установке.
// C5: ответы — DTO без значений секретов.
// M6: повторная установка (совпал sha) — no-op, Granted/Secrets не сбрасываются.
public class PluginsControllerTests : IDisposable
{
    private const string OwnerA = "owner-a";
    private const string OwnerB = "owner-b";

    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly PluginSecretsStore _secrets;
    private readonly PluginInstaller _installer;
    private readonly StubCascade _cascade;
    private readonly PluginUninstall _uninstall;
    private readonly PluginBridge _bridge;

    public PluginsControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_plugins_ctl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new PluginStore(BuildConfig(_dir));
        _secrets = new PluginSecretsStore(BuildConfig(_dir));
        _installer = new PluginInstaller(_store, new PluginPackageValidator());
        _cascade = new StubCascade();
        _uninstall = new PluginUninstall(_store, _secrets, _cascade);
        _bridge = new PluginBridge([]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static IConfiguration BuildConfig(string dataDir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dataDir, "projects.json"),
            })
            .Build();

    // --- Хелперы ---

    private PluginsController NewController(string userId, bool serviceToken = false,
        bool subsystemEnabled = true, bool flagOn = true)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Subsystems:plugins:Enabled"] = subsystemEnabled ? "true" : "false",
            })
            .Build();
        var gate = new StubFlagGate { PluginsMvp = flagOn };
        var ctl = new PluginsController(config, gate, _store, _installer, _uninstall, _secrets);
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Principal(userId, serviceToken) },
        };
        return ctl;
    }

    private static ClaimsPrincipal Principal(string userId, bool serviceToken)
    {
        var claims = new List<Claim> { new("sub", userId) };
        if (serviceToken) claims.Add(new("typ", "svc"));
        return new(new ClaimsIdentity(claims, "test"));
    }

    // Пакет в каталоге черновика владельца (draft:<slug>): манифест ccs-plugin.json
    // (id/version/permissions) + нативный .claude-plugin/plugin.json (R10-1, name = slug)
    // + один навык (skills/a/SKILL.md — в allow-list валидатора). Возвращает метку источника (draft:<slug>).
    private string CreateDraftPackage(string ownerId, string id, string version, List<string> requested,
        List<(string Name, bool Required)>? secrets = null)
    {
        var slug = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        var root = _store.DraftDir(ownerId, slug);
        Directory.CreateDirectory(Path.Combine(root, "skills", "a"));
        File.WriteAllText(Path.Combine(root, "skills", "a", "SKILL.md"), "# Skill A\n");

        var manifest = new
        {
            id,
            name = slug,
            version,
            contributes = new
            {
                skills = new[] { "skills/a" },
                // PluginSecret читает name/required в нижнем регистре — сериализуем явно.
                requires = secrets is null
                    ? null
                    : new { secrets = secrets.Select(s => new { name = s.Item1, required = s.Item2 }) },
            },
            permissions = new { requested },
        };
        File.WriteAllText(Path.Combine(root, "ccs-plugin.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        // R10-1: нативный манифест обязателен; name = slug (CLI берёт из него имя пакета).
        Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
        File.WriteAllText(Path.Combine(root, ".claude-plugin", "plugin.json"),
            JsonSerializer.Serialize(new { name = slug, version }, new JsonSerializerOptions { WriteIndented = true }));
        return "draft:" + slug;
    }

    // Установить пакет (user-токен, флаг вкл) по метке источника и вернуть запись из стора.
    private PluginInstall Install(string ownerId, string id, string version, List<string> requested,
        List<(string Name, bool Required)>? secrets = null)
    {
        var source = CreateDraftPackage(ownerId, id, version, requested, secrets);
        var ctl = NewController(ownerId);
        ctl.Install(new PluginInstallRequest { Source = source }).Should().BeOfType<OkObjectResult>();
        return _store.LoadInstalled(ownerId).Single(r => r.Id == id);
    }

    // Пакет с панелью (QA-C): draft + panel/index.html + манифест c contributes.panels
    // (entry = panel/index.html, как в контракте assets). Возвращает метку источника (draft:<slug>).
    private string CreatePanelPackage(string ownerId, string id, string version, List<string> requested,
        string panelId = "main", string title = "Test",
        List<(string Name, bool Required)>? secrets = null)
    {
        var slug = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        var root = _store.DraftDir(ownerId, slug);
        Directory.CreateDirectory(Path.Combine(root, "panel"));
        File.WriteAllText(Path.Combine(root, "panel", "index.html"),
            "<html><body><h1>Panel</h1></body></html>");

        var manifest = new
        {
            id,
            name = slug,
            version,
            contributes = new
            {
                panels = new[]
                {
                    new { id = panelId, slot = "sidebar", entry = "panel/index.html", title },
                },
                // PluginSecret читает name/required в нижнем регистре — сериализуем явно.
                requires = secrets is null
                    ? null
                    : new { secrets = secrets.Select(s => new { name = s.Item1, required = s.Item2 }) },
            },
            permissions = new { requested },
        };
        File.WriteAllText(Path.Combine(root, "ccs-plugin.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        // R10-1: нативный манифест обязателен; name = slug.
        Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
        File.WriteAllText(Path.Combine(root, ".claude-plugin", "plugin.json"),
            JsonSerializer.Serialize(new { name = slug, version }, new JsonSerializerOptions { WriteIndented = true }));
        return "draft:" + slug;
    }

    // Установить панель-пакет (user-токен) и вернуть запись из стора.
    private PluginInstall InstallPanel(string ownerId, string id, string version, List<string> requested,
        string panelId = "main", string title = "Test",
        List<(string Name, bool Required)>? secrets = null)
    {
        var source = CreatePanelPackage(ownerId, id, version, requested, panelId, title, secrets);
        var ctl = NewController(ownerId);
        ctl.Install(new PluginInstallRequest { Source = source }).Should().BeOfType<OkObjectResult>();
        return _store.LoadInstalled(ownerId).Single(r => r.Id == id);
    }

    // Контроллер моста (для сквозной проверки assets по entry из списка).
    private PluginBridgeController NewBridgeController(string userId, bool serviceToken = false)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Subsystems:plugins:Enabled"] = "true",
            })
            .Build();
        var ctl = new PluginBridgeController(config, new StubFlagGate { PluginsMvp = true }, _store, _bridge, null);
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Principal(userId, serviceToken) },
        };
        return ctl;
    }

    // --- Гейты: 503 (подсистема) и 403 (флаг) ---

    [Fact]
    public void ФлагВыключен_Отдаёт403СПричиной()
    {
        var ctl = NewController(OwnerA, flagOn: false);
        var res = ctl.List();

        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
        obj.Value!.ToString().Should().Contain("plugins-mvp");
    }

    [Fact]
    public void ПодсистемаВыключена_Отдаёт503СПричиной()
    {
        var ctl = NewController(OwnerA, subsystemEnabled: false);
        var res = ctl.List();

        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(503);
        obj.Value!.ToString().Should().Contain("Подсистема плагинов выключена");
    }

    // --- C5: список — DTO без значений секретов ---

    [Fact]
    public void List_ОтветНеНесётЗначенийСекретов()
    {
        // Установить + выдать секрет с значением; GET /api/plugins не должен нести "value".
        // requested включает secret:LINEAR_TOKEN (minor 4: выданный секрет покрывается правом).
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read", "secret:LINEAR_TOKEN"], secrets: [("LINEAR_TOKEN", true)]);
        var ctl = NewController(OwnerA);
        ctl.Grant("local-952568751510~pkg", new PluginGrantRequest
        {
            Permissions = ["notes:read"],
            Secrets = [new PluginGrantSecret { Name = "LINEAR_TOKEN", Value = "super-secret-value" }],
        }).Should().BeOfType<OkObjectResult>();

        var ok = (OkObjectResult)ctl.List();
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("LINEAR_TOKEN", "имя секрета присутствует");
        json.Should().NotContain("super-secret-value", "значение секрета наружу не уходит");
        json.Should().NotContain("\"value\"", "ключ value в ответе отсутствует");
    }

    // --- C1: source — метка из белого списка, не путь ---

    [Fact]
    public void Install_ПроизвольныйПуть_Отдаёт400()
    {
        // Реальный пакет в standalone-каталоге, но Source — его ПУТЬ (без префикса белого
        // списка). C1: source — метка, не путь → 400. (Мутация «source как путь» → 200.)
        var realDir = Path.Combine(_dir, "rawpath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(realDir, "skills", "a"));
        File.WriteAllText(Path.Combine(realDir, "skills", "a", "SKILL.md"), "# A\n");
        File.WriteAllText(Path.Combine(realDir, "ccs-plugin.json"),
            JsonSerializer.Serialize(new
            {
                id = "local-952568751510/pkg", name = "pkg", version = "1.0.0",
                contributes = new { skills = new[] { "skills/a" } },
                permissions = new { requested = new[] { "notes:read" } },
            }, new JsonSerializerOptions { WriteIndented = true }));

        var ctl = NewController(OwnerA);
        var res = ctl.Install(new PluginInstallRequest { Source = realDir });

        var bad = (BadRequestObjectResult)res;
        bad.StatusCode.Should().Be(400);
    }

    [Fact]
    public void Install_МеткаDraft_УстанавливаетПакет()
    {
        var source = CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        var res = ctl.Install(new PluginInstallRequest { Source = source });
        res.Should().BeOfType<OkObjectResult>();

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-952568751510/pkg");
        record.Source.Should().Be("draft:pkg", "source пишется как метка, не путь");
    }

    // --- C2: install — только user JWT, granted = [] ---

    [Fact]
    public void Install_СервисныйJwt_Отдаёт403()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA, serviceToken: true);

        var res = ctl.Install(new PluginInstallRequest { Source = "draft:pkg" });
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Install_GrantedПусто_ЗапрошенныеИзМанифеста()
    {
        // C2: granted при установке = [] (человек выдаёт через grant), requested — из манифеста.
        var record = Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read", "net:api.linear.app"]);

        record.Granted.Should().BeEmpty("granted при установке пуст");
        record.Requested.Should().Equal("notes:read", "net:api.linear.app");
        record.Enabled.Should().BeTrue();
    }

    // --- M6: повторная установка (совпал sha) — no-op, Granted/Secrets не сбрасываются ---

    [Fact]
    public void Install_Повтор_СовпалSha_NoOp_СохраняетGrantedИСекреты()
    {
        // requested включает secret:LINEAR_TOKEN (minor 4: выданный секрет покрывается правом).
        var source = CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read", "secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", true)]);
        var ctl = NewController(OwnerA);

        // Первая установка + выдача прав и секрета.
        ctl.Install(new PluginInstallRequest { Source = source }).Should().BeOfType<OkObjectResult>();
        ctl.Grant("local-952568751510~pkg", new PluginGrantRequest
        {
            Permissions = ["notes:read"],
            Secrets = [new PluginGrantSecret { Name = "LINEAR_TOKEN", Value = "tok" }],
        }).Should().BeOfType<OkObjectResult>();

        // Повторная установка того же пакета (sha не изменился) — no-op.
        var second = ctl.Install(new PluginInstallRequest { Source = source });
        second.Should().BeOfType<OkObjectResult>();

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-952568751510/pkg");
        record.Granted.Should().Equal(new[] { "notes:read" }, "granted не сброшен повторной установкой");
        record.Secrets.Should().Contain(s => s.Name == "LINEAR_TOKEN" && s.Set,
            "секреты не сброшены повторной установкой");
    }

    // --- M5: атомарность — копирование во временный .staging-{guid} ---

    [Fact]
    public void Install_Атомарность_КопируетВStaging_ИПеремещает()
    {
        var source = CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        ctl.Install(new PluginInstallRequest { Source = source }).Should().BeOfType<OkObjectResult>();

        // Пакет на месте, временного .staging-* не осталось.
        var targetDir = _store.PackageDir(OwnerA, "local-952568751510/pkg", "1.0.0");
        Directory.Exists(targetDir).Should().BeTrue("пакет перемещён в packages/{id}/{version}");
        var packagesRoot = Path.GetDirectoryName(targetDir)!;
        Directory.EnumerateDirectories(packagesRoot, ".staging-*").Should().BeEmpty(
            "временный каталог .staging-{guid} убран после Directory.Move");
    }

    // --- Grant: сервисный JWT → 403 ---

    [Fact]
    public void Grant_СервисныйJwt_Отдаёт403()
    {
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA, serviceToken: true);

        var res = ctl.Grant("local-952568751510~pkg", new PluginGrantRequest { Permissions = ["notes:read"] });
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
    }

    // --- Grant: право вне requested → 400 ---

    [Fact]
    public void Grant_ПравоВнеRequested_Отдаёт400СИменем()
    {
        // requested = notes:read; пробуем выдать tasks:read (не в requested) → 400.
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        var res = ctl.Grant("local-952568751510~pkg", new PluginGrantRequest { Permissions = ["tasks:read"] });
        var bad = (BadRequestObjectResult)res;
        JsonSerializer.Serialize(bad.Value).Should().Contain("tasks:read");
    }

    // --- Grant: секрет не заявлен в манифесте → 400 (minor) ---

    [Fact]
    public void Grant_СекретНеЗаявленВМанифесте_Отдаёт400()
    {
        // Манифест не declares секреты; grant с незнакомым именем → 400.
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        var res = ctl.Grant("local-952568751510~pkg", new PluginGrantRequest
        {
            Permissions = ["notes:read"],
            Secrets = [new PluginGrantSecret { Name = "GHOST_SECRET", Value = "x" }],
        });
        var bad = (BadRequestObjectResult)res;
        JsonSerializer.Serialize(bad.Value).Should().Contain("GHOST_SECRET");
    }

    // --- Grant: обязательный секрет не задан → 400 ---

    [Fact]
    public void Grant_ОбязательныйСекретНеЗадан_Отдаёт400СоСписком()
    {
        // Манифест требует LINEAR_TOKEN (required=true); grant без него → 400.
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"],
            secrets: [("LINEAR_TOKEN", true)]);
        var ctl = NewController(OwnerA);

        var res = ctl.Grant("local-952568751510~pkg", new PluginGrantRequest
        {
            Permissions = ["notes:read"],
            Secrets = [],
        });
        var bad = (BadRequestObjectResult)res;
        JsonSerializer.Serialize(bad.Value).Should().Contain("LINEAR_TOKEN");
    }

    // --- Grant: не включает выключенный плагин (minor) ---

    [Fact]
    public void Grant_НеВключаетВыключенныйПлагин()
    {
        var record = Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        // Выключаем, затем grant: Enabled должен остаться false.
        ctl.Disable("local-952568751510~pkg").Should().BeOfType<OkObjectResult>();
        _store.LoadInstalled(OwnerA).Single().Enabled.Should().BeFalse();

        ctl.Grant("local-952568751510~pkg", new PluginGrantRequest { Permissions = ["notes:read"] })
            .Should().BeOfType<OkObjectResult>();

        _store.LoadInstalled(OwnerA).Single().Enabled.Should().BeFalse(
            "grant не включает выключенный плагин");
        _ = record;
    }

    // --- Grant: happy path — granted = одобренные, секреты записаны ---

    [Fact]
    public void Grant_Успех_ЗаписываетGrantedИСекреты()
    {
        // requested: notes:read + net:* + secret:LINEAR_TOKEN (minor 4: выданный секрет
        // покрывается запрошенным правом secret:{name}). Grant одобряет notes:read.
        // Манифест декларирует LINEAR_TOKEN (non-required) — grant сверяет имя с requires.secrets.
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read", "net:api.linear.app", "secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", false)]);
        var ctl = NewController(OwnerA);

        ctl.Grant("local-952568751510~pkg", new PluginGrantRequest
        {
            Permissions = ["notes:read"],
            Secrets = [new PluginGrantSecret { Name = "LINEAR_TOKEN", Value = "tok" }],
        }).Should().BeOfType<OkObjectResult>();

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-952568751510/pkg");
        record.Granted.Should().Equal("notes:read");
        record.Secrets.Should().Contain(s => s.Name == "LINEAR_TOKEN" && s.Set);
        record.Enabled.Should().BeTrue();
    }

    // --- Install: пакет изменился (sha256) → 409 ---

    [Fact]
    public void Install_ПакетИзменилсяSha256_Отдаёт409()
    {
        var source = CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        // Первая установка — ок.
        ctl.Install(new PluginInstallRequest { Source = source }).Should().BeOfType<OkObjectResult>();

        // Меняем файл пакета в каталоге источника → новый sha256, тот же id → 409 «обновить».
        var draftDir = _store.DraftDir(OwnerA, "pkg");
        File.WriteAllText(Path.Combine(draftDir, "skills", "a", "SKILL.md"), "# Skill A changed\n");
        var second = ctl.Install(new PluginInstallRequest { Source = source });
        var conflict = (ConflictObjectResult)second;
        conflict.StatusCode.Should().Be(409);
    }

    // --- Uninstall: каскад через контроллер ---

    [Fact]
    public void Uninstall_УдаляетЗапись_ИВозвращает200()
    {
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        ctl.Uninstall("local-952568751510~pkg").Should().BeOfType<OkObjectResult>();
        _store.LoadInstalled(OwnerA).Should().BeEmpty();
    }

    [Fact]
    public void Uninstall_ЧужойПакет_Отдаёт404()
    {
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerB);

        ctl.Uninstall("local-952568751510~pkg").Should().BeOfType<NotFoundResult>();
    }

    // --- Disable / Enable ---

    [Fact]
    public void DisableEnable_ПереключаютEnabled()
    {
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        ctl.Disable("local-952568751510~pkg").Should().BeOfType<OkObjectResult>();
        _store.LoadInstalled(OwnerA).Single().Enabled.Should().BeFalse();

        ctl.Enable("local-952568751510~pkg").Should().BeOfType<OkObjectResult>();
        _store.LoadInstalled(OwnerA).Single().Enabled.Should().BeTrue();
    }

    // --- minor 6: тумблер enable/disable — только пользовательский JWT (агент не включает
    // обратно плагин, выключенный человеком) ---

    [Fact]
    public void Enable_СервисныйJwt_Отдаёт403()
    {
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA, serviceToken: true);

        var res = ctl.Enable("local-952568751510~pkg");
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Disable_СервисныйJwt_Отдаёт403()
    {
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA, serviceToken: true);

        var res = ctl.Disable("local-952568751510~pkg");
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
    }

    // --- Секреты (шаг 3.3): PUT/DELETE /secrets/{name}, значение не уходит наружу ---

    // PUT /secrets/{name}: сервисный JWT → 403 (секрет вводит только пользовательский токен).
    [Fact]
    public void PutSecret_СервисныйJwt_Отдаёт403()
    {
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", false)]);
        var ctl = NewController(OwnerA, serviceToken: true);

        var res = ctl.PutSecret("local-952568751510~pkg", "LINEAR_TOKEN",
            new PluginSecretValueRequest { Value = "super-secret" });
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
    }

    // PUT /secrets/{name}: user JWT → 200, значение в store, в списке — {name,set,updatedAt}
    // без значения (M7: значение не в installed.json, а в plugin-secrets.json).
    [Fact]
    public void PutSecret_UserJwt_ЗаписываетЗначение_СписокБезЗначения()
    {
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", false)]);
        var ctl = NewController(OwnerA);
        // Выдать право (critical 2: PutSecret сверяет Granted, а не Requested).
        ctl.Grant("local-952568751510~pkg", new PluginGrantRequest { Permissions = ["secret:LINEAR_TOKEN"] })
            .Should().BeOfType<OkObjectResult>();

        var res = ctl.PutSecret("local-952568751510~pkg", "LINEAR_TOKEN",
            new PluginSecretValueRequest { Value = "super-secret-value" });
        res.Should().BeOfType<OkObjectResult>();

        // Значение записано в per-owner архив.
        _secrets.Get(OwnerA, "local-952568751510/pkg", "LINEAR_TOKEN").Should().Be("super-secret-value");

        // Список: {name, set, updatedAt} — без значения.
        var ok = (OkObjectResult)ctl.List();
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("LINEAR_TOKEN", "имя секрета в списке");
        json.Should().Contain("\"set\":true", "секрет помечен заданным");
        json.Should().Contain("updatedAt", "время изменения в списке");
        json.Should().NotContain("super-secret-value", "значение секрета наружу не уходит");
    }

    // PUT /secrets/{name}: имя не заявлено в манифесте → 400.
    [Fact]
    public void PutSecret_ИмяНеЗаявлено_Отдаёт400()
    {
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", false)]);
        var ctl = NewController(OwnerA);

        var res = ctl.PutSecret("local-952568751510~pkg", "GHOST_SECRET",
            new PluginSecretValueRequest { Value = "x" });
        var bad = (BadRequestObjectResult)res;
        JsonSerializer.Serialize(bad.Value).Should().Contain("GHOST_SECRET");
    }

    // PUT /secrets/{name}: право не выдано (в requested, но не в granted) → 400
    // (rework 12, critical 2: раньше сверяли Requested — значение сохранялось без права).
    // Мутация: вернуть проверку к Requested → тест красный.
    [Fact]
    public void PutSecret_ПравоНеВыдано_Отдаёт400()
    {
        // requested включает secret:LINEAR_TOKEN, но grant выдаёт только notes:read
        // (granted = [notes:read], без secret:LINEAR_TOKEN) → PutSecret → 400.
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read", "secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", false)]);
        var ctl = NewController(OwnerA);
        ctl.Grant("local-952568751510~pkg", new PluginGrantRequest { Permissions = ["notes:read"] })
            .Should().BeOfType<OkObjectResult>();

        var res = ctl.PutSecret("local-952568751510~pkg", "LINEAR_TOKEN",
            new PluginSecretValueRequest { Value = "x" });
        var bad = (BadRequestObjectResult)res;
        JsonSerializer.Serialize(bad.Value).Should().Contain("secret:LINEAR_TOKEN");
    }

    // PUT /secrets/{name}: выключенный плагин → 409 (rework 12, major 3, как у call/assets/panel).
    [Fact]
    public void PutSecret_ВыключенныйПлагин_Отдаёт409()
    {
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", false)]);
        var ctl = NewController(OwnerA);
        ctl.Grant("local-952568751510~pkg", new PluginGrantRequest { Permissions = ["secret:LINEAR_TOKEN"] })
            .Should().BeOfType<OkObjectResult>();
        ctl.Disable("local-952568751510~pkg").Should().BeOfType<OkObjectResult>();

        var res = ctl.PutSecret("local-952568751510~pkg", "LINEAR_TOKEN",
            new PluginSecretValueRequest { Value = "x" });
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(409);
    }

    // DELETE /secrets/{name}: выключенный плагин → 409 (rework 12, major 3).
    [Fact]
    public void DeleteSecret_ВыключенныйПлагин_Отдаёт409()
    {
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", false)]);
        var ctl = NewController(OwnerA);
        ctl.Grant("local-952568751510~pkg", new PluginGrantRequest { Permissions = ["secret:LINEAR_TOKEN"] })
            .Should().BeOfType<OkObjectResult>();
        ctl.Disable("local-952568751510~pkg").Should().BeOfType<OkObjectResult>();

        var res = ctl.DeleteSecret("local-952568751510~pkg", "LINEAR_TOKEN");
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(409);
    }

    // PUT /secrets/{name}: пустое значение → 400 (rework 12, minor 5).
    [Fact]
    public void PutSecret_ПустоеЗначение_Отдаёт400()
    {
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", false)]);
        var ctl = NewController(OwnerA);
        ctl.Grant("local-952568751510~pkg", new PluginGrantRequest { Permissions = ["secret:LINEAR_TOKEN"] })
            .Should().BeOfType<OkObjectResult>();

        var res = ctl.PutSecret("local-952568751510~pkg", "LINEAR_TOKEN",
            new PluginSecretValueRequest { Value = "   " });
        var bad = (BadRequestObjectResult)res;
        bad.StatusCode.Should().Be(400);
    }

    // DELETE /secrets/{name}: user JWT → 200, значение убрано, set=false.
    [Fact]
    public void DeleteSecret_UserJwt_УбираетЗначение()
    {
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", false)]);
        var ctl = NewController(OwnerA);
        // Выдать право (critical 2: PutSecret сверяет Granted, а не Requested).
        ctl.Grant("local-952568751510~pkg", new PluginGrantRequest { Permissions = ["secret:LINEAR_TOKEN"] })
            .Should().BeOfType<OkObjectResult>();
        ctl.PutSecret("local-952568751510~pkg", "LINEAR_TOKEN",
            new PluginSecretValueRequest { Value = "super-secret-value" })
            .Should().BeOfType<OkObjectResult>();

        ctl.DeleteSecret("local-952568751510~pkg", "LINEAR_TOKEN").Should().BeOfType<OkObjectResult>();

        _secrets.Get(OwnerA, "local-952568751510/pkg", "LINEAR_TOKEN").Should().BeNull("значение убрано");
        var ok = (OkObjectResult)ctl.List();
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"set\":false", "после удаления set=false");
        json.Should().NotContain("super-secret-value");
    }

    // DELETE /secrets/{name}: сервисный JWT → 403.
    [Fact]
    public void DeleteSecret_СервисныйJwt_Отдаёт403()
    {
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", false)]);
        var ctl = NewController(OwnerA, serviceToken: true);

        var res = ctl.DeleteSecret("local-952568751510~pkg", "LINEAR_TOKEN");
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
    }

    // --- QA-C: список (GET /api/plugins) отдаёт contributes.panels из манифеста установленной версии ---

    // Установленный плагин с панелью → в списке contributes.panels с верным entry/id/title.
    [Fact]
    public void List_ПлагинСПанелью_ОтдаётContributesPanelsСEntry()
    {
        InstallPanel(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"], panelId: "main", title: "My Panel");
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.List();
        var resp = ((IEnumerable<PluginResponse>)ok.Value!).Single();

        resp.Contributes.Panels.Should().ContainSingle();
        resp.Contributes.Panels[0].Id.Should().Be("main");
        resp.Contributes.Panels[0].Title.Should().Be("My Panel");
        resp.Contributes.Panels[0].Entry.Should().Be("panel/index.html",
            "entry — путь относительно корня пакета, как в contributes.panels[].entry манифеста");
    }

    // Пакет без панели (только навык) → contributes.panels = пустой массив.
    [Fact]
    public void List_БезПанели_ОтдаётПустойМассив()
    {
        Install(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.List();
        var resp = ((IEnumerable<PluginResponse>)ok.Value!).Single();

        resp.Contributes.Panels.Should().BeEmpty("пакет без панели → пустой массив, не null");
    }

    // Два плагина → у каждого свои панели (id различаются, не перепутаны).
    [Fact]
    public void List_ДваПлагина_УКаждогоСвоиПанели()
    {
        InstallPanel(OwnerA, "local-952568751510/alpha", "1.0.0", ["notes:read"], panelId: "a", title: "Alpha");
        InstallPanel(OwnerA, "local-952568751510/beta", "1.0.0", ["notes:read"], panelId: "b", title: "Beta");
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.List();
        var list = ((IEnumerable<PluginResponse>)ok.Value!).ToList();

        var alpha = list.Single(r => r.Id == "local-952568751510/alpha");
        var beta = list.Single(r => r.Id == "local-952568751510/beta");
        alpha.Contributes.Panels.Should().ContainSingle().Which.Id.Should().Be("a");
        beta.Contributes.Panels.Should().ContainSingle().Which.Id.Should().Be("b");
    }

    // Ответ не содержит значений секретов и хостовых путей (data/plugins/…): только id/title/entry.
    [Fact]
    public void List_ОтветНеНесётЗначенийСекретовИХостовыхПутей()
    {
        // Панель + выданный секрет с значением: значение и путь хоста наружу не едут.
        InstallPanel(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read", "secret:LINEAR_TOKEN"],
            panelId: "main", title: "My Panel", secrets: [("LINEAR_TOKEN", false)]);
        var ctl = NewController(OwnerA);
        ctl.Grant("local-952568751510~pkg", new PluginGrantRequest
        {
            Permissions = ["notes:read"],
            Secrets = [new PluginGrantSecret { Name = "LINEAR_TOKEN", Value = "super-secret-value" }],
        }).Should().BeOfType<OkObjectResult>();

        var ok = (OkObjectResult)ctl.List();
        var json = JsonSerializer.Serialize(ok.Value);

        json.Should().Contain("panel/index.html", "entry панели в ответе");
        json.Should().NotContain("super-secret-value", "значение секрета наружу не уходит");
        json.Should().NotContain("data/plugins", "хостовый путь пакета наружу не уходит");
    }

    // Сквозной контракт путей: GET /api/plugins отдаёт entry = panel/index.html, и
    // GET /api/plugins/{id}/{ver}/assets/{entry} по этому же entry возвращает 200.
    [Fact]
    public void List_ИAssets_КонтрактEntryСходитсяEndToEnd()
    {
        var record = InstallPanel(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"],
            panelId: "main", title: "My Panel");
        var listCtl = NewController(OwnerA);

        var ok = (OkObjectResult)listCtl.List();
        var resp = ((IEnumerable<PluginResponse>)ok.Value!).Single();
        var entry = resp.Contributes.Panels[0].Entry;
        entry.Should().Be("panel/index.html");

        // Тот же entry — в assets-эндпоинт: файл существует (panel/index.html) → 200.
        var bridgeCtl = NewBridgeController(OwnerA);
        var assetRes = bridgeCtl.Assets("local-952568751510~pkg", record.Version, entry);
        assetRes.Should().BeOfType<FileContentResult>("assets по entry из списка → 200");
        _ = record;
    }

    // --- Тестовые заглушки ---

    // IFeatureFlagGate: единственный интересующий флаг — plugins-mvp.
    private sealed class StubFlagGate : IFeatureFlagGate
    {
        public bool PluginsMvp;
        public bool IsEnabled(string userId, string key) =>
            key == FeatureFlagKeys.PluginsMvp && PluginsMvp;
    }

    // IPluginInstallCascade: фиксирует вызов (в контроллер-тестах каскад не предмет).
    private sealed class StubCascade : IPluginInstallCascade
    {
        public List<(string Owner, string Id)> Calls { get; } = [];
        public void OnUninstalled(string ownerId, string pluginId) => Calls.Add((ownerId, pluginId));
    }
}
