using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Mcp;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.ImageEditor.Chats;

// Чат картинки на сервере (ADR-018 §1, §3, шаг 10 плана v2): создание по первому сообщению,
// выбор собеседника (перенесено из тестов «Обсудить»), поиск по пути, перепривязка без
// сдвига UpdatedAt, изоляция владельцев и снимок холста в сообщении хаба.
public class ImageChatEndpointTests : IAsyncLifetime
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    private readonly TestWebApplicationFactory _factory = new();
    private readonly List<HubConnection> _connections = [];
    private readonly HttpClient _client;
    private readonly string _ownerId;
    private readonly string _projectId;
    private readonly string _root;

    public ImageChatEndpointTests()
    {
        CharacterEndpointsTests.EnableFlag(_factory, TestWebApplicationFactory.TestUsername);
        (_projectId, _root) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.TestUsername);
        _ownerId = Users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        _client = _factory.CreateAuthenticatedClient();
        WriteImage("images/hero.png");
        WriteImage("images/hero.v2.png");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
        _factory.Dispose();
    }

    private UserStore Users => _factory.Services.GetRequiredService<UserStore>();
    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private ProjectManager Projects => _factory.Services.GetRequiredService<ProjectManager>();
    private PersonaManager Personas => _factory.Services.GetRequiredService<PersonaManager>();
    private string Api(string? projectId = null) => $"/api/projects/{projectId ?? _projectId}/image-editor/chats";

    private void WriteImage(string rel, string? root = null)
    {
        var full = Path.Combine(root ?? _root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Png);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private async Task<Session> CreateChat(string sourcePath = "images/hero.png", string? personaId = null)
    {
        var resp = await _client.PostAsJsonAsync(Api(), new { sourcePath, personaId });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return Sessions.GetById((await Json(resp)).GetProperty("id").GetString()!)!;
    }

    private async Task<(string? Current, List<string> Continued)> Find(string path)
    {
        var resp = await _client.GetAsync($"{Api()}?path={Uri.EscapeDataString(path)}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Json(resp);
        var current = body.GetProperty("current");
        return (current.ValueKind == JsonValueKind.Null ? null : current.GetProperty("id").GetString(),
            body.GetProperty("continued").EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToList());
    }

    // ── Создание ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Создание_даёт_чат_картинки_с_явным_именем_без_сообщений()
    {
        var resp = await _client.PostAsJsonAsync(Api(), new { sourcePath = "images/hero.png" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        var body = await Json(resp);
        body.GetProperty("imageChat").GetProperty("currentPath").GetString().Should().Be("images/hero.png");

        var session = Sessions.GetById(body.GetProperty("id").GetString()!)!;
        session.ProjectId.Should().Be(_projectId);
        session.Name.Should().Be("hero.png · правка");
        session.NameLocked.Should().BeTrue("имя явное — авто-заголовок его не переписывает");
        session.ImageChat!.CurrentPath.Should().Be("images/hero.png");
        session.ImageChat.Lineage.Should().BeEmpty();
        session.AutoAllowTools.Should().BeEquivalentTo(
            "mcp__image-editor__image_generate", "mcp__image-editor__image_suggest_prompt");
        session.PersonaId.Should().NotBeNullOrEmpty("новый чат человека — только с персоной");
        (await Sessions.GetHistoryAsync(session.Id)).Should().BeEmpty("ручка создаёт чат и ничего не отправляет");
    }

    [Theory]
    [InlineData("images/nope.png")]
    [InlineData("../outside.png")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public async Task Создание_с_плохим_путём_400_и_чата_нет(string sourcePath)
    {
        var resp = await _client.PostAsJsonAsync(Api(), new { sourcePath });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Sessions.GetByProject(_projectId).Should().BeEmpty();
    }

    [Fact]
    public async Task Собеседник_руководитель_проекта_если_он_назначен()
    {
        var lead = Personas.Create(_ownerId, "Алекс", "Тимлид", null, null, null, null,
            PersonaScope.Project, _projectId, null, null, memoryEnabled: false);
        Projects.SetDefaultPersona(_projectId, lead.Id);

        (await CreateChat()).PersonaId.Should().Be(lead.Id);
    }

    [Fact]
    public async Task Руководитель_чужого_владельца_не_берётся()
    {
        var foreign = Personas.Create("someone-else", "Чужой", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false);
        Projects.SetDefaultPersona(_projectId, foreign.Id);

        (await CreateChat()).PersonaId.Should().NotBe(foreign.Id)
            .And.NotBeNullOrEmpty("вместо чужого руководителя — личный ассистент владельца");
    }

    [Fact]
    public async Task Собеседник_из_композера_важнее_руководителя()
    {
        var lead = Personas.Create(_ownerId, "Алекс", "Тимлид", null, null, null, null,
            PersonaScope.Project, _projectId, null, null, memoryEnabled: false);
        Projects.SetDefaultPersona(_projectId, lead.Id);
        var chosen = Personas.Create(_ownerId, "Майя", "Дизайнер", null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false);

        (await CreateChat(personaId: chosen.Id)).PersonaId.Should().Be(chosen.Id);
    }

    [Fact]
    public async Task Чужая_персона_из_композера_400_и_чата_нет()
    {
        var foreign = Personas.Create("someone-else", "Чужой", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false);

        var resp = await _client.PostAsJsonAsync(Api(), new { sourcePath = "images/hero.png", personaId = foreign.Id });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Sessions.GetByProject(_projectId).Should().BeEmpty();
    }

    // ── Поиск ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Поиск_current_свежий_неархивный_иначе_свежий_архивный()
    {
        var older = await CreateChat();
        var newer = await CreateChat();
        older.UpdatedAt = DateTime.UtcNow.AddHours(-2);
        newer.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        (await Find("images/hero.png")).Current.Should().Be(newer.Id);

        // Свежий в архиве — current отдаёт неархивный, хоть тот и старше
        Sessions.SetArchived(newer.Id, true, "user");
        (await Find("images/hero.png")).Current.Should().Be(older.Id);

        // Все в архиве — самый свежий архивный
        Sessions.SetArchived(older.Id, true, "user");
        (await Find("images/hero.png")).Current.Should().Be(newer.Id);
    }

    [Fact]
    public async Task Поиск_continued_по_Lineage_и_только_в_своём_проекте()
    {
        var moved = await CreateChat();
        Sessions.SetImageChatPath(moved.Id, "images/hero.v2.png");
        var plain = await _client.PostAsJsonAsync($"/api/projects/{_projectId}/sessions", new { mode = "auto" });
        plain.EnsureSuccessStatusCode();

        // Тот же относительный путь в другом проекте владельца не подмешивается
        var (otherProject, otherRoot) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.TestUsername);
        WriteImage("images/hero.png", otherRoot);
        (await _client.PostAsJsonAsync(Api(otherProject), new { sourcePath = "images/hero.png" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var old = await Find("images/hero.png");
        old.Current.Should().BeNull("чат ушёл на новую версию — сам на старый файл не перескакивает");
        old.Continued.Should().Equal(moved.Id);

        var fresh = await Find("images/hero.v2.png");
        fresh.Current.Should().Be(moved.Id);
        fresh.Continued.Should().BeEmpty();
    }

    [Fact]
    public async Task Поиск_по_пути_вне_проекта_400()
    {
        (await _client.GetAsync($"{Api()}?path={Uri.EscapeDataString("../x.png")}"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Перепривязка ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Смена_пути_не_двигает_UpdatedAt_и_ведёт_Lineage()
    {
        var chat = await CreateChat();
        var stamp = DateTime.UtcNow.AddDays(-1);
        chat.UpdatedAt = stamp;
        Sessions.SetArchived(chat.Id, true, "user");

        var resp = await _client.PutAsJsonAsync($"{Api()}/{chat.Id}/path", new { path = "images/hero.v2.png" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        chat.ImageChat!.CurrentPath.Should().Be("images/hero.v2.png");
        chat.ImageChat.Lineage.Should().Equal("images/hero.png");
        chat.UpdatedAt.Should().Be(stamp, "перепривязка — настройка, а не активность");
        chat.IsArchived.Should().BeTrue("настройка не выводит чат из архива");

        // Возврат к старой версии: она снова текущая и уходит из Lineage
        Sessions.SetImageChatPath(chat.Id, "images/hero.png")!.ImageChat!.Lineage.Should().Equal("images/hero.v2.png");
        chat.UpdatedAt.Should().Be(stamp);
    }

    [Fact]
    public async Task Чужой_и_несуществующий_чат_в_PUT_path_одинаковые_404()
    {
        var own = await CreateChat();
        // Чат второго владельца в его проекте
        var second = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        CharacterEndpointsTests.EnableFlag(_factory, TestWebApplicationFactory.SecondUsername);
        var (foreignProject, foreignRoot) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.SecondUsername);
        WriteImage("images/hero.png", foreignRoot);
        var foreignResp = await second.PostAsJsonAsync(Api(foreignProject), new { sourcePath = "images/hero.png" });
        foreignResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var foreign = Sessions.GetById((await Json(foreignResp)).GetProperty("id").GetString()!)!;
        // Обычный чат своего проекта — тоже не чат картинки
        var plainResp = await _client.PostAsJsonAsync($"/api/projects/{_projectId}/sessions", new { mode = "auto" });
        var plainId = (await Json(plainResp)).GetProperty("id").GetString()!;

        async Task<(HttpStatusCode, string)> Put(string sessionId)
        {
            var resp = await _client.PutAsJsonAsync($"{Api()}/{sessionId}/path", new { path = "images/hero.v2.png" });
            return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
        }

        var missing = await Put("no-such-session");
        missing.Item1.Should().Be(HttpStatusCode.NotFound);
        (await Put(foreign.Id)).Should().Be(missing, "чужой чат неотличим от несуществующего");
        (await Put(plainId)).Should().Be(missing, "обычный чат неотличим от несуществующего");
        foreign.ImageChat!.CurrentPath.Should().Be("images/hero.png", "чужой чат не тронут");

        // Свой чат по адресу чужого проекта — тоже 404: проект чужой
        (await _client.PutAsJsonAsync($"{Api(foreignProject)}/{own.Id}/path", new { path = "images/hero.v2.png" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        own.ImageChat!.CurrentPath.Should().Be("images/hero.png");
    }

    [Fact]
    public async Task PUT_path_через_выход_из_проекта_и_ссылку_наружу_400()
    {
        var chat = await CreateChat();
        var outside = Path.Combine(_factory.TempDir, "outside_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "leak.png"), Png);
        Directory.CreateSymbolicLink(Path.Combine(_root, "refs"), outside);

        foreach (var path in new[] { "../x.png", "refs/leak.png", "/etc/passwd", "images/nope.png" })
        {
            var resp = await _client.PutAsJsonAsync($"{Api()}/{chat.Id}/path", new { path });
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, path);
        }
        chat.ImageChat!.CurrentPath.Should().Be("images/hero.png");
        chat.ImageChat.Lineage.Should().BeEmpty();
    }

    [Fact]
    public async Task Флаг_выключен_ручки_чатов_404_и_чата_нет()
    {
        var chat = await CreateChat();
        Users.SetFeatureFlag(_ownerId, FeatureFlagKeys.ImageEditor, false).Should().BeTrue();

        var responses = new[]
        {
            await _client.PostAsJsonAsync(Api(), new { sourcePath = "images/hero.png" }),
            await _client.GetAsync($"{Api()}?path=images/hero.png"),
            await _client.PutAsJsonAsync($"{Api()}/{chat.Id}/path", new { path = "images/hero.v2.png" }),
        };

        responses.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.NotFound);
        Sessions.GetByProject(_projectId).Should().ContainSingle();
        chat.ImageChat!.CurrentPath.Should().Be("images/hero.png");
    }

    // ── Сообщение с пометкой снимка (хаб) ───────────────────────────────────────

    private async Task<HubConnection> ConnectAsync()
    {
        var token = _factory.GetToken(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/session"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        connection.HandshakeTimeout = TimeSpan.FromSeconds(60);
        connection.ServerTimeout = TimeSpan.FromSeconds(60);
        await connection.StartAsync();
        _connections.Add(connection);
        return connection;
    }

    [Fact]
    public async Task SendImageChatMessage_пишет_пометку_снимка_в_историю()
    {
        var chat = await CreateChat();
        var hub = await ConnectAsync();

        var outcome = await hub.InvokeAsync<string>("SendImageChatMessage", chat.Id, "убери провод",
            new List<string> { "images/hero.png" }, null, new StoredImageSnapshot("rev-1", false));

        outcome.Should().Be("started");
        var message = (await Sessions.GetHistoryAsync(chat.Id)).OfType<StoredUserMessage>().Should().ContainSingle().Subject;
        message.Text.Should().Be("убери провод");
        message.ImageSnapshot.Should().Be(new StoredImageSnapshot("rev-1", false));
        Sessions.GetById(chat.Id)!.Name.Should().Be("hero.png · правка", "первое сообщение не переименовывает чат");
    }

    [Fact]
    public async Task SendImageChatMessage_в_обычный_чат_отказ()
    {
        var plainResp = await _client.PostAsJsonAsync($"/api/projects/{_projectId}/sessions", new { mode = "auto" });
        var plainId = (await Json(plainResp)).GetProperty("id").GetString()!;
        var hub = await ConnectAsync();

        var act = () => hub.InvokeAsync<string>("SendImageChatMessage", plainId, "текст",
            new List<string>(), null, new StoredImageSnapshot("rev-1", true));

        (await act.Should().ThrowAsync<HubException>()).Which.Message.Should().Contain("не чат картинки");
        (await Sessions.GetHistoryAsync(plainId)).Should().BeEmpty();
    }

    // Имена в AutoAllowTools собраны из имени сервера и инструментов тулсета модуля:
    // переименование инструмента без правки списка тихо вернуло бы карточки разрешения
    [Fact]
    public void AutoAllowTools_совпадают_с_инструментами_тулсета()
    {
        ImageChatDefaults.AutoAllowTools.Should().BeEquivalentTo(
            $"mcp__{ImageEditorToolset.ServerName}__{ImageEditorToolset.ToolGenerate}",
            $"mcp__{ImageEditorToolset.ServerName}__{ImageEditorToolset.ToolSuggestPrompt}");
    }
}
