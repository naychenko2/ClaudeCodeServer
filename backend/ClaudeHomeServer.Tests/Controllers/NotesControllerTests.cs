using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Изоляция заметок по владельцу (per-owner по claim sub, как у задач). Базовый CRUD
// заметок не за фич-флагом — тестируем прямо через HTTP.
public class NotesControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;     // владелец
    private readonly HttpClient _stranger;   // второй юзер

    public NotesControllerTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    private static string Url(string id) => $"/api/notes/{Uri.EscapeDataString(id)}";

    private async Task<string> CreateNoteAsync(string title, string content = "тело")
    {
        var resp = await _client.PostAsJsonAsync("/api/notes", new { title, content, source = "personal" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    // ─── CRUD владельца ──────────────────────────────────────────────────────

    [Fact]
    public async Task Create_БезЗаголовка_400()
    {
        (await _client.PostAsJsonAsync("/api/notes", new { title = "  ", source = "personal" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CRUD_СвояЗаметка_Работает()
    {
        var id = await CreateNoteAsync("Моя заметка", "исходное тело");

        (await _client.GetAsync(Url(id))).StatusCode.Should().Be(HttpStatusCode.OK);

        var upd = await _client.PutAsJsonAsync(Url(id), new { content = "новое тело" });
        upd.StatusCode.Should().Be(HttpStatusCode.OK);
        (await upd.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("content").GetString()
            .Should().Contain("новое тело");

        (await _client.DeleteAsync(Url(id))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.GetAsync(Url(id))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Изоляция по владельцу ───────────────────────────────────────────────

    [Fact]
    public async Task ЧужаяЗаметка_НедоступнаВторомуЮзеру()
    {
        var id = await CreateNoteAsync("Секретная заметка");

        (await _stranger.GetAsync(Url(id))).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await _stranger.PutAsJsonAsync(Url(id), new { content = "взлом" })).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await _stranger.DeleteAsync(Url(id))).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_НеСодержитЧужихЗаметок()
    {
        await CreateNoteAsync("Только моя заметка");

        var resp = await _stranger.GetAsync("/api/notes");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await resp.Content.ReadFromJsonAsync<JsonElement>();
        list.EnumerateArray().Should()
            .NotContain(n => n.GetProperty("title").GetString() == "Только моя заметка");
    }

    [Fact]
    public async Task ЗаметкаВладельца_ПослеСозданияЕстьВСписке()
    {
        await CreateNoteAsync("Заметка в списке");

        var list = await (await _client.GetAsync("/api/notes")).Content.ReadFromJsonAsync<JsonElement>();
        list.EnumerateArray().Should()
            .Contain(n => n.GetProperty("title").GetString() == "Заметка в списке");
    }
}
