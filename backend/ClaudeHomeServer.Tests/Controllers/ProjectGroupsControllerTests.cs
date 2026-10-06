using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

public class ProjectGroupsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly HttpClient _stranger;

    public ProjectGroupsControllerTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    private async Task<JsonElement> CreateGroupAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/project-groups", new { name, color = "#D97757" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Create_ВозвращаетГруппуИОнаВСписке()
    {
        var group = await CreateGroupAsync("Группа А");
        var id = group.GetProperty("id").GetString();
        group.GetProperty("name").GetString().Should().Be("Группа А");

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/project-groups");
        list.EnumerateArray().Select(g => g.GetProperty("id").GetString()).Should().Contain(id);
    }

    [Fact]
    public async Task Create_ПустоеИмя_400()
    {
        var response = await _client.PostAsJsonAsync("/api/project-groups", new { name = " " });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_СоЗначкомИУставом_СохраняетОбаСразу()
    {
        var response = await _client.PostAsJsonAsync("/api/project-groups",
            new { name = "Со значком", color = "#D97757", icon = "house", charter = "Устав сферы" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("icon").GetString().Should().Be("house");
        body.GetProperty("charter").GetString().Should().Be("Устав сферы");
    }

    [Fact]
    public async Task Create_НегодныйЗначок_400()
    {
        var response = await _client.PostAsJsonAsync("/api/project-groups",
            new { name = "Х", icon = "нет-такой-иконки" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_СлишкомДлинныйУстав_400()
    {
        var response = await _client.PostAsJsonAsync("/api/project-groups",
            new { name = "Х", charter = new string('x', ClaudeHomeServer.Models.Sphere.CharterMaxLength + 1) });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_МеняетИмяИЦвет()
    {
        var group = await CreateGroupAsync("до");
        var id = group.GetProperty("id").GetString();

        var response = await _client.PutAsJsonAsync($"/api/project-groups/{id}", new
        {
            name = "после",
            color = "#000000"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("name").GetString().Should().Be("после");
        body.GetProperty("color").GetString().Should().Be("#000000");
    }

    [Fact]
    public async Task Update_ПринимаетЗначокИХартию_ОтвергаетНегодные()
    {
        var id = (await CreateGroupAsync("сфера")).GetProperty("id").GetString();

        var ok = await _client.PutAsJsonAsync($"/api/project-groups/{id}", new { icon = "piggy-bank", charter = "# Хартия" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("icon").GetString().Should().Be("piggy-bank");
        body.GetProperty("charter").GetString().Should().Be("# Хартия");

        (await _client.PutAsJsonAsync($"/api/project-groups/{id}", new { icon = "нет-такой-иконки" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PutAsJsonAsync($"/api/project-groups/{id}", new { charter = new string('x', 4001) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_ЧужаяГруппа_404()
    {
        var group = await CreateGroupAsync("моя");
        var id = group.GetProperty("id").GetString();

        var response = await _stranger.PutAsJsonAsync($"/api/project-groups/{id}", new { name = "чужой" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reorder_ПереставляетГруппыПоСписку()
    {
        var g1 = (await CreateGroupAsync("первая")).GetProperty("id").GetString()!;
        var g2 = (await CreateGroupAsync("вторая")).GetProperty("id").GetString()!;

        var response = await _client.PostAsJsonAsync("/api/project-groups/reorder", new
        {
            orderedIds = new[] { g2, g1 }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = list.EnumerateArray().Select(g => g.GetProperty("id").GetString()).ToList();
        ids.IndexOf(g2).Should().BeLessThan(ids.IndexOf(g1));
    }

    [Fact]
    public async Task Delete_УдаляетГруппу()
    {
        var id = (await CreateGroupAsync("на удаление")).GetProperty("id").GetString();

        var response = await _client.DeleteAsync($"/api/project-groups/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deletedMemory").GetInt32().Should().Be(0);

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/project-groups");
        list.EnumerateArray().Select(g => g.GetProperty("id").GetString()).Should().NotContain(id);
    }

    [Fact]
    public async Task Delete_ЧужаяГруппа_404()
    {
        var id = (await CreateGroupAsync("моя")).GetProperty("id").GetString();

        (await _stranger.DeleteAsync($"/api/project-groups/{id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAll_НеПоказываетЧужиеГруппы()
    {
        var id = (await CreateGroupAsync("приватная")).GetProperty("id").GetString();

        var list = await _stranger.GetFromJsonAsync<JsonElement>("/api/project-groups");

        list.EnumerateArray().Select(g => g.GetProperty("id").GetString()).Should().NotContain(id);
    }
}
