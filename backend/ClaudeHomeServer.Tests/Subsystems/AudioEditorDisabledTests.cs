using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;

namespace ClaudeHomeServer.Tests.Subsystems;

// Модуль «Звук» выключен (ADR-021 §6) — двумя независимыми рубильниками: гейтом
// Subsystems:AudioEditor:Enabled=false (Register не вызывается) и записью
// DynamicModules[audioeditor].Enabled=false (dll не грузится). В обоих случаях модуль не активен,
// а его ручки уходят из маршрутизации: 404, а не 500. Настройка доезжает до хоста через
// UseSetting — механика и почему это не гонка разобраны в шапке NotesDisabledTests.
//
// Пока модуль — скелет без ручек, поэтому предмет проверки — активность подсистемы с контролем
// по включённому хосту; HTTP-кейс фиксирует контракт для будущих ручек audio-editor/*.
public class AudioEditorDisabledTests : IDisposable
{
    // Индекс записи audioeditor в DynamicModules appsettings.Testing.json
    private const int ModuleIndex = 5;

    private readonly GateDisabledFactory _gateDisabled = new();
    private readonly ModuleDisabledFactory _moduleDisabled = new();

    // Контрольный хост: без оверрайда модуль грузится — значит пустота ниже от рубильника,
    // а не от опечатки в ключе или пути к dll
    private readonly TestWebApplicationFactory _enabled = new();

    public void Dispose()
    {
        _gateDisabled.Dispose();
        _moduleDisabled.Dispose();
        _enabled.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class GateDisabledFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Subsystems:AudioEditor:Enabled", "false");
        }
    }

    private sealed class ModuleDisabledFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting($"DynamicModules:{ModuleIndex}:Key", "audioeditor");
            builder.UseSetting($"DynamicModules:{ModuleIndex}:Enabled", "false");
        }
    }

    private TestWebApplicationFactory Host(string which) => which switch
    {
        "gate" => _gateDisabled,
        "module" => _moduleDisabled,
        _ => throw new ArgumentOutOfRangeException(nameof(which)),
    };

    private static async Task<bool> IsActive(TestWebApplicationFactory factory)
    {
        var subsystems = await factory.CreateAuthenticatedClient()
            .GetFromJsonAsync<JsonElement>("/api/admin/subsystems");
        return subsystems.EnumerateArray().Any(s =>
            s.GetProperty("key").GetString() == "audioeditor" && s.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Контроль_включённый_модуль_активен()
    {
        (await IsActive(_enabled)).Should().BeTrue("dll лежит в modules/audio-editor и грузится ModuleLoader'ом");
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("module")]
    public async Task Выключенный_модуль_не_активен(string which)
    {
        (await IsActive(Host(which))).Should().BeFalse("рубильник обязан доехать до ModuleLoader");
    }

    [Theory]
    [InlineData("gate", "GET", "catalog")]
    [InlineData("gate", "POST", "jobs")]
    [InlineData("module", "GET", "catalog")]
    [InlineData("module", "POST", "jobs")]
    public async Task Ручки_модуля_при_выключенном_модуле_404_а_не_500(string which, string method, string tail)
    {
        var client = Host(which).CreateAuthenticatedClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/projects/any/audio-editor/{tail}");
        if (method is "POST") request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty("404 от маршрутизации, а не от гейта контроллера");
    }
}
