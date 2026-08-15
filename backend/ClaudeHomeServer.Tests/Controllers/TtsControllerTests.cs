using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// POST /api/tts — синтез речи голосового режима. Коды ответа — контракт фолбэка фронта:
// 503 not_configured (постоянно, фронт запоминает), 502 upstream (временно), 400 — плохой текст.
// В тестовом окружении Yandex:SpeechKit не задан — штатный путь «ключа нет».
public class TtsControllerTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    [Fact]
    public async Task БезКонфига_503_СПричинойNotConfigured()
    {
        var resp = await _client.PostAsJsonAsync("/api/tts", new { text = "Привет, как дела?" });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("reason").GetString().Should().Be("not_configured");
    }

    [Fact]
    public async Task ПустойТекст_400()
    {
        (await _client.PostAsJsonAsync("/api/tts", new { text = "" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsJsonAsync("/api/tts", new { text = "   " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task СлишкомДлинныйТекст_400()
    {
        var text = new string('а', TtsController.MaxTextLength + 1);

        (await _client.PostAsJsonAsync("/api/tts", new { text }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task БезАвторизации_401()
    {
        var anonymous = factory.CreateClient();

        (await anonymous.PostAsJsonAsync("/api/tts", new { text = "Привет" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
