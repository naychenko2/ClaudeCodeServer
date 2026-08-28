using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// «Стена» (/api/me/wall): персист набора чатов в User.WallChatIds через UserStore,
// молчаливая валидация PUT (дедуп → отброс чужих/мёртвых → потолок), ленивая
// фильтрация мёртвых id на GET, кандидаты для пикера.
public class MyWallControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MyWallControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    // Чат вне проекта — самый дешёвый способ получить живую сессию владельца
    private async Task<string> CreateChatAsync(HttpClient? client = null)
    {
        var resp = await (client ?? _client).PostAsJsonAsync("/api/chats", new { });
        resp.EnsureSuccessStatusCode();
        var chat = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return chat.GetProperty("id").GetString()!;
    }

    private async Task<List<string>> GetWallIdsAsync()
    {
        var resp = await _client.GetAsync("/api/me/wall");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return body.GetProperty("chats").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString()!).ToList();
    }

    [Fact]
    public async Task Get_БезТокена_401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/me/wall");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Put_И_Get_СохраняютПорядок()
    {
        var a = await CreateChatAsync();
        var b = await CreateChatAsync();

        var put = await _client.PutAsJsonAsync("/api/me/wall", new { chatIds = new[] { b, a } });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetWallIdsAsync()).Should().Equal(b, a);
    }

    [Fact]
    public async Task Put_ЧужойИНесуществующийId_МолчаОтбрасываются()
    {
        var mine = await CreateChatAsync();
        var stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var foreign = await CreateChatAsync(stranger);

        var put = await _client.PutAsJsonAsync("/api/me/wall",
            new { chatIds = new[] { mine, foreign, "no-such-chat" } });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetWallIdsAsync()).Should().Equal(mine);
    }

    [Fact]
    public async Task Put_Дубли_Схлопываются()
    {
        var a = await CreateChatAsync();
        var b = await CreateChatAsync();

        await _client.PutAsJsonAsync("/api/me/wall", new { chatIds = new[] { a, b, a, a } });

        (await GetWallIdsAsync()).Should().Equal(a, b);
    }

    [Fact]
    public async Task Put_БезТела_400()
    {
        var put = await _client.PutAsJsonAsync("/api/me/wall", new { chatIds = (string[]?)null });
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_УдалённыйЧат_ВыпадаетИзНабора()
    {
        var a = await CreateChatAsync();
        var b = await CreateChatAsync();
        await _client.PutAsJsonAsync("/api/me/wall", new { chatIds = new[] { a, b } });

        (await _client.DeleteAsync($"/api/chats/{a}")).EnsureSuccessStatusCode();

        // Мёртвый id фильтруется лениво на чтении, оставшийся порядок сохраняется
        (await GetWallIdsAsync()).Should().Equal(b);
    }

    [Fact]
    public async Task Put_ПерсистЧерезСтор_ВидноПослеПовторногоЧтения()
    {
        var a = await CreateChatAsync();
        await _client.PutAsJsonAsync("/api/me/wall", new { chatIds = new[] { a } });

        // Новый клиент (новый токен) — состав читается из UserStore, а не из памяти запроса
        var fresh = _factory.CreateAuthenticatedClient();
        var resp = await fresh.GetAsync("/api/me/wall");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("chats").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString()).Should().Contain(a);
    }

    [Fact]
    public async Task Put_ПотолокНабора_ЛишниеОбрезаютсяМолча()
    {
        // 26 чатов при потолке 24: PUT принимает всё, но сохраняет первые 24
        var ids = new List<string>();
        for (var i = 0; i < 26; i++) ids.Add(await CreateChatAsync());

        var put = await _client.PutAsJsonAsync("/api/me/wall", new { chatIds = ids });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var wall = await GetWallIdsAsync();
        wall.Should().HaveCount(24);
        wall.Should().Equal(ids.Take(24));
    }

    [Fact]
    public async Task Put_МёртвыйId_ВычищаетсяИзСтораНавсегда()
    {
        var a = await CreateChatAsync();
        var b = await CreateChatAsync();
        (await _client.DeleteAsync($"/api/chats/{a}")).EnsureSuccessStatusCode();

        // PUT с мёртвым id: ответ и сохранённый состав содержат только живой чат
        var put = await _client.PutAsJsonAsync("/api/me/wall", new { chatIds = new[] { a, b } });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await put.Content.ReadAsStringAsync());
        body.GetProperty("chats").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString()).Should().Equal(b);

        (await GetWallIdsAsync()).Should().Equal(b);
    }

    [Fact]
    public async Task Get_АрхивныйЧат_ВыпадаетИзНабора()
    {
        var a = await CreateChatAsync();
        var b = await CreateChatAsync();
        await _client.PutAsJsonAsync("/api/me/wall", new { chatIds = new[] { a, b } });

        (await _client.PutAsJsonAsync($"/api/chats/{a}", new { archived = true })).EnsureSuccessStatusCode();

        // Архивный чат для стены такой же мёртвый, как удалённый: колонки ему быть не должно
        (await GetWallIdsAsync()).Should().Equal(b);
    }

    [Fact]
    public async Task Put_АрхивныйId_ВычищаетсяИзСтора()
    {
        var a = await CreateChatAsync();
        var b = await CreateChatAsync();
        (await _client.PutAsJsonAsync($"/api/chats/{a}", new { archived = true })).EnsureSuccessStatusCode();

        var put = await _client.PutAsJsonAsync("/api/me/wall", new { chatIds = new[] { a, b } });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await put.Content.ReadAsStringAsync());
        body.GetProperty("chats").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString()).Should().Equal(b);

        (await GetWallIdsAsync()).Should().Equal(b);
    }

    [Fact]
    public async Task Candidates_АрхивныеЧатыНеПредлагаются()
    {
        var live = await CreateChatAsync();
        var archived = await CreateChatAsync();
        (await _client.PutAsJsonAsync($"/api/chats/{archived}", new { archived = true })).EnsureSuccessStatusCode();

        var resp = await _client.GetAsync("/api/me/wall/candidates");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var ids = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();

        ids.Should().Contain(live);
        ids.Should().NotContain(archived);
    }

    [Fact]
    public async Task Candidates_ОтдаётТолькоСвоиЧаты()
    {
        var mine = await CreateChatAsync();
        var stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var foreign = await CreateChatAsync(stranger);

        var resp = await _client.GetAsync("/api/me/wall/candidates");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var ids = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();

        ids.Should().Contain(mine);
        ids.Should().NotContain(foreign);
    }
}
