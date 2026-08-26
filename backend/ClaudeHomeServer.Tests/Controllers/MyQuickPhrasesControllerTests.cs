using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Быстрые фразы (/api/me/quick-phrases): персист набора в User.QuickPhrases через
// UserStore и молчаливая валидация PUT (обрезка пробелов → отброс пустых → дедуп
// без учёта регистра → потолок длины и количества).
public class MyQuickPhrasesControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MyQuickPhrasesControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private static async Task<List<string>> PhrasesOf(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return body.GetProperty("phrases").EnumerateArray().Select(p => p.GetString()!).ToList();
    }

    [Fact]
    public async Task Get_БезТокена_401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/me/quick-phrases");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Put_И_Get_СохраняютПорядок()
    {
        var put = await _client.PutAsJsonAsync("/api/me/quick-phrases",
            new { phrases = new[] { "продолжай", "покажи дифф" } });
        (await PhrasesOf(put)).Should().Equal("продолжай", "покажи дифф");

        (await PhrasesOf(await _client.GetAsync("/api/me/quick-phrases")))
            .Should().Equal("продолжай", "покажи дифф");
    }

    [Fact]
    public async Task Put_ПустыеИДубли_МолчаОтбрасываются()
    {
        var put = await _client.PutAsJsonAsync("/api/me/quick-phrases",
            new { phrases = new[] { "  закоммить  ", "", "   ", "ЗАКОММИТЬ", "закрой задачу" } });

        (await PhrasesOf(put)).Should().Equal("закоммить", "закрой задачу");
    }

    [Fact]
    public async Task Put_ДлиннуюФразуОбрезает_И_ДержитПотолокКоличества()
    {
        var many = Enumerable.Range(0, 40).Select(i => $"фраза {i}").ToList();
        many.Insert(0, new string('я', 700));

        var phrases = await PhrasesOf(await _client.PutAsJsonAsync("/api/me/quick-phrases", new { phrases = many }));

        phrases.Should().HaveCount(24);
        phrases[0].Should().HaveLength(500);
    }

    [Fact]
    public async Task Put_ПустойНабор_ОчищаетСписок()
    {
        await _client.PutAsJsonAsync("/api/me/quick-phrases", new { phrases = new[] { "продолжай" } });

        (await PhrasesOf(await _client.PutAsJsonAsync("/api/me/quick-phrases", new { phrases = Array.Empty<string>() })))
            .Should().BeEmpty();
        (await PhrasesOf(await _client.GetAsync("/api/me/quick-phrases"))).Should().BeEmpty();
    }

    [Fact]
    public async Task Put_БезТела_400()
    {
        var resp = await _client.PutAsJsonAsync("/api/me/quick-phrases", new { phrases = (string[]?)null });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
