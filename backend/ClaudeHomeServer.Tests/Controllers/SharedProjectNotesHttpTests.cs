using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Git;
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
/// записи (читатель) — 403; чтение — 200; путь владельца не меняется. С S2c-7 редактор
/// общего проекта получает WriteContent и пишет те же ручки — 200/204, файл в notes/ проекта.
///
/// Проект и основная заметка — ОДНИ на весь класс: кэш модели NotesService ключуется по id
/// пользователя с TTL 2 с (NotesService.cs:51-52), и новый проект на каждый тест попадал бы
/// в несвежую модель участника — чтение вернуло бы пустой список по причине свежести, а не
/// по проверяемой. Роль участника меняется PUT-ом между тестами, состав — явный в каждом.
/// </summary>
[Trait("Category", "Host")]
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
    private static string? _projectDir;
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
            _projectDir = dir;

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
    /// второй пользователь посторонний).
    ///
    /// После любой смены состава — «резканье» участника (его личная запись): модель
    /// заметок per-owner (TTL 2 с, NotesService.cs:52-53) не снимается ни сменой участности,
    /// ни чужими записями (InvalidateWrite покрывает вызывающего и арендатора,
    /// NotesService.cs:128-134), и прогретая прежним тестом модель вернула бы участнику
    /// снимок старого состава — не по проверяемой причине, а по свежести кэша (F-8:
    /// тест «Посторонний читает комментарии» оставил модель участника без проекта, и
    /// следующий читатель получал бы 404 вместо 403). Своя операция участника снимает
    /// его ключ (NotesService.cs:130) — тот же приём, что «Резканье владельца» в тесте
    /// редактора, только через личный источник: проектную запись участник сделать не может.</summary>
    private async Task SetMemberAsync(string? role)
    {
        var projectId = await SharedProjectIdAsync();
        var owner = Owner();

        if (_memberId is null)
        {
            if (role is not null)
            {
                var added = await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                    new { username = TestWebApplicationFactory.SecondUsername, role });
                added.EnsureSuccessStatusCode();
                _memberId = JsonSerializer.Deserialize<JsonElement>(await added.Content.ReadAsStringAsync())
                    .GetProperty("members").EnumerateArray()
                    .First(m => m.GetProperty("username").GetString() == TestWebApplicationFactory.SecondUsername)
                    .GetProperty("userId").GetString()!;
            }
        }
        else if (role is null)
        {
            (await owner.DeleteAsync($"/api/projects/{projectId}/members/{_memberId}")).EnsureSuccessStatusCode();
            _memberId = null;
        }
        else
        {
            (await owner.PutAsJsonAsync($"/api/projects/{projectId}/members/{_memberId}", new { role }))
                .EnsureSuccessStatusCode();
        }

        (await Member().PostAsJsonAsync("/api/notes",
            new { title = $"Резканье {Guid.NewGuid():N}", content = "резканье", source = "personal" }))
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

    // ── отказ записи: 403 читателю, 404 невидимому; редактор пишет (S2c-7, ниже) ───

    [Fact]
    public async Task Читатель_ПишетПроектнуюЗаметку_403_И_ЗаметкаНеМеняется()
    {
        await SetMemberAsync("reader");
        var noteId = _noteId!;

        await WritesAreAsync(Member(), noteId, HttpStatusCode.Forbidden);

        await NoteIntactAsync(noteId);
    }

    /// <summary>Двери снимка (toc/translate) — тоже двери записи содержимого: поток доходит
    /// до notes.Update → ResolveRoot(WriteContent) → UnauthorizedAccessException
    /// (NotesService.cs:1398); без catch в действии это был 500 через UnhandledExceptionHandler.
    /// Раннер в тестовом хосте — заглушка TestWebApplicationFactory ("[]"), поэтому до Update
    /// поток доходит без живого LLM; заметка не меняется — Update бросает до записи файла.</summary>
    [Fact]
    public async Task Читатель_OglavlenieIPerevod_403_И_ЗаметкаНеМеняется()
    {
        await SetMemberAsync("reader");
        var noteId = _noteId!;

        (await Member().PostAsync($"/api/notes/{noteId}/toc", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Member().PostAsync($"/api/notes/{noteId}/translate", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await NoteIntactAsync(noteId);
    }

    // ── запись редактора (S2c-7): 200/204, файл — в notes/ проекта (арендатора) ────

    [Fact]
    public async Task Редактор_ПишетПроектнуюЗаметку_200_И_ФайлВПапкеПроекта()
    {
        await SetMemberAsync("editor");
        var projectId = await SharedProjectIdAsync();
        var member = Member();

        var create = await member.PostAsJsonAsync("/api/notes",
            new { title = "Заметка редактора", content = "Текст редактора", source = projectId });
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var newId = IdOf(await create.Content.ReadAsStringAsync());

        // Хранилище — арендатор: файл лежит в notes/ проекта (несмежение vault'ов
        // проверяется на уровне сервиса, SharedProjectNotesWriteContentTests)
        File.Exists(Path.Combine(_projectDir!, "notes", "Заметка редактора.md")).Should().BeTrue();

        var put = await member.PutAsJsonAsync($"/api/notes/{newId}", new { content = "Правка редактора" });
        put.EnsureSuccessStatusCode();

        // Владелец видит ту же заметку с той же правкой: корень один на всех.
        // Кэш модели per-owner живёт 2 с и чужая запись его не снимает (NotesService.cs:106),
        // поэтому резканье владельца инициирует его собственняя операция — как в реальном
        // клиенте после hub-события: следующий скан видит состояние после правки редактора.
        await OwnNoteAsync("Резканье владельца");
        var ownerView = await Owner().GetAsync($"/api/notes/{newId}");
        ownerView.EnsureSuccessStatusCode();
        JsonSerializer.Deserialize<JsonElement>(await ownerView.Content.ReadAsStringAsync())
            .GetProperty("content").GetString().Should().Be("Правка редактора");

        // Перенос внутри проекта — тоже открыт (та же дверь содержимого)
        var moved = await member.PostAsJsonAsync($"/api/notes/{newId}/move", new { folder = "Архив" });
        moved.EnsureSuccessStatusCode();
        var movedId = IdOf(await moved.Content.ReadAsStringAsync());
        File.Exists(Path.Combine(_projectDir!, "notes", "Архив", "Заметка редактора.md")).Should().BeTrue();

        (await member.DeleteAsync($"/api/notes/{movedId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        File.Exists(Path.Combine(_projectDir!, "notes", "Архив", "Заметка редактора.md")).Should().BeFalse();
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

    // ── F-8: чтение комментариев участником (решение Андрея 01.11) ───────────────────

    /// <summary>F-8: читатель общего проекта видит комментарии к видимой ему заметке.
    /// До F-8 GET /api/notes/annotations давал читателю 403: ResolveDoc требовал
    /// WriteContent и для чтения документа (NotesService.Annotations.cs, S2c-7).</summary>
    [Theory]
    [InlineData("editor")]
    [InlineData("reader")]
    public async Task Участник_ЧитаетПроектныеКомментарии_200(string role)
    {
        var projectId = await SharedProjectIdAsync();

        // Порядок важен: состояние создаёт владелец ДО состава и его резкана (SetMemberAsync).
        // Резканье заканчивается GetDetail созданной заметки — Build модели на этот момент;
        // если комментарий владельца создан раньше, он в прогретой модели есть, и чтение
        // участника не зависит от TTL кэша (2 с).
        var docTitle = $"Документ для чтения комментариев {role}";
        await OwnNoteAsync(docTitle);
        var annotated = await Owner().PostAsJsonAsync("/api/notes/annotate", new
        {
            doc = new { scope = projectId, path = $"notes/{docTitle}.md" },
            selection = new { start = 0, end = 0, text = "Упоминание ЦелеваяТема" },
            comment = $"Комментарий для {role}",
        });
        annotated.EnsureSuccessStatusCode();
        var commentId = IdOf(await annotated.Content.ReadAsStringAsync());

        (await Owner().PostAsJsonAsync($"/api/notes/{commentId}/reply", new { comment = "Ответ владельца" }))
            .EnsureSuccessStatusCode();

        await SetMemberAsync(role);
        var member = Member();

        var annotations = await member.GetAsync(
            $"/api/notes/annotations?scope={projectId}&path={Uri.EscapeDataString($"notes/{docTitle}.md")}");
        annotations.EnsureSuccessStatusCode();
        var rows = JsonSerializer.Deserialize<JsonElement>(await annotations.Content.ReadAsStringAsync());
        rows.EnumerateArray().Select(a => a.GetProperty("noteId").GetString()).Should().Contain(commentId);
        // Привязка резолвится у участника так же: ^id, записанный владельцем, виден и статус exact
        rows.EnumerateArray().Single(a => a.GetProperty("noteId").GetString() == commentId)
            .GetProperty("state").GetString().Should().Be("exact");

        var replies = await member.GetAsync($"/api/notes/{commentId}/replies");
        replies.EnsureSuccessStatusCode();
        JsonSerializer.Deserialize<JsonElement>(await replies.Content.ReadAsStringAsync())
            .EnumerateArray().Select(r => r.GetProperty("title").GetString())
            .Should().Contain("Ответ владельца");
    }

    /// <summary>F-8: запись комментариев остаётся закрытой читателю: annotate/repin/reply/status
    /// — 403 (ResolveDoc: WriteContent по умолчанию; SetAnnotationStatusCore — ResolveRoot с
    /// WriteContent). Модель читателя прогревается здесь только резканьем SetMemberAsync —
    /// ПОСЛЕ комментария владельца, поэтому repin/reply (они ищут комментарий в модели
    /// per-owner) находят его и дают проверяемые 403, а не 404 прежнего состава.
    ///
    /// Свой документ для попытки читателя — свежий, без ^id: Annotate держит ДВЕ двери
    /// (ResolveDoc и CreateCore→ResolveRoot(WriteContent), NotesService.cs:826), и проверка
    /// на документе с уже готовым якорем не различила бы, какая из них отказала: ошибочный
    /// Read на ResolveDoc в Annotate пропустил бы читателя к записи ^id в файл проекта
    /// перед 403 второй двери. На свежем документе это видно по содержанию: отказ обязан
    /// прийти ДО любой записи — файл остаётся ровно таким, каким был создан.</summary>
    [Fact]
    public async Task Читатель_АннотируетОтвечаетЗакрываетКомментарий_403_И_КомментарийНеМеняется()
    {
        var projectId = await SharedProjectIdAsync();

        // Тот же порядок, что в тесте 200: комментарий владельца — ДО состава и его резкана,
        // чтобы прогреваемая резканьем модель читателя содержала его (repin/reply ищут
        // комментарий в модели per-owner: без него — 404 вместо проверяемых 403).
        var docTitle = "Документ для отказа комментариев";
        await OwnNoteAsync(docTitle);
        var annotated = await Owner().PostAsJsonAsync("/api/notes/annotate", new
        {
            doc = new { scope = projectId, path = $"notes/{docTitle}.md" },
            selection = new { start = 0, end = 0, text = "Упоминание ЦелеваяТема" },
            comment = "Комментарий владельца для отказа",
        });
        annotated.EnsureSuccessStatusCode();
        var commentId = IdOf(await annotated.Content.ReadAsStringAsync());

        // Свежий документ (без ^id) — именно на нём читатель пытается аннотировать.
        var freshTitle = "Документ для отказа без якоря";
        var freshId = await OwnNoteAsync(freshTitle);

        await SetMemberAsync("reader");
        var member = Member();

        (await member.PostAsJsonAsync("/api/notes/annotate", new
        {
            doc = new { scope = projectId, path = $"notes/{freshTitle}.md" },
            selection = new { start = 0, end = 0, text = "Упоминание ЦелеваяТема" },
            comment = "Комментарий читателя",
        })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsJsonAsync($"/api/notes/{commentId}/repin",
            new { start = 0, end = 0, text = "Упоминание ЦелеваяТема" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsJsonAsync($"/api/notes/{commentId}/reply", new { comment = "Ответ читателя" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsJsonAsync($"/api/notes/{commentId}/status", new { status = "resolved" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Резканье владельца перед проверками: его модель per-owner прогрета его Create-ами
        // ДО попыток читателя, а отказавший читатель не инвалидирует её (CreateCore бросает
        // до InvalidateWrite). Без резкана проверки ниже читали бы снимок файлов до попыток —
        // ложно-зелёный при любой правке, которую читатель всё-таки успел сделать.
        // Тот же приём, что «Резканье владельца» в тесте редактора.
        await OwnNoteAsync("Резканье владельца F-8 отказ");

        // Отказы не тронули комментарий: статус open, тред пуст
        var detail = await Owner().GetAsync($"/api/notes/{commentId}");
        detail.EnsureSuccessStatusCode();
        JsonSerializer.Deserialize<JsonElement>(await detail.Content.ReadAsStringAsync())
            .GetProperty("annotation").GetProperty("status").GetString().Should().Be("open");
        var replies = await Owner().GetAsync($"/api/notes/{commentId}/replies");
        replies.EnsureSuccessStatusCode();
        JsonSerializer.Deserialize<JsonElement>(await replies.Content.ReadAsStringAsync())
            .EnumerateArray().Should().BeEmpty();

        // Отказ предшествовал любой записи: свежий документ не тронут ни ^id, ни чем иным
        // (мутация «Read на ResolveDoc в Annotate» записала бы ^id до 403 второй двери —
        // это сравнение краснеет).
        var fresh = await Owner().GetAsync($"/api/notes/{freshId}");
        fresh.EnsureSuccessStatusCode();
        JsonSerializer.Deserialize<JsonElement>(await fresh.Content.ReadAsStringAsync())
            .GetProperty("content").GetString().Should().Be(NoteContent);
    }

    /// <summary>F-8: посторонний читает проектные комментарии — 404: нет права Read →
    /// KeyNotFoundException (ResolveDoc), «чужое» и «несуществующее» не различаем.
    /// GetReplies сначала ищет комментарий в модели per-owner — резканье в SetMemberAsync
    /// снимает модель, прогретую пока участник ещё был участником, и Build для постороннего
    /// проекта не видит.</summary>
    [Fact]
    public async Task Посторонний_ЧитаетПроектныеКомментарии_404()
    {
        var projectId = await SharedProjectIdAsync();

        var docTitle = "Документ для постороннего";
        await OwnNoteAsync(docTitle);
        var annotated = await Owner().PostAsJsonAsync("/api/notes/annotate", new
        {
            doc = new { scope = projectId, path = $"notes/{docTitle}.md" },
            selection = new { start = 0, end = 0, text = "Упоминание ЦелеваяТема" },
            comment = "Комментарий для постороннего",
        });
        annotated.EnsureSuccessStatusCode();
        var commentId = IdOf(await annotated.Content.ReadAsStringAsync());

        // Состав снимаются после создания состояния: резканье внутри SetMemberAsync греет
        // модель уже БЕЗ проекта — GET replies резолвится по этой модели и не находит
        // комментарий (404), GET annotations отбивается на ResolveDoc до всякой модели.
        await SetMemberAsync(role: null);
        var member = Member();

        (await member.GetAsync(
            $"/api/notes/annotations?scope={projectId}&path={Uri.EscapeDataString($"notes/{docTitle}.md")}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await member.GetAsync($"/api/notes/{commentId}/replies"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
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

    /// <summary>QA-D1 (дефект 805c570b): сквозная цепочка Notes → журнал правок → git на
    /// настоящем репозитории, без фейк-швов. Репозиторий и снятие игнора заметок создаёт
    /// само приглашение (AddAsync: InitAsync + EnsureNotesVersionedAsync), коммит — сброс
    /// журнала. До QA-D1 тест был красным: .gitignore шаблона игнорировал notes/, git молча
    /// не брал путь в index, коммит по pathspec выходил пустым (тот самый дефект).</summary>
    [Fact]
    public async Task Участник_ПишетЗаметку_Журнал_Коммитит_Её_В_Git_С_Автором_Участника()
    {
        await SetMemberAsync("editor");
        var projectId = await SharedProjectIdAsync();
        var title = "Журнальная заметка";
        var created = await Member().PostAsJsonAsync("/api/notes",
            new { title, content = "тело журнальной заметки", source = projectId });
        created.StatusCode.Should().Be(HttpStatusCode.OK);

        // Тот же синглтон, что греет NotesService через IHumanEditJournal: Record уже
        // произошёл в записи заметки; здесь — сброс буфера (окно дебаунса не ждём).
        var committer = _factory.Services.GetRequiredService<HumanEditCommitter>();
        await committer.FlushAsync(all: true);

        var git = _factory.Services.GetRequiredService<GitService>();
        var log = (await git.RunAsync(null, _projectDir!,
            ["log", "--format=%an|%s", "--", $"notes/{title}.md"])).Stdout.Trim();
        // Ровно один коммит по своему пути. Тема — общая для группы правок одного автора
        // (так устроен журнал): в неё могли лечь и незакоммиченные записи предыдущих
        // тестов класса — проверяется своё: автор и свой путь в теме.
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1);
        lines[0].Should().StartWith("seconduser|Правка: ").And.Contain($"notes/{title}.md");
    }
}
