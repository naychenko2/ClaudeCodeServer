using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Plugins.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Plugins;

// REST черновиков build-it-yourself (ADR-031, шаг 4.3a) — unit-тесты контроллера
// (контроллер конструируется напрямую, ControllerContext несёт ClaimsPrincipal).
//
// Гейты: Subsystems:plugins:Enabled (SubsystemGate) → 503; флаг plugins-mvp → 403;
// флаг plugins-build-yourself → 403. На ВСЕХ ручках: сервисный JWT → 403, чужой
// владелец → 404.
//
// Accept: установка по метке draft:<slug> (PluginInstaller — единственный арбитр).
//   - первая установка → granted = [];
//   - обновление → granted = старые ∩ новые запрошенные; consentRequired + newPermissions;
//   - 409 при том же id+version и другом sha; no-op при том же sha (granted/secrets не сбрасываются);
//   - заданные секреты прошлой версии сохраняются; черновик после успеха не удаляется.
public class PluginDraftsControllerTests : IDisposable
{
    private const string OwnerA = "owner-a";
    private const string OwnerB = "owner-b";

    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly PluginSecretsStore _secrets;
    private readonly PluginDraftService _drafts;
    private readonly PluginInstaller _installer;

    public PluginDraftsControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_drafts_ctl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new PluginStore(BuildConfig(_dir));
        _secrets = new PluginSecretsStore(BuildConfig(_dir));
        _drafts = new PluginDraftService(_store, new PluginPackageValidator());
        _installer = new PluginInstaller(_store, new PluginPackageValidator());
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

    private PluginDraftsController NewController(string userId, bool serviceToken = false,
        bool subsystemEnabled = true, bool mvpOn = true, bool buildYourselfOn = true)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Subsystems:plugins:Enabled"] = subsystemEnabled ? "true" : "false",
            })
            .Build();
        var gate = new StubFlagGate { PluginsMvp = mvpOn, PluginsBuildYourself = buildYourselfOn };
        var ctl = new PluginDraftsController(config, gate, _store, _drafts, _installer, new PluginDraftDiff(_store));
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
                requires = secrets is null
                    ? null
                    : new { secrets = secrets.Select(s => new { name = s.Item1, required = s.Item2 }) },
            },
            permissions = new { requested },
        };
        File.WriteAllText(Path.Combine(root, "ccs-plugin.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
        File.WriteAllText(Path.Combine(root, ".claude-plugin", "plugin.json"),
            JsonSerializer.Serialize(new { name = slug, version }, new JsonSerializerOptions { WriteIndented = true }));
        return "draft:" + slug;
    }

    // Изменить содержимое навыка в черновике (→ новый sha256, тот же id+version).
    private void MutateDraft(string ownerId, string slug)
    {
        var draftDir = _store.DraftDir(ownerId, slug);
        File.WriteAllText(Path.Combine(draftDir, "skills", "a", "SKILL.md"), "# Skill A changed\n");
    }

    // Добавить панель в черновик: _drafts/{slug}/panel/index.html.
    private void AddPanelToDraft(string ownerId, string slug, string html)
    {
        var root = _store.DraftDir(ownerId, slug);
        Directory.CreateDirectory(Path.Combine(root, "panel"));
        File.WriteAllText(Path.Combine(root, "panel", "index.html"), html);
    }

    // Выдать права/секреты записи напрямую (как делает человек через grant): granted + set-секреты.
    private void GrantDirectly(string ownerId, string id, List<string> granted,
        List<(string Name, string Value)>? secrets = null)
    {
        _store.Update(ownerId, records =>
        {
            var rec = records.FirstOrDefault(r => r.Id == id);
            if (rec is not null)
            {
                rec.Granted = granted;
                rec.Secrets = (secrets ?? [])
                    .Select(s => new PluginInstallSecret { Name = s.Name, Set = true, UpdatedAt = DateTime.UtcNow })
                    .ToList();
            }
            return records;
        });
        foreach (var s in secrets ?? [])
            _secrets.Set(ownerId, id, s.Name, s.Value);
    }

    // --- Гейты: 403 (флаг plugins-build-yourself) и 503 (подсистема) ---

    [Fact]
    public void ФлагBuildYourselfВыключен_Отдаёт403СПричиной()
    {
        var ctl = NewController(OwnerA, buildYourselfOn: false);
        var res = ctl.List();

        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
        obj.Value!.ToString().Should().Contain("plugins-build-yourself");
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

    // --- Сервисный JWT → 403 на каждой ручке ---

    [Fact]
    public void List_СервисныйJwt_Отдаёт403()
    {
        var ctl = NewController(OwnerA, serviceToken: true);
        var res = ctl.List();
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Preview_СервисныйJwt_Отдаёт403()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA, serviceToken: true);
        var res = ctl.Preview("pkg");
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Accept_СервисныйJwt_Отдаёт403()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA, serviceToken: true);
        var res = ctl.Accept("pkg");
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
    }

    // --- Чужой владелец → 404 (черновик другого владельца не в _drafts текущего) ---

    [Fact]
    public void Preview_ЧужойВладелец_Отдаёт404()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerB);
        var res = ctl.Preview("pkg");
        res.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void Accept_ЧужойВладелец_Отдаёт404()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerB);
        var res = ctl.Accept("pkg");
        res.Should().BeOfType<NotFoundObjectResult>();
    }

    // --- List: поля сводки (slug, id, version, время, число проблем, «такая же версия») ---

    [Fact]
    public void List_СводкаЧерновиков_СодержитПоля()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.List();
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"Slug\":\"pkg\"", "slug в списке");
        json.Should().Contain("\"Id\":\"local-a/pkg\"", "id из манифеста");
        json.Should().Contain("\"Version\":\"1.0.0\"", "версия из манифеста");
        json.Should().Contain("\"Problems\":0", "валидный черновик — 0 проблем");
        json.Should().Contain("\"InstalledSameVersion\":false", "ещё не установлена такая версия");
    }

    [Fact]
    public void List_УстановленаТакаяВерсия_ПризнакTrue()
    {
        var source = CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();

        var ok = (OkObjectResult)ctl.List();
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"InstalledSameVersion\":true", "после accept — установлена такая же версия");
        _ = source;
    }

    // --- Preview: тот же PluginDraftService.Preview, что у draft_preview ---

    [Fact]
    public void Preview_ВозвращаетСводку_КакDraftPreview()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.Preview("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"Name\":\"pkg\"", "имя пакета");
        json.Should().Contain("\"Version\":\"1.0.0\"", "версия");
        json.Should().Contain("notes:read", "запрошенные права");
        json.Should().Contain("\"IsValid\":true", "валидный черновик");
    }

    // --- Panel: 200 (panel/index.html → srcdoc-ready HTML с CSP + шимом моста) ---

    [Fact]
    public void Panel_ОтдаётSrcdocСCsp()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        AddPanelToDraft(OwnerA, "pkg", "<html><body><h1>Panel</h1></body></html>");
        var ctl = NewController(OwnerA);

        var content = (ContentResult)ctl.Panel("pkg");
        content.Content.Should().Contain("Content-Security-Policy", "CSP в meta");
        content.Content.Should().Contain("ccsPlugin", "шим моста");
        content.Content.Should().Contain("<h1>Panel</h1>", "HTML панели в теле");
    }

    // --- Panel: полный HTML-документ со своим <head> (дефект 3caf4f13) ---
    //
    // До фикса: BuildSrcdoc просто вставлял весь HTML панели в <body> хоста.
    // Если панель — полный документ со своим <head> (и meta CSP), тот <head>
    // оказывался ВНУТРИ <body> — браузер его игнорировал и писал красную
    // ошибку в консоль. Фикс: извлечь head/body панели и вставить их содержимое
    // в head/body хоста; meta CSP хоста остаётся ПЕРВОЙ в <head>.

    [Fact]
    public void Panel_ПолныйДокумент_CspMetaВнутриHead()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        // Панель — ПОЛНЫЙ HTML-документ со своим <head> и meta CSP.
        const string panel =
            "<!DOCTYPE html>\n<html>\n<head>\n" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'unsafe-inline'\">\n" +
            "<title>Panel Title</title>\n" +
            "</head>\n<body>\n<h1>Panel</h1>\n</body>\n</html>";
        AddPanelToDraft(OwnerA, "pkg", panel);
        var ctl = NewController(OwnerA);

        var content = (ContentResult)ctl.Panel("pkg");
        var srcdoc = content.Content;

        // Вырезаем <head>...</head> итогового документа (от <head> до </head>).
        var headStart = srcdoc.IndexOf("<head>");
        var headEnd = srcdoc.IndexOf("</head>");
        var head = srcdoc.Substring(headStart, headEnd - headStart);

        // 1) Meta CSP хоста — ПЕРВАЯ в <head> (политика не ослаблена, та же строка).
        head.Should().StartWith("<head>\n  <meta http-equiv=\"Content-Security-Policy\"",
            "meta CSP хоста обязана быть первой в <head>");

        // 2) Содержимое <head> панели (title, meta CSP панели) теперь ВНУТРИ <head> хоста
        //    (до фикса — в <body>, браузер игнорировал). Проверяем по уникальному маркеру:
        //    <title>Panel Title</title> — есть только в <head> панели.
        head.Should().Contain("<title>Panel Title</title>",
            "содержимое <head> панели обязано оказаться внутри <head> итогового документа");

        // 3) Тело панели на месте.
        srcdoc.Should().Contain("<h1>Panel</h1>", "HTML панели в теле");
    }

    // --- Panel: 403 (сервисный JWT) ---

    [Fact]
    public void Panel_СервисныйJwt_Отдаёт403()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        AddPanelToDraft(OwnerA, "pkg", "<html></html>");
        var ctl = NewController(OwnerA, serviceToken: true);
        var obj = (ObjectResult)ctl.Panel("pkg");
        obj.StatusCode.Should().Be(403);
    }

    // --- Panel: 404 (чужой владелец) ---

    [Fact]
    public void Panel_ЧужойВладелец_Отдаёт404()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        AddPanelToDraft(OwnerA, "pkg", "<html></html>");
        var ctl = NewController(OwnerB);
        ctl.Panel("pkg").Should().BeOfType<NotFoundObjectResult>();
    }

    // --- Panel: 404 (нет панели) ---

    [Fact]
    public void Panel_БезПанели_Отдаёт404()
    {
        // Черновик есть, но panel/index.html нет.
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Panel("pkg").Should().BeOfType<NotFoundObjectResult>();
    }

    // --- Panel: 413 (файл > 4 MiB) ---
    // Мутация (критерий): убрать проверку лимита размера → этот тест падает.

    [Fact]
    public void Panel_ФайлБольшой_Отдаёт413()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var root = _store.DraftDir(OwnerA, "pkg");
        Directory.CreateDirectory(Path.Combine(root, "panel"));
        File.WriteAllBytes(Path.Combine(root, "panel", "index.html"), new byte[4 * 1024 * 1024 + 1]);
        var ctl = NewController(OwnerA);
        var obj = (ObjectResult)ctl.Panel("pkg");
        obj.StatusCode.Should().Be(413);
    }

    // --- Panel: «../» в slug отбит (slug не по грамматике → 404, не 500 от SafePath.Join) ---

    [Fact]
    public void Panel_ТочкаТочкаВSlug_Отбита()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        AddPanelToDraft(OwnerA, "pkg", "<html></html>");
        var ctl = NewController(OwnerA);
        ctl.Panel("../pkg").Should().BeOfType<NotFoundObjectResult>();
    }

    // --- Preview: slug длиннее 64 (потолок PluginId.MaxSlugLength) → 404, не 500 ---
    // Клиент isDraftSlug (frontend/src/features/plugins/feed/records.tsx) держит тот же 64:
    // граница не разъезжается. Мутация: убрать потолок в PluginId.IsValidSlug → slug 65 проходит
    // IsValidSlug → DraftDir не null → Preview не бросает ArgumentException → 200, а не 404 → тест красный.

    [Fact]
    public void Preview_SlugДлиннее64_Отдаёт404()
    {
        var ctl = NewController(OwnerA);
        ctl.Preview(new string('a', 65)).Should().BeOfType<NotFoundObjectResult>();
    }

    // --- Panel: symlink не следовать (→ 404) ---

    [Fact]
    public void Panel_Symlink_Отбита()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var root = _store.DraftDir(OwnerA, "pkg");
        Directory.CreateDirectory(Path.Combine(root, "panel"));
        var outside = Path.Combine(_dir, "outside.html");
        File.WriteAllText(outside, "<html>outside</html>");
        var link = Path.Combine(root, "panel", "index.html");
        try { File.CreateSymbolicLink(link, outside); }
        catch (PlatformNotSupportedException) { File.Copy(outside, link); }
        var ctl = NewController(OwnerA);
        ctl.Panel("pkg").Should().BeOfType<NotFoundObjectResult>();
    }

    // --- Accept: 400 при проблемах валидатора ---

    [Fact]
    public void Accept_ПроблемыВалидатора_Отдаёт400СоСписком()
    {
        // Пакет без вклада (no-contribution) → валидатор/манифест ругается → 400.
        var slug = "badpkg";
        var root = _store.DraftDir(OwnerA, slug);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ccs-plugin.json"),
            JsonSerializer.Serialize(new
            {
                id = "local-a/badpkg",
                name = slug,
                version = "1.0.0",
                contributes = new PluginContributes(),
                permissions = new { requested = new[] { "notes:read" } },
            }, new JsonSerializerOptions { WriteIndented = true }));
        Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
        File.WriteAllText(Path.Combine(root, ".claude-plugin", "plugin.json"),
            JsonSerializer.Serialize(new { name = slug, version = "1.0.0" }, new JsonSerializerOptions { WriteIndented = true }));

        var ctl = NewController(OwnerA);
        var res = ctl.Accept(slug);

        var bad = (BadRequestObjectResult)res;
        bad.StatusCode.Should().Be(400);
        JsonSerializer.Serialize(bad.Value).Should().Contain("problems", "список проблем в ответе");
    }

    // --- Accept: первая установка → granted = [], consentRequired = true, newPermissions = все Requested ---
    // Дефект 86dec98d: раньше consentRequired=false → фронт не открывал диалог согласия,
    // плагин оставался без выданных прав. Теперь: все запрошенные права «новые» для человека.

    [Fact]
    public void Accept_ПерваяУстановкаСПравами_ConsentRequired_NewPermissionsВсе_GrantedПусто()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read", "net:api.linear.app"]);
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.Accept("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"granted\":[", "granted = [] (accept ничего не выдаёт сам)");
        json.Should().Contain("\"consentRequired\":true", "первая установка с правами → требуется согласие");
        json.Should().Contain("notes:read", "notes:read в newPermissions");
        json.Should().Contain("net:api.linear.app", "net:api.linear.app в newPermissions");

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-a/pkg");
        record.Granted.Should().BeEmpty("granted при первой установке пуст");
        record.Requested.Should().Equal("notes:read", "net:api.linear.app");
        record.Enabled.Should().BeTrue();
        record.Source.Should().Be("draft:pkg");
    }

    // --- Accept: первая установка БЕЗ прав → consentRequired = false ---

    [Fact]
    public void Accept_ПерваяУстановкаБезПрав_ConsentRequiredFalse()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", []);
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.Accept("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"consentRequired\":false", "без запрошенных прав → согласия не требуется");
        json.Should().Contain("\"newPermissions\":[", "newPermissions пуст");

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-a/pkg");
        record.Granted.Should().BeEmpty();
        record.Requested.Should().BeEmpty();
    }

    // --- Accept: обновление БЕЗ новых прав → consentRequired = false ---

    [Fact]
    public void Accept_ОбновлениеБезНовыхПрав_ConsentRequiredFalse()
    {
        // v1: requested = [notes:read]; accept → granted = [notes:read].
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();
        GrantDirectly(OwnerA, "local-a/pkg", ["notes:read"]);

        // v2: та же id, версия 2.0.0, тот же requested = [notes:read] (нет новых прав).
        CreateDraftPackage(OwnerA, "local-a/pkg", "2.0.0", ["notes:read"]);
        var ok = (OkObjectResult)ctl.Accept("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"consentRequired\":false", "обновление без новых прав → согласия не требуется");
        json.Should().Contain("\"newPermissions\":[", "newPermissions пуст");

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-a/pkg");
        record.Version.Should().Be("2.0.0");
        record.Granted.Should().Equal(new[] { "notes:read" }, "granted = старые ∩ новые");
    }

    // --- Accept: обновление с новым правом → consentRequired, granted не расширяется ---

    [Fact]
    public void Accept_ОбновлениеСНовымПравом_ConsentRequired_НеРасширяетGranted()
    {
        // v1: requested = [notes:read]; accept (granted=[]) → человек выдал notes:read.
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();
        GrantDirectly(OwnerA, "local-a/pkg", ["notes:read"]);

        // v2: та же id, версия 2.0.0, requested = [notes:read, net:api.linear.app] (новое право).
        CreateDraftPackage(OwnerA, "local-a/pkg", "2.0.0", ["notes:read", "net:api.linear.app"]);
        var res = ctl.Accept("pkg");

        var ok = (OkObjectResult)res;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"consentRequired\":true", "новое запрошенное право → требуется согласие");
        json.Should().Contain("net:api.linear.app", "новое право в списке newPermissions");

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-a/pkg");
        record.Version.Should().Be("2.0.0");
        record.Granted.Should().Equal(new[] { "notes:read" }, "granted = старые ∩ новые: новое право не выдано");
    }

    // --- Accept: 409 при том же id+version и другом sha ---

    [Fact]
    public void Accept_ТотжеIdВерсия_ДругойSha_Отдаёт409()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();

        // Меняем файл в черновике (тот же id+version) → новый sha256 → 409 «поднимите версию».
        MutateDraft(OwnerA, "pkg");
        var res = ctl.Accept("pkg");
        var conflict = (ConflictObjectResult)res;
        conflict.StatusCode.Should().Be(409);
    }

    // --- Accept: no-op при том же sha → granted/secrets не сбрасываются ---

    [Fact]
    public void Accept_ТотжеSha_NoOp_СохраняетGrantedИСекреты()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read", "secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", true)]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();
        GrantDirectly(OwnerA, "local-a/pkg", ["notes:read", "secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", "tok")]);

        // Повторный accept того же пакета (sha не изменился) → no-op.
        var res = ctl.Accept("pkg");
        var ok = (OkObjectResult)res;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"consentRequired\":false", "no-op — новых прав нет");

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-a/pkg");
        record.Granted.Should().Equal(new[] { "notes:read", "secret:LINEAR_TOKEN" }, "granted не сброшен no-op");
        record.Secrets.Should().Contain(s => s.Name == "LINEAR_TOKEN" && s.Set,
            "секреты не сброшены no-op");
        _secrets.Get(OwnerA, "local-a/pkg", "LINEAR_TOKEN").Should().Be("tok", "значение секрета сохранено");
    }

    // --- Accept: заданные секреты прошлой версии сохраняются при обновлении ---

    [Fact]
    public void Accept_Обновление_СохраняетСекретыПрошлойВерсии()
    {
        // v1: secret LINEAR_TOKEN (required); accept → человек выдал secret:LINEAR_TOKEN + значение.
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", true)]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();
        GrantDirectly(OwnerA, "local-a/pkg", ["secret:LINEAR_TOKEN"], secrets: [("LINEAR_TOKEN", "tok")]);

        // v2: та же id, версия 2.0.0, тот же secret (required), тот же requested.
        CreateDraftPackage(OwnerA, "local-a/pkg", "2.0.0", ["secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", true)]);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-a/pkg");
        record.Version.Should().Be("2.0.0");
        record.Secrets.Should().Contain(s => s.Name == "LINEAR_TOKEN" && s.Set,
            "заданный секрет прошлой версии сохранён при обновлении");
        _secrets.Get(OwnerA, "local-a/pkg", "LINEAR_TOKEN").Should().Be("tok", "значение секрета пережило обновление");
    }

    // --- Accept: черновик после успеха НЕ удаляется ---

    [Fact]
    public void Accept_Успех_ЧерновикНеУдаляется()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();

        Directory.Exists(_store.DraftDir(OwnerA, "pkg")).Should().BeTrue(
            "черновик остаётся на месте после accept (агент может править дальше)");
    }

    // --- Accept: два параллельных accept одного slug не портят installed.json ---

    [Fact]
    public async Task Accept_ДваПараллельных_НеПортятInstalledJson()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        // Два параллельных accept одного slug: каждый делает store.Update (load→mutate→save
        // под одной блокировкой) — итог: ровно одна запись, не «половинная» и не дубль.
        var results = await Task.WhenAll(
            Task.Run(() => ctl.Accept("pkg")),
            Task.Run(() => ctl.Accept("pkg")));
        results[0].Should().BeOfType<OkObjectResult>();
        results[1].Should().BeOfType<OkObjectResult>();

        var records = _store.LoadInstalled(OwnerA);
        records.Count(r => r.Id == "local-a/pkg").Should().Be(1, "ровно одна запись, installed.json не потерян");
        records.Single(r => r.Id == "local-a/pkg").Sha256.Should().NotBeEmpty("запись целая (sha на месте)");
    }

    // --- Reject: перенос в _rejected, убирает из активных (шаг 4.3c) ---

    [Fact]
    public void Reject_Переносит_ИУбираетИзАктивных()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.Reject("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"rejectedAt\":", "время отклонения в ответе");
        json.Should().Contain("\"expiresAt\":", "дата исчезновения в ответе");

        Directory.Exists(_store.DraftDir(OwnerA, "pkg")).Should().BeFalse("черновик убран из _drafts");
        Directory.Exists(_store.RejectedDir(OwnerA, "pkg")).Should().BeTrue("черновик перенесён в _rejected");

        // List: активный исчез, отклонённый помечен rejected: true + дата исчезновения.
        var listJson = JsonSerializer.Serialize(((OkObjectResult)ctl.List()).Value);
        listJson.Should().Contain("\"Rejected\":true", "отклонённый помечен rejected: true");
        listJson.Should().Contain("\"ExpiresAt\":", "дата исчезновения в списке");
    }

    [Fact]
    public void Reject_СервисныйJwt_Отдаёт403()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA, serviceToken: true);
        var obj = (ObjectResult)ctl.Reject("pkg");
        obj.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Reject_ЧужойВладелец_Отдаёт404()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerB);
        ctl.Reject("pkg").Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void Reject_ТочкаТочкаВSlug_Отбита()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        // «../pkg» — не по грамматике slug → Exists=false → 404 (не 500 от SafePath.Join).
        ctl.Reject("../pkg").Should().BeOfType<NotFoundObjectResult>();
    }

    // --- Restore: возврат в _drafts, конфликт slug → 409 (шаг 4.3c) ---

    [Fact]
    public void Restore_Возвращает_ВDrafts()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Reject("pkg").Should().BeOfType<OkObjectResult>();

        var ok = (OkObjectResult)ctl.Restore("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"restored\":true", "черновик возвращён");
        Directory.Exists(_store.DraftDir(OwnerA, "pkg")).Should().BeTrue("черновик снова в _drafts");
        Directory.Exists(_store.RejectedDir(OwnerA, "pkg")).Should().BeFalse("_rejected пуст");
    }

    [Fact]
    public void Restore_КонфликтSlug_Отдаёт409()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Reject("pkg").Should().BeOfType<OkObjectResult>();
        // Новый черновик того же slug занял _drafts/pkg.
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);

        var conflict = (ConflictObjectResult)ctl.Restore("pkg");
        conflict.StatusCode.Should().Be(409);
    }

    [Fact]
    public void Restore_СервисныйJwt_Отдаёт403()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA, serviceToken: true);
        var obj = (ObjectResult)ctl.Restore("pkg");
        obj.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Restore_ЧужойВладелец_Отдаёт404()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerB);
        ctl.Restore("pkg").Should().BeOfType<NotFoundObjectResult>();
    }

    // --- Тестовая заглушка флага ---

    // IFeatureFlagGate: интересующие флаги — plugins-mvp и plugins-build-yourself.
    private sealed class StubFlagGate : IFeatureFlagGate
    {
        public bool PluginsMvp;
        public bool PluginsBuildYourself;
        public bool IsEnabled(string userId, string key) =>
            key == FeatureFlagKeys.PluginsMvp ? PluginsMvp
            : key == FeatureFlagKeys.PluginsBuildYourself ? PluginsBuildYourself
            : false;
    }
}
