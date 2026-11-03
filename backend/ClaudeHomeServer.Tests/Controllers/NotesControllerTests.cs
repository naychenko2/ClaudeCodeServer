using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Изоляция заметок по владельцу (per-owner по claim sub, как у задач). Базовый CRUD
// заметок не за фич-флагом — тестируем прямо через HTTP.
[Trait("Category", "Host")]
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

    // ─── S2c-2 · хеш содержимого и 409 при сохранении (К8) ───────────────────

    // То же правило хеша, что на сервере: SHA-256 по UTF-8-байтам текста.
    private static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private async Task<(string Etag, JsonElement Body)> GetNoteAsync(string id)
    {
        var resp = await _client.GetAsync(Url(id));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var etag = resp.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null;
        return (etag!, body);
    }

    private async Task<HttpResponseMessage> PutWithMatchAsync(string id, string? etag, string content)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, Url(id))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { content }), Encoding.UTF8, "application/json"),
        };
        if (etag is not null) req.Headers.TryAddWithoutValidation("If-Match", etag);
        return await _client.SendAsync(req);
    }

    [Fact]
    public async Task Get_ОтдаётETagХешСодержимого()
    {
        var id = await CreateNoteAsync("Хеш GET", "тело заметки");

        var (etag, body) = await GetNoteAsync(id);
        var content = body.GetProperty("content").GetString()!;
        etag.Should().NotBeNull("GET заметки обязан отдавать ETag (К8)");
        etag.Should().Be($"\"{Sha256Hex(content)}\"", "ETag — quoted SHA-256 от отдаваемого содержимого");
    }

    [Fact]
    public async Task Put_СовпадающийIfMatch_Записывает()
    {
        var id = await CreateNoteAsync("If-Match совпал", "исходное тело");
        var (etag, _) = await GetNoteAsync(id);

        var resp = await PutWithMatchAsync(id, etag, "записано по ETag");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var (_, after) = await GetNoteAsync(id);
        after.GetProperty("content").GetString().Should().Contain("записано по ETag");
    }

    [Fact]
    public async Task Put_НесовпадающийIfMatch_409NoteChanged_ЗаметкаНеМеняется()
    {
        var id = await CreateNoteAsync("If-Match расшёлся", "исходное тело");
        var (etag1, _) = await GetNoteAsync(id);

        // Первая запись по актуальному ETag проходит
        (await PutWithMatchAsync(id, etag1, "первая правка")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // Вторая — по устаревшему: 409 с актуальным состоянием, заметка не тронута
        var conflict = await PutWithMatchAsync(id, etag1, "вторая правка");
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("note_changed");

        var (etag2, after) = await GetNoteAsync(id);
        after.GetProperty("content").GetString().Should()
            .Contain("первая правка").And.NotContain("вторая правка");

        // Тело по К8: актуальный хеш и содержимое, время правки
        body.GetProperty("sha256").GetString().Should().Be(etag2.Trim('"'));
        body.GetProperty("content").GetString().Should().Contain("первая правка");
        body.GetProperty("changedAt").GetString().Should().NotBeNullOrEmpty();

        // author — null: сервер не знает автора победившей правки (его источник — буфер
        // коммиттера, S3-3); id вызывающего в author не попадает — он мог проиграть гонку
        // (доработка S2c-2, как в S2c-1 для файлов)
        body.GetProperty("author").ValueKind.Should().Be(JsonValueKind.Null,
            "author в теле 409 — null, пока нет серверного источника (S3-3)");
    }

    [Fact]
    public async Task Put_БезIfMatch_ПрежнееПоведение()
    {
        var id = await CreateNoteAsync("Без If-Match", "исходное тело");

        // Без заголовка проверка не делается — запись проходит, как до S2c-2
        (await PutWithMatchAsync(id, null, "записано без проверки")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var (_, after) = await GetNoteAsync(id);
        after.GetProperty("content").GetString().Should().Contain("записано без проверки");
    }

    [Fact]
    public async Task ХешМеняетсяПослеЗаписи()
    {
        var id = await CreateNoteAsync("Хеш после записи", "первое тело");
        var (etag1, _) = await GetNoteAsync(id);

        (await PutWithMatchAsync(id, etag1, "второе тело")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var (etag2, _) = await GetNoteAsync(id);
        etag2.Should().NotBe(etag1, "после записи содержимое другое — и хеш другой");
    }

    [Fact]
    public async Task Гонка_ДваСохраненияСОднимIfMatch_ПроходитРовноОдно()
    {
        var id = await CreateNoteAsync("Гонка", "исходное тело");
        var (etag, _) = await GetNoteAsync(id);

        // Оба сохранения стартуют одновременно (TCS-барьер, без Task.Delay): если между
        // проверкой хеша и записью есть окно, оба прочтут старый хеш и оба запишут.
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(HttpStatusCode Status, JsonElement Body)> Put(string marker)
        {
            await start.Task;
            var resp = await PutWithMatchAsync(id, etag, $"тело {marker}");
            return (resp.StatusCode, await resp.Content.ReadFromJsonAsync<JsonElement>());
        }
        var t1 = Task.Run(() => Put("A"));
        var t2 = Task.Run(() => Put("B"));
        start.SetResult(true);
        var (r1, r2) = (await t1, await t2);

        new[] { r1, r2 }.Count(r => r.Status == HttpStatusCode.OK).Should()
            .Be(1, "два сохранения с одним If-Match: ровно одно проходит проверку");
        new[] { r1, r2 }.Count(r => r.Status == HttpStatusCode.Conflict).Should().Be(1);

        var (winner, loser) = r1.Status == HttpStatusCode.OK ? (r1, r2) : (r2, r1);
        var winnerBody = winner.Body.GetProperty("content").GetString()!;
        var winnerMarker = winnerBody.Contains("тело A") ? "тело A" : "тело B";
        var loserMarker = winnerMarker == "тело A" ? "тело B" : "тело A";

        // Заметка содержит запись победителя
        var (etagAfter, current) = await GetNoteAsync(id);
        current.GetProperty("content").GetString().Should()
            .Contain(winnerMarker).And.NotContain(loserMarker);

        // Проигравший получил актуальное состояние
        loser.Body.GetProperty("code").GetString().Should().Be("note_changed");
        loser.Body.GetProperty("content").GetString().Should().Contain(winnerMarker);
        loser.Body.GetProperty("sha256").GetString().Should().Be(etagAfter.Trim('"'));
    }
}
