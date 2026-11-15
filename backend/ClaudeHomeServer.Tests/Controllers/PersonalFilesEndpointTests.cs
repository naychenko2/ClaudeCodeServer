using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// «Мои файлы» личного чата (ADR-040 Ф2-9): api/personal/chats/{sessionId}/files на собранном приложении.
// Владелец — владелец чата; чужой и администратор — 404; GET папку не создаёт; путь не выходит за корень
// «Личного» ни через «..», ни через абсолютный путь, ни через символическую ссылку; корень не удалить и не
// переименовать; проектные api/projects/{id}/files «Личное» не открывают.
[Trait("Category", "Host")]
public class PersonalFilesEndpointTests : IClassFixture<PersonalFilesEndpointTests.Host>
{
    // «Личное» в памяти: папка владельца — внутри TempDir, боевой стор требует домашней папки пользователя
    public sealed class Host : TestWebApplicationFactory
    {
        public FakePersonal Personal { get; }

        public Host()
        {
            Personal = new FakePersonal(Path.Combine(TempDir, "Personal"));
            // Стаб клиента для save-from-url: настоящей сети нет, поведение — по пути URL.
            // Адреса в тестах — публичные литералы (8.8.8.8) или приватные (127.0.0.1):
            // SSRF-guard решает по ним как в бою, без DNS
            ExtraServices = services =>
            {
                services.AddSingleton<IPersonalProjects>(Personal);
                services.AddSingleton<IHttpClientFactory>(new StubHttpFactory());
            };
        }
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());

        private sealed class StubHandler : HttpMessageHandler
        {
            private static readonly byte[] Small = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var path = request.RequestUri!.AbsolutePath;
                HttpResponseMessage resp;
                if (path.EndsWith("/small.png"))
                    resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Small) };
                else if (path.EndsWith("/huge"))
                {
                    // Тело с объявленной длиной больше предела: отказ по заголовку, до чтения
                    var content = new ByteArrayContent([]);
                    content.Headers.ContentLength = 200L * 1024 * 1024 + 1;
                    resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                }
                else
                    resp = new HttpResponseMessage(HttpStatusCode.NotFound);
                return Task.FromResult(resp);
            }
        }
    }

    public sealed class FakePersonal(string baseDir) : IPersonalProjects
    {
        private readonly Dictionary<string, Project> _byOwner = [];

        public string RootOf(string ownerId) => Path.Combine(baseDir, ownerId);

        public void Reset()
        {
            _byOwner.Clear();
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
        }

        public Project? GetPersonalOf(string ownerId) => _byOwner.GetValueOrDefault(ownerId);

        public Task<Project> EnsurePersonalAsync(string ownerId, CancellationToken ct = default)
        {
            Directory.CreateDirectory(RootOf(ownerId));
            return Task.FromResult(_byOwner[ownerId] = new Project
                { Id = "personal-" + ownerId, OwnerId = ownerId, RootPath = RootOf(ownerId), Kind = ProjectKind.Personal });
        }

        public Project? GetByIdIncludingPersonal(string id) => null;

        public IReadOnlyCollection<Project> GetAllPersonal() => [.. _byOwner.Values];

        public bool DeleteOf(string ownerId) => _byOwner.Remove(ownerId);
    }

    private readonly Host _host;
    private readonly HttpClient _owner;
    private readonly string _ownerId;

    public PersonalFilesEndpointTests(Host host)
    {
        _host = host;
        _host.Personal.Reset();
        // Владелец — обычный пользователь: администратор «чужого» личного чата не видит
        _owner = _host.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        _ownerId = IdOf(TestWebApplicationFactory.SecondUsername);
    }

    private string IdOf(string username) => _host.Services.GetRequiredService<UserStore>().FindByUsername(username)!.Id;

    private async Task<string> Chat(string? ownerId = null) =>
        (await _host.Services.GetRequiredService<SessionManager>()
            .CreateChatAsync(ownerId ?? _ownerId, ClaudeMode.AcceptEdits, name: "Личный")).Id;

    private static string Url(string chat, string tail = "") => $"/api/personal/chats/{chat}/files{tail}";

    private static string Q(string path) => "path=" + Uri.EscapeDataString(path);

    // Папка «Личного» владельца с готовым деревом: images/a.png, notes.txt, docs/readme.md
    private async Task<string> Seeded()
    {
        var project = await _host.Personal.EnsurePersonalAsync(_ownerId);
        var root = project.RootPath;
        Directory.CreateDirectory(Path.Combine(root, "images"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        await File.WriteAllBytesAsync(Path.Combine(root, "images", "a.png"), [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "заметка");
        await File.WriteAllTextAsync(Path.Combine(root, "docs", "readme.md"), "# привет");
        return root;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    // ── Без папки ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Без_папки_Личного_список_дерево_и_поиск_пусты_папка_не_создаётся()
    {
        var chat = await Chat();

        foreach (var tail in new[] { "", "/tree", "/search?q=a" })
        {
            var resp = await _owner.GetAsync(Url(chat, tail));
            resp.StatusCode.Should().Be(HttpStatusCode.OK, tail);
            (await Json(resp)).GetArrayLength().Should().Be(0, tail);
        }
        (await _owner.GetAsync(Url(chat, "?path=images"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _owner.GetAsync(Url(chat, "/content?" + Q("a.txt")))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _owner.GetAsync(Url(chat, "/stream?" + Q("a.txt")))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _owner.DeleteAsync(Url(chat, "?" + Q("a.txt")))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _owner.PostAsJsonAsync(Url(chat, "/rename"), new { oldPath = "a", newPath = "b" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        Directory.Exists(_host.Personal.RootOf(_ownerId)).Should().BeFalse();
    }

    [Fact]
    public async Task Недопустимый_путь_mkdir_не_создаёт_папку_Личного_а_допустимый_создаёт()
    {
        var chat = await Chat();

        (await _owner.PostAsJsonAsync(Url(chat, "/mkdir"), new { path = "../x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Directory.Exists(_host.Personal.RootOf(_ownerId)).Should().BeFalse();

        (await _owner.PostAsJsonAsync(Url(chat, "/mkdir"), new { path = "images/new" })).StatusCode.Should().Be(HttpStatusCode.OK);
        Directory.Exists(Path.Combine(_host.Personal.RootOf(_ownerId), "images", "new")).Should().BeTrue();
    }

    // ── Чтение ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Владелец_видит_дерево_читает_предпросмотр_поток_и_скачивание()
    {
        await Seeded();
        var chat = await Chat();

        var list = await Json(await _owner.GetAsync(Url(chat)));
        list.EnumerateArray().Select(e => e.GetProperty("name").GetString()).Should().Contain(["images", "docs", "notes.txt"]);

        var sub = await Json(await _owner.GetAsync(Url(chat, "?path=images")));
        sub.EnumerateArray().Select(e => e.GetProperty("name").GetString()).Should().Equal("a.png");

        var tree = await Json(await _owner.GetAsync(Url(chat, "/tree")));
        tree.EnumerateArray().Select(e => e.GetProperty("path").GetString()).Should().Contain("docs/readme.md");

        var content = await Json(await _owner.GetAsync(Url(chat, "/content?" + Q("docs/readme.md"))));
        content.GetProperty("content").GetString().Should().Be("# привет");

        var stream = await _owner.GetAsync(Url(chat, "/stream?" + Q("images/a.png")));
        stream.StatusCode.Should().Be(HttpStatusCode.OK);
        stream.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await stream.Content.ReadAsByteArrayAsync()).Length.Should().Be(7);

        var download = await _owner.GetAsync(Url(chat, "/download?" + Q("notes.txt")));
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentDisposition!.FileName.Should().Be("notes.txt");

        (await _owner.GetAsync(Url(chat, "/content?" + Q("нет.txt")))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _owner.GetAsync(Url(chat, "/download?" + Q("docs")))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Доступ ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Чужой_пользователь_и_администратор_получают_404_на_каждой_ручке_и_ничего_не_меняют()
    {
        var root = await Seeded();
        var chat = await Chat();
        var admin = _host.CreateAuthenticatedClient();
        var stranger = _host.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        // Чат второго пользователя глазами администратора; чат администратора — глазами второго
        var adminChat = await Chat(IdOf(TestWebApplicationFactory.TestUsername));

        foreach (var (client, id) in new[] { (admin, chat), (stranger, adminChat) })
        {
            foreach (var tail in new[] { "", "/tree", "/search?q=a", "/content?" + Q("notes.txt"), "/stream?" + Q("notes.txt"),
                         "/download?" + Q("notes.txt") })
                (await client.GetAsync(Url(id, tail))).StatusCode.Should().Be(HttpStatusCode.NotFound, tail);
            (await client.PostAsJsonAsync(Url(id, "/mkdir"), new { path = "x" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.PostAsJsonAsync(Url(id, "/create"), new { path = "x.txt" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.PostAsJsonAsync(Url(id, "/rename"), new { oldPath = "notes.txt", newPath = "n2.txt" }))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.DeleteAsync(Url(id, "?" + Q("notes.txt")))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        File.Exists(Path.Combine(root, "notes.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "x")).Should().BeFalse();
        (await _owner.GetAsync(Url("нет-такого-чата"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Выход из корня ───────────────────────────────────────────────────────────

    public static TheoryData<string> BadPaths() => new()
    {
        "../secret.txt", "..", "a/../../secret.txt", "images/../../secret.txt", "/etc/passwd",
        @"..\secret.txt", @"images\..\..\secret.txt", "a\0b",
    };

    [Theory]
    [MemberData(nameof(BadPaths))]
    public async Task Путь_за_пределами_корня_отказ_на_чтении_и_записи(string bad)
    {
        var root = await Seeded();
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "secret.txt");
        await File.WriteAllTextAsync(outside, "секрет");
        var chat = await Chat();

        (await _owner.GetAsync(Url(chat, "?" + Q(bad)))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.GetAsync(Url(chat, "/tree?" + Q(bad)))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.GetAsync(Url(chat, "/content?" + Q(bad)))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.GetAsync(Url(chat, "/stream?" + Q(bad)))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.GetAsync(Url(chat, "/download?" + Q(bad)))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.DeleteAsync(Url(chat, "?" + Q(bad)))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.PostAsJsonAsync(Url(chat, "/mkdir"), new { path = bad })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.PostAsJsonAsync(Url(chat, "/create"), new { path = bad })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.PostAsJsonAsync(Url(chat, "/rename"), new { oldPath = "notes.txt", newPath = bad }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.PostAsJsonAsync(Url(chat, "/rename"), new { oldPath = bad, newPath = "x.txt" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        File.ReadAllText(outside).Should().Be("секрет");
        File.Exists(Path.Combine(root, "notes.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Символическая_ссылка_наружу_не_читается_не_листается_не_удаляется_и_не_переименовывается()
    {
        var root = await Seeded();
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "секрет");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // символические ссылки недоступны на этом стенде
        }
        var chat = await Chat();

        (await _owner.GetAsync(Url(chat, "?path=link"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.GetAsync(Url(chat, "/content?" + Q("link/secret.txt")))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.GetAsync(Url(chat, "/stream?" + Q("link/secret.txt")))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.DeleteAsync(Url(chat, "?" + Q("link/secret.txt")))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.DeleteAsync(Url(chat, "?path=link"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.PostAsJsonAsync(Url(chat, "/rename"), new { oldPath = "link", newPath = "moved" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.PostAsJsonAsync(Url(chat, "/rename"), new { oldPath = "notes.txt", newPath = "link/n.txt" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        File.ReadAllText(Path.Combine(outside, "secret.txt")).Should().Be("секрет");
        Directory.GetFiles(outside).Length.Should().Be(1);
    }

    // ── Корень и правки внутри ───────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("a/..")]
    [InlineData("images/..")]
    public async Task Корень_Личного_нельзя_удалить_и_переименовать(string rootPath)
    {
        var root = await Seeded();
        var chat = await Chat();

        (await _owner.DeleteAsync(Url(chat, "?" + Q(rootPath)))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.PostAsJsonAsync(Url(chat, "/rename"), new { oldPath = rootPath, newPath = "other" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.PostAsJsonAsync(Url(chat, "/rename"), new { oldPath = "notes.txt", newPath = rootPath }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        Directory.Exists(root).Should().BeTrue();
        File.Exists(Path.Combine(root, "notes.txt")).Should().BeTrue();
        File.Exists(Path.Combine(root, "images", "a.png")).Should().BeTrue();
    }

    [Fact]
    public async Task Создание_переименование_перенос_и_удаление_внутри_корня()
    {
        var root = await Seeded();
        var chat = await Chat();

        (await _owner.PostAsJsonAsync(Url(chat, "/mkdir"), new { path = "docs/sub" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _owner.PostAsJsonAsync(Url(chat, "/create"), new { path = "docs/new.txt", content = "x" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _owner.PostAsJsonAsync(Url(chat, "/create"), new { path = "docs/new.txt" })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await _owner.PostAsJsonAsync(Url(chat, "/rename"), new { oldPath = "notes.txt", newPath = "docs/sub/n.txt" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        File.Exists(Path.Combine(root, "docs", "sub", "n.txt")).Should().BeTrue();
        File.Exists(Path.Combine(root, "notes.txt")).Should().BeFalse();

        // Цель занята — 409, источник цел; папка внутрь себя — отказ
        (await _owner.PostAsJsonAsync(Url(chat, "/rename"), new { oldPath = "docs/new.txt", newPath = "docs/readme.md" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        File.Exists(Path.Combine(root, "docs", "new.txt")).Should().BeTrue();
        (await _owner.PostAsJsonAsync(Url(chat, "/rename"), new { oldPath = "docs", newPath = "docs/sub/docs" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.PostAsJsonAsync(Url(chat, "/rename"), new { oldPath = "нет", newPath = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await _owner.DeleteAsync(Url(chat, "?" + Q("docs/new.txt")))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        File.Exists(Path.Combine(root, "docs", "new.txt")).Should().BeFalse();
        (await _owner.DeleteAsync(Url(chat, "?path=docs"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        Directory.Exists(Path.Combine(root, "docs")).Should().BeFalse();
        Directory.Exists(root).Should().BeTrue();
        (await _owner.DeleteAsync(Url(chat, "?" + Q("уже-нет")))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Нормализация пути, родитель цели и размер содержимого (низкие пункты ревью Ф2-9) ──

    [Fact]
    public async Task Пробелы_по_краям_пути_снимаются_до_диска_и_файла_с_пробелами_не_появляется()
    {
        var root = await Seeded();
        var chat = await Chat();

        // Запись идёт по тому же значению, что проверила SyntaxOk: на диске — без пробелов
        (await _owner.PostAsJsonAsync(Url(chat, "/mkdir"), new { path = " docs/sub " })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _owner.PostAsJsonAsync(Url(chat, "/create"), new { path = "  notes2.txt  ", content = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        Directory.Exists(Path.Combine(root, "docs", "sub")).Should().BeTrue();
        File.Exists(Path.Combine(root, "notes2.txt")).Should().BeTrue();
        File.Exists(Path.Combine(root, "  notes2.txt  ")).Should().BeFalse();
        Directory.Exists(Path.Combine(root, " docs ")).Should().BeFalse();

        // Чтение по пути с пробелами — тот же файл, и в списке его имя без пробелов
        (await _owner.GetAsync(Url(chat, "/content?" + Q("  notes2.txt  "))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(await _owner.GetAsync(Url(chat)))).EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).Should().Contain("notes2.txt");

        // Переименование и удаление — тоже по обрезанному: «  notes2.txt  » → docs/sub/n.txt
        (await _owner.PostAsJsonAsync(Url(chat, "/rename"), new { oldPath = " notes2.txt ", newPath = " docs/sub/n.txt " }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        File.Exists(Path.Combine(root, "docs", "sub", "n.txt")).Should().BeTrue();
        File.Exists(Path.Combine(root, "notes2.txt")).Should().BeFalse();
        (await _owner.DeleteAsync(Url(chat, "?" + Q(" docs/sub/n.txt ")))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        File.Exists(Path.Combine(root, "docs", "sub", "n.txt")).Should().BeFalse();
    }

    // Trim — по краям пути, не по сегментам: «a b.txt» — законное имя, оно пишется, читается,
    // перечисляется, переносится и удаляется как есть
    [Fact]
    public async Task Пробел_внутри_сегмента_имени_не_снимается_файл_живёт_под_ним()
    {
        var root = await Seeded();
        var chat = await Chat();
        (await _owner.PostAsJsonAsync(Url(chat, "/mkdir"), new { path = "docs/sub" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _owner.PostAsJsonAsync(Url(chat, "/create"), new { path = "a b.txt", content = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "имя с пробелом внутри — законное");
        File.Exists(Path.Combine(root, "a b.txt")).Should().BeTrue();
        File.Exists(Path.Combine(root, "ab.txt")).Should().BeFalse("пробелы внутри имени не вырезаются");

        (await _owner.GetAsync(Url(chat, "/content?" + Q("a b.txt")))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(await _owner.GetAsync(Url(chat)))).EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).Should().Contain("a b.txt");

        (await _owner.PostAsJsonAsync(Url(chat, "/rename"), new { oldPath = "a b.txt", newPath = "docs/sub/a b.txt" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        File.Exists(Path.Combine(root, "docs", "sub", "a b.txt")).Should().BeTrue();
        File.Exists(Path.Combine(root, "a b.txt")).Should().BeFalse();
        (await _owner.GetAsync(Url(chat, "/content?" + Q("docs/sub/a b.txt")))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _owner.DeleteAsync(Url(chat, "?" + Q("docs/sub/a b.txt")))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        File.Exists(Path.Combine(root, "docs", "sub", "a b.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Переименование_в_несуществующую_папку_даёт_404_и_источник_остаётся_целым()
    {
        var root = await Seeded();
        var chat = await Chat();

        // Родителя цели нет — нет и цели: 404 «нет такого пути», а не 409 «имя занято»
        (await _owner.PostAsJsonAsync(Url(chat, "/rename"), new { oldPath = "notes.txt", newPath = "нет-папки/notes.txt" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        File.Exists(Path.Combine(root, "notes.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "нет-папки")).Should().BeFalse();

        // Папку — тот же отказ, её содержимое цело
        (await _owner.PostAsJsonAsync(Url(chat, "/rename"), new { oldPath = "docs", newPath = "нет-папки/docs" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        File.Exists(Path.Combine(root, "docs", "readme.md")).Should().BeTrue();

        // Занятая цель — по-прежнему 409: новый отказ её не подменил
        (await _owner.PostAsJsonAsync(Url(chat, "/rename"), new { oldPath = "notes.txt", newPath = "docs/readme.md" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        File.Exists(Path.Combine(root, "notes.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Содержимое_на_пределе_создаётся_а_больше_предела_даёт_413_без_файла()
    {
        var root = await Seeded();
        var chat = await Chat();
        const int limit = 1024 * 1024; // MaxContentBytes контроллера

        // Ровно на пределе — ещё можно: интервал [0, limit]
        (await _owner.PostAsJsonAsync(Url(chat, "/create"), new { path = "at-limit.txt", content = new string('a', limit) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        new FileInfo(Path.Combine(root, "at-limit.txt")).Length.Should().Be(limit);

        // На байт больше — отказ с понятным кодом; кириллица считается её длиной в UTF-8
        var resp = await _owner.PostAsJsonAsync(Url(chat, "/create"), new { path = "over.txt", content = new string('а', limit) });
        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await Json(resp)).GetProperty("code").GetString().Should().Be("content_too_large");
        File.Exists(Path.Combine(root, "over.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Содержимое_больше_предела_отказано_до_создания_папки_Личного()
    {
        var chat = await Chat();

        (await _owner.PostAsJsonAsync(Url(chat, "/create"), new { path = "over.txt", content = new string('а', 1024 * 1024) }))
            .StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        Directory.Exists(_host.Personal.RootOf(_ownerId)).Should().BeFalse();
    }

    // ── Загрузка и сохранение по URL (Ф-6) ────────────────────────────────────────

    private static MultipartFormDataContent Form(byte[] bytes, string fileName)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", fileName);
        return form;
    }

    [Fact]
    public async Task Загрузка_пишет_в_корень_и_в_папку_перезапись_атомарна_и_temp_не_остаётся()
    {
        var root = await Seeded();
        var chat = await Chat();

        var resp = await _owner.PostAsync(Url(chat, "/upload"), Form([0x89, 0x50, 0x4E, 0x47], "кот.png"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Json(resp)).GetProperty("path").GetString().Should().Be("кот.png");
        (await File.ReadAllBytesAsync(Path.Combine(root, "кот.png"))).Should().Equal([0x89, 0x50, 0x4E, 0x47]);

        // Каталог из query: файл ложится в него, путь в ответе — полный
        (await _owner.PostAsync(Url(chat, "/upload?path=images"), Form([1, 2], "doc.bin")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await File.ReadAllBytesAsync(Path.Combine(root, "images", "doc.bin"))).Should().Equal([1, 2]);

        // Перезапись цели — атомарная подмена: содержимое новое, temp не пережил запись
        (await _owner.PostAsync(Url(chat, "/upload"), Form([9, 9], "кот.png"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await File.ReadAllBytesAsync(Path.Combine(root, "кот.png"))).Should().Equal([9, 9]);
        Directory.GetFiles(root).Select(Path.GetFileName).Should().NotContain(n => n!.EndsWith(".tmp"));

        // Загруженное читается как остальное: поток с типом по сигнатуре
        var stream = await _owner.GetAsync(Url(chat, "/stream?" + Q("кот.png")));
        stream.StatusCode.Should().Be(HttpStatusCode.OK);
        stream.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
    }

    [Fact]
    public async Task Загрузка_без_файла_с_пустым_именем_или_за_корнем_отказана_и_папки_Личного_не_создана()
    {
        var chat = await Chat();

        // Нет поля file
        (await _owner.PostAsync(Url(chat, "/upload"), new MultipartFormDataContent()))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Пустой файл
        (await _owner.PostAsync(Url(chat, "/upload"), Form([], "пусто.bin"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Имя — одни путь-сегменты: Path.GetFileName даёт пусто
        (await _owner.PostAsync(Url(chat, "/upload"), Form([1], "x/"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Каталог за корнем — отказ до любого эффекта, папка «Личного» не создана
        (await _owner.PostAsync(Url(chat, "/upload?path=../evil"), Form([1], "a.bin")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Directory.Exists(_host.Personal.RootOf(_ownerId)).Should().BeFalse();

        // Чужого и несуществующего чата нет, как на остальных ручках
        (await _owner.PostAsync(Url("нет-такого-чата", "/upload"), Form([1], "a.bin"))).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Загрузка_больше_100_МБ_даёт_413_и_файла_нет()
    {
        var root = await Seeded();
        var chat = await Chat();

        var resp = await _owner.PostAsync(Url(chat, "/upload"), Form(new byte[100 * 1024 * 1024 + 1], "big.bin"));
        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await Json(resp)).GetProperty("code").GetString().Should().Be("file_too_large");
        File.Exists(Path.Combine(root, "big.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task Сохранение_по_URL_пишет_в_медиальную_папку_а_вне_неё_400_до_создания_папки_Личного()
    {
        var chat = await Chat();

        // Путь проверяется до резолва адреса и до сети: вне медиальной папки — 400, папки нет
        foreach (var bad in new[] { "заметки.txt", "images", "docs/x.png", "images/../x.png", "" })
            (await _owner.PostAsJsonAsync(Url(chat, "/save-from-url"), new { url = "http://8.8.8.8/small.png", path = bad }))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest, bad);
        Directory.Exists(_host.Personal.RootOf(_ownerId)).Should().BeFalse();

        // Медиальная папка — можно; сама папка вида создаётся на записи
        var resp = await _owner.PostAsJsonAsync(Url(chat, "/save-from-url"), new { url = "http://8.8.8.8/small.png", path = "images/cat.png" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Json(resp)).GetProperty("path").GetString().Should().Be("images/cat.png");
        var root = _host.Personal.RootOf(_ownerId);
        (await File.ReadAllBytesAsync(Path.Combine(root, "images", "cat.png"))).Should().Equal([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);

        // audio/ и video/ — тоже можно, вложенность допустима
        (await _owner.PostAsJsonAsync(Url(chat, "/save-from-url"), new { url = "http://8.8.8.8/small.png", path = "audio/sound.wav" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await _owner.PostAsJsonAsync(Url(chat, "/save-from-url"), new { url = "http://8.8.8.8/small.png", path = "video/sub/clip.mp4" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        File.Exists(Path.Combine(root, "video", "sub", "clip.mp4")).Should().BeTrue();
    }

    [Fact]
    public async Task Сохранение_по_URL_отказывает_внутренний_адрес_и_плохой_URL_а_на_404_адреса_отвечает_502()
    {
        var root = await Seeded();
        var chat = await Chat();

        // Тот же SSRF-guard, что у проектной ручки: 127.0.0.1 — литерал, отказа хватает без DNS
        (await _owner.PostAsJsonAsync(Url(chat, "/save-from-url"), new { url = "http://127.0.0.1/small.png", path = "images/x.png" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.PostAsJsonAsync(Url(chat, "/save-from-url"), new { url = "ftp://8.8.8.8/x.png", path = "images/x.png" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _owner.PostAsJsonAsync(Url(chat, "/save-from-url"), new { url = "не URL", path = "images/x.png" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Адрес ответил 404 — 502 с причиной, файла нет
        var resp = await _owner.PostAsJsonAsync(Url(chat, "/save-from-url"), new { url = "http://8.8.8.8/gone", path = "images/gone.png" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await Json(resp)).GetProperty("code").GetString().Should().Be("fetch_failed");
        File.Exists(Path.Combine(root, "images", "gone.png")).Should().BeFalse();
    }

    [Fact]
    public async Task Сохранение_по_URL_больше_200_МБ_даёт_413_и_цель_остаётся_как_была()
    {
        var root = await Seeded();
        var chat = await Chat();
        // По целевому пути уже есть файл: отказ по заголовку не должен его тронуть
        await File.WriteAllBytesAsync(Path.Combine(root, "images", "cat.png"), [1, 2, 3]);

        var resp = await _owner.PostAsJsonAsync(Url(chat, "/save-from-url"), new { url = "http://8.8.8.8/huge", path = "images/cat.png" });
        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await Json(resp)).GetProperty("code").GetString().Should().Be("file_too_large");
        (await File.ReadAllBytesAsync(Path.Combine(root, "images", "cat.png"))).Should().Equal([1, 2, 3]);
    }

    [Fact]
    public async Task Чужой_получает_404_на_загрузку_и_сохранение_по_URL_и_ничего_не_создаёт()
    {
        var root = await Seeded();
        var chat = await Chat();
        var stranger = _host.CreateAuthenticatedClient(); // администратор чужого личного чата — чужой

        (await stranger.PostAsync(Url(chat, "/upload"), Form([1], "a.bin"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.PostAsJsonAsync(Url(chat, "/save-from-url"), new { url = "http://8.8.8.8/small.png", path = "images/x.png" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        File.Exists(Path.Combine(root, "a.bin")).Should().BeFalse();
        File.Exists(Path.Combine(root, "images", "x.png")).Should().BeFalse();
    }

    [Fact]
    public void Загрузка_и_сохранение_несут_пределы_тела_как_проектные_ручки()
    {
        // Предел держит транспорт (RequestSizeLimit), а не только код ручки: без него Kestrel
        // отказал бы разрешённый файл дефолтным 30 МБ (функциональный путь >100 МБ — в тесте выше)
        static long LimitOf(string methodName) =>
            ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)typeof(PersonalFilesController)
                .GetMethod(methodName)!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute), false).Single())
            .MaxRequestBodySize!.Value;

        LimitOf(nameof(PersonalFilesController.Upload)).Should().Be(100L * 1024 * 1024);
        LimitOf(nameof(PersonalFilesController.SaveFromUrl)).Should().Be(200L * 1024 * 1024);
    }

    // ── Проектные маршруты «Личное» не открывают ─────────────────────────────────

    [Fact]
    public async Task Проектные_файлы_по_id_Личного_и_по_имени_папки_отвечают_404()
    {
        await Seeded();
        var personalId = _host.Personal.GetPersonalOf(_ownerId)!.Id;

        foreach (var tail in new[] { "", "/tree", "/search?q=a", "/content?" + Q("notes.txt"), "/stream?" + Q("notes.txt") })
            (await _owner.GetAsync($"/api/projects/{personalId}/files{tail}")).StatusCode
                .Should().Be(HttpStatusCode.NotFound, tail);
        (await _owner.PostAsJsonAsync($"/api/projects/{personalId}/files/mkdir", new { path = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _owner.DeleteAsync($"/api/projects/{personalId}/files?" + Q("notes.txt")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        Directory.Exists(Path.Combine(_host.Personal.RootOf(_ownerId), "x")).Should().BeFalse();
        File.Exists(Path.Combine(_host.Personal.RootOf(_ownerId), "notes.txt")).Should().BeTrue();
    }
}
