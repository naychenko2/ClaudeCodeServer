using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.Architecture;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;

namespace ClaudeHomeServer.Tests.DynamicModules;

// Architecture как динамический модуль (Viaduct 10.2): полный подъём хоста — Program.cs
// читает запись architecture в DynamicModules, ModuleLoader грузит dll из
// modules/architecture (копия CopyArchitectureModuleForTests), контроллер подключается
// AssemblyPart'ом. Main на компиляции типы вертикали не видит — отвечает только загруженная
// сборка. Образец — StubModuleEndpointTests.
public class ArchitectureModuleEndpointTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public ArchitectureModuleEndpointTests()
    {
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Модель_проекта__контроллер_загруженного_модуля_отвечает_200()
    {
        var dir = Path.Combine(_factory.TempDir, "arch-project");
        Directory.CreateDirectory(dir);
        var created = await _client.PostAsJsonAsync("/api/projects", new { name = "ArchProject", rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var response = await _client.GetAsync($"/api/projects/{projectId}/architecture/model");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "контроллер динамически загруженного модуля найден роутером, сервисы зарегистрированы");
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("exists").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Статус_подсистем__architecture_активна_по_факту_загрузки()
    {
        var response = await _client.GetAsync("/api/auth/me");
        response.EnsureSuccessStatusCode();

        // Именно поле subsystems: проверка по всему телу прошла бы вхолостую
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("subsystems").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("architecture",
                "ModuleLoader записал модуль в SubsystemStateStore — ключ уходит фронту в /api/auth/me");
        // Фич-флага у раздела нет (решение 2026-09-26): включение — только конфигом модуля
        body.GetProperty("featureFlags").TryGetProperty("architecture", out _)
            .Should().BeFalse("раздел гейтится загрузкой модуля, а не флагом владельца");
    }
    // Ветка /modules/viaduct на ПОЛНОМ хосте (а не воспроизведённым у себя циклом Program.cs):
    // вклад IStaticBranchContributor загруженного модуля реально встал в конвейер. В тестовом
    // хосте собранного фронта нет, SPA-фолбэка нет, и промах маршрутизации тоже дал бы 404 —
    // поэтому проверка различает ветку по телу: код NotInstalledCode пишет только она.
    [Fact]
    public async Task Ветка_viaduct__стоит_в_конвейере_и_честно_говорит_не_установлено()
    {
        var response = await _client.GetAsync(ViaductStaticHosting.RequestPath + "/");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("error").GetString().Should().Be(ViaductStaticHosting.NotInstalledCode,
            "ответила именно ветка Viaduct, а не общий 404 роутинга или SPA-фолбэк");
    }

    // ─── Отключаемость: Subsystems:architecture:Enabled=false ─────────────────────

    // Настройка едет UseSetting'ом — хост-конфигурацией в CreateBuilder(args), то есть успевает
    // к ModuleLoader.LoadAll (разбор механики — шапка NotesDisabledTests). Запись в
    // DynamicModules остаётся Enabled=true: выключает именно гейт подсистемы.
    private sealed class DisabledArchitectureFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Subsystems:architecture:Enabled", "false");
        }
    }

    [Fact]
    public async Task Гейт_выключен__модуль_не_загружен_REST_и_viaduct_недоступны()
    {
        using var disabled = new DisabledArchitectureFactory();
        var client = disabled.CreateAuthenticatedClient();

        // Гейт доехал, RestartRequired честный: в конфиге выключено и в процессе не активно
        var subsystems = await client.GetFromJsonAsync<JsonElement>("/api/admin/subsystems");
        var arch = subsystems.EnumerateArray().Single(s => s.GetProperty("key").GetString() == "architecture");
        arch.GetProperty("enabled").GetBoolean().Should().BeFalse();
        arch.GetProperty("active").GetBoolean().Should().BeFalse("ModuleLoader не звал Register");
        arch.GetProperty("restartRequired").GetBoolean().Should().BeFalse("конфиг и процесс согласны");

        var me = JsonSerializer.Deserialize<JsonElement>(await client.GetStringAsync("/api/auth/me"));
        me.GetProperty("subsystems").EnumerateArray().Select(e => e.GetString())
            .Should().NotContain("architecture");

        // Контроллера нет: на СУЩЕСТВУЮЩЕМ своём проекте включённый хост отвечает 200
        // (тест выше), здесь маршрут не сопоставлен — общий 404 без тела контроллера
        var dir = Path.Combine(disabled.TempDir, "arch-off");
        Directory.CreateDirectory(dir);
        var created = await client.PostAsJsonAsync("/api/projects", new { name = "ArchOff", rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        (await client.GetAsync($"/api/projects/{projectId}/architecture/model"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "ApplicationPart модуля не подключён");

        // Ветки /modules/viaduct нет: 404 без кода NotInstalledCode (его пишет только ветка)
        var viaduct = await client.GetAsync(ViaductStaticHosting.RequestPath + "/");
        viaduct.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await viaduct.Content.ReadAsStringAsync()).Should().NotContain(ViaductStaticHosting.NotInstalledCode);
    }
}
