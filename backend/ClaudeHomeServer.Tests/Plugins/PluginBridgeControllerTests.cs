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

// REST моста панели v1 (ADR-031 §3, шаг 3.1): assets (200/404/409), call (200/403/404/409),
// srcdoc. Контроллер конструируется напрямую, ControllerContext несёт ClaimsPrincipal.
//
// Гейты: Subsystems:plugins:Enabled → 503, флаг plugins-mvp → 403 (как в PluginsController).
// call: сервисный JWT → 403, чужой пакет → 404, выключенный → 409.
// assets: файл нет → 404, чужой пакет → 404, выключенный → 409.
public class PluginBridgeControllerTests : IDisposable
{
    private const string OwnerA = "owner-a";
    private const string OwnerB = "owner-b";

    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly PluginBridge _bridge;

    public PluginBridgeControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_bridge_ctl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new PluginStore(BuildConfig(_dir));
        _bridge = new PluginBridge([]); // нет обработчиков (MVP: Notes/Tasks — отдельная задача)
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

    private PluginBridgeController NewBridgeController(string userId, bool serviceToken = false,
        bool subsystemEnabled = true, bool flagOn = true,
        PluginBridge? bridge = null)
    {
        var config = NewConfig(userId, subsystemEnabled, flagOn);
        var ctl = new PluginBridgeController(config, new StubFlagGate { PluginsMvp = flagOn }, _store, bridge ?? _bridge);
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Principal(userId, serviceToken) },
        };
        return ctl;
    }

    // PluginsController — для Install/Grant/Disable (методы из пачки A).
    private PluginsController NewPluginsController(string userId, bool serviceToken = false,
        bool subsystemEnabled = true, bool flagOn = true)
    {
        var config = NewConfig(userId, subsystemEnabled, flagOn);
        var installer = new PluginInstaller(_store, new PluginPackageValidator());
        var secrets = new PluginSecretsStore(BuildConfig(_dir));
        var uninstall = new PluginUninstall(_store, secrets, new StubCascade());
        var ctl = new PluginsController(config, new StubFlagGate { PluginsMvp = flagOn }, _store, installer, uninstall, secrets);
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Principal(userId, serviceToken) },
        };
        return ctl;
    }

    private IConfiguration NewConfig(string userId, bool subsystemEnabled, bool flagOn) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = System.IO.Path.Combine(_dir, "projects.json"),
                ["Subsystems:plugins:Enabled"] = subsystemEnabled ? "true" : "false",
            })
            .Build();

    private static ClaimsPrincipal Principal(string userId, bool serviceToken)
    {
        var claims = new List<Claim> { new("sub", userId) };
        if (serviceToken) claims.Add(new("typ", "svc"));
        return new(new ClaimsIdentity(claims, "test"));
    }

    // Пакет с панелью: draft + skills/a/SKILL.md + panel/index.html.
    private string CreatePanelPackage(string ownerId, string id, string version, List<string> requested)
    {
        var slug = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        var root = _store.DraftDir(ownerId, slug);
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "skills", "a"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "skills", "a", "SKILL.md"), "# Skill A\n");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "panel"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "panel", "index.html"),
            "<html><body><h1>Panel</h1></body></html>");

        var manifest = new
        {
            id,
            name = slug,
            version,
            contributes = new
            {
                skills = new[] { "skills/a" },
                panels = new[]
                {
                    new { id = "main", slot = "sidebar", entry = "panel/index.html", title = "Test" },
                },
            },
            permissions = new { requested },
        };
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "ccs-plugin.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, ".claude-plugin"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, ".claude-plugin", "plugin.json"),
            JsonSerializer.Serialize(new { name = slug, version }, new JsonSerializerOptions { WriteIndented = true }));
        return "draft:" + slug;
    }

    // Установить пакет с панелью (через PluginsController, user-токен, флаг вкл).
    private PluginInstall InstallPanel(string ownerId, string id, string version, List<string> requested)
    {
        var source = CreatePanelPackage(ownerId, id, version, requested);
        var ctl = NewPluginsController(ownerId);
        ctl.Install(new PluginInstallRequest { Source = source }).Should().BeOfType<OkObjectResult>();
        return _store.LoadInstalled(ownerId).Single(r => r.Id == id);
    }

    // Выдать права (через PluginsController).
    private void Grant(string ownerId, string id, List<string> permissions)
    {
        var ctl = NewPluginsController(ownerId);
        ctl.Grant(id.Replace('/', '~'), new PluginGrantRequest { Permissions = permissions })
            .Should().BeOfType<OkObjectResult>();
    }

    // Выключить пакет (через PluginsController).
    private void Disable(string ownerId, string id)
    {
        var ctl = NewPluginsController(ownerId);
        ctl.Disable(id.Replace('/', '~')).Should().BeOfType<OkObjectResult>();
    }

    // --- Assets: 200 (файл существует) ---

    [Fact]
    public void Assets_ФайлСуществует_ОтдаётFileResult()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        var res = ctl.Assets("local-a~pkg", "1.0.0", "index.html");
        res.Should().BeOfType<FileContentResult>("файл панели отдаётся FileContentResult (200)");
    }

    // --- Assets: 404 (файла нет) ---

    [Fact]
    public void Assets_ФайлаНет_Отдаёт404()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        var res = ctl.Assets("local-a~pkg", "1.0.0", "missing.html");
        res.Should().BeOfType<NotFoundResult>();
    }

    // --- Assets: 404 (чужой пакет) ---

    [Fact]
    public void Assets_ЧужойПакет_Отдаёт404()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerB);

        var res = ctl.Assets("local-a~pkg", "1.0.0", "index.html");
        res.Should().BeOfType<NotFoundResult>();
    }

    // --- Assets: 409 (выключенный пакет) ---

    [Fact]
    public void Assets_ВыключенныйПакет_Отдаёт409()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        Disable(OwnerA, "local-a/pkg");
        var ctl = NewBridgeController(OwnerA);

        var res = ctl.Assets("local-a~pkg", "1.0.0", "index.html");
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(409);
    }

    // --- Assets: 503 (подсистема выключена) ---

    [Fact]
    public void Assets_ПодсистемаВыключена_Отдаёт503()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA, subsystemEnabled: false);

        var res = ctl.Assets("local-a~pkg", "1.0.0", "index.html");
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(503);
    }

    // --- Call: 403 (сервисный JWT) ---
    // Мутация (критерий): убираем проверку IsServiceToken → этот тест падает.

    [Fact]
    public async Task Call_СервисныйJwt_Отдаёт403()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA, serviceToken: true);

        var res = await ctl.Call("local-a~pkg",
            new PluginCallRequest { Capability = "notes.read" }, CancellationToken.None);
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
    }

    // --- Call: 404 (чужой пакет) ---

    [Fact]
    public async Task Call_ЧужойПакет_Отдаёт404()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerB);

        var res = await ctl.Call("local-a~pkg",
            new PluginCallRequest { Capability = "notes.read" }, CancellationToken.None);
        res.Should().BeOfType<NotFoundResult>();
    }

    // --- Call: 409 (выключенный пакет) ---

    [Fact]
    public async Task Call_ВыключенныйПакет_Отдаёт409()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        Disable(OwnerA, "local-a/pkg");
        var ctl = NewBridgeController(OwnerA);

        var res = await ctl.Call("local-a~pkg",
            new PluginCallRequest { Capability = "notes.read" }, CancellationToken.None);
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(409);
    }

    // --- Call: 503 (обработчик не зарегистрирован) ---
    // MVP: нет обработчиков IPluginCapability → dispatch возвращает unavailable.

    [Fact]
    public async Task Call_НетОбработчика_Отдаёт503()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        Grant(OwnerA, "local-a/pkg", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        var res = await ctl.Call("local-a~pkg",
            new PluginCallRequest { Capability = "notes.read" }, CancellationToken.None);
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(503);
    }

    // --- Call: 200 (есть обработчик, право выдано) ---

    [Fact]
    public async Task Call_ЕстьОбработчикИПраво_Отдаёт200()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        Grant(OwnerA, "local-a/pkg", ["notes:read"]);

        var handler = new StubCapability("notes:read");
        var bridge = new PluginBridge([handler]);
        var ctl = NewBridgeController(OwnerA, bridge: bridge);

        var res = await ctl.Call("local-a~pkg",
            new PluginCallRequest { Capability = "notes.read" }, CancellationToken.None);
        var ok = (OkObjectResult)res;
        var resp = (PluginCallResponse)ok.Value!;
        resp.Ok.Should().BeTrue();
    }

    // --- Call: 403 (право не выдано) ---

    [Fact]
    public async Task Call_ПравоНеВыдано_Отдаёт403()
    {
        // Пакет установлен, но grant не сделан — granted пуст.
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var handler = new StubCapability("notes:read");
        var bridge = new PluginBridge([handler]);
        var ctl = NewBridgeController(OwnerA, bridge: bridge);

        var res = await ctl.Call("local-a~pkg",
            new PluginCallRequest { Capability = "notes.read" }, CancellationToken.None);
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
    }

    // --- Srcdoc: 200 (panel/index.html → srcdoc-ready HTML) ---

    [Fact]
    public void PanelSrcdoc_ФайлСуществует_ОтдаётContent()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        var res = ctl.PanelSrcdoc("local-a~pkg", "1.0.0");
        var content = (ContentResult)res;
        content.Content.Should().Contain("Content-Security-Policy");
        content.Content.Should().Contain("ccsPlugin");
    }

    // --- Srcdoc: 404 (файла нет) ---

    [Fact]
    public void PanelSrcdoc_ФайлаНет_Отдаёт404()
    {
        // Пакет без panel/index.html.
        var slug = "pkg";
        var root = _store.DraftDir(OwnerA, slug);
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "skills", "a"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "skills", "a", "SKILL.md"), "# A\n");
        var manifest = new
        {
            id = "local-a/pkg", name = slug, version = "1.0.0",
            contributes = new { skills = new[] { "skills/a" } },
            permissions = new { requested = new[] { "notes:read" } },
        };
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "ccs-plugin.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, ".claude-plugin"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, ".claude-plugin", "plugin.json"),
            JsonSerializer.Serialize(new { name = slug, version = "1.0.0" },
                new JsonSerializerOptions { WriteIndented = true }));
        // panel/ каталог есть, но index.html нет.
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "panel"));

        var ctl = NewPluginsController(OwnerA);
        ctl.Install(new PluginInstallRequest { Source = "draft:" + slug }).Should().BeOfType<OkObjectResult>();

        var bridgeCtl = NewBridgeController(OwnerA);
        var res = bridgeCtl.PanelSrcdoc("local-a~pkg", "1.0.0");
        res.Should().BeOfType<NotFoundResult>();
    }

    // --- Тестовые заглушки ---

    private sealed class StubFlagGate : IFeatureFlagGate
    {
        public bool PluginsMvp;
        public bool IsEnabled(string userId, string key) =>
            key == FeatureFlagKeys.PluginsMvp && PluginsMvp;
    }

    private sealed class StubCascade : IPluginInstallCascade
    {
        public void OnUninstalled(string ownerId, string pluginId) { }
    }

    private sealed class StubCapability : IPluginCapability
    {
        public string Permission { get; }
        public StubCapability(string permission) => Permission = permission;
        public Task<PluginCapabilityResult> HandleAsync(
            PluginCallContext context, string op, JsonElement args, IReadOnlyList<string> granted, CancellationToken ct)
            => Task.FromResult(new PluginCapabilityResult(Ok: true, Data: "ok"));
    }
}
