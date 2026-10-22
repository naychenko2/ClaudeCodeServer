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
    // panelTitle (D2): добавить панель с title в contributes — для теста человекочитаемого
    // имени пакета; файл panel/index.html создаётся (валидатор требует существование entry).
    private string CreateDraftPackage(string ownerId, string id, string version, List<string> requested,
        List<(string Name, bool Required)>? secrets = null, string? panelTitle = null)
    {
        var slug = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        var root = _store.DraftDir(ownerId, slug);
        Directory.CreateDirectory(Path.Combine(root, "skills", "a"));
        File.WriteAllText(Path.Combine(root, "skills", "a", "SKILL.md"), "# Skill A\n");

        if (panelTitle is not null)
        {
            Directory.CreateDirectory(Path.Combine(root, "panel"));
            File.WriteAllText(Path.Combine(root, "panel", "index.html"), "<html><body>" + panelTitle + "</body></html>");
        }

        var manifest = new
        {
            id,
            name = slug,
            version,
            contributes = new
            {
                skills = new[] { "skills/a" },
                panels = panelTitle is null
                    ? null
                    : new[] { new { id = "main", slot = "sidebar", entry = "panel/index.html", title = panelTitle } },
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

    // Черновик БЕЗ id в манифесте (QA-фикс N): id выводит сервер (local-{ownerShort}/{slug}).
    // Тело то же, что у CreateDraftPackage, но поле id в ccs-plugin.json отсутствует.
    private void CreateDraftWithoutId(string ownerId, string slug, string version, List<string> requested)
    {
        var root = _store.DraftDir(ownerId, slug);
        Directory.CreateDirectory(Path.Combine(root, "skills", "a"));
        File.WriteAllText(Path.Combine(root, "skills", "a", "SKILL.md"), "# Skill A\n");

        var manifest = new
        {
            name = slug,
            version,
            contributes = new { skills = new[] { "skills/a" } },
            permissions = new { requested },
        };
        File.WriteAllText(Path.Combine(root, "ccs-plugin.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
        File.WriteAllText(Path.Combine(root, ".claude-plugin", "plugin.json"),
            JsonSerializer.Serialize(new { name = slug, version }, new JsonSerializerOptions { WriteIndented = true }));
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
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA, serviceToken: true);
        var res = ctl.Preview("pkg");
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Accept_СервисныйJwt_Отдаёт403()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA, serviceToken: true);
        var res = ctl.Accept("pkg");
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
    }

    // --- Чужой владелец → 404 (черновик другого владельца не в _drafts текущего) ---

    [Fact]
    public void Preview_ЧужойВладелец_Отдаёт404()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerB);
        var res = ctl.Preview("pkg");
        res.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void Accept_ЧужойВладелец_Отдаёт404()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerB);
        var res = ctl.Accept("pkg");
        res.Should().BeOfType<NotFoundObjectResult>();
    }

    // --- List: поля сводки (slug, id, version, время, число проблем, «такая же версия») ---

    [Fact]
    public void List_СводкаЧерновиков_СодержитПоля()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.List();
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"Slug\":\"pkg\"", "slug в списке");
        json.Should().Contain("\"Id\":\"local-952568751510/pkg\"", "id из манифеста");
        json.Should().Contain("\"Version\":\"1.0.0\"", "версия из манифеста");
        json.Should().Contain("\"Problems\":0", "валидный черновик — 0 проблем");
        json.Should().Contain("\"InstalledSameVersion\":false", "ещё не установлена такая версия");
        json.Should().Contain("\"InstalledVersion\":null", "плагин не установлен → null (QA-фикс R)");
    }

    [Fact]
    public void List_УстановленаТакаяВерсия_ПризнакTrue()
    {
        var source = CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();

        var ok = (OkObjectResult)ctl.List();
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"InstalledSameVersion\":true", "после accept — установлена такая же версия");
        json.Should().Contain("\"InstalledVersion\":\"1.0.0\"", "после accept — версия установленного плагина (QA-фикс R)");
        _ = source;
    }

    // --- Preview: тот же PluginDraftService.Preview, что у draft_preview ---

    [Fact]
    public void Preview_ВозвращаетСводку_КакDraftPreview()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.Preview("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"Name\":\"pkg\"", "имя пакета");
        json.Should().Contain("\"Version\":\"1.0.0\"", "версия");
        json.Should().Contain("notes:read", "запрошенные права");
        json.Should().Contain("\"IsValid\":true", "валидный черновик");
        json.Should().Contain("\"InstalledVersion\":null", "плагин не установлен → null (QA-фикс R)");
    }

    // --- Preview: установленный плагин (QA-фикс R) ---
    // После accept + grant (секрет задан) превью несёт InstalledVersion и Secrets[].Set:
    // карточка показывает «установлена версия N» и «секрет задан», а не «ещё не установлен»/«не задан».

    [Fact]
    public void Preview_УстановленПлагин_InstalledVersionИСекретЗадан()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read", "secret:TOKEN"],
            secrets: [("TOKEN", true)]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();
        // Выдача прав + значение секрета (как делает человек через grant в диалоге согласия).
        GrantDirectly(OwnerA, "local-952568751510/pkg", ["notes:read", "secret:TOKEN"],
            [("TOKEN", "top-secret-value")]);

        var ok = (OkObjectResult)ctl.Preview("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"InstalledVersion\":\"1.0.0\"", "версия установленного плагина с тем же id");
        json.Should().Contain("\"Set\":true", "секрет задан в записи установленного плагина");
        json.Should().NotContain("top-secret-value", "значение секрета (plugin-secrets.json) в ответ не уходит");
    }

    // --- Panel: 200 (сырой файл панели, QA-фикс H: сервер не разбирает HTML) ---
    // Мутация: вернуть BuildSrcdoc (разбор head/body, вставка CSP-meta) → тест красный.

    [Fact]
    public void Panel_СыройФайл_БайтВБайт()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        const string html = "<html><head><title>T</title></head><body><h1>Panel</h1></body></html>";
        AddPanelToDraft(OwnerA, "pkg", html);
        var ctl = NewController(OwnerA);

        var res = ctl.Panel("pkg");
        var fileRes = (FileContentResult)res;
        fileRes.FileContents.Should().BeEquivalentTo(
            System.Text.Encoding.UTF8.GetBytes(html),
            "ответ байт-в-байт равен файлу на диске (сервер не разобрал и не вставил ничего)");
    }

    // --- Panel: полный HTML-документ со своим <head> — сервер не трогает (QA-фикс H) ---
    // До фикса: BuildSrcdoc извлекал head/body и вставлял в хост. Теперь: сырой файл.

    [Fact]
    public void Panel_ПолныйДокумент_СыройФайлНеТрогнут()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        const string panel =
            "<!DOCTYPE html>\n<html>\n<head>\n" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'unsafe-inline'\">\n" +
            "<title>Panel Title</title>\n" +
            "</head>\n<body>\n<h1>Panel</h1>\n</body>\n</html>";
        AddPanelToDraft(OwnerA, "pkg", panel);
        var ctl = NewController(OwnerA);

        var res = ctl.Panel("pkg");
        var fileRes = (FileContentResult)res;
        // Ответ = сырой файл: <head> панели на месте, <title> на месте, CSP-meta панели не вырезана.
        fileRes.FileContents.Should().BeEquivalentTo(
            System.Text.Encoding.UTF8.GetBytes(panel),
            "полный HTML-документ панели возвращается как есть (head/body не извлечены)");
    }

    // --- Panel: заголовки CSP + nosniff (QA-фикс H, пункт D) ---
    // Мутация: вернуть PanelCsp (старый CSP) → тест заголовка красный.

    [Fact]
    public void Panel_Заголовки_CspSandboxINosniff()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        AddPanelToDraft(OwnerA, "pkg", "<html></html>");
        var ctl = NewController(OwnerA);

        ctl.Panel("pkg").Should().BeOfType<FileContentResult>();

        var csp = ctl.Response.Headers["Content-Security-Policy"].ToString();
        csp.Should().Be("sandbox; default-src 'none'",
            "CSP сырого ответа: sandbox; default-src 'none'");

        var nosniff = ctl.Response.Headers["X-Content-Type-Options"].ToString();
        nosniff.Should().Be("nosniff", "X-Content-Type-Options: nosniff");
    }

    // --- Panel: 403 (сервисный JWT) ---

    [Fact]
    public void Panel_СервисныйJwt_Отдаёт403()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        AddPanelToDraft(OwnerA, "pkg", "<html></html>");
        var ctl = NewController(OwnerA, serviceToken: true);
        var obj = (ObjectResult)ctl.Panel("pkg");
        obj.StatusCode.Should().Be(403);
    }

    // --- Panel: 404 (чужой владелец) ---

    [Fact]
    public void Panel_ЧужойВладелец_Отдаёт404()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        AddPanelToDraft(OwnerA, "pkg", "<html></html>");
        var ctl = NewController(OwnerB);
        ctl.Panel("pkg").Should().BeOfType<NotFoundObjectResult>();
    }

    // --- Panel: 404 (нет панели) ---

    [Fact]
    public void Panel_БезПанели_Отдаёт404()
    {
        // Черновик есть, но panel/index.html нет.
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Panel("pkg").Should().BeOfType<NotFoundObjectResult>();
    }

    // --- Panel: 413 (файл > 256 КиБ, QA-фикс H: потолок MaxPanelFileBytes) ---
    // Мутация: убрать проверку MaxPanelFileBytes → этот тест падает.

    [Fact]
    public void Panel_ФайлБольшой256КиБ_Отдаёт413()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var root = _store.DraftDir(OwnerA, "pkg");
        Directory.CreateDirectory(Path.Combine(root, "panel"));
        File.WriteAllBytes(Path.Combine(root, "panel", "index.html"),
            new byte[PluginPackageValidator.MaxPanelFileBytes + 1]);
        var ctl = NewController(OwnerA);
        var obj = (ObjectResult)ctl.Panel("pkg");
        obj.StatusCode.Should().Be(413);
    }

    // --- Panel: ровно 256 КиБ → 200 (потолок не превышен) ---

    [Fact]
    public void Panel_ФайлРовно256КиБ_Отдаёт200()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var root = _store.DraftDir(OwnerA, "pkg");
        Directory.CreateDirectory(Path.Combine(root, "panel"));
        File.WriteAllBytes(Path.Combine(root, "panel", "index.html"),
            new byte[PluginPackageValidator.MaxPanelFileBytes]);
        var ctl = NewController(OwnerA);
        ctl.Panel("pkg").Should().BeOfType<FileContentResult>("ровно потолок → 200");
    }

    // --- Panel: «../» в slug отбит (slug не по грамматике → 404, не 500 от SafePath.Join) ---

    [Fact]
    public void Panel_ТочкаТочкаВSlug_Отбита()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
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
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
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
                id = "local-952568751510/badpkg",
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

    // --- Accept: человекочитаемое имя пакета (D2, находка 4c903d8e) ---
    // Имя — title первой панели манифеста черновика (вывод — PluginDisplayName.Of, единственная
    // точка на бэке); панелей нет → null, фронт остаётся на fallback humanizeId. Мутация:
    // убрать строку displayName в ответе — оба теста красные.

    [Fact]
    public void Accept_ПакетСTitleПанели_ОтдаётЧеловеческоеИмя()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/weather", "0.1.0", [], panelTitle: "Погода");
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.Accept("weather");
        // Сравниваем разобранное значение, не подстроку: JsonSerializer экранирует кириллицу
        // в \u-последовательности, и Contain по «Погода» не совпал бы с «По…».
        var payload = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(ok.Value));
        payload.GetProperty("displayName").GetString().Should().Be("Погода",
            "в диалоге согласия — title панели, а не humanizeId(id)");
    }

    [Fact]
    public void Accept_ПакетБезПанелей_ИмяNull()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.Accept("pkg");
        var payload = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(ok.Value));
        payload.GetProperty("displayName").ValueKind.Should().Be(JsonValueKind.Null,
            "панелей нет → null, а не пустая строка: фронт остаётся на fallback");
    }

    // --- List: сводка списка несёт то же имя (D2) ---

    [Fact]
    public void List_ЧерновикСПанелью_СводкаСодержитИмя()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/weather", "0.1.0", [], panelTitle: "Погода");
        CreateDraftPackage(OwnerA, "local-952568751510/plain", "1.0.0", []);

        var list = _drafts.List(OwnerA);
        list.Should().HaveCount(2);
        list.Single(s => s.Slug == "weather").DisplayName.Should().Be("Погода");
        list.Single(s => s.Slug == "plain").DisplayName.Should().BeNull("без панелей — null, не пустая строка");
    }

    // --- Accept: первая установка → granted = [], consentRequired = true, newPermissions = все Requested ---
    // Дефект 86dec98d: раньше consentRequired=false → фронт не открывал диалог согласия,
    // плагин оставался без выданных прав. Теперь: все запрошенные права «новые» для человека.

    [Fact]
    public void Accept_ПерваяУстановкаСПравами_ConsentRequired_NewPermissionsВсе_GrantedПусто()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read", "net:api.linear.app"]);
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.Accept("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"granted\":[", "granted = [] (accept ничего не выдаёт сам)");
        json.Should().Contain("\"consentRequired\":true", "первая установка с правами → требуется согласие");
        json.Should().Contain("notes:read", "notes:read в newPermissions");
        json.Should().Contain("net:api.linear.app", "net:api.linear.app в newPermissions");

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-952568751510/pkg");
        record.Granted.Should().BeEmpty("granted при первой установке пуст");
        record.Requested.Should().Equal("notes:read", "net:api.linear.app");
        record.Enabled.Should().BeTrue();
        record.Source.Should().Be("draft:pkg");
    }

    // --- Шаг B2: право storage в requested доходит до диалога согласия целиком ---
    // Мутация (убрать storage из requested в PluginInstaller): requested/newPermissions
    // без storage → тест красный.

    [Fact]
    public void Accept_ЗапросСStorage_ДоходитДоДиалогаЦеликом()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["net:api.linear.app", "storage"]);
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.Accept("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"requested\":[\"net:api.linear.app\",\"storage\"]",
            "requested в ответе — полный, storage не отброшен как неизвестное");
        json.Should().Contain("\"newPermissions\":[\"net:api.linear.app\",\"storage\"]",
            "storage в newPermissions — человек может его выдать");

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-952568751510/pkg");
        record.Requested.Should().Equal("net:api.linear.app", "storage");
    }

    // --- Accept: первая установка БЕЗ прав → consentRequired = false ---

    [Fact]
    public void Accept_ПерваяУстановкаБезПрав_ConsentRequiredFalse()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", []);
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.Accept("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"consentRequired\":false", "без запрошенных прав → согласия не требуется");
        json.Should().Contain("\"newPermissions\":[", "newPermissions пуст");

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-952568751510/pkg");
        record.Granted.Should().BeEmpty();
        record.Requested.Should().BeEmpty();
    }

    // --- Accept: обновление БЕЗ новых прав, НО granted непусто → consentRequired = true (MAJOR-1) ---
    // Выданные права переносятся на новое содержимое → согласие требуется, даже если новых прав нет.
    // inheritedPermissions = старые выданные ∩ новые запрошенные = [notes:read].
    // Это и есть сценарий S5 из ревью Глеба (тот же slug, v2, те же права, granted непусто).

    [Fact]
    public void Accept_ОбновлениеБезНовыхПрав_GrantedНеПусто_ConsentRequiredTrue_InheritedЗаполнен()
    {
        // v1: requested = [notes:read]; accept → granted = [notes:read].
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();
        GrantDirectly(OwnerA, "local-952568751510/pkg", ["notes:read"]);

        // v2: та же id, версия 2.0.0, тот же requested = [notes:read] (нет новых прав).
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "2.0.0", ["notes:read"]);
        var ok = (OkObjectResult)ctl.Accept("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"consentRequired\":true",
            "обновление с непустым granted → согласие требуется (MAJOR-1), даже без новых прав");
        json.Should().Contain("\"newPermissions\":[", "newPermissions пуст");
        json.Should().Contain("\"inheritedPermissions\":[\"notes:read\"]",
            "inheritedPermissions = старые выданные ∩ новые запрошенные");

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-952568751510/pkg");
        record.Version.Should().Be("2.0.0");
        record.Granted.Should().Equal(new[] { "notes:read" }, "granted = старые ∩ новые");
    }

    // --- Accept: обновление, granted ПУСТО (человек не выдавал) → consentRequired = false ---
    // MAJOR-1: согласие нужно только если granted непусто ИЛИ есть новые права; здесь ни то, ни другое.

    [Fact]
    public void Accept_Обновление_GrantedПусто_БезНовыхПрав_ConsentRequiredFalse()
    {
        // v1: requested = [notes:read]; accept (granted = [] — человек НЕ выдал).
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();

        // v2: та же id, версия 2.0.0, тот же requested = [notes:read] (нет новых прав).
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "2.0.0", ["notes:read"]);
        var ok = (OkObjectResult)ctl.Accept("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"consentRequired\":false",
            "обновление: granted пуст и новых прав нет → согласия не нужно (MAJOR-1)");
        json.Should().Contain("\"newPermissions\":[", "newPermissions пуст");
        json.Should().Contain("\"inheritedPermissions\":[", "inheritedPermissions пуст (granted пуст)");

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-952568751510/pkg");
        record.Version.Should().Be("2.0.0");
        record.Granted.Should().BeEmpty("granted пуст (человек не выдавал)");
    }

    // --- Accept: обновление с новым правом → consentRequired, granted не расширяется ---

    [Fact]
    public void Accept_ОбновлениеСНовымПравом_ConsentRequired_НеРасширяетGranted()
    {
        // v1: requested = [notes:read]; accept (granted=[]) → человек выдал notes:read.
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();
        GrantDirectly(OwnerA, "local-952568751510/pkg", ["notes:read"]);

        // v2: та же id, версия 2.0.0, requested = [notes:read, net:api.linear.app] (новое право).
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "2.0.0", ["notes:read", "net:api.linear.app"]);
        var res = ctl.Accept("pkg");

        var ok = (OkObjectResult)res;
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"consentRequired\":true", "новое запрошенное право → требуется согласие");
        json.Should().Contain("net:api.linear.app", "новое право в списке newPermissions");

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-952568751510/pkg");
        record.Version.Should().Be("2.0.0");
        record.Granted.Should().Equal(new[] { "notes:read" }, "granted = старые ∩ новые: новое право не выдано");
    }

    // --- Шаг B2: bump версии — выданный storage сохраняется в granted и не считается новым ---
    // Правило granted = старые ∩ новые ничего не special-case'ит для net:, storage не должен
    // теряться «потому что не сетевой». Мутация (фильтр ∩ только net:-правов): тест красный.

    [Fact]
    public void Accept_ОбновлениеВерсии_StorageСохраняется_НеСчитаетсяНовым()
    {
        // v1: requested = [net:..., storage]; accept, человек выдал оба права.
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["net:api.linear.app", "storage"]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();
        GrantDirectly(OwnerA, "local-952568751510/pkg", ["net:api.linear.app", "storage"]);

        // v2: тот же id, версия 2.0.0, тот же requested (storage не новый).
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "2.0.0", ["net:api.linear.app", "storage"]);
        var ok = (OkObjectResult)ctl.Accept("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"newPermissions\":[", "storage не появился как новое право");
        json.Should().Contain("\"inheritedPermissions\":[\"net:api.linear.app\",\"storage\"]",
            "storage наследуется на новую версию наравне с net:");

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-952568751510/pkg");
        record.Version.Should().Be("2.0.0");
        record.Granted.Should().Equal(new[] { "net:api.linear.app", "storage" },
            "granted = старые ∩ новые: storage не потерян при bump");
    }

    // --- Accept: bump (новая версия) кладёт пакет НОВОЙ версии на диск (defect 8565ccff) ---
    //
    // До фикса: Install возвращал Conflict ДО Directory.Move, и контроллер писал запись
    // 2.0.0 в installed.json, а каталога packages/{id}/2.0.0/ на диске не было (навыки/панель
    // этой версии не доставлялись). Теперь: штатное обновление кладёт новую версию на диск,
    // старая версия остаётся, granted = старые ∩ новые.

    [Fact]
    public void Accept_Bump_УкладываетНовуюВерсиюНаДиск_СтараяОстаётся()
    {
        // v1: устанавливаем.
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();

        // v2: bump версии (содержимое навыка то же, версия в манифесте 2.0.0 → другой sha).
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "2.0.0", ["notes:read"]);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();

        // Пакет НОВОЙ версии на диске (навык + панель этой версии доставляются).
        var newDir = _store.PackageDir(OwnerA, "local-952568751510/pkg", "2.0.0");
        Directory.Exists(newDir).Should().BeTrue("каталог packages/{id}/2.0.0/ создан при bump");
        File.Exists(Path.Combine(newDir, "skills", "a", "SKILL.md")).Should().BeTrue(
            "навык новой версии на диске");

        // Старая версия остаётся на диске (пакеты неизменяемы, bump не затирает старую).
        var oldDir = _store.PackageDir(OwnerA, "local-952568751510/pkg", "1.0.0");
        Directory.Exists(oldDir).Should().BeTrue("каталог старой версии 1.0.0 остаётся");

        // Запись: версия 2.0.0, granted = старые ∩ новые.
        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-952568751510/pkg");
        record.Version.Should().Be("2.0.0");
    }

    // --- Accept: bump с ошибкой валидации → запись не остаётся, старая версия работает ---
    //
    // Если новая версия не проходит валидацию (например, битый манифест), установка
    // прерывается: запись 2.0.0 НЕ пишется, старая запись 1.0.0 (с granted) остаётся,
    // и пакет старой версии на диске работает.

    [Fact]
    public void Accept_Bump_ОшибкаВалидации_СтараяВерсияРаботает()
    {
        // v1: устанавливаем + выдаём право.
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();
        GrantDirectly(OwnerA, "local-952568751510/pkg", ["notes:read"]);

        // v2: bump версии, но битый манифест (unknown-permission) → валидация падает.
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "2.0.0", ["notes:read"]);
        var root = _store.DraftDir(OwnerA, "pkg");
        File.WriteAllText(Path.Combine(root, "ccs-plugin.json"),
            JsonSerializer.Serialize(new
            {
                id = "local-952568751510/pkg", name = "pkg", version = "2.0.0",
                contributes = new { skills = new[] { "skills/a" } },
                permissions = new { requested = new[] { "unknown:perm" } },
            }, new JsonSerializerOptions { WriteIndented = true }));

        var res = ctl.Accept("pkg");
        res.Should().BeOfType<BadRequestObjectResult>("ошибка валидации → 400, не 200");

        // Старая запись 1.0.0 (с granted) остаётся, битой записи 2.0.0 нет.
        var records = _store.LoadInstalled(OwnerA);
        var rec = records.Single(r => r.Id == "local-952568751510/pkg");
        rec.Version.Should().Be("1.0.0", "запись осталась старой версией");
        rec.Granted.Should().Equal(new[] { "notes:read" }, "granted старой версии не сброшен");
        records.Count(r => r.Version == "2.0.0").Should().Be(0, "битой записи 2.0.0 нет");

        // Пакет старой версии на диске работает.
        Directory.Exists(_store.PackageDir(OwnerA, "local-952568751510/pkg", "1.0.0")).Should().BeTrue();
    }

    // --- Accept: isUpdate/previousVersion (Глеб MAJOR 4) ---
    //
    // isUpdate — признак, что плагин уже был установлен (обновление/повторный accept), а не
    // первая установка; previousVersion — версия до этого accept (null — первая установка).
    // Фронт по isUpdate открывает диалог согласия в режиме modify (обновление) / install (первая).

    [Fact]
    public void Accept_ПерваяУстановка_IsUpdateFalse_PreviousVersionNull()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.Accept("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"isUpdate\":false", "первая установка — не обновление");
        json.Should().Contain("\"previousVersion\":null", "первая установка — предыдущей версии нет");
    }

    [Fact]
    public void Accept_Обновление_IsUpdateTrue_PreviousVersionСтарая()
    {
        // v1: устанавливаем.
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();

        // v2: bump версии → обновление.
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "2.0.0", ["notes:read"]);
        var ok = (OkObjectResult)ctl.Accept("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"isUpdate\":true", "обновление — isUpdate true");
        json.Should().Contain("\"previousVersion\":\"1.0.0\"", "previousVersion — старая версия");
    }

    // QA-фикс N (major 2, единая точка id): плагин БЕЗ id в манифесте — id выводит сервер
    // (local-{ownerShort}/{slug}). Установка, затем обновление черновиком: isUpdate=true,
    // previousVersion заполнен; diff — 200 (не 404: раньше пустой id в манифесте бросал
    // ArgumentException); сводка списка — с непустым (выведенным) id.
    // Мутация: вернуть сырой manifest.Id в контроллере/diff/сводке → id пуст → красный.
    [Fact]
    public void Accept_БезIdВМанифесте_ВыведенныйId_ОбновлениеDiffИСводка()
    {
        var expectedId = PluginIdentity.DeriveId(OwnerA, "pkg");

        // v1: черновик без id → accept Ok, id выведен сервером.
        CreateDraftWithoutId(OwnerA, "pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        var first = (OkObjectResult)ctl.Accept("pkg");
        var firstJson = JsonSerializer.Serialize(first.Value);
        firstJson.Should().Contain($"\"id\":\"{expectedId}\"", "id выведен сервером, не взят из (пустого) манифеста");

        // v2: bump версии → обновление.
        CreateDraftWithoutId(OwnerA, "pkg", "2.0.0", ["notes:read"]);
        var ok = (OkObjectResult)ctl.Accept("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"isUpdate\":true", "обновление — isUpdate true");
        json.Should().Contain("\"previousVersion\":\"1.0.0\"", "previousVersion — старая версия");

        // diff: 200 (не 404) — установленная версия того же (выведенного) id найдена.
        // (Сериализация здесь — PascalCase: JsonSerializer.Serialize без web-опций.)
        var diff = (OkObjectResult)ctl.Diff("pkg");
        var diffJson = JsonSerializer.Serialize(diff.Value);
        diffJson.Should().Contain("\"Status\":\"updated\"", "diff с установленной версией того же выведенного id");
        diffJson.Should().Contain($"\"Id\":\"{expectedId}\"", "id в diff — выведенный, не пустой");

        // Сводка списка: непустой (выведенный) id.
        var list = (OkObjectResult)ctl.List();
        var listJson = JsonSerializer.Serialize(list.Value);
        listJson.Should().Contain($"\"Id\":\"{expectedId}\"", "сводка черновика — с выведенным id");
    }

    [Fact]
    public void Accept_NoOp_ТотжеSha_IsUpdateTrue()
    {
        // Повторный accept той же версии (sha не изменился) → no-op; isUpdate true
        // (плагин уже установлен), previousVersion = текущая версия.
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();

        var ok = (OkObjectResult)ctl.Accept("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"isUpdate\":true", "no-op повторного accept — плагин уже установлен");
        json.Should().Contain("\"previousVersion\":\"1.0.0\"", "previousVersion = текущая версия");
    }

    // --- Accept: 409 при том же id+version и другом sha ---

    [Fact]
    public void Accept_ТотжеIdВерсия_ДругойSha_Отдаёт409()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
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
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read", "secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", true)]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();
        GrantDirectly(OwnerA, "local-952568751510/pkg", ["notes:read", "secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", "tok")]);

        // Повторный accept того же пакета (sha не изменился) → no-op.
        var res = ctl.Accept("pkg");
        var ok = (OkObjectResult)res;
        var json = JsonSerializer.Serialize(ok.Value);
        // MAJOR-1: granted непусто → consentRequired=true (accept ВСЕГДА требует согласия,
        // даже no-op: новые прав нет, но выданные переносятся).
        json.Should().Contain("\"consentRequired\":true", "no-op с непустым granted → согласие (MAJOR-1)");

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-952568751510/pkg");
        record.Granted.Should().Equal(new[] { "notes:read", "secret:LINEAR_TOKEN" }, "granted не сброшен no-op");
        record.Secrets.Should().Contain(s => s.Name == "LINEAR_TOKEN" && s.Set,
            "секреты не сброшены no-op");
        _secrets.Get(OwnerA, "local-952568751510/pkg", "LINEAR_TOKEN").Should().Be("tok", "значение секрета сохранено");
    }

    // --- Accept: заданные секреты прошлой версии сохраняются при обновлении ---

    [Fact]
    public void Accept_Обновление_СохраняетСекретыПрошлойВерсии()
    {
        // v1: secret LINEAR_TOKEN (required); accept → человек выдал secret:LINEAR_TOKEN + значение.
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", true)]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();
        GrantDirectly(OwnerA, "local-952568751510/pkg", ["secret:LINEAR_TOKEN"], secrets: [("LINEAR_TOKEN", "tok")]);

        // v2: та же id, версия 2.0.0, тот же secret (required), тот же requested.
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "2.0.0", ["secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", true)]);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-952568751510/pkg");
        record.Version.Should().Be("2.0.0");
        record.Secrets.Should().Contain(s => s.Name == "LINEAR_TOKEN" && s.Set,
            "заданный секрет прошлой версии сохранён при обновлении");
        _secrets.Get(OwnerA, "local-952568751510/pkg", "LINEAR_TOKEN").Should().Be("tok", "значение секрета пережило обновление");
    }

    // --- Accept: обновление сохраняет включённость (minor 3: выключенный не включается) ---
    // Мутация: accept-обновление сбрасывает Enabled=true (старое поведение) → тест красный.

    [Fact]
    public void Accept_Обновление_СохраняетВключённость()
    {
        // v1: secret + выдать + выключить плагин.
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", true)]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();
        GrantDirectly(OwnerA, "local-952568751510/pkg", ["secret:LINEAR_TOKEN"], secrets: [("LINEAR_TOKEN", "tok")]);
        _store.Update(OwnerA, records =>
        {
            var r = records.Single(x => x.Id == "local-952568751510/pkg");
            r.Enabled = false;
            return records;
        });

        // v2: bump версии, тот же secret в requested.
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "2.0.0", ["secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", true)]);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-952568751510/pkg");
        record.Version.Should().Be("2.0.0");
        record.Enabled.Should().BeFalse("выключенный плагин не включается accept-обновлением");
    }

    // --- Accept: чистка секретов по новому списку (minor 3: имя исчезло → не висит) ---

    [Fact]
    public void Accept_Обновление_СекретИсчезИзМанифеста_Чистится()
    {
        // v1: secret LINEAR_TOKEN (в requested) задан.
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["secret:LINEAR_TOKEN"],
            secrets: [("LINEAR_TOKEN", true)]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();
        GrantDirectly(OwnerA, "local-952568751510/pkg", ["secret:LINEAR_TOKEN"], secrets: [("LINEAR_TOKEN", "tok")]);

        // v2: bump версии, но secret:LINEAR_TOKEN НЕ в requested (имя исчезло из нового манифеста).
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "2.0.0", ["notes:read"]);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-952568751510/pkg");
        record.Secrets.Should().NotContain(s => s.Name == "LINEAR_TOKEN",
            "секрет, чьё имя исчезло из нового манифеста, не остаётся висеть");
    }

    // --- Accept: черновик после успеха НЕ удаляется ---

    [Fact]
    public void Accept_Успех_ЧерновикНеУдаляется()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();

        Directory.Exists(_store.DraftDir(OwnerA, "pkg")).Should().BeTrue(
            "черновик остаётся на месте после accept (агент может править дальше)");
    }

    // --- Accept: два параллельных accept одного slug не портят installed.json ---

    [Fact]
    public async Task Accept_ДваПараллельных_НеПортятInstalledJson()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        // Два параллельных accept одного slug: каждый делает store.Update (load→mutate→save
        // под одной блокировкой) — итог: ровно одна запись, не «половинная» и не дубль.
        var results = await Task.WhenAll(
            Task.Run(() => ctl.Accept("pkg")),
            Task.Run(() => ctl.Accept("pkg")));
        results[0].Should().BeOfType<OkObjectResult>();
        results[1].Should().BeOfType<OkObjectResult>();

        var records = _store.LoadInstalled(OwnerA);
        records.Count(r => r.Id == "local-952568751510/pkg").Should().Be(1, "ровно одна запись, installed.json не потерян");
        records.Single(r => r.Id == "local-952568751510/pkg").Sha256.Should().NotBeEmpty("запись целая (sha на месте)");
    }

    // --- Reject: перенос в _rejected, убирает из активных (шаг 4.3c) ---

    [Fact]
    public void Reject_Переносит_ИУбираетИзАктивных()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
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
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA, serviceToken: true);
        var obj = (ObjectResult)ctl.Reject("pkg");
        obj.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Reject_ЧужойВладелец_Отдаёт404()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerB);
        ctl.Reject("pkg").Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void Reject_ТочкаТочкаВSlug_Отбита()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        // «../pkg» — не по грамматике slug → Exists=false → 404 (не 500 от SafePath.Join).
        ctl.Reject("../pkg").Should().BeOfType<NotFoundObjectResult>();
    }

    // --- Restore: возврат в _drafts, конфликт slug → 409 (шаг 4.3c) ---

    [Fact]
    public void Restore_Возвращает_ВDrafts()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
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
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);
        ctl.Reject("pkg").Should().BeOfType<OkObjectResult>();
        // Новый черновик того же slug занял _drafts/pkg.
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);

        var conflict = (ConflictObjectResult)ctl.Restore("pkg");
        conflict.StatusCode.Should().Be(409);
    }

    [Fact]
    public void Restore_СервисныйJwt_Отдаёт403()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA, serviceToken: true);
        var obj = (ObjectResult)ctl.Restore("pkg");
        obj.StatusCode.Should().Be(403);
    }

    [Fact]
    public void Restore_ЧужойВладелец_Отдаёт404()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerB);
        ctl.Restore("pkg").Should().BeOfType<NotFoundObjectResult>();
    }

    // --- QA-фикс L4 (минор Глеба): installed.json пишется ОДИН раз за accept ---
    // До фикса: Install (PluginInstaller) писал record, а контроллер Accept переписывал ту же
    // запись вторым store.Update → 2 записи. Теперь: одна (Install). Счётчик записей на
    // фейковом сторе фиксирует ровно 1 (мутация: вернуть store.Update в контроллер → 2 → красный).

    [Fact]
    public void Accept_ЗаписьInstalledJson_ОдинРаз()
    {
        CreateDraftPackage(OwnerA, "local-952568751510/pkg", "1.0.0", ["notes:read"]);

        var counting = new CountingPluginStore(BuildConfig(_dir));
        var drafts = new PluginDraftService(counting, new PluginPackageValidator());
        var installer = new PluginInstaller(counting, new PluginPackageValidator());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Subsystems:plugins:Enabled"] = "true",
            })
            .Build();
        var gate = new StubFlagGate { PluginsMvp = true, PluginsBuildYourself = true };
        var ctl = new PluginDraftsController(config, gate, counting, drafts, installer, new PluginDraftDiff(counting));
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Principal(OwnerA, false) },
        };

        ctl.Accept("pkg").Should().BeOfType<OkObjectResult>();

        counting.UpdateCalls.Should().Be(1,
            "installed.json пишется один раз за accept (дубль store.Update в контроллере убран)");
    }

    // Счётчик записей installed.json: подменяет PluginStore.Update (единственная запись в
    // accept-потоке), считает вызовы, делегирует в base.
    private sealed class CountingPluginStore : PluginStore
    {
        public int UpdateCalls;
        public CountingPluginStore(IConfiguration config) : base(config) { }
        public override List<PluginInstall> Update(string ownerId, Func<List<PluginInstall>, List<PluginInstall>> mutate)
        {
            UpdateCalls++;
            return base.Update(ownerId, mutate);
        }
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
