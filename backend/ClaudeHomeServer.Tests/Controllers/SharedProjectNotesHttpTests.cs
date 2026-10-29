using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Контракт HTTP для заметок общего проекта (shared-projects, доработка S1-3b по ревью c5006a6d,
/// MAJOR-1). Отказ «источник не мой/не существует» — KeyNotFoundException (NotesService.cs:1287,
/// 1292), и на HTTP это один и тот же 404: «чужое» и «несуществующее» не различаем. До доработки
/// PUT/DELETE/Move/LinkMention/SetStatus не ловили этот отказ в NotesController и отдавали 500
/// через UnhandledExceptionHandler — регрессия против прежнего 403. Право чтения без права
/// записи (участник в S1) — 403; чтение — 200; путь владельца не меняется.
///
/// Проект и основная заметка — ОДНИ на весь класс: кэш модели NotesService ключуется по id
/// пользователя с TTL 2 с (NotesService.cs:51-52), и новый проект на каждый тест попадал бы
/// в несвежую модель участника — чтение вернуло бы пустой список по причине свежести, а не
/// по проверяемой. Роль участника меняется PUT-ом между тестами, состав — явный в каждом.
/// </summary>
public class SharedProjectNotesHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private const string NoteTitle = "Заметка проекта";
    private const string NoteContent = "Текст проектной заметки. Упоминание ЦелеваяТема.";

    private readonly TestWebApplicationFactory _factory;

    // xUnit создаёт новый экземпляр класса на каждый тест: общий на класс состав
    // (проект, заметка, участник) живёт в static-полях — экземплярные поля были бы
    // пусты для каждого теста, и каждый создавал бы свой проект с тем же именем (400).
    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static string? _projectId;
    private static string? _noteId;
    private static string? _memberId;

    public SharedProjectNotesHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private HttpClient Member() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private static async Task SetFlagAsync(HttpClient client, bool on) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = on }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    /// <summary>Проект владельца с основной заметкой в нём — создан ровно раз на класс.</summary>
    private async Task<string> SharedProjectIdAsync()
    {
        await InitLock.WaitAsync();
        try
        {
            if (_projectId is not null) return _projectId;

            var owner = Owner();
            await SetFlagAsync(owner, true);
            await SetFlagAsync(Member(), true);
            var dir = Path.Combine(_factory.TempDir, "s13b_notes");
            Directory.CreateDirectory(dir);

            var project = await owner.PostAsJsonAsync("/api/projects",
                new { name = "Заметки общего проекта", rootPath = dir });
            project.EnsureSuccessStatusCode();
            _projectId = IdOf(await project.Content.ReadAsStringAsync());

            var note = await owner.PostAsJsonAsync("/api/notes",
                new { title = NoteTitle, content = NoteContent, source = _projectId });
            note.EnsureSuccessStatusCode();
            _noteId = IdOf(await note.Content.ReadAsStringAsync());
            return _projectId;
        }
        finally { InitLock.Release(); }
    }

    /// <summary>Состав проекта перед тестом: участник в заданной роли (null — участника нет,
    /// второй пользователь посторонний).</summary>
    private async Task SetMemberAsync(string? role)
    {
        var projectId = await SharedProjectIdAsync();
        var owner = Owner();

        if (_memberId is null)
        {
            if (role is null) return;
            var added = await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role });
            added.EnsureSuccessStatusCode();
            _memberId = JsonSerializer.Deserialize<JsonElement>(await added.Content.ReadAsStringAsync())
                .GetProperty("members").EnumerateArray()
                .First(m => m.GetProperty("username").GetString() == TestWebApplicationFactory.SecondUsername)
                .GetProperty("userId").GetString()!;
            return;
        }

        if (role is null)
        {
            (await owner.DeleteAsync($"/api/projects/{projectId}/members/{_memberId}")).EnsureSuccessStatusCode();
            _memberId = null;
            return;
        }

        (await owner.PutAsJsonAsync($"/api/projects/{projectId}/members/{_memberId}", new { role }))
            .EnsureSuccessStatusCode();
    }

    /// <summary>Своя заметка проекта от владельца (для действий «как раньше» — чтобы
    /// основной заметки не касаться).</summary>
    private async Task<string> OwnNoteAsync(string title)
    {
        var projectId = await SharedProjectIdAsync();
        var note = await Owner().PostAsJsonAsync("/api/notes",
            new { title, content = NoteContent, source = projectId });
        note.EnsureSuccessStatusCode();
        return IdOf(await note.Content.ReadAsStringAsync());
    }

    /// <summary>Все действия записи над заметкой дают указанный код: PUT, DELETE, move,
    /// link-mention, status.</summary>
    private static async Task WritesAreAsync(HttpClient client, string noteId, HttpStatusCode code)
    {
        (await client.PutAsJsonAsync($"/api/notes/{noteId}", new { content = "правка" }))
            .StatusCode.Should().Be(code);
        (await client.DeleteAsync($"/api/notes/{noteId}")).StatusCode.Should().Be(code);
        (await client.PostAsJsonAsync($"/api/notes/{noteId}/move", new { folder = "Архив" }))
            .StatusCode.Should().Be(code);
        (await client.PostAsJsonAsync($"/api/notes/{noteId}/link-mention", new { targetTitle = "ЦелеваяТема" }))
            .StatusCode.Should().Be(code);
        (await client.PostAsJsonAsync($"/api/notes/{noteId}/status", new { status = "resolved" }))
            .StatusCode.Should().Be(code);
    }

    /// <summary>Заметка на диске не тронута отказами: ни правки, ни связывания.</summary>
    private async Task NoteIntactAsync(string noteId)
    {
        var got = await Owner().GetAsync($"/api/notes/{noteId}");
        got.EnsureSuccessStatusCode();
        var content = JsonSerializer.Deserialize<JsonElement>(await got.Content.ReadAsStringAsync())
            .GetProperty("content").GetString();
        content.Should().Be(NoteContent);
    }

    // ── отказ записи: 403 участнику, 404 невидимому ────────────────────────────────

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Участник_ПишетПроектнуюЗаметку_403_И_ЗаметкаНеМеняется(string role)
    {
        await SetMemberAsync(role);
        var noteId = _noteId!;

        await WritesAreAsync(Member(), noteId, HttpStatusCode.Forbidden);

        await NoteIntactAsync(noteId);
    }

    [Fact]
    public async Task Посторонний_ПишетПроектнуюЗаметку_404()
    {
        await SetMemberAsync(role: null);
        var noteId = _noteId!;

        await WritesAreAsync(Member(), noteId, HttpStatusCode.NotFound);

        await NoteIntactAsync(noteId);
    }

    [Fact]
    public async Task ФлагУчастникаВыключен_ПишетПроектнуюЗаметку_404()
    {
        await SetMemberAsync("editor");
        var noteId = _noteId!;
        await SetFlagAsync(Member(), false);
        try
        {
            await WritesAreAsync(Member(), noteId, HttpStatusCode.NotFound);
        }
        finally { await SetFlagAsync(Member(), true); }

        await NoteIntactAsync(noteId);
    }

    [Fact]
    public async Task РубильникИнстансаВыключен_УчастникПишетПроектнуюЗаметку_404()
    {
        await SetMemberAsync("editor");
        var noteId = _noteId!;
        var config = _factory.Services.GetRequiredService<IConfiguration>();
        config[SharedProjectsGate.SwitchKey] = "false";
        try
        {
            await WritesAreAsync(Member(), noteId, HttpStatusCode.NotFound);
        }
        finally { config[SharedProjectsGate.SwitchKey] = "true"; }

        await NoteIntactAsync(noteId);
    }

    // ── чтение участника: 200 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Участник_ЧитаетПроектнуюЗаметку_200(string role)
    {
        var projectId = await SharedProjectIdAsync();
        await SetMemberAsync(role);
        var member = Member();

        var list = await member.GetAsync($"/api/notes?source={projectId}");
        list.EnsureSuccessStatusCode();
        JsonSerializer.Deserialize<JsonElement>(await list.Content.ReadAsStringAsync())
            .EnumerateArray().Select(n => n.GetProperty("title").GetString())
            .Should().Contain(NoteTitle);

        var detail = await member.GetAsync($"/api/notes/{_noteId!}");
        detail.EnsureSuccessStatusCode();
        JsonSerializer.Deserialize<JsonElement>(await detail.Content.ReadAsStringAsync())
            .GetProperty("content").GetString().Should().Contain("Текст проектной заметки.");
    }

    // ── владелец: прежний путь не изменился ─────────────────────────────────────────

    [Fact]
    public async Task Владелец_ПишетПроектнуюЗаметку_КакРаньше()
    {
        await SetMemberAsync("editor");
        var owner = Owner();

        var editId = await OwnNoteAsync("Заметка для правки");
        var put = await owner.PutAsJsonAsync($"/api/notes/{editId}",
            new { content = "правка владельца. Упоминание ЦелеваяТема." });
        put.EnsureSuccessStatusCode();
        JsonSerializer.Deserialize<JsonElement>(await put.Content.ReadAsStringAsync())
            .GetProperty("content").GetString().Should().Contain("правка владельца");

        var linked = await owner.PostAsJsonAsync($"/api/notes/{editId}/link-mention",
            new { targetTitle = "ЦелеваяТема" });
        linked.EnsureSuccessStatusCode();
        JsonSerializer.Deserialize<JsonElement>(await linked.Content.ReadAsStringAsync())
            .GetProperty("content").GetString().Should().Contain("[[ЦелеваяТема]]");

        var moveId = await OwnNoteAsync("Заметка для переноса");
        var moved = await owner.PostAsJsonAsync($"/api/notes/{moveId}/move", new { folder = "Архив" });
        moved.EnsureSuccessStatusCode();
        var movedId = IdOf(await moved.Content.ReadAsStringAsync());
        movedId.Should().NotBe(moveId, "перенос меняет id: источник+путь в id");
        (await owner.GetAsync($"/api/notes/{movedId}")).EnsureSuccessStatusCode();

        // Удаление — своим путём: 204 без тела
        (await owner.DeleteAsync($"/api/notes/{movedId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>SetStatus — действие над комментарием: обычный путь владельца (аннотация
    /// к своей документной заметке, статус open → resolved).</summary>
    [Fact]
    public async Task Владелец_МеняетСтатусКомментария_КакРаньше()
    {
        await SetMemberAsync("editor");
        var projectId = await SharedProjectIdAsync();
        var owner = Owner();
        // сам файл важнее id: путь документа — notes/Документ для комментария.md
        await OwnNoteAsync("Документ для комментария");

        var annotated = await owner.PostAsJsonAsync("/api/notes/annotate", new
        {
            doc = new { scope = projectId, path = "notes/Документ для комментария.md" },
            // офсеты — только хинт, истина — посимвольная сверка уникального текста
            selection = new { start = 0, end = 0, text = "Упоминание ЦелеваяТема" },
            comment = "Комментарий к проектной заметке",
        });
        annotated.EnsureSuccessStatusCode();
        var commentId = IdOf(await annotated.Content.ReadAsStringAsync());

        var status = await owner.PostAsJsonAsync($"/api/notes/{commentId}/status", new { status = "resolved" });
        status.EnsureSuccessStatusCode();
        JsonSerializer.Deserialize<JsonElement>(await status.Content.ReadAsStringAsync())
            .GetProperty("annotation").GetProperty("status").GetString().Should().Be("resolved");
    }
}
