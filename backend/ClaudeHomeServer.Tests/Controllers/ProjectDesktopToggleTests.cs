using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Тумблер грани десктопа в проекте (ADR-008, «Два уровня, которые нельзя смешивать»).
///
/// Главное здесь — каскад: выключение обязано гасить сеансы рук проекта, иначе тумблер не
/// рубильник. Состав инструментов зафиксирован на момент запуска CLI, поэтому живой процесс
/// доработает ход с гранью в руках, и «выключено» значило бы «в следующий раз не выдадим».
/// </summary>
public class ProjectDesktopToggleTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public ProjectDesktopToggleTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "desktop_toggle_tests");
        Directory.CreateDirectory(_tempDir);
    }

    private async Task SetFlagAsync(bool enabled) =>
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.DesktopAgent}", new { enabled }))
            .EnsureSuccessStatusCode();

    private async Task<string> CreateProjectAsync()
    {
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await _client.PostAsJsonAsync("/api/projects",
            new { name = "ToggleProject", rootPath = dir });
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private async Task<HttpResponseMessage> ToggleAsync(string projectId, bool enabled) =>
        await _client.PutAsJsonAsync($"/api/projects/{projectId}/desktop-agent", new { enabled });

    private async Task<string> CreateDesktopChatAsync(string projectId)
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", desktop = true });
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Включение_ВидноВКарточкеПроекта()
    {
        await SetFlagAsync(true);
        var projectId = await CreateProjectAsync();

        (await ToggleAsync(projectId, true)).StatusCode.Should().Be(HttpStatusCode.OK);

        var card = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/projects/{projectId}")).Content.ReadAsStringAsync());
        card.GetProperty("desktopAgentEnabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task БезФлага_ВключитьНельзя()
    {
        await SetFlagAsync(false);
        var projectId = await CreateProjectAsync();

        (await ToggleAsync(projectId, true)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await SetFlagAsync(true);
    }

    [Fact]
    public async Task Выключение_ГаситСеансРукПроекта()
    {
        await SetFlagAsync(true);
        var projectId = await CreateProjectAsync();
        (await ToggleAsync(projectId, true)).EnsureSuccessStatusCode();
        var chatId = await CreateDesktopChatAsync(projectId);

        // Сеанс стартует только с устройства, поэтому в тесте зовём службу напрямую.
        // Владелец в боевом пути приходит из токена устройства — здесь берём его из
        // реестра чатов грани, чтобы не гадать про идентификатор тестового пользователя.
        var hands = _factory.Services.GetRequiredService<DesktopHandsSessionService>();
        var ownerId = _factory.Services.GetRequiredService<IDesktopChatDirectory>().Find(chatId)!.OwnerId;
        var started = await hands.StartAsync(ownerId, "dev-1", "home", chatId);
        started.Started.Should().BeTrue(because: started.Message);

        var response = await ToggleAsync(projectId, false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("handsStopped").GetInt32().Should().Be(1);
        hands.ForChat(chatId).Should().BeNull();
    }

    [Fact]
    public async Task Выключение_БезСеансов_ПроходитИСнимаетГрань()
    {
        await SetFlagAsync(true);
        var projectId = await CreateProjectAsync();
        (await ToggleAsync(projectId, true)).EnsureSuccessStatusCode();

        var response = await ToggleAsync(projectId, false);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("handsStopped").GetInt32().Should().Be(0);
        body.GetProperty("project").GetProperty("desktopAgentEnabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ЧужойПроект_404()
    {
        await SetFlagAsync(true);
        var projectId = await CreateProjectAsync();
        var stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await stranger.PutAsJsonAsync($"/api/projects/{projectId}/desktop-agent",
            new { enabled = false });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
