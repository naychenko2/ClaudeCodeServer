using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Голосовой режим чата (Session.VoiceMode): тумблер включается через PUT ОБОИХ путей —
// /api/chats/{id} (чаты вне проектов) и /api/projects/{pid}/sessions/{sid} (проектные).
// Sentinel-семантика как у notificationsMuted: поле не прислано (null) — значение не трогаем.
public class VoiceModeUpdateTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public VoiceModeUpdateTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private async Task<string> CreateProjectlessChatAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/chats", new { mode = "auto" });
        resp.EnsureSuccessStatusCode();
        var chat = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return chat.GetProperty("id").GetString()!;
    }

    private async Task<(string ProjectId, string SessionId)> CreateProjectSessionAsync()
    {
        var dir = Path.Combine(_factory.TempDir, "voicemode_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var projectResp = await _client.PostAsJsonAsync("/api/projects", new { name = "VoiceMode", rootPath = dir });
        projectResp.EnsureSuccessStatusCode();
        var project = JsonSerializer.Deserialize<JsonElement>(await projectResp.Content.ReadAsStringAsync());
        var projectId = project.GetProperty("id").GetString()!;

        var sessionResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        sessionResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var session = JsonSerializer.Deserialize<JsonElement>(await sessionResp.Content.ReadAsStringAsync());
        return (projectId, session.GetProperty("id").GetString()!);
    }

    private async Task<JsonElement> GetChatAsync(string id)
    {
        var resp = await _client.GetAsync($"/api/chats/{id}");
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Put_ЧатВнеПроекта_ВключаетИВыключаетРежим()
    {
        var id = await CreateProjectlessChatAsync();

        var on = await _client.PutAsJsonAsync($"/api/chats/{id}", new { voiceMode = true });
        on.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetChatAsync(id)).GetProperty("voiceMode").GetBoolean().Should().BeTrue();

        var off = await _client.PutAsJsonAsync($"/api/chats/{id}", new { voiceMode = false });
        off.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetChatAsync(id)).GetProperty("voiceMode").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Put_ЧатВнеПроекта_БезПоля_НеСбрасываетЗначение()
    {
        var id = await CreateProjectlessChatAsync();
        await _client.PutAsJsonAsync($"/api/chats/{id}", new { voiceMode = true });

        // PUT без voiceMode (например, переименование) не должен сбросить включённый режим
        var rename = await _client.PutAsJsonAsync($"/api/chats/{id}", new { name = "Переименован" });
        rename.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetChatAsync(id)).GetProperty("voiceMode").GetBoolean()
            .Should().BeTrue("отсутствие поля в PUT — «не менять», а не «выключить»");
    }

    [Fact]
    public async Task Put_ПроектнаяСессия_ВключаетИВыключаетРежим()
    {
        var (projectId, sessionId) = await CreateProjectSessionAsync();

        var on = await _client.PutAsJsonAsync($"/api/projects/{projectId}/sessions/{sessionId}", new { voiceMode = true });
        on.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await on.Content.ReadAsStringAsync());
        body.GetProperty("voiceMode").GetBoolean().Should().BeTrue();

        var off = await _client.PutAsJsonAsync($"/api/projects/{projectId}/sessions/{sessionId}", new { voiceMode = false });
        off.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonSerializer.Deserialize<JsonElement>(await off.Content.ReadAsStringAsync())
            .GetProperty("voiceMode").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Put_ПроектнаяСессия_БезПоля_НеСбрасываетЗначение()
    {
        var (projectId, sessionId) = await CreateProjectSessionAsync();
        await _client.PutAsJsonAsync($"/api/projects/{projectId}/sessions/{sessionId}", new { voiceMode = true });

        var rename = await _client.PutAsJsonAsync($"/api/projects/{projectId}/sessions/{sessionId}", new { name = "Переименована" });
        rename.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonSerializer.Deserialize<JsonElement>(await rename.Content.ReadAsStringAsync())
            .GetProperty("voiceMode").GetBoolean()
            .Should().BeTrue("отсутствие поля в PUT — «не менять», а не «выключить»");
    }

    // Стиль озвучки (Session.VoiceStyle) принадлежит УСТРОЙСТВУ, поэтому приезжает и
    // отдельным запросом — у чата, где озвучка уже включена с другого устройства.
    // Условие «в теле есть voiceMode» такой запрос молча потеряло бы.
    [Fact]
    public async Task Put_ТолькоСтиль_СохраняетсяИНеГаситРежим()
    {
        var id = await CreateProjectlessChatAsync();
        await _client.PutAsJsonAsync($"/api/chats/{id}", new { voiceMode = true });

        var styleOnly = await _client.PutAsJsonAsync($"/api/chats/{id}", new { voiceStyle = "digest" });
        styleOnly.StatusCode.Should().Be(HttpStatusCode.OK);

        var chat = await GetChatAsync(id);
        chat.GetProperty("voiceStyle").GetString().Should().Be("digest");
        chat.GetProperty("voiceMode").GetBoolean()
            .Should().BeTrue("запрос без voiceMode не должен выключать озвучку");
    }

    [Fact]
    public async Task Put_ПроектнаяСессия_ТолькоСтиль_Сохраняется()
    {
        var (projectId, sessionId) = await CreateProjectSessionAsync();

        var resp = await _client.PutAsJsonAsync($"/api/projects/{projectId}/sessions/{sessionId}", new { voiceStyle = "digest" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("voiceStyle").GetString().Should().Be("digest");
    }

    [Fact]
    public async Task Put_НеизвестныйСтиль_ОтбиваетсяОшибкой()
    {
        var id = await CreateProjectlessChatAsync();

        var resp = await _client.PutAsJsonAsync($"/api/chats/{id}", new { voiceStyle = "shout" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var chat = await GetChatAsync(id);
        (chat.TryGetProperty("voiceStyle", out var style) && style.ValueKind == JsonValueKind.String)
            .Should().BeFalse("мусор из API не должен доезжать до стора");
    }

    // Дефолт: поле пустое — стиль talk, то есть прежнее поведение старых чатов
    [Fact]
    public async Task НовыйЧат_БезСтиля_ЭтоРазговор()
    {
        var id = await CreateProjectlessChatAsync();
        var chat = await GetChatAsync(id);

        var style = chat.TryGetProperty("voiceStyle", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
        ClaudeHomeServer.Models.VoiceStyles.Normalize(style).Should().Be(ClaudeHomeServer.Models.VoiceStyles.Talk);
    }
}
