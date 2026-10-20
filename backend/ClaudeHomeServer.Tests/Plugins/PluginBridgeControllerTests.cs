using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Plugins.Controllers;
using ClaudeHomeServer.Services.Tasks;
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
        PluginBridge? bridge = null, IProjectManager? projects = null)
    {
        var config = NewConfig(userId, subsystemEnabled, flagOn);
        // projects: null — гейт по проекту слота (QA-фикс L1) no-op (ProjectId в теле null);
        // тест гейта (чужой/свой/локальный проект) — со стабом IProjectManager.
        var ctl = new PluginBridgeController(config, new StubFlagGate { PluginsMvp = flagOn }, _store, bridge ?? _bridge, projects);
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

    // --- Assets: 403 (сервисный JWT, rework 12, minor 1: единообразие с call/черновиками) ---
    // Мутация: убрать проверку IsServiceToken в Assets → этот тест падает.

    [Fact]
    public void Assets_СервисныйJwt_Отдаёт403()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA, serviceToken: true);

        var res = ctl.Assets("local-a~pkg", "1.0.0", "panel/index.html");
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
    }

    // --- Assets: 413 (файл панели больше 256 КиБ, QA-фикс H: потолок MaxPanelFileBytes) ---
    // Мутация: убрать проверку MaxPanelFileBytes в Assets → этот тест падает.

    [Fact]
    public void Assets_ФайлБольшой256КиБ_Отдаёт413()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        // index.html панели больше потолка 256 КиБ.
        var packageDir = _store.PackageDir(OwnerA, "local-a/pkg", "1.0.0");
        System.IO.File.WriteAllBytes(
            System.IO.Path.Combine(packageDir, "panel", "index.html"),
            new byte[PluginPackageValidator.MaxPanelFileBytes + 1]);

        var res = ctl.Assets("local-a~pkg", "1.0.0", "panel/index.html");
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(413);
    }

    // --- Assets: ровно 256 КиБ → 200 (потолок не превышен) ---

    [Fact]
    public void Assets_ФайлРовно256КиБ_Отдаёт200()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        var packageDir = _store.PackageDir(OwnerA, "local-a/pkg", "1.0.0");
        System.IO.File.WriteAllBytes(
            System.IO.Path.Combine(packageDir, "panel", "index.html"),
            new byte[PluginPackageValidator.MaxPanelFileBytes]);

        var res = ctl.Assets("local-a~pkg", "1.0.0", "panel/index.html");
        res.Should().BeOfType<FileContentResult>("ровно потолок → 200");
    }

    // --- Assets: сырой файл, байт-в-байт равен файлу на диске (QA-фикс H: нет разбора) ---
    // Мутация: вернуть любую вставку/разбор (BuildSrcdoc, SplitPanelDocument) → тест красный.

    [Fact]
    public void Assets_СыройФайл_БайтВБайт()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        // Записываем известный HTML с «ловушками» для разбора: </head> в строке скрипта,
        // несколько <HEAD>, атрибуты с >, meta refresh.
        const string html =
            "<html><head><title>T</title></head><body>\n" +
            "<script>var s = \"</head><body>\";</script>\n" +
            "<HEAD foo=\"1\">\n" +
            "<meta http-equiv=\"refresh\" content=\"0;url=https://evil\">\n" +
            "<h1>Panel</h1>\n" +
            "</body></html>";
        var packageDir = _store.PackageDir(OwnerA, "local-a/pkg", "1.0.0");
        System.IO.File.WriteAllText(System.IO.Path.Combine(packageDir, "panel", "index.html"), html);

        var res = ctl.Assets("local-a~pkg", "1.0.0", "panel/index.html");
        var fileRes = (FileContentResult)res;
        fileRes.FileContents.Should().BeEquivalentTo(
            System.IO.File.ReadAllBytes(System.IO.Path.Combine(packageDir, "panel", "index.html")),
            "ответ байт-в-байт равен файлу на диске (сервер не разобрал и не вставил ничего)");
    }

    // --- Assets: 255 КиБ повторов <head — время ответа < 500 мс (ReDoS, MAJOR 1) ---
    // Мутация: вернуть regex-разбор (SplitPanelDocument) → время растёт до секунд → тест красный.

    [Fact]
    public void Assets_255КиБПовторовHead_ВремяОтветыМенее500мс()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        // 255 КиБ = 255 * 1024 байт, заполнено повторами "<head" (по 5 байт).
        var chunk = new byte[255 * 1024];
        var pattern = System.Text.Encoding.ASCII.GetBytes("<head");
        for (var i = 0; i < chunk.Length; i += pattern.Length)
            System.Buffer.BlockCopy(pattern, 0, chunk, i,
                Math.Min(pattern.Length, chunk.Length - i));

        var packageDir = _store.PackageDir(OwnerA, "local-a/pkg", "1.0.0");
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(packageDir, "panel", "index.html"), chunk);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = ctl.Assets("local-a~pkg", "1.0.0", "panel/index.html");
        sw.Stop();

        res.Should().BeOfType<FileContentResult>("255 КиB < потолка 256 КиB → 200");
        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "ответ на 255 КиБ повторов <head должен быть < 500 мс (нет квадратичного разбора)");
    }

    // --- Assets: заголовки CSP + nosniff (QA-фикс H, пункт D) ---
    // Мутация: вернуть PanelCsp (старый CSP) → тест заголовка красный.

    [Fact]
    public void Assets_Заголовки_CspSandboxINosniff()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        // Заголовки ставятся на Response — проверяем через HttpContext.
        var res = ctl.Assets("local-a~pkg", "1.0.0", "panel/index.html");
        res.Should().BeOfType<FileContentResult>();

        var csp = ctl.Response.Headers["Content-Security-Policy"].ToString();
        csp.Should().Be("sandbox; default-src 'none'",
            "CSP сырого ответа: sandbox; default-src 'none' (не старый PanelCsp)");

        var nosniff = ctl.Response.Headers["X-Content-Type-Options"].ToString();
        nosniff.Should().Be("nosniff", "X-Content-Type-Options: nosniff");
    }

    // --- Assets: 404 (symlink в СЕРЕДИНЕ пути, rework 12, minor 2) ---
    // Мутация: проверять ReparsePoint только у финального файла (старое поведение) →
    // symlink-каталог в середине пути не отбивается → этот тест падает.

    [Fact]
    public void Assets_SymlinkВСерединеПути_Отдаёт404()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewBridgeController(OwnerA);

        // panel/sub — symlink на внешний каталог; файл sub/inner.html «внутри» ссылки.
        var packageDir = _store.PackageDir(OwnerA, "local-a/pkg", "1.0.0");
        var outsideDir = Path.Combine(_dir, "outside_dir");
        System.IO.Directory.CreateDirectory(outsideDir);
        System.IO.File.WriteAllText(Path.Combine(outsideDir, "inner.html"), "<html>inner</html>");
        var linkDir = Path.Combine(packageDir, "panel", "sub");
        try { System.IO.Directory.CreateSymbolicLink(linkDir, outsideDir); }
        catch (PlatformNotSupportedException)
        {
            // Без symlink (Windows без прав) — создаём обычный каталог: тест всё равно
            // проверяет, что путь panel/sub/inner.html отбивается (реальный symlink-кейс
            // покрыт Assets_Symlink_Отдаёт404 для финального файла).
            System.IO.Directory.CreateDirectory(linkDir);
            System.IO.File.Copy(Path.Combine(outsideDir, "inner.html"), Path.Combine(linkDir, "inner.html"));
        }

        var res = ctl.Assets("local-a~pkg", "1.0.0", "panel/sub/inner.html");
        res.Should().BeOfType<NotFoundResult>("symlink-каталог в середине пути → 404 (не следовать)");
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

    // --- Assets: bump версии — запрос СТАРОЙ версии → 404, НОВОЙ → 200 (QA-фикс K, minor 5) ---
    // Каталоги прошлых версий остаются на диске после bump; без сверки версии из URL с
    // record.Version Assets читал бы файлы неустановленной (старой) версии. Мутация: убрать
    // проверку record.Version != ver → запрос старой версии отдаёт 200 → тест красный.

    [Fact]
    public void Assets_Bump_СтараяВерсия_404_Новая_200()
    {
        // v1: установка 1.0.0.
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        // v2: bump на 2.0.0 (тот же id, новая версия → другой sha; каталог 1.0.0 остаётся).
        InstallPanel(OwnerA, "local-a/pkg", "2.0.0", ["notes:read"]);

        var ctl = NewBridgeController(OwnerA);

        // Запрос старой версии (1.0.0, каталог ещё на диске) → 404 (не установленная версия).
        var resOld = ctl.Assets("local-a~pkg", "1.0.0", "panel/index.html");
        resOld.Should().BeOfType<NotFoundResult>("старая версия после bump → 404");

        // Запрос установленной версии (2.0.0) → 200.
        var resNew = ctl.Assets("local-a~pkg", "2.0.0", "panel/index.html");
        resNew.Should().BeOfType<FileContentResult>("установленная версия → 200");
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

    // --- Активация: подсистема выключена → PluginStore/PluginBridge не в DI (null),
    // гейт даёт 503, а не 500 (NullReferenceException) — по образцу двух соседних
    // контроллеров (rework 12, minor 5: = null в конструкторе, чтобы активация не падала).

    [Fact]
    public void ПодсистемаВыключена_АктивацияСNullStore_Отдаёт503Не500()
    {
        // Подсистема выключена → DI не регистрирует PluginStore/PluginBridge: контроллер
        // активируется с null-зависимостями (значения по умолчанию конструктора).
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Subsystems:plugins:Enabled"] = "false",
            })
            .Build();
        var ctl = new PluginBridgeController(config, new StubFlagGate { PluginsMvp = true }, null, null, null);
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Principal(OwnerA, serviceToken: false) },
        };

        // Гейт (503) срабатывает ДО обращения к null-store → не 500.
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

    // --- Call: 404 (projectId чужого проекта, QA-фикс L1) ---
    // GetById не фильтрует по владельцу (IProjectManager) — проверка владельца в контроллере.
    // Мутация: убрать проверку владельца (project.OwnerId != UserId) → этот тест красный.

    [Fact]
    public async Task Call_ProjectIdЧужогоПроекта_Отдаёт404()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        Grant(OwnerA, "local-a/pkg", ["notes:read"]);
        var bridge = new PluginBridge([new StubCapability("notes:read")]);
        // Проект существует, но принадлежит OwnerB (чужой для OwnerA).
        var projects = new StubProjectManager(new Project { Id = "proj-b", OwnerId = OwnerB });
        var ctl = NewBridgeController(OwnerA, bridge: bridge, projects: projects);

        var res = await ctl.Call("local-a~pkg",
            new PluginCallRequest { Capability = "notes.read", ProjectId = "proj-b" }, CancellationToken.None);
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(404, "чужой проект → 404 (not_found), а не данные");
    }

    // --- Call: 404 (projectId несуществующего проекта, QA-фикс L1) ---

    [Fact]
    public async Task Call_ProjectIdНесуществующего_Отдаёт404()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        Grant(OwnerA, "local-a/pkg", ["notes:read"]);
        var bridge = new PluginBridge([new StubCapability("notes:read")]);
        // Пустый менеджер: GetById → null.
        var projects = new StubProjectManager();
        var ctl = NewBridgeController(OwnerA, bridge: bridge, projects: projects);

        var res = await ctl.Call("local-a~pkg",
            new PluginCallRequest { Capability = "notes.read", ProjectId = "no-such" }, CancellationToken.None);
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(404, "несуществующий проект → 404");
    }

    // --- Call: projectId своего → tasks.list ограничен проектом слота (QA-фикс L1) ---
    // Два проекта владельца, в каждом задача; слот = X → видны только задачи X.
    // Мутация: игнорировать projectId (ProjectId: null в контексте) → этот тест красный
    // (без слота capability вернёт только личные, а не задачи X).

    [Fact]
    public async Task Call_ProjectIdСвоего_ТаскиОграниченыПроектомСлота()
    {
        InstallPanel(OwnerA, "local-a/pkg", "1.0.0", ["tasks:read"]);
        Grant(OwnerA, "local-a/pkg", ["tasks:read"]);

        // TaskManager в изолированном подкаталоге (tasks.json отдельно от projects.json).
        var taskDir = Path.Combine(_dir, "tasks");
        Directory.CreateDirectory(taskDir);
        var taskConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(taskDir, "projects.json"),
                ["Tasks:SaveDebounceMs"] = "0",
            })
            .Build();
        var tasks = new TaskManager(taskConfig);
        tasks.Create("proj-x", OwnerA, new CreateTaskRequest("x-task"));
        tasks.Create("proj-y", OwnerA, new CreateTaskRequest("y-task"));
        tasks.Create(null, OwnerA, new CreateTaskRequest("personal-task"));

        var bridge = new PluginBridge([new TasksPluginCapability(tasks)]);
        // Проект X принадлежит OwnerA (свой) → гейт пропускает.
        var projects = new StubProjectManager(new Project { Id = "proj-x", OwnerId = OwnerA });
        var ctl = NewBridgeController(OwnerA, bridge: bridge, projects: projects);

        var res = await ctl.Call("local-a~pkg",
            new PluginCallRequest { Capability = "tasks.list", ProjectId = "proj-x" }, CancellationToken.None);
        var ok = (OkObjectResult)res;
        var resp = (PluginCallResponse)ok.Value!;
        resp.Ok.Should().BeTrue();
        var json = JsonSerializer.SerializeToElement(resp.Result!);
        json.GetProperty("tasks").GetArrayLength().Should().Be(1, "слот = X → только задачи X");
        json.GetProperty("tasks")[0].GetProperty("title").GetString().Should().Be("x-task");
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

    // Стаб IProjectManager для гейта по проекту слота (QA-фикс L1): GetById возвращает
    // заданный проект, если его id совпадает, иначе null. Остальные методы — пустые.
    private sealed class StubProjectManager : IProjectManager
    {
        private readonly Project? _project;
        public StubProjectManager(Project? project = null) => _project = project;
        public Project? GetById(string id) => _project is not null && _project.Id == id ? _project : null;
        public IReadOnlyCollection<Project> GetByOwner(string userId) => [];
        public IReadOnlyCollection<Project> GetAll() => [];
        public IReadOnlyCollection<Project> GetByRootPath(string rootPath) => [];
    }
}
