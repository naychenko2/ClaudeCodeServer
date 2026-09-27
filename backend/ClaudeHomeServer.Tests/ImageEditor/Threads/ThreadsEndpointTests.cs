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
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// Нити картинок в обычном чате проекта (ADR-019 §1, §2) на собранном приложении: GET нитей и
// PUT фокуса с ревизией, смена фокуса — настройка (UpdatedAt и sessions.json не меняются),
// чужой чат и чужая нить неотличимы от несуществующих, удаление чата сносит нити через шину,
// запись модуля в ленту через IChatFeed — активность с живой парой module_record.
public class ThreadsEndpointTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _ownerId;
    private readonly string _projectId;

    public ThreadsEndpointTests()
    {
        CharacterEndpointsTests.EnableFlag(_factory, TestWebApplicationFactory.TestUsername);
        (_projectId, _) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.TestUsername);
        _ownerId = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private ImageThreadStore Store => _factory.Services.GetRequiredService<ImageThreadStore>();
    private string Threads(string sessionId, string? projectId = null) =>
        $"/api/projects/{projectId ?? _projectId}/image-editor/sessions/{sessionId}/threads";

    private string SessionsFile => Path.Combine(_factory.TempDir, "sessions.json");

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private async Task<Session> Chat() => await Sessions.CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Обычный чат");

    [Fact]
    public async Task Нити_обычного_чата_проекта_читаются()
    {
        var chat = await Chat();
        var (_, thread) = Store.Create(_ownerId, chat.Id, "images/hero.png", null, focus: true);

        var resp = await _client.GetAsync(Threads(chat.Id));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Json(resp);
        body.GetProperty("focus").GetString().Should().Be(thread.Id);
        body.GetProperty("revision").GetInt64().Should().Be(1);
        body.GetProperty("threads")[0].GetProperty("file").GetString().Should().Be("images/hero.png");
    }

    [Fact]
    public async Task Смена_фокуса_не_двигает_UpdatedAt_и_не_пишет_sessions_json()
    {
        var chat = await Chat();
        var (state, thread) = Store.Create(_ownerId, chat.Id, "images/hero.png", null, focus: false);
        Sessions.SaveSessions();
        var updatedBefore = Sessions.GetById(chat.Id)!.UpdatedAt;
        var fileBefore = File.ReadAllText(SessionsFile);

        var resp = await _client.PutAsJsonAsync($"{Threads(chat.Id)}/focus", new { threadId = thread.Id, revision = state.Revision });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Json(resp)).GetProperty("focus").GetString().Should().Be(thread.Id);
        Sessions.GetById(chat.Id)!.UpdatedAt.Should().Be(updatedBefore,
            "выбор картинки — настройка, а не активность: чат не поднимается и не выходит из архива");
        File.ReadAllText(SessionsFile).Should().Be(fileBefore, "фокус живёт в хранилище модуля, а не в Session");
        Store.Get(_ownerId, chat.Id).Focus.Should().Be(thread.Id);
    }

    [Fact]
    public async Task Фокус_со_старой_ревизией_409_с_актуальным_состоянием()
    {
        var chat = await Chat();
        var (_, a) = Store.Create(_ownerId, chat.Id, "a.png", null, focus: false);
        var (state, b) = Store.Create(_ownerId, chat.Id, "b.png", null, focus: false);

        var first = await _client.PutAsJsonAsync($"{Threads(chat.Id)}/focus", new { threadId = a.Id, revision = state.Revision });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var stale = await _client.PutAsJsonAsync($"{Threads(chat.Id)}/focus", new { threadId = b.Id, revision = state.Revision });

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await Json(stale);
        body.GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.RevisionConflict);
        body.GetProperty("state").GetProperty("focus").GetString().Should().Be(a.Id);
        Store.Get(_ownerId, chat.Id).Focus.Should().Be(a.Id, "устаревшая запись не затирает принятую");
    }

    [Fact]
    public async Task Снять_выбор()
    {
        var chat = await Chat();
        var (state, _) = Store.Create(_ownerId, chat.Id, "a.png", null, focus: true);

        var resp = await _client.PutAsJsonAsync($"{Threads(chat.Id)}/focus", new { threadId = (string?)null, revision = state.Revision });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        Store.Get(_ownerId, chat.Id).Focus.Should().BeNull();
    }

    [Fact]
    public async Task Чужой_и_несуществующий_чат_404_неотличимо()
    {
        var chat = await Chat();
        Store.Create(_ownerId, chat.Id, "a.png", null, focus: true);

        var second = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        CharacterEndpointsTests.EnableFlag(_factory, TestWebApplicationFactory.SecondUsername);
        var (otherProject, _) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.SecondUsername);
        var (myOtherProject, _) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.TestUsername);

        var responses = new[]
        {
            // Чужой чат через свой проект
            await second.GetAsync(Threads(chat.Id, otherProject)),
            await second.PutAsJsonAsync($"{Threads(chat.Id, otherProject)}/focus", new { threadId = (string?)null, revision = 1 }),
            // Свой чат, но через другой свой проект
            await _client.GetAsync(Threads(chat.Id, myOtherProject)),
            // Чата нет вовсе
            await _client.GetAsync(Threads("nope")),
            await _client.PutAsJsonAsync($"{Threads("nope")}/focus", new { threadId = (string?)null, revision = 0 }),
        };

        var bodies = new List<string>();
        foreach (var resp in responses)
        {
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
            bodies.Add(await resp.Content.ReadAsStringAsync());
        }
        bodies.Distinct().Should().ContainSingle("чужой чат неотличим от несуществующего");
        Store.Get(_ownerId, chat.Id).Focus.Should().NotBeNull("чужая запись не дошла до нитей");
    }

    [Fact]
    public async Task Чужой_проект_404()
    {
        var chat = await Chat();
        var second = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        CharacterEndpointsTests.EnableFlag(_factory, TestWebApplicationFactory.SecondUsername);

        var resp = await second.GetAsync(Threads(chat.Id));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Нить_другого_чата_404_как_несуществующая()
    {
        var chat = await Chat();
        var other = await Chat();
        var (_, foreign) = Store.Create(_ownerId, other.Id, "a.png", null, focus: false);
        var (state, _) = Store.Create(_ownerId, chat.Id, "b.png", null, focus: false);

        var foreignResp = await _client.PutAsJsonAsync($"{Threads(chat.Id)}/focus", new { threadId = foreign.Id, revision = state.Revision });
        var missingResp = await _client.PutAsJsonAsync($"{Threads(chat.Id)}/focus", new { threadId = "nope", revision = state.Revision });

        foreignResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missingResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreignResp.Content.ReadAsStringAsync()).Should().Be(await missingResp.Content.ReadAsStringAsync());
        (await Json(foreignResp)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.ThreadNotFound);
    }

    [Fact]
    public async Task Флаг_выключен_404()
    {
        var chat = await Chat();
        var (state, thread) = Store.Create(_ownerId, chat.Id, "a.png", null, focus: false);
        // Флаг включён по умолчанию — выключаем override'ом пользователя
        var users = _factory.Services.GetRequiredService<UserStore>();
        users.SetFeatureFlag(_ownerId, FeatureFlagKeys.ImageEditor, false).Should().BeTrue();

        var get = await _client.GetAsync(Threads(chat.Id));
        var put = await _client.PutAsJsonAsync($"{Threads(chat.Id)}/focus", new { threadId = thread.Id, revision = state.Revision });

        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        put.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Store.Get(_ownerId, chat.Id).Focus.Should().BeNull();
    }

    [Fact]
    public async Task Удаление_чата_сносит_его_нити()
    {
        var chat = await Chat();
        Store.Create(_ownerId, chat.Id, "a.png", null, focus: true);
        File.Exists(Store.StatePath(_ownerId, chat.Id)).Should().BeTrue();

        await Sessions.DeleteAsync(chat.Id);

        File.Exists(Store.StatePath(_ownerId, chat.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Запись_модуля_через_IChatFeed_ложится_в_историю_и_двигает_UpdatedAt()
    {
        var chat = await Chat();
        var before = Sessions.GetById(chat.Id)!.UpdatedAt;
        await Task.Delay(20);
        var feed = _factory.Services.GetRequiredService<IChatFeed>();

        var ok = await feed.AppendRecordAsync(chat.Id, new StoredModuleRecord
        {
            Module = "imageeditor", RecordType = "image_thread",
            Data = JsonSerializer.SerializeToElement(new { threadId = "t1", stackId = "s1" }),
            Fallback = "Картинка: hero.png",
        });

        ok.Should().BeTrue();
        var record = (await Sessions.GetHistoryAsync(chat.Id)).OfType<StoredModuleRecord>().Should().ContainSingle().Subject;
        record.RecordType.Should().Be("image_thread");
        record.Data!.Value.GetProperty("stackId").GetString().Should().Be("s1");
        record.Timestamp.Should().NotBeNull("время ставит сервер, если модуль его не задал");
        Sessions.GetById(chat.Id)!.UpdatedAt.Should().BeAfter(before, "запись в ленту — активность");
        (await feed.AppendRecordAsync("nope", record)).Should().BeFalse();
    }
}
