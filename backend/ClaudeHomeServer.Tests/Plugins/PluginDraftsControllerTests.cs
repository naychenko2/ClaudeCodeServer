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

    // --- Accept: первая установка → granted = [] ---

    [Fact]
    public void Accept_ПерваяУстановка_GrantedПусто()
    {
        CreateDraftPackage(OwnerA, "local-a/pkg", "1.0.0", ["notes:read", "net:api.linear.app"]);
        var ctl = NewController(OwnerA);

        var ok = (OkObjectResult)ctl.Accept("pkg");
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"granted\":[", "granted в ответе");
        json.Should().Contain("\"consentRequired\":false", "первая установка — согласия не требуется");

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-a/pkg");
        record.Granted.Should().BeEmpty("granted при первой установке пуст");
        record.Requested.Should().Equal("notes:read", "net:api.linear.app");
        record.Enabled.Should().BeTrue();
        record.Source.Should().Be("draft:pkg");
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
