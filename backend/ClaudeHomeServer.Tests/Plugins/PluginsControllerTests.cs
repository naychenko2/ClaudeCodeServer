using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Plugins.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Plugins;

// REST установки и согласия (ADR-031 §2.3, §4, шаг 1.4) — unit-тесты контроллера
// (паттерн SpendControllerTests: контроллер конструируется напрямую, ControllerContext
// несёт ClaimsPrincipal). Каждый сценарий «Поведения» — свой тест с ожидаемым кодом
// и телом ответа.
//
// Гейты: Subsystems:plugins:Enabled (SubsystemGate) и флаг plugins-mvp (IFeatureFlagGate)
// — оба → 403 с читаемой причиной. Чужой пакет → 404. Сервисный JWT (typ=svc) на
// grant/uninstall → 403. Право вне requested → 400. Обязательный секрет не задан → 400.
// Пакет изменился (sha256) → 409.
public class PluginsControllerTests : IDisposable
{
    private const string OwnerA = "owner-a";
    private const string OwnerB = "owner-b";

    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly PluginPackageValidator _validator;
    private readonly StubCascade _cascade;
    private readonly PluginUninstall _uninstall;

    public PluginsControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_plugins_ctl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new PluginStore(BuildConfig(_dir));
        _validator = new PluginPackageValidator();
        _cascade = new StubCascade();
        _uninstall = new PluginUninstall(_store, _cascade);
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
        var ctl = new PluginsController(config, gate, _store, _validator, _uninstall);
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

    // Пакет на диске: манифест ccs-plugin.json (id/version/permissions) + один навык
    // (skills/a/SKILL.md — в allow-list валидатора). Возвращает путь к папке (source).
    private string CreatePackage(string id, string version, List<string> requested,
        List<(string Name, bool Required)>? secrets = null)
    {
        var root = Path.Combine(_dir, "pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "skills", "a"));
        File.WriteAllText(Path.Combine(root, "skills", "a", "SKILL.md"), "# Skill A\n");

        var manifest = new
        {
            id,
            name = "Pkg",
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
        return root;
    }

    // Установить пакет (user-токен, флаг вкл) и вернуть запись.
    private PluginInstall Install(string ownerId, string id, string version, List<string> requested,
        List<(string Name, bool Required)>? secrets = null)
    {
        var source = CreatePackage(id, version, requested, secrets);
        var ctl = NewController(ownerId);
        var res = ctl.Install(new PluginInstallRequest { Source = source, RequestedPermissions = requested });
        var ok = (OkObjectResult)res;
        return (PluginInstall)ok.Value!;
    }

    // --- Гейты: 403 ---

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
    public void ПодсистемаВыключена_Отдаёт403СПричиной()
    {
        var ctl = NewController(OwnerA, subsystemEnabled: false);
        var res = ctl.List();

        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
        obj.Value!.ToString().Should().Contain("Подсистема плагинов выключена");
    }

    // --- Список и 404 (чужой пакет) ---

    [Fact]
    public void List_ВозвращаетУстановленныеВладельца()
    {
        Install(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        var list = (List<PluginInstall>)((OkObjectResult)ctl.List()).Value!;
        list.Should().HaveCount(1);
        list[0].Id.Should().Be("local-a/pkg");
    }

    [Fact]
    public void ЧужойПакет_Permissions_Отдаёт404()
    {
        // Установлен у owner-a; owner-b спрашивает → 404 (не подтверждаем существование).
        Install(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerB);

        ctl.Permissions("local-a/pkg").Should().BeOfType<NotFoundResult>();
    }

    // --- Grant: сервисный JWT → 403 ---

    [Fact]
    public void Grant_СервисныйJwt_Отдаёт403()
    {
        Install(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA, serviceToken: true);

        var res = ctl.Grant("local-a/pkg", new PluginGrantRequest { Permissions = ["notes:read"] });
        var obj = (ObjectResult)res;
        obj.StatusCode.Should().Be(403);
    }

    // --- Grant: право вне requested → 400 ---

    [Fact]
    public void Grant_ПравоВнеRequested_Отдаёт400СИменем()
    {
        // requested = notes:read; пробуем выдать tasks:read (не в requested) → 400.
        Install(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        var res = ctl.Grant("local-a/pkg", new PluginGrantRequest { Permissions = ["tasks:read"] });
        var bad = (BadRequestObjectResult)res;
        JsonSerializer.Serialize(bad.Value).Should().Contain("tasks:read");
    }

    // --- Grant: обязательный секрет не задан → 400 ---

    [Fact]
    public void Grant_ОбязательныйСекретНеЗадан_Отдаёт400СоСписком()
    {
        // Манифест требует LINEAR_TOKEN (required=true); grant без него → 400.
        Install(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"],
            secrets: [("LINEAR_TOKEN", true)]);
        var ctl = NewController(OwnerA);

        var res = ctl.Grant("local-a/pkg", new PluginGrantRequest
        {
            Permissions = ["notes:read"],
            Secrets = [],
        });
        var bad = (BadRequestObjectResult)res;
        JsonSerializer.Serialize(bad.Value).Should().Contain("LINEAR_TOKEN");
    }

    // --- Grant: happy path — granted = одобренные, секреты записаны ---

    [Fact]
    public void Grant_Успех_ЗаписываетGrantedИСекреты()
    {
        // requested: notes:read + net:* (net:* не авто-выдаётся). Grant одобряет notes:read.
        Install(OwnerA, "local-a/pkg", "1.0.0", ["notes:read", "net:api.linear.app"]);
        var ctl = NewController(OwnerA);

        ctl.Grant("local-a/pkg", new PluginGrantRequest
        {
            Permissions = ["notes:read"],
            Secrets = [new PluginGrantSecret { Name = "LINEAR_TOKEN", Value = "tok" }],
        }).Should().BeOfType<OkObjectResult>();

        var record = _store.LoadInstalled(OwnerA).Single(r => r.Id == "local-a/pkg");
        record.Granted.Should().Equal("notes:read");
        record.Secrets.Should().Contain(s => s.Name == "LINEAR_TOKEN" && s.Value == "tok");
        record.Enabled.Should().BeTrue();
    }

    // --- Install: пакет изменился (sha256) → 409 ---

    [Fact]
    public void Install_ПакетИзменилсяSha256_Отдаёт409()
    {
        var source = CreatePackage("local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        // Первая установка — ок.
        ctl.Install(new PluginInstallRequest { Source = source, RequestedPermissions = ["notes:read"] })
            .Should().BeOfType<OkObjectResult>();

        // Меняем файл пакета → новый sha256, тот же id → 409 «обновить».
        File.WriteAllText(Path.Combine(source, "skills", "a", "SKILL.md"), "# Skill A changed\n");
        var second = ctl.Install(new PluginInstallRequest { Source = source, RequestedPermissions = ["notes:read"] });
        var conflict = (ConflictObjectResult)second;
        conflict.StatusCode.Should().Be(409);
    }

    // --- Install: net:* не авто-выдаётся (granted = requested минус net:*) ---

    [Fact]
    public void Install_AutoGrant_НеВыдаётNet()
    {
        var record = Install(OwnerA, "local-a/pkg", "1.0.0", ["notes:read", "net:api.linear.app"]);

        // notes:read авто-выдан, net:* — нет (ждёт явного grant).
        record.Granted.Should().Equal("notes:read");
        record.Requested.Should().Equal("notes:read", "net:api.linear.app");
    }

    // --- Uninstall: каскад через контроллер ---

    [Fact]
    public void Uninstall_УдаляетЗапись_ИВозвращает200()
    {
        Install(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        ctl.Uninstall("local-a/pkg").Should().BeOfType<OkObjectResult>();
        _store.LoadInstalled(OwnerA).Should().BeEmpty();
    }

    [Fact]
    public void Uninstall_ЧужойПакет_Отдаёт404()
    {
        Install(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerB);

        ctl.Uninstall("local-a/pkg").Should().BeOfType<NotFoundResult>();
    }

    // --- Disable / Enable ---

    [Fact]
    public void DisableEnable_ПереключаютEnabled()
    {
        Install(OwnerA, "local-a/pkg", "1.0.0", ["notes:read"]);
        var ctl = NewController(OwnerA);

        ctl.Disable("local-a/pkg").Should().BeOfType<OkObjectResult>();
        _store.LoadInstalled(OwnerA).Single().Enabled.Should().BeFalse();

        ctl.Enable("local-a/pkg").Should().BeOfType<OkObjectResult>();
        _store.LoadInstalled(OwnerA).Single().Enabled.Should().BeTrue();
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
