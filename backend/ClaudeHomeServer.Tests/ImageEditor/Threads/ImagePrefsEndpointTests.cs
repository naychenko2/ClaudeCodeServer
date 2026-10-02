using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// Выбор человека в полосе «Картинки» проекта на собранном приложении: GET без файла — умолчания,
// PUT сохраняет и рассылает image_prefs_changed владельцу, чужой проект — 404 и чужой выбор не
// трогает, новая нить из ручки нитей наследует сохранённый выбор.
public class ImagePrefsEndpointTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly HttpClient _client;
    private readonly string _ownerId;
    private readonly string _projectId;
    private readonly string _root;

    public ImagePrefsEndpointTests()
    {
        _factory.ExtraServices = services => services.AddSingleton<ISessionBroadcaster>(_broadcaster);
        CharacterEndpointsTests.EnableFlag(_factory, TestWebApplicationFactory.TestUsername);
        (_projectId, _root) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.TestUsername);
        _ownerId = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private string Prefs(string? projectId = null) => $"/api/projects/{projectId ?? _projectId}/image-editor/prefs";

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private static object Body(string? provider = "fal", string? model = "m1", int count = 3, bool match = false,
        string? character = null) =>
        new { provider, model, count, matchSourceSize = match, characterSlug = character };

    [Fact]
    public async Task Без_сохранённого_выбора_умолчания()
    {
        var resp = await _client.GetAsync(Prefs());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(resp);
        body.GetProperty("provider").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("model").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("count").GetInt32().Should().Be(2);
        body.GetProperty("matchSourceSize").GetBoolean().Should().BeTrue();
        body.GetProperty("characterSlug").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task PUT_сохраняет_читается_GET_и_рассылается_владельцу()
    {
        var put = await _client.PutAsJsonAsync(Prefs(), Body());

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        (await Json(put)).GetProperty("provider").GetString().Should().Be("fal");
        var got = await Json(await _client.GetAsync(Prefs()));
        got.GetProperty("model").GetString().Should().Be("m1");
        got.GetProperty("count").GetInt32().Should().Be(3);
        got.GetProperty("matchSourceSize").GetBoolean().Should().BeFalse();

        var sent = _broadcaster.ToOwnerCalls.Select(c => c.Message).OfType<ImageProjectPrefsChangedMessage>()
            .Should().ContainSingle().Subject;
        _broadcaster.ToOwnerCalls.Single(c => c.Message == sent).OwnerId.Should().Be(_ownerId);
        sent.ProjectId.Should().Be(_projectId);
        sent.Prefs.Provider.Should().Be("fal");
    }

    [Fact]
    public async Task PUT_старого_фронта_без_режимов_не_затирает_выбор_режимов()
    {
        var withModes = new
        {
            provider = "fal", model = (string?)null, count = 2, matchSourceSize = true, characterSlug = (string?)null,
            create = new { provider = "local", model = "qwen-image-2.1", count = 1 },
            edit = new { provider = "higgsfield", model = (string?)null, count = 3, op = "removeBackground", editMode = "fast", ratio = "16:9" },
        };
        (await _client.PutAsJsonAsync(Prefs(), withModes)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Старый бандл: без режимов вовсе и с режимами, явно равными null (эхо прошлого GET)
        (await _client.PutAsJsonAsync(Prefs(), Body(provider: "higgsfield"))).StatusCode.Should().Be(HttpStatusCode.OK);
        var echoed = new { provider = "fal", model = "m1", count = 4, matchSourceSize = false, characterSlug = (string?)null,
            create = (object?)null, edit = (object?)null };
        (await _client.PutAsJsonAsync(Prefs(), echoed)).StatusCode.Should().Be(HttpStatusCode.OK);

        var got = await Json(await _client.GetAsync(Prefs()));
        got.GetProperty("count").GetInt32().Should().Be(4);
        got.GetProperty("create").GetProperty("provider").GetString().Should().Be("local");
        got.GetProperty("create").GetProperty("count").GetInt32().Should().Be(1);
        var edit = got.GetProperty("edit");
        edit.GetProperty("provider").GetString().Should().Be("higgsfield");
        edit.GetProperty("op").GetString().Should().Be("removeBackground");
        edit.GetProperty("editMode").GetString().Should().Be("fast");
        edit.GetProperty("ratio").GetString().Should().Be("16:9");
    }

    [Fact]
    public async Task Недопустимая_операция_правки_400_без_записи()
    {
        var resp = await _client.PutAsJsonAsync(Prefs(), new
        {
            provider = "fal", model = (string?)null, count = 2, matchSourceSize = true, characterSlug = (string?)null,
            edit = new { op = "generate" },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(await _client.GetAsync(Prefs()))).GetProperty("provider").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Неверное_число_вариантов_400_без_записи()
    {
        var resp = await _client.PutAsJsonAsync(Prefs(), Body(count: 9));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(await _client.GetAsync(Prefs()))).GetProperty("count").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Чужой_и_несуществующий_проект_404_и_чужой_выбор_цел()
    {
        await _client.PutAsJsonAsync(Prefs(), Body());
        var second = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        CharacterEndpointsTests.EnableFlag(_factory, TestWebApplicationFactory.SecondUsername);

        (await second.GetAsync(Prefs())).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await second.PutAsJsonAsync(Prefs(), Body(provider: "higgsfield"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync(Prefs("missing-project"))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await Json(await _client.GetAsync(Prefs()))).GetProperty("provider").GetString()
            .Should().Be("fal", "запись чужака до выбора владельца не дошла");
    }

    [Fact]
    public async Task Удалённый_персонаж_читается_как_null()
    {
        var dir = Path.Combine(_root, "characters", "anya");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "character.json"), """{"name":"Аня","slug":"anya","photos":[]}""");
        (await _client.PutAsJsonAsync(Prefs(), Body(character: "anya"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(await _client.GetAsync(Prefs()))).GetProperty("characterSlug").GetString().Should().Be("anya");

        Directory.Delete(dir, recursive: true);

        (await Json(await _client.GetAsync(Prefs()))).GetProperty("characterSlug").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Новая_нить_из_ручки_нитей_наследует_выбор_проекта()
    {
        await _client.PutAsJsonAsync(Prefs(), Body());
        var chat = await _factory.Services.GetRequiredService<SessionManager>()
            .CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Обычный чат");

        var resp = await _client.PostAsJsonAsync($"/api/projects/{_projectId}/image-editor/sessions/{chat.Id}/threads",
            new { draftFolder = "", revision = 0 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        _factory.Services.GetRequiredService<ImageThreadStore>().Get(_ownerId, chat.Id).Threads.Single().Settings
            .Should().Be(new ImageThreadSettings("fal", "m1", 3, false));
    }
}
