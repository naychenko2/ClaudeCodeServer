using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// К2b (разрез «Знания репетитора» §2): ручки материалов предмета — опись, загрузка,
/// удаление — только админ (класс: роль по claim + AdminByStore по стору; [ChildAllowed]
/// нет — И6). Работа через боевой ChildSubjectMaterialsStore К2а (тот же синглтон хоста).
///
/// Пределы стора опущены конфигом (1 КБ на файл, 1500 Б на предмет): «на байт больше»
/// дешёвый, фикстуры маленькие (тот же приём, что у ChildAttachmentsControllerTests с
/// MaxAttachmentBytes).
///
/// Отказ ребёнка на этой ручке — 403 БЕЗ тела {error:"child_mode"}: классовой роли
/// токен ребёнка (role "user", JwtService.cs:58) не удовлетворяет на UseAuthorization,
/// а это раньше детского гейта (Program.cs:1151 стоит до UseChildModeGate, :1155).
/// Ребёнок закрыт ролью, а не гейтом — тот же признак, что у UsersChildProfileTests
/// .NotAdmin_Returns403 на PUT child-profile с теми же атрибутами. Тест проверяет
/// статус 403 и то, что до стора запрос ребёнка не дошёл (опись пуста) — И6.
///
/// Мутация (критерий c3): поставить [ChildAllowed] на GET materials — краснеет
/// ChildModeEndpointGuardTests.ChildAllowedList_IsFrozen (список размеченных заморожен);
/// тесты этой ручки от метки не краснеют: роль отказывает ребёнка одинаково с ней и без неё.
///
/// КИ1c (разрез §9, таблица исходов загрузки): картинка на этой ручке идёт веткой
/// распознавания боевого стора — растр в хосте настоящий (вертикаль Images включена
/// гейтом по умолчанию), распознавание — фейк ILocalPageOcr с журналом вызовов
/// (подмена форвардера LlmSubsystem.cs:491: RemoveAll + своя регистрация после
/// встроенных — побеждает; стор резолвит шов лениво, при первом запросе видит фейк).
/// Фейк переключается тестом перед запросом (тесты класса последовательны), счётчик
/// — доказательство «до OCR дело не дошло» (отказ ребёнка, предпроверка стора).
/// </summary>
[Trait("Category", "Host")]
public class ChildSubjectMaterialsControllerTests : IClassFixture<ChildSubjectMaterialsControllerTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory
    {
        public FakeOcr Ocr { get; } = new();

        public Hosts()
        {
            ExtraConfig["ChildMode:Materials:MaxFileBytes"] = "1024";
            ExtraConfig["ChildMode:Materials:MaxSubjectBytes"] = "1500";
            ExtraServices = services =>
            {
                services.RemoveAll<ILocalPageOcr>();
                services.AddSingleton<ILocalPageOcr>(Ocr);
            };
        }

        // Фейк OCR с счётчиком и переключаемым исходом (тот же приём, что RecordingOcr
        // в сторовых тестах КИ1b, но общий на хост: тесты класса последовательны).
        public sealed class FakeOcr : ILocalPageOcr
        {
            public int Calls { get; private set; }
            public OcrResult Next { get; set; } = OcrResult.Fail(OcrFailureReason.Disabled, "нет результата (фейк)");

            public Task<OcrResult> RecognizeAsync(ReadOnlyMemory<byte> png, OcrRequestInfo info, CancellationToken ct)
            {
                Calls++;
                return Task.FromResult(Next);
            }
        }
    }

    private readonly Hosts _hosts;
    private readonly UserStore _users;
    private readonly HttpClient _admin;

    public ChildSubjectMaterialsControllerTests(Hosts hosts)
    {
        _hosts = hosts;
        _users = _hosts.Services.GetRequiredService<UserStore>();
        _admin = _hosts.CreateAuthenticatedClient();
    }

    private ChildSubjectMaterialsStore Store => _hosts.Services.GetRequiredService<ChildSubjectMaterialsStore>();

    private static string Url(string childId, string subjectId, string? materialId = null) =>
        $"/api/users/{childId}/child-subjects/{subjectId}/materials" + (materialId is null ? "" : "/" + materialId);

    // Ребёнок — второй пользователь фабрики: профиль пишется прямо в сторе (тот же приём,
    // что у ChildSubjectsEndpointsTests.SetupChild / ChildModeEndpointGuardTests.SetChild).
    private string SetupChild(params string[] subjectIds)
    {
        var user = _users.FindByUsername(TestWebApplicationFactory.SecondUsername)!;
        user.ChildMode = new ChildProfile
        {
            Enabled = true, TutorPersonaId = "tutor", ProviderKey = "key", Model = "sonnet",
            Subjects = [.. subjectIds.Select(s => new ChildSubject
            {
                Id = s, Label = "Предмет " + s, Glyph = "Аа", Color = "info",
                ProviderKey = "key", Model = "sonnet",
            })],
        };
        return user.Id;
    }

    private HttpClient ChildClient() =>
        _hosts.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private static MultipartFormDataContent Multipart(string name, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var form = new MultipartFormDataContent();
        form.Add(content, "file", name);
        return form;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    private static async Task<string> CodeAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        if (text.Length == 0) return "";
        var body = JsonSerializer.Deserialize<JsonElement>(text);
        return body.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
    }

    // Каталог предмета в боевом сторе хоста: корень — директория DataPath (TestWebApplicationFactory.cs:115),
    // т.е. TempDir фабрики + child-subjects.
    private string SubjectDir(string childId, string subjectId) =>
        Path.Combine(_hosts.TempDir, ChildSubjectMaterialsStore.DirName, childId, subjectId);

    // ── c1: полный цикл админа ───────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_Upload_List_Delete_Roundtrip()
    {
        var childId = SetupChild("math");
        var bytes = Encoding.UTF8.GetBytes("конспект: дроби, сложение — текст");

        var post = await _admin.PostAsync(Url(childId, "math"), Multipart("конспект.txt", bytes));
        post.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await JsonAsync(post);
        var id = created.GetProperty("id").GetString()!;
        id.Should().MatchRegex("^m_[0-9a-f]{12}$");
        created.GetProperty("name").GetString().Should().Be("конспект.txt");
        created.GetProperty("kind").GetString().Should().Be(ChildSubjectMaterialsStore.KindText);
        created.GetProperty("bytes").GetInt64().Should().Be(bytes.LongLength);

        var get = await _admin.GetAsync(Url(childId, "math"));
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = (await JsonAsync(get)).EnumerateArray().ToList();
        rows.Should().HaveCount(1);
        // Контракт описи: ровно {id, name, kind, bytes, uploadedAt}. Содержания нет;
        // внутренних имён стора (storedName/textName) и автора загрузки в ответе нет.
        rows[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "name", "kind", "bytes", "uploadedAt");
        rows[0].GetProperty("id").GetString().Should().Be(id);
        rows[0].GetProperty("bytes").GetInt64().Should().Be(bytes.LongLength);

        // uploadedBy — id вызывающего админа: в ответе поля нет, в сторе оно есть.
        var adminId = _users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        (await Store.ListAsync(childId, "math")).Single().UploadedBy.Should().Be(adminId);

        var del = await _admin.DeleteAsync(Url(childId, "math", id));
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await _admin.GetAsync(Url(childId, "math"));
        after.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(after)).EnumerateArray().Should().BeEmpty();
        (await Store.ListAsync(childId, "math")).Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_UnknownMaterial_Is404MaterialNotFound()
    {
        var childId = SetupChild("del");
        var del = await _admin.DeleteAsync(Url(childId, "del", "m_000000000000"));
        del.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeAsync(del)).Should().Be("material_not_found");
    }

    // ── c1: коды отказов загрузки ────────────────────────────────────────────────────

    // Текст по сигнатуре: IsUsableText отбрасывает нулевые байты (ChildAttachmentPolicy.cs:106),
    // «много нулей» — не текст, а unsupported_type; размеры меряются текстовыми фикстурами.
    private static byte[] Text(int length) => Encoding.UTF8.GetBytes(new string('a', length));

    // Картинка (PNG/JPEG/WEBP) с КИ1b — не unsupported_type: она идёт веткой распознавания
    // (её отказ — ocr_unavailable/image_no_text — контракт КИ1c). 415 здесь проверяется на
    // HEIC: сигнатура вне allow-list стора (ChildAttachmentPolicy.Detect), как и было.
    [Fact]
    public async Task Upload_Heic_IsRefusedUnsupportedType()
    {
        var childId = SetupChild("heic");
        // «ftypheic…» — box-заголовок HEIC: не PNG/JPEG/WEBP/PDF и не текст (нулевые байты).
        var heic = new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63, 0x00, 0x00, 0x00, 0x00, 0x68, 0x65, 0x69, 0x63, 0x6D, 0x69, 0x66, 0x31 };

        var post = await _admin.PostAsync(Url(childId, "heic"), Multipart("page.heic", heic));
        post.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await CodeAsync(post)).Should().Be(ChildSubjectMaterialsStore.CodeUnsupportedType);
        (await Store.ListAsync(childId, "heic")).Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_TooLarge_IsRefused413()
    {
        var childId = SetupChild("big");
        // MaxFileBytes = 1024 (ExtraConfig): ровно на байт больше потолка.
        var post = await _admin.PostAsync(Url(childId, "big"), Multipart("big.txt", Text(1025)));
        post.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await CodeAsync(post)).Should().Be(ChildSubjectMaterialsStore.CodeTooLarge);
        (await Store.ListAsync(childId, "big")).Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_SubjectFull_IsRefused413()
    {
        var childId = SetupChild("geo");
        // MaxSubjectBytes = 1500: первая 1024 проходит, вторая 600 переводит сумму за потолок.
        var first = await _admin.PostAsync(Url(childId, "geo"), Multipart("a.txt", Text(1024)));
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await _admin.PostAsync(Url(childId, "geo"), Multipart("b.txt", Text(600)));
        second.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await CodeAsync(second)).Should().Be(ChildSubjectMaterialsStore.CodeSubjectFull);
        (await Store.ListAsync(childId, "geo")).Should().HaveCount(1);
    }

    // ── КИ1c: картинка через фейк-OCR — 201 с preview, 503 ocr_unavailable, 422 image_no_text ──

    // Настоящий декодируемый PNG (тот же приём, что PngOf в сторовых тестах КИ1b): его
    // прочитает боевой SkiaImageRaster хоста; маленький — в опущенный MaxFileBytes (1024).
    private static byte[] PngOf(int w, int h)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(240, 240, 240));
            using var paint = new SKPaint { Color = new SKColor(30, 30, 30) };
            canvas.DrawRect(2, 2, w - 4, h - 4, paint);
            canvas.Flush();
        }
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6));
        return data!.ToArray();
    }

    [Fact]
    public async Task UploadImage_FakeOcrSuccess_Created201WithPreview_GetHasNoPreview()
    {
        var childId = SetupChild("ocr-ok");
        var text = "распознанный текст страницы: дроби и сложение";
        _hosts.Ocr.Next = OcrResult.Success(text, "qwen3.8-27b", 120, 10, 20);
        var callsBefore = _hosts.Ocr.Calls;

        var post = await _admin.PostAsync(Url(childId, "ocr-ok"), Multipart("страница.png", PngOf(30, 20)));
        post.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await JsonAsync(post);
        created.GetProperty("id").GetString()!.Should().MatchRegex("^m_[0-9a-f]{12}$");
        created.GetProperty("kind").GetString().Should().Be(ChildSubjectMaterialsStore.KindImage);
        // preview ответа загрузки (КИ1c): ровно то, что вернул фейк (короче 600 — целиком).
        created.GetProperty("preview").GetString().Should().Be(text);
        _hosts.Ocr.Calls.Should().Be(callsBefore + 1);

        // Опись (GET) — без содержимого: строка есть, ключа preview в ней нет (И6).
        var get = await _admin.GetAsync(Url(childId, "ocr-ok"));
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = (await JsonAsync(get)).EnumerateArray().ToList();
        rows.Should().HaveCount(1);
        rows[0].GetProperty("id").GetString().Should().Be(created.GetProperty("id").GetString());
        rows[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "name", "kind", "bytes", "uploadedAt");
    }

    [Fact]
    public async Task UploadImage_OcrRefused_Is503OcrUnavailable_NoTrace()
    {
        var childId = SetupChild("ocr-off");
        _hosts.Ocr.Next = OcrResult.Fail(OcrFailureReason.Disabled, "OCR выключен (фейк)");

        var post = await _admin.PostAsync(Url(childId, "ocr-off"), Multipart("page.png", PngOf(30, 20)));
        post.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await CodeAsync(post)).Should().Be(ChildSubjectMaterialsStore.CodeOcrUnavailable);

        // Отказ — ни файла, ни строки описи: опись пустая.
        var get = await _admin.GetAsync(Url(childId, "ocr-off"));
        (await JsonAsync(get)).EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task UploadImage_OcrReturnedOnlyMarkers_Is422ImageNoText_NoTrace()
    {
        var childId = SetupChild("ocr-empty");
        // «Одни [?] с пробелами» — страница прочитана и пуста: парный pdf_no_text, не 503.
        _hosts.Ocr.Next = OcrResult.Success("[?] [?]  [?]", "qwen3.8-27b", 30, 5, 5);

        var post = await _admin.PostAsync(Url(childId, "ocr-empty"), Multipart("page.png", PngOf(30, 20)));
        post.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(post)).Should().Be(ChildSubjectMaterialsStore.CodeImageNoText);

        var get = await _admin.GetAsync(Url(childId, "ocr-empty"));
        (await JsonAsync(get)).EnumerateArray().Should().BeEmpty();
    }

    // preview текстового вида (КИ1c): тот же предел 600, что у картинки (PreviewMaxChars
    // стора); текст 700 символов → превью ровно 600. Опись — по-прежнему без содержимого.
    [Fact]
    public async Task UploadText700Chars_CreatedPreviewIsFirst600_GetRowsHaveNoPreview()
    {
        var childId = SetupChild("prev");
        // Латиница: 1 байт/символ — 700 символов это ровно 700 байта, в опущенный
        // MaxFileBytes (1024) входит, а предельный размер тут не предмет теста.
        var body = new string('a', 700);

        var post = await _admin.PostAsync(Url(childId, "prev"), Multipart("урок.txt", Encoding.UTF8.GetBytes(body)));
        post.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await JsonAsync(post);
        var preview = created.GetProperty("preview").GetString()!;
        preview.Length.Should().Be(600);
        body.StartsWith(preview).Should().BeTrue();

        var get = await _admin.GetAsync(Url(childId, "prev"));
        var rows = (await JsonAsync(get)).EnumerateArray().ToList();
        rows.Should().HaveCount(1);
        foreach (var row in rows) row.TryGetProperty("preview", out _).Should().BeFalse();
    }

    // И6 (путь к инварианту КИ1c): ребёнок с JWT — отбит классовой ролью на POST картинки,
    // и до распознавания дело не дошло: счётчик фейка не сдвинулся (предупреждение: чужой
    // тест класса уже звал фейк — проверяем дельту, не абсолют).
    [Fact]
    public async Task ChildToken_PostImage_Is403_OcrNeverCalled()
    {
        var childId = SetupChild("kid-img");
        var child = ChildClient();
        var callsBefore = _hosts.Ocr.Calls;

        (await child.PostAsync(Url(childId, "kid-img"), Multipart("page.png", PngOf(30, 20))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _hosts.Ocr.Calls.Should().Be(callsBefore);
        (await Store.ListAsync(childId, "kid-img")).Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownSubject_Is404OnAllThreeEndpoints()
    {
        var childId = SetupChild("math");

        var get = await _admin.GetAsync(Url(childId, "history"));
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeAsync(get)).Should().Be("subject_not_found");

        var post = await _admin.PostAsync(Url(childId, "history"), Multipart("a.txt", "текст"u8.ToArray()));
        post.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeAsync(post)).Should().Be("subject_not_found");

        var del = await _admin.DeleteAsync(Url(childId, "history", "m_000000000000"));
        del.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeAsync(del)).Should().Be("subject_not_found");

        // До стора дело не дошло: каталога чужого предмета нет.
        Directory.Exists(SubjectDir(childId, "history")).Should().BeFalse();
    }

    [Fact]
    public async Task NotChild_Is404ChildNotFound()
    {
        // testuser — админ, но не ребёнок: профиля нет.
        var adminId = _users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var get = await _admin.GetAsync(Url(adminId, "math"));
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeAsync(get)).Should().Be("child_not_found");

        // Несуществующий id — то же тело: существование не раскрываем.
        var ghost = await _admin.GetAsync(Url("no-such-user", "math"));
        ghost.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeAsync(ghost)).Should().Be("child_not_found");

        // Выключенный профиль — обычный пользователь (ChildModePolicy: «профиль выключен»
        // = обычный): тот же 404, проверка до стора.
        var childId = SetupChild("math");
        _users.GetById(childId)!.ChildMode!.Enabled = false;
        var off = await _admin.GetAsync(Url(childId, "math"));
        off.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeAsync(off)).Should().Be("child_not_found");
    }

    // ── c2: ребёнок закрыт на всех трёх ручках; роль не по стору — 403 ───────────────

    [Fact]
    public async Task ChildToken_IsRefusedOnAllThreeEndpoints()
    {
        // Свой предмет на тест: порядок исполнения тестов класса не определён, чужая
        // запись в описи не должна делать проверку ни зелёной на пустоте, ни красной от чужого состояния.
        var childId = SetupChild("kid");
        var child = ChildClient();

        // 403 без тела {error:"child_mode"}: классовой роли токен ребёнка не
        // удовлетворяет раньше детского гейта (см. заголовок класса) — механизм отказа
        // на этой ручке роль, а не гейт; статус 403 и есть гарантия И6.
        (await child.GetAsync(Url(childId, "kid"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await child.PostAsync(Url(childId, "kid"), Multipart("a.txt", "текст"u8.ToArray())))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await child.DeleteAsync(Url(childId, "kid", "m_000000000000"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Ни один запрос ребёнка до стора не дошёл: описи нет.
        (await Store.ListAsync(childId, "kid")).Should().BeEmpty();
    }

    [Fact]
    public async Task AdminClaimWithoutStoreRole_IsRefused()
    {
        var childId = SetupChild("math");
        // Токен админа (claim role=admin) + снятое админство в сторе: классовой роли
        // запрос проходит по claim, политики AdminByStore — нет: «источник истины о
        // роли — UserStore» (AdminByStore.cs:18).
        var token = _hosts.GetToken(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        var user = _users.FindByUsername(TestWebApplicationFactory.TestUsername)!;
        var wasRole = user.Role;
        user.Role = "user";
        try
        {
            using var client = _hosts.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            (await client.GetAsync(Url(childId, "math"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            user.Role = wasRole;
        }
    }

    // ── c2 / путь №2: WebDAV не раздаёт data/child-subjects даже админу ──────────────

    [Fact]
    public async Task WebDav_DoesNotServeDataChildSubjects_EvenToAdmin()
    {
        var childId = SetupChild("web");
        var post = await _admin.PostAsync(Url(childId, "web"), Multipart("конспект.txt", "текст"u8.ToArray()));
        post.StatusCode.Should().Be(HttpStatusCode.Created);

        // Непустота проверки: материал реально лежит в data/child-subjects — под
        // каталогом стора есть файлы.
        var dir = SubjectDir(childId, "web");
        Directory.Exists(dir).Should().BeTrue();
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Should().NotBeEmpty();

        // WebDAV корневается по корням проектов (WebDavHandler.cs:158), а не по каталогу
        // data: список /projects/ (Basic-вход админа) не называет child-subjects…
        // Basic — на всём клиенте: GET без заголовка получил бы 401 раньше разбора пути.
        using var dav = _hosts.CreateClient();
        dav.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{TestWebApplicationFactory.TestUsername}:{TestWebApplicationFactory.TestPassword}")));
        HttpRequestMessage Propfind(string path)
        {
            var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), path);
            // Depth: 1 — список детей корня реально запрашивается: иначе «в списке нет
            // child-subjects» было бы зелёным на пустом списке.
            req.Headers.Add("Depth", "1");
            return req;
        }

        var root = await dav.SendAsync(Propfind("/projects/"));
        root.StatusCode.Should().Be(HttpStatusCode.MultiStatus);
        (await root.Content.ReadAsStringAsync()).Should().NotContain("child-subjects");

        // …и прямой путь к каталогу стора — 404: проекта с именем child-subjects нет
        // (WebDavHandler.cs:150), поэтому ни коллекция, ни файл описи через /projects/
        // не отдаются вовсе.
        var direct = await dav.SendAsync(Propfind($"/projects/{ChildSubjectMaterialsStore.DirName}/"));
        direct.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var file = await dav.GetAsync($"/projects/{ChildSubjectMaterialsStore.DirName}/{childId}/web/materials.json");
        file.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

/// <summary>
/// Ревью К2b (минор 1): ветка «исключение стора → 500 {code:"storage_error"}» в catch трёх
/// ручек контроллера (List/Add/Delete) не была покрыта ни одним тестом. Подмена регистрации
/// ChildSubjectMaterialsStore на экземпляр с null-root: SafePath.Join бросает на построении
/// пути (ArgumentNullException) — это общая точка всех трёх ручек (SubjectDir вызывается до
/// их внутренних try и до internal-catch стора), т.е. ровно то, что обязан ловить catch
/// контроллера. Тот же приём, что UsersDeleteChildMaterialsBrokenStoreTests.
///
/// Мутация (критерий c2): убрать catch в AddMaterial — краснеет AddMaterial_StoreThrows…
/// (необработанное исключение вместо 500 storage_error); то же для List/Delete.
/// </summary>
[Trait("Category", "Host")]
public class ChildSubjectMaterialsStorageFailureTests : IClassFixture<ChildSubjectMaterialsStorageFailureTests.BrokenHosts>
{
    public sealed class BrokenHosts : TestWebApplicationFactory
    {
        public BrokenHosts() => ExtraServices = services =>
        {
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Markitdown:Command"] = "ccs-no-such-converter-9f3a",
            }).Build();
            // Подмена боевой регистрации: null-root → SubjectDir бросает на SafePath.Join.
            services.RemoveAll<ChildSubjectMaterialsStore>();
            services.AddSingleton(new ChildSubjectMaterialsStore(
                null!,
                new ClaudeHomeServer.Services.Docs.MarkitdownService(cfg, NullLogger<ClaudeHomeServer.Services.Docs.MarkitdownService>.Instance),
                cfg,
                _ => true,
                new UnusedOcr(),
                raster: null,
                NullLogger<ChildSubjectMaterialsStore>.Instance));
        };
    }

    private sealed class UnusedOcr : ILocalPageOcr
    {
        public Task<OcrResult> RecognizeAsync(ReadOnlyMemory<byte> png, OcrRequestInfo info, CancellationToken ct) =>
            throw new InvalidOperationException("OCR в этом тесте не вызывается");
    }

    private readonly BrokenHosts _hosts;
    private readonly UserStore _users;
    private readonly HttpClient _admin;

    public ChildSubjectMaterialsStorageFailureTests(BrokenHosts hosts)
    {
        _hosts = hosts;
        _users = _hosts.Services.GetRequiredService<UserStore>();
        _admin = _hosts.CreateAuthenticatedClient();
    }

    private static string Url(string childId, string subjectId, string? materialId = null) =>
        $"/api/users/{childId}/child-subjects/{subjectId}/materials" + (materialId is null ? "" : "/" + materialId);

    // Ребёнок — второй пользователь этого хоста: профиль пишется прямо в сторе (тот же
    // приём, что SetupChild выше). Предмет валиден — контроллер обязан дойти до стора.
    private string SetupChild(params string[] subjectIds)
    {
        var user = _users.FindByUsername(TestWebApplicationFactory.SecondUsername)!;
        user.ChildMode = new ChildProfile
        {
            Enabled = true, TutorPersonaId = "tutor", ProviderKey = "key", Model = "sonnet",
            Subjects = [.. subjectIds.Select(s => new ChildSubject
            {
                Id = s, Label = "Предмет " + s, Glyph = "Аа", Color = "info",
                ProviderKey = "key", Model = "sonnet",
            })],
        };
        return user.Id;
    }

    private static MultipartFormDataContent Multipart(string name, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var form = new MultipartFormDataContent();
        form.Add(content, "file", name);
        return form;
    }

    private static async Task<string> CodeAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        if (text.Length == 0) return "";
        var body = JsonSerializer.Deserialize<JsonElement>(text);
        return body.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
    }

    [Fact]
    public async Task ListMaterials_StoreThrows_Returns500StorageError()
    {
        var childId = SetupChild("math");
        var get = await _admin.GetAsync(Url(childId, "math"));
        get.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await CodeAsync(get)).Should().Be(ChildSubjectMaterialsStore.CodeStorageError);
    }

    [Fact]
    public async Task AddMaterial_StoreThrows_Returns500StorageError()
    {
        var childId = SetupChild("math");
        var post = await _admin.PostAsync(Url(childId, "math"), Multipart("урок.txt", "текст"u8.ToArray()));
        post.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await CodeAsync(post)).Should().Be(ChildSubjectMaterialsStore.CodeStorageError);
    }

    [Fact]
    public async Task DeleteMaterial_StoreThrows_Returns500StorageError()
    {
        var childId = SetupChild("math");
        var del = await _admin.DeleteAsync(Url(childId, "math", "m_000000000000"));
        del.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await CodeAsync(del)).Should().Be(ChildSubjectMaterialsStore.CodeStorageError);
    }
}
