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
        // projects: null — серверный гейт ProjectCapabilities (minor 7) в 3.1 no-op
        // (ProjectId в контексте всегда null); тест гейта — отдельно со стабом.
        var ctl = new PluginBridgeController(config, new StubFlagGate { PluginsMvp = flagOn }, _store, bridge ?? _bridge, null);
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

    // --- Assets: 200 (файл панели по новому контракту: path относительно корня пакета) ---
    // Дефект a537ea41: фронт запрашивает /assets/panel/index.html (путь = entry из манифеста,
    // относительно корня пакета), а бэк искал в panel/{path} (т.е. ждал /assets/index.html).
    // Контракт: path — относительно корня версии пакета; отдаём ТОЛЬКО файлы под panel/**.

    [Fact]
    public void Assets_ПанельПоНовомуПути_ОтдаётFileResult()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        var res = ctl.Assets("local-a~pkg", "1.0.0", "panel/index.html");
        res.Should().BeOfType<FileContentResult>("файл панели отдаётся FileContentResult (200)");
    }

    // --- Assets: 404 (файла нет под panel/) ---

    [Fact]
    public void Assets_ФайлаНет_Отдаёт404()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        var res = ctl.Assets("local-a~pkg", "1.0.0", "panel/missing.html");
        res.Should().BeOfType<NotFoundResult>();
    }

    // --- Assets: 404 (старый путь index.html — не под panel/) ---
    // Совместимость со старым контрактом не нужна: «index.html» (без panel/) — 404.

    [Fact]
    public void Assets_СтарыйПутьIndexHtml_Отдаёт404()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        var res = ctl.Assets("local-a~pkg", "1.0.0", "index.html");
        res.Should().BeOfType<NotFoundResult>("старый путь (без panel/) — 404, совместимость не нужна");
    }

    // --- Assets: 404 (чужой пакет) ---

    [Fact]
    public void Assets_ЧужойПакет_Отдаёт404()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerB);

        var res = ctl.Assets("local-a~pkg", "1.0.0", "panel/index.html");
        res.Should().BeOfType<NotFoundResult>();
    }

    // --- Assets: 409 (выключенный пакет) ---

    [Fact]
    public void Assets_ВыключенныйПакет_Отдаёт409()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        Disable(OwnerA, "local-a/pkg");
        var ctl = NewBridgeController(OwnerA);

        var res = ctl.Assets("local-a~pkg", "1.0.0", "panel/index.html");
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(409);
    }

    // --- Assets: 503 (подсистема выключена) ---

    [Fact]
    public void Assets_ПодсистемаВыключена_Отдаёт503()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA, subsystemEnabled: false);

        var res = ctl.Assets("local-a~pkg", "1.0.0", "panel/index.html");
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(503);
    }

    // --- Assets: path traversal и файлы вне panel/ → 404 (дефект a537ea41) ---
    // Путь строится только через SafePath.Join; «../», абсолютные пути, обратные слеши,
    // symlink и ReparsePoint отбиваются. Всё, что не под panel/**, — 404.

    [Fact]
    public void Assets_ПутьTraversal_Отдаёт404()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        // «../ccs-plugin.json» — выводит за корень пакета (path traversal).
        var res = ctl.Assets("local-a~pkg", "1.0.0", "../ccs-plugin.json");
        res.Should().BeOfType<NotFoundResult>("«../» вывел за корень пакета → 404");
    }

    [Fact]
    public void Assets_ПанельParentParent_Отдаёт404()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        // «panel/../../x» — начинается с panel/, но разрешается в root/x (вне panel/).
        var res = ctl.Assets("local-a~pkg", "1.0.0", "panel/../../x");
        res.Should().BeOfType<NotFoundResult>("«panel/../../x» разрешается вне panel/ → 404");
    }

    [Fact]
    public void Assets_ФайлВнеPanel_Отдаёт404()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        // skills/a/SKILL.md — внутри пакета, но НЕ под panel/ → 404.
        var res = ctl.Assets("local-a~pkg", "1.0.0", "skills/a/SKILL.md");
        res.Should().BeOfType<NotFoundResult>("файл вне panel/ → 404");
    }

    [Fact]
    public void Assets_Манифест_Отдаёт404()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        // ccs-plugin.json — манифест, не должен торчать наружу.
        var res = ctl.Assets("local-a~pkg", "1.0.0", "ccs-plugin.json");
        res.Should().BeOfType<NotFoundResult>("манифест вне panel/ → 404");
    }

    [Fact]
    public void Assets_Symlink_Отдаёт404()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        // Symlink в panel/ на внешний файл → не следовать → 404.
        var packageDir = _store.PackageDir(OwnerA, "local-a/pkg", "1.0.0");
        var outside = Path.Combine(_dir, "outside.html");
        System.IO.File.WriteAllText(outside, "<html>outside</html>");
        var link = Path.Combine(packageDir, "panel", "link.html");
        try { System.IO.File.CreateSymbolicLink(link, outside); }
        catch (PlatformNotSupportedException) { System.IO.File.Copy(outside, link); }

        var res = ctl.Assets("local-a~pkg", "1.0.0", "panel/link.html");
        res.Should().BeOfType<NotFoundResult>("symlink в panel/ → 404 (не следовать)");
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

    // --- Call: 404 (id существует только как черновик, не установлен) ---
    // Предпросмотр панели черновика работает БЕЗ выданных прав: у черновика нет записи
    // установки, значит прав нет → call даёт отказ (404), а не данные.

    [Fact]
    public async Task Call_ТолькоЧерновик_Отдаёт404()
    {
        // Черновик создан (draft), но НЕ установлен — записи в installed.json нет.
        CreatePanelPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

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

    // --- Call: маппинг кодов ошибок (rework 12, major 4) ---
    // timeout → 504, limit → 429, upstream → 502 (раньше все падали в 500).
    // Код в теле JSON не меняется — фронт различает по нему.

    [Fact]
    public async Task Call_Timeout_Отдаёт504()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        Grant(OwnerA, "local-a/pkg", ["notes:read"]);
        var bridge = new PluginBridge([new StubErrorCapability("notes:read", "timeout")]);
        var ctl = NewBridgeController(OwnerA, bridge: bridge);

        var res = await ctl.Call("local-a~pkg",
            new PluginCallRequest { Capability = "notes.read" }, CancellationToken.None);
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(504);
    }

    [Fact]
    public async Task Call_Limit_Отдаёт429()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        Grant(OwnerA, "local-a/pkg", ["notes:read"]);
        var bridge = new PluginBridge([new StubErrorCapability("notes:read", "limit")]);
        var ctl = NewBridgeController(OwnerA, bridge: bridge);

        var res = await ctl.Call("local-a~pkg",
            new PluginCallRequest { Capability = "notes.read" }, CancellationToken.None);
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(429);
    }

    [Fact]
    public async Task Call_Upstream_Отдаёт502()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        Grant(OwnerA, "local-a/pkg", ["notes:read"]);
        var bridge = new PluginBridge([new StubErrorCapability("notes:read", "upstream")]);
        var ctl = NewBridgeController(OwnerA, bridge: bridge);

        var res = await ctl.Call("local-a~pkg",
            new PluginCallRequest { Capability = "notes.read" }, CancellationToken.None);
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(502);
    }

    // --- Assets: файл больше 4 MiB → 413 (rework 12, minor 6) ---

    [Fact]
    public void Assets_ФайлБольшой_Отдаёт413()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        // Файл панели больше потолка 4 MiB.
        var packageDir = _store.PackageDir(OwnerA, "local-a/pkg", "1.0.0");
        System.IO.File.WriteAllBytes(
            System.IO.Path.Combine(packageDir, "panel", "big.bin"),
            new byte[4 * 1024 * 1024 + 1]);

        var res = ctl.Assets("local-a~pkg", "1.0.0", "panel/big.bin");
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(413);
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

    // Обработчик, возвращающий ошибку с заданным кодом (для маппинга кодов, major 4).
    private sealed class StubErrorCapability : IPluginCapability
    {
        public string Permission { get; }
        private readonly string _code;
        public StubErrorCapability(string permission, string code)
        {
            Permission = permission;
            _code = code;
        }
        public Task<PluginCapabilityResult> HandleAsync(
            PluginCallContext context, string op, JsonElement args, IReadOnlyList<string> granted, CancellationToken ct)
            => Task.FromResult(new PluginCapabilityResult(false, Error: new PluginCallError(_code, "test")));
    }
}
