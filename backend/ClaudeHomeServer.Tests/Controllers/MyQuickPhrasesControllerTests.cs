using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Быстрые фразы (/api/me/quick-phrases): персист набора в User.QuickPhrases через
// UserStore и молчаливая валидация PUT (обрезка пробелов → отброс пустых → дедуп
// пары «группа + фраза» → потолки длины и количества). Группа — второй уровень попапа.
public class MyQuickPhrasesControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MyQuickPhrasesControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private static async Task<List<(string Text, string? Group)>> PhrasesOf(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return body.GetProperty("phrases").EnumerateArray()
            .Select(p => (
                p.GetProperty("text").GetString()!,
                p.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null))
            .ToList();
    }

    [Fact]
    public async Task Get_БезТокена_401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/me/quick-phrases");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Put_И_Get_СохраняютПорядокИГруппы()
    {
        var put = await _client.PutAsJsonAsync("/api/me/quick-phrases", new
        {
            phrases = new object[]
            {
                new { text = "продолжай" },
                new { text = "закоммить", group = "ГИТ" },
            },
        });
        (await PhrasesOf(put)).Should().Equal(("продолжай", null), ("закоммить", "ГИТ"));

        (await PhrasesOf(await _client.GetAsync("/api/me/quick-phrases")))
            .Should().Equal(("продолжай", null), ("закоммить", "ГИТ"));
    }

    [Fact]
    public async Task Put_ПустыеИДубли_МолчаОтбрасываются()
    {
        var put = await _client.PutAsJsonAsync("/api/me/quick-phrases", new
        {
            phrases = new object[]
            {
                new { text = "  закоммить  " },
                new { text = "" },
                new { text = "   " },
                new { text = "ЗАКОММИТЬ" },
                new { text = "закрой задачу" },
            },
        });

        (await PhrasesOf(put)).Should().Equal(("закоммить", null), ("закрой задачу", null));
    }

    [Fact]
    public async Task Put_ОдинаковыйТекстВРазныхГруппах_Остаётся()
    {
        var put = await _client.PutAsJsonAsync("/api/me/quick-phrases", new
        {
            phrases = new object[]
            {
                new { text = "статус", group = "ГИТ" },
                new { text = "статус", group = "Задачи" },
                new { text = "статус", group = " ГИТ " },   // тот же пункт после обрезки — дубль
            },
        });

        (await PhrasesOf(put)).Should().Equal(("статус", "ГИТ"), ("статус", "Задачи"));
    }

    [Fact]
    public async Task Put_ПустаяГруппа_СхлопываетсяВОтсутствие()
    {
        var put = await _client.PutAsJsonAsync("/api/me/quick-phrases", new
        {
            phrases = new object[] { new { text = "продолжай", group = "   " } },
        });

        (await PhrasesOf(put)).Should().Equal(("продолжай", (string?)null));
    }

    [Fact]
    public async Task Put_ДлинныйТекстИГруппуОбрезает_И_ДержитПотолокКоличества()
    {
        var many = Enumerable.Range(0, 40).Select(i => new { text = $"фраза {i}", group = "" }).ToList<object>();
        many.Insert(0, new { text = new string('я', 700), group = new string('г', 60) });

        var phrases = await PhrasesOf(await _client.PutAsJsonAsync("/api/me/quick-phrases", new { phrases = many }));

        phrases.Should().HaveCount(24);
        phrases[0].Text.Should().HaveLength(500);
        phrases[0].Group.Should().HaveLength(40);
    }

    [Fact]
    public async Task Put_ПустойНабор_ОчищаетСписок()
    {
        await _client.PutAsJsonAsync("/api/me/quick-phrases", new { phrases = new object[] { new { text = "продолжай" } } });

        (await PhrasesOf(await _client.PutAsJsonAsync("/api/me/quick-phrases", new { phrases = Array.Empty<object>() })))
            .Should().BeEmpty();
        (await PhrasesOf(await _client.GetAsync("/api/me/quick-phrases"))).Should().BeEmpty();
    }

    [Fact]
    public async Task Put_БезТела_400()
    {
        var resp = await _client.PutAsJsonAsync("/api/me/quick-phrases", new { phrases = (object[]?)null });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // Наборы, заведённые до появления групп, лежат в users.json списком голых строк
    [Fact]
    public void СтарыйФормат_ЧитаетсяКакФразаБезГруппы()
    {
        var list = JsonSerializer.Deserialize<List<QuickPhrase>>("""["продолжай", {"text":"закоммить","group":"ГИТ"}]""")!;

        list.Should().Equal(new QuickPhrase("продолжай"), new QuickPhrase("закоммить", "ГИТ"));
    }

    // Фраза без группы пишется без поля group — иначе старые записи после перезаписи
    // отличались бы от новых формой, а не содержанием
    [Fact]
    public void ФразаБезГруппы_ПишетсяБезПоляGroup()
    {
        // Латиница, чтобы не спорить с экранированием кириллицы дефолтным энкодером
        JsonSerializer.Serialize(new QuickPhrase("go on")).Should().Be("""{"text":"go on"}""");
        JsonSerializer.Serialize(new QuickPhrase("commit", "GIT")).Should().Be("""{"text":"commit","group":"GIT"}""");
    }
}
