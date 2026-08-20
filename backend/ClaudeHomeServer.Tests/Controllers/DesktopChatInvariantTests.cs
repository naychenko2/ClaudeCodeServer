using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Инвариант десктопного чата (ADR-008): у него собственный ClaudeSessionId — чат нельзя
/// создать из чужого resumeSessionId и нельзя продолжить обычным чатом из его транскрипта.
/// Второе направление важнее: в .jsonl десктопного чата лежат кадры рабочего стола, и
/// обычный чат вынес бы их за периметр грани (включая межпровайдерный фолбэк, из которого
/// десктопный чат выведен намеренно).
///
/// Флаг desktop-agent выставляется явно в каждом тесте: состояние per-user живёт в общей
/// фабрике класса, а порядок тестов не гарантирован.
/// </summary>
public class DesktopChatInvariantTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public DesktopChatInvariantTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "desktop_chat_tests");
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
            new { name = "DesktopProject", rootPath = dir });
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> CreateSessionAsync(string projectId, object body)
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", body);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ДесктопныйЧат_СоздаётсяСоСвоейСессией()
    {
        await SetFlagAsync(true);
        var projectId = await CreateProjectAsync();

        var session = await CreateSessionAsync(projectId, new { mode = "auto", desktop = true });

        session.GetProperty("desktopChat").GetBoolean().Should().BeTrue();
        // Транскрипта у нового чата ещё нет: ClaudeSessionId ставит CLI на первом ходу,
        // и он гарантированно свой — заимствовать чужой резюмом запрещено (тест ниже)
        session.GetProperty("claudeSessionId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ДесктопныйЧат_ИзResumeSessionId_400()
    {
        await SetFlagAsync(true);
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", desktop = true, resumeSessionId = "cli-session-1" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("собственная сессия");
    }

    [Fact]
    public async Task ДесктопныйЧат_БезФлага_400()
    {
        await SetFlagAsync(false);
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", desktop = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await SetFlagAsync(true); // не оставляем флаг снятым другим тестам класса
    }

    [Fact]
    public async Task ОбычнаяСессияПроекта_ИзТранскриптаДесктопного_400()
    {
        await SetFlagAsync(true);
        var projectId = await CreateProjectAsync();
        var csid = await DesktopChatWithTranscriptAsync(projectId);

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", resumeSessionId = csid });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("десктопного чата");
    }

    [Fact]
    public async Task ЧатВнеПроекта_ИзТранскриптаДесктопного_400()
    {
        await SetFlagAsync(true);
        var projectId = await CreateProjectAsync();
        var csid = await DesktopChatWithTranscriptAsync(projectId);

        var response = await _client.PostAsJsonAsync("/api/chats", new { mode = "auto", resumeSessionId = csid });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("десктопного чата");
    }

    [Fact]
    public async Task ДесктопныйЧат_ВнеПроекта_400()
    {
        await SetFlagAsync(true);

        var response = await _client.PostAsJsonAsync("/api/chats", new { mode = "auto", desktop = true });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("только в проекте");
    }

    [Fact]
    public async Task ОбычныйЧат_ИзЧужогоТранскрипта_НеЗапрещён()
    {
        await SetFlagAsync(true);
        var projectId = await CreateProjectAsync();

        // Транскрипт обычного чата резюмится как прежде — гейт ловит ровно десктопный
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", resumeSessionId = "cli-session-" + Guid.NewGuid().ToString("N")[..8] });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>
    /// Десктопный чат с транскриптом. ClaudeSessionId ставит CLI на первом ходу, а в тестах
    /// ход не идёт — проставляем поле напрямую в живой карточке чата (GetById отдаёт её же).
    /// </summary>
    private async Task<string> DesktopChatWithTranscriptAsync(string projectId)
    {
        var session = await CreateSessionAsync(projectId, new { mode = "auto", desktop = true });
        var sessionId = session.GetProperty("id").GetString()!;
        var csid = "desktop-csid-" + Guid.NewGuid().ToString("N")[..8];

        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        sessions.GetById(sessionId)!.ClaudeSessionId = csid;
        return csid;
    }
}
