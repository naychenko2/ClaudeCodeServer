using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
            ExtraServices = services => services.AddSingleton<IPersonalProjects>(Personal);
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
