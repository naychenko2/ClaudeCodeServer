using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

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
/// </summary>
[Trait("Category", "Host")]
public class ChildSubjectMaterialsControllerTests : IClassFixture<ChildSubjectMaterialsControllerTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts()
        {
            ExtraConfig["ChildMode:Materials:MaxFileBytes"] = "1024";
            ExtraConfig["ChildMode:Materials:MaxSubjectBytes"] = "1500";
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

    [Fact]
    public async Task Upload_Png_IsRefusedUnsupportedType()
    {
        var childId = SetupChild("png");
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 };

        var post = await _admin.PostAsync(Url(childId, "png"), Multipart("page.png", png));
        post.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await CodeAsync(post)).Should().Be(ChildSubjectMaterialsStore.CodeUnsupportedType);
        (await Store.ListAsync(childId, "png")).Should().BeEmpty();
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
