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

// Шаг B3: настоящий пакет «Погода» (tools/plugins/weather/, ветка feat/weather-plugin,
// манифест f6d7e7674) проходит серверный путь установки целиком: черновик → валидация →
// принятие → выдача прав → отзыв → bump версии → удаление. Манифесты — дословно из
// пакета, право storage добавлено (решение 22.10: пакет запрашивает storage). Файлы пакета
// не правятся (их держат фронтовые исполнители) — здесь они воспроизводятся в временном
// каталоге, как в существующих тестах плагинов.
//
// Пункт «фикстуры вынесены из каталога пакета» проверен по git-дереву пакета: в нём ровно
// три файла (ccs-plugin.json, .claude-plugin/plugin.json, panel/index.html) — фикстур нет.
// Панель — короткий placeholder: валидатор проверяет у panel/** только существование entry
// и потолок 256 КиБ (содержимое панели сервер не интерпретирует).
public class WeatherPackagePathTests : IDisposable
{
    private const string Owner = "owner-a";
    private const string Slug = "weather";
    // id не задан в манифесте пакета — его выводит сервер (PluginIdentity.Resolve),
    // поэтому ожидаемый id = выведенный.
    private static readonly string Id = PluginIdentity.DeriveId(Owner, Slug);

    // Манифест пакета дословно (f6d7e7674) + "storage" в requested.
    private const string CcsManifest = """
{
  "name": "weather",
  "version": "0.1.0",
  "bridge": { "v": 1 },
  "contributes": {
    "panels": [{ "id": "main", "slot": "sidebar", "entry": "panel/index.html", "title": "Погода" }]
  },
  "permissions": {
    "requested": ["net:api.open-meteo.com", "net:geocoding-api.open-meteo.com", "storage"]
  }
}
""";

    private const string NativeManifest = """
{
  "name": "weather",
  "version": "0.1.0",
  "description": "Погода: сейчас и на 7 дней"
}
""";

    private const string PanelHtml = """
<!doctype html>
<html lang="ru"><head><title>Погода</title></head><body>Погода: сейчас и на 7 дней</body></html>
""";

    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly PluginSecretsStore _secrets;
    private readonly PluginDraftService _drafts;
    private readonly PluginInstaller _installer;
    private readonly PluginUninstall _uninstall;
    private readonly StubCascade _cascade;

    public WeatherPackagePathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_weather_path_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new PluginStore(BuildConfig(_dir));
        _secrets = new PluginSecretsStore(BuildConfig(_dir));
        _drafts = new PluginDraftService(_store, new PluginPackageValidator());
        _installer = new PluginInstaller(_store, new PluginPackageValidator());
        _cascade = new StubCascade();
        _uninstall = new PluginUninstall(_store, _secrets, _cascade);
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

    // Пакет «Погода» в каталоге черновика: ровно те три файла, что в настоящем пакете.
    private void WriteWeatherPackage(string version = "0.1.0")
    {
        var root = _store.DraftDir(Owner, Slug);
        Directory.CreateDirectory(Path.Combine(root, "panel"));
        File.WriteAllText(Path.Combine(root, "ccs-plugin.json"),
            CcsManifest.Replace("\"version\": \"0.1.0\"", $"\"version\": \"{version}\""));
        Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
        File.WriteAllText(Path.Combine(root, ".claude-plugin", "plugin.json"),
            NativeManifest.Replace("\"version\": \"0.1.0\"", $"\"version\": \"{version}\""));
        File.WriteAllText(Path.Combine(root, "panel", "index.html"), PanelHtml);
    }

    private PluginDraftsController NewDraftsController()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Subsystems:plugins:Enabled"] = "true",
            })
            .Build();
        var gate = new StubFlagGate { PluginsMvp = true, PluginsBuildYourself = true };
        var ctl = new PluginDraftsController(config, gate, _store, _drafts, _installer, new PluginDraftDiff(_store));
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Principal(Owner) },
        };
        return ctl;
    }

    private PluginsController NewPluginsController()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Subsystems:plugins:Enabled"] = "true",
            })
            .Build();
        var gate = new StubFlagGate { PluginsMvp = true };
        var ctl = new PluginsController(config, gate, _store, _installer, _uninstall, _secrets);
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Principal(Owner) },
        };
        return ctl;
    }

    private static ClaimsPrincipal Principal(string userId)
    {
        var claims = new List<Claim> { new("sub", userId) };
        return new(new ClaimsIdentity(claims, "test"));
    }

    // Установка + выдача всех трёх прав — общая подготовка для отзыва/bump/удаления.
    private void InstallAndGrantAll()
    {
        WriteWeatherPackage();
        NewDraftsController().Accept(Slug).Should().BeOfType<OkObjectResult>();
        NewPluginsController().Grant(Id.Replace('/', '~'), new PluginGrantRequest
        {
            Permissions = ["net:api.open-meteo.com", "net:geocoding-api.open-meteo.com", "storage"],
        }).Should().BeOfType<OkObjectResult>();
    }

    // --- Пункт 1: валидация настоящего пакета с storage — 0 проблем ---
    // Мутация (name = "Погода" вместо slug "weather"): CheckNameEqualsSlug даёт
    // deny-name-not-equal-slug → тест красный (проверено прогоном).
    [Fact]
    public void ValidateFull_НастоящийПакетПогоды_БезПроблем()
    {
        WriteWeatherPackage();

        var result = _drafts.Validate(Owner, Slug);

        result.IsValid.Should().BeTrue(
            "файлы пакета в allow-list (манифест + panel/**), права известные, entry существует; " +
            "фикстур в каталоге пакета нет — они не вернулись: " +
            string.Join("; ", result.Problems.Select(p => $"{p.Code}: {p.Message}")));
        // Имя пакета — slug каталога, а не «Погода»: name «Погода» дал бы
        // name-slug-mismatch (M3), title панели «Погода» при этом допустим.
    }

    // --- Пункт 2: путь черновик → валидация → принятие → выдача: все три права в granted ---
    // Мутация (PluginInstaller не пишет requested из манифеста): record.Requested без
    // storage → первая assertion красная.
    [Fact]
    public void AcceptThenGrant_ВсеТриПраваДоходятДоGranted()
    {
        WriteWeatherPackage();

        // Валидация до принятия — тот же арбитр, что видит человек («Принять»).
        _drafts.Validate(Owner, Slug).IsValid.Should().BeTrue();

        var ok = (OkObjectResult)NewDraftsController().Accept(Slug);
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"requested\":[\"net:api.open-meteo.com\",\"net:geocoding-api.open-meteo.com\",\"storage\"]",
            "requested в ответе — полный, storage не отброшен");
        json.Should().Contain("\"newPermissions\":[\"net:api.open-meteo.com\",\"net:geocoding-api.open-meteo.com\",\"storage\"]",
            "все три права видны в диалоге согласия");

        // Первая установка: requested — все три, granted — пустой (выдаёт человек).
        var record = _store.LoadInstalled(Owner).Single(r => r.Id == Id);
        record.Requested.Should().Equal("net:api.open-meteo.com", "net:geocoding-api.open-meteo.com", "storage");
        record.Granted.Should().BeEmpty("первая установка — прав ещё не выдано");

        // Выдача всех трёх — тем же путём, что net: (без фильтров по типу права).
        var grantOk = (OkObjectResult)NewPluginsController().Grant(Id.Replace('/', '~'), new PluginGrantRequest
        {
            Permissions = ["net:api.open-meteo.com", "net:geocoding-api.open-meteo.com", "storage"],
        });
        var grantJson = JsonSerializer.Serialize(grantOk.Value);
        grantJson.Should().Contain("storage", "storage в granted ответа (панель увидит его в state.granted)");

        record = _store.LoadInstalled(Owner).Single(r => r.Id == Id);
        record.Granted.Should().Equal("net:api.open-meteo.com", "net:geocoding-api.open-meteo.com", "storage");
    }

    // --- Пункт 3: отзыв права геокодера не сносит storage и api (сценарий приёмки Веры) ---
    // Мутация (grant = union вместо полной замены): geocoding остался бы в granted →
    // «отозван» не подтвердился бы, тест красный.
    [Fact]
    public void Grant_ОтзывГеокодера_StorageИApiНаМесте()
    {
        InstallAndGrantAll();

        NewPluginsController().Grant(Id.Replace('/', '~'), new PluginGrantRequest
        {
            Permissions = ["net:api.open-meteo.com", "storage"],
        }).Should().BeOfType<OkObjectResult>();

        var record = _store.LoadInstalled(Owner).Single(r => r.Id == Id);
        record.Granted.Should().Equal(new[] { "net:api.open-meteo.com", "storage" },
            "отзыв net:geocoding-api.open-meteo.com не срезает storage и api: главный экран жив");
    }

    // --- Пункт 4: bump версии пакета — granted не теряет storage ---
    // Мутация (granted = ∩ только net:-правов при bump): storage выпадет из granted →
    // тест красный.
    [Fact]
    public void Accept_BumpВерсии_GrantedНеТеряетStorage()
    {
        InstallAndGrantAll();

        // Та же метка черновика, версия 0.2.0, тот же requested (storage не новый).
        WriteWeatherPackage("0.2.0");
        var ok = (OkObjectResult)NewDraftsController().Accept(Slug);
        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"newPermissions\":[]", "на bump нет новых прав: storage не считается новым");
        json.Should().Contain("\"inheritedPermissions\":[\"net:api.open-meteo.com\",\"net:geocoding-api.open-meteo.com\",\"storage\"]",
            "storage наследуется на новую версию наравне с net:");

        var record = _store.LoadInstalled(Owner).Single(r => r.Id == Id);
        record.Version.Should().Be("0.2.0");
        record.Granted.Should().Equal(new[] { "net:api.open-meteo.com", "net:geocoding-api.open-meteo.com", "storage" },
            "granted = старые ∩ новые: storage не потерян при bump");
    }

    // --- Пункт 5: удаление установки не оставляет хвостов ---
    // Мутация (PluginUninstall не удаляет запись): LoadInstalled непустой → тест красный.
    [Fact]
    public void Uninstall_Погода_НеОставляетХвостов()
    {
        InstallAndGrantAll();

        var pkgDir = _store.PackageDir(Owner, Id, "0.1.0");
        var draftDir = _store.DraftDir(Owner, Slug);
        Directory.Exists(pkgDir).Should().BeTrue("пакет на диске до удаления");

        _uninstall.Uninstall(Owner, Id).Should().BeTrue();

        _store.LoadInstalled(Owner).Should().BeEmpty("запись с granted из трёх прав удалена целиком");
        Directory.Exists(pkgDir).Should().BeFalse("каталог пакета удалён — хвостов на диске нет");
        Directory.Exists(draftDir).Should().BeFalse("черновик удалён — хвостов нет");
    }

    // Флаг-гейт и каскад — те же заглушки, что в других тестах контроллеров (nested-классы
    // не общие: каждая файл-область держит свою).
    private sealed class StubFlagGate : IFeatureFlagGate
    {
        public bool PluginsMvp;
        public bool PluginsBuildYourself;
        public bool IsEnabled(string userId, string key) =>
            key == FeatureFlagKeys.PluginsMvp ? PluginsMvp
            : key == FeatureFlagKeys.PluginsBuildYourself ? PluginsBuildYourself
            : false;
    }

    private sealed class StubCascade : IPluginInstallCascade
    {
        public List<(string Owner, string Id)> Calls { get; } = [];
        public void OnUninstalled(string ownerId, string pluginId) => Calls.Add((ownerId, pluginId));
    }
}
