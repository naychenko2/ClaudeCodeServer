using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.AudioEditor;

// Ручки модуля «Звук» на собранном приложении (ADR-021 §2, §6): авторизация и изоляция владельцев
// (чужое неотличимо от несуществующего), ревизия нитей — 409, отдача файла версии с Range, сохранение
// в проект новыми файлами с версионированием имени и папкой стемов, отказ путям вне проекта, личный
// чат без сохранения в проект. Поставщик — фейк в памяти поверх настоящего исполнителя задач.
public class AudioEditorControllerTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _ownerId;
    private readonly string _projectId;
    private readonly string _projectRoot;

    // Байты результатов: по ним проверяются Range и содержимое сохранённых файлов
    private static readonly byte[] Mp3 = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
    private static readonly byte[] Abc = "X:1\nK:C\nCDEF|"u8.ToArray();
    private static readonly byte[] Vocals = [1, 1, 1, 1];
    private static readonly byte[] Drums = [2, 2, 2, 2];

    public AudioEditorControllerTests()
    {
        _factory.ExtraServices = services => services.AddSingleton<IAudioEngine>(new FakeEngine());
        var users = _factory.Services.GetRequiredService<UserStore>();
        _ownerId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        EnableFlag(TestWebApplicationFactory.TestUsername);
        (_projectId, _projectRoot) = CreateProject(TestWebApplicationFactory.TestUsername);
        File.WriteAllBytes(Path.Combine(_projectRoot, "intro.mp3"), [9, 9, 9]);
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    // Озвучка даёт звук и партитуру, разбор на дорожки — два стема; область не отвергает
    private sealed class FakeEngine : IAudioEngine
    {
        private static readonly AudioModelInfo Model = new("fake-model", "Фейк",
            new AudioCaps([AudioOp.Speak, AudioOp.Separate], ["ru"], [], [AudioOutputs.Audio], AudioLicenses.Mit, AudioPriceUnits.Free),
            new AudioPriceHint(0, AudioPriceUnits.Free, "run"));

        public string Key => "fake";
        public string Label => "Фейк";
        public string PriceUnit => AudioPriceUnits.Free;
        public bool Enabled => true;
        public IReadOnlyList<AudioModelInfo> Models => [Model];

        public Task<AudioResult> RunAsync(AudioRequest req, IProgress<AudioProgress> progress, CancellationToken ct)
        {
            IReadOnlyList<AudioFile> files = req.Op == AudioOp.Separate
                ? [new("stem:vocals", Vocals, "audio/mpeg", ".mp3"), new("stem:drums", Drums, "audio/mpeg", ".mp3")]
                : [new("main", Mp3, "audio/mpeg", ".mp3"), new("score", Abc, "text/plain", ".abc")];
            return Task.FromResult(new AudioResult(AudioOutcome.Ok, files, new AudioCost(0, AudioPriceUnits.Free), false, null, null));
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();

    private void EnableFlag(string username)
    {
        var users = _factory.Services.GetRequiredService<UserStore>();
        users.SetFeatureFlag(users.FindByUsername(username)!.Id, FeatureFlagKeys.AudioEditor, true).Should().BeTrue();
    }

    private (string Id, string Root) CreateProject(string username)
    {
        var user = _factory.Services.GetRequiredService<UserStore>().FindByUsername(username)!;
        var dir = Path.Combine(_factory.TempDir, "snd_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var id = _factory.Services.GetRequiredService<ProjectManager>()
            .Create("snd-" + Guid.NewGuid().ToString("N")[..6], dir, user.Id, username).Id;
        return (id, dir);
    }

    private string Root => $"/api/projects/{_projectId}/audio-editor";

    private string Chat(string sessionId) => $"{Root}/sessions/{sessionId}";

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private async Task<string> ProjectChat() => (await Sessions.CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Звук")).Id;

    private async Task<long> Revision(string chatUrl) =>
        (await Json(await _client.GetAsync($"{chatUrl}/threads"))).GetProperty("revision").GetInt64();

    private async Task<string> OpenFile(string chatUrl, string file = "intro.mp3")
    {
        var resp = await _client.PostAsJsonAsync($"{chatUrl}/threads", new { file, revision = await Revision(chatUrl) });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await Json(resp)).GetProperty("focus").GetString()!;
    }

    // Котировка → запуск в нить → ожидание итога; возвращает id новой версии
    private async Task<string> Generate(string scopeUrl, string chatId, string threadId, string mode = "voice", string op = "speak")
    {
        var quote = await _client.PostAsJsonAsync($"{scopeUrl}/quote",
            new { mode, operation = op, provider = "fake", model = "fake-model", count = 1 });
        quote.StatusCode.Should().Be(HttpStatusCode.OK, await quote.Content.ReadAsStringAsync());
        var quoteId = (await Json(quote)).GetProperty("quoteId").GetString()!;

        using var form = new MultipartFormDataContent
        {
            { new StringContent(quoteId), "quoteId" },
            { new StringContent(chatId), "sessionId" },
            { new StringContent(threadId), "threadId" },
            { new StringContent("Привет"), "text" },
        };
        var started = await _client.PostAsync($"{scopeUrl}/jobs", form);
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        var jobId = (await Json(started)).GetProperty("jobId").GetString()!;
        await _factory.Services.GetRequiredService<AudioEditJobService>().WhenDone(jobId);

        var job = await Json(await _client.GetAsync($"{scopeUrl}/jobs/{jobId}"));
        job.GetProperty("status").GetString().Should().BeEquivalentTo("completed", job.ToString());
        var store = _factory.Services.GetRequiredService<AudioJobThreads>().Store;
        return store.Get(_ownerId, chatId).Threads.Single(t => t.Id == threadId).Versions.Single(v => v.JobId == jobId).Id;
    }

    private async Task<(string ChatUrl, string ThreadId, string VersionId)> ProjectVersion(string mode = "voice", string op = "speak")
    {
        var chatId = await ProjectChat();
        var threadId = await OpenFile(Chat(chatId));
        var versionId = await Generate(Root, chatId, threadId, mode, op);
        return (Chat(chatId), threadId, versionId);
    }

    // ── Авторизация и изоляция ─────────────────────────────────────────────────

    [Fact]
    public async Task Без_токена_401()
    {
        var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync($"{Root}/catalog")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/audio-editor/chats/any/state")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Флаг_выключен_ручки_404()
    {
        var users = _factory.Services.GetRequiredService<UserStore>();
        users.SetFeatureFlag(_ownerId, FeatureFlagKeys.AudioEditor, false).Should().BeTrue();
        var chatId = await ProjectChat();

        var responses = new[]
        {
            await _client.GetAsync($"{Root}/catalog"),
            await _client.PostAsJsonAsync($"{Root}/quote", new { mode = "voice" }),
            await _client.GetAsync($"{Chat(chatId)}/state"),
            await _client.PostAsJsonAsync($"{Chat(chatId)}/threads", new { draftFolder = "", revision = 0 }),
        };

        responses.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Чужой_проект_чат_нить_и_файл_неотличимы_от_несуществующих()
    {
        var (chatUrl, threadId, versionId) = await ProjectVersion();
        var second = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        EnableFlag(TestWebApplicationFactory.SecondUsername);

        // Чужой проект: каталог, состояние, файл версии и сохранение
        (await second.GetAsync($"{Root}/catalog")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await second.GetAsync($"{chatUrl}/state")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await second.GetAsync($"{chatUrl}/threads/{threadId}/versions/{versionId}/files/main")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await second.PostAsJsonAsync($"{chatUrl}/threads/{threadId}/save", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Своя нить из другого чата того же проекта — 404 thread_not_found, а не её файлы
        var otherChat = Chat(await ProjectChat());
        var foreign = await _client.GetAsync($"{otherChat}/threads/{threadId}/versions/{versionId}/files/main");
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(foreign)).GetProperty("code").GetString().Should().Be(AudioEditErrorCodes.ThreadNotFound);
        var save = await _client.PostAsJsonAsync($"{otherChat}/threads/{threadId}/save", new { });
        (await Json(save)).GetProperty("code").GetString().Should().Be(AudioEditErrorCodes.ThreadNotFound);

        // Чат чужого проекта под своим проектом — chat_not_found
        var (otherProject, _) = CreateProject(TestWebApplicationFactory.SecondUsername);
        var strangerChat = await Sessions.CreateAsync(otherProject, ClaudeMode.AcceptEdits, name: "Чужой");
        var stranger = await _client.GetAsync($"{Chat(strangerChat.Id)}/state");
        stranger.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(stranger)).GetProperty("code").GetString().Should().Be(AudioEditErrorCodes.ChatNotFound);

        // Чужая задача
        var jobId = _factory.Services.GetRequiredService<AudioJobThreads>().Store.Get(_ownerId, chatUrl.Split('/')[^1])
            .Threads.Single().Versions.Single(v => v.Id == versionId).JobId!;
        var (secondProject, _) = CreateProject(TestWebApplicationFactory.SecondUsername);
        (await second.GetAsync($"/api/projects/{secondProject}/audio-editor/jobs/{jobId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Чужая_нить_в_запуске_404_до_исполнителя()
    {
        var chatA = await ProjectChat();
        var threadA = await OpenFile(Chat(chatA));
        var chatB = await ProjectChat();
        var quote = await Json(await _client.PostAsJsonAsync($"{Root}/quote",
            new { mode = "voice", operation = "speak", provider = "fake", model = "fake-model" }));

        using var form = new MultipartFormDataContent
        {
            { new StringContent(quote.GetProperty("quoteId").GetString()!), "quoteId" },
            { new StringContent(chatB), "sessionId" },
            { new StringContent(threadA), "threadId" },
        };
        var started = await _client.PostAsync($"{Root}/jobs", form);

        started.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(started)).GetProperty("code").GetString().Should().Be(AudioEditErrorCodes.ThreadNotFound);
    }

    // ── Нити и ревизия ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Устаревшая_ревизия_409_с_актуальным_состоянием()
    {
        var chatUrl = Chat(await ProjectChat());
        var threadId = await OpenFile(chatUrl);
        var actual = await Revision(chatUrl);

        var stale = await _client.PutAsJsonAsync($"{chatUrl}/threads/focus", new { threadId = (string?)null, revision = actual - 1 });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await Json(stale);
        body.GetProperty("code").GetString().Should().Be(AudioEditErrorCodes.RevisionConflict);
        body.GetProperty("state").GetProperty("revision").GetInt64().Should().Be(actual);
        body.GetProperty("state").GetProperty("focus").GetString().Should().Be(threadId, "устаревшая запись ничего не сняла");

        var settings = await _client.PutAsJsonAsync($"{chatUrl}/threads/{threadId}/settings",
            new { settings = new { mode = "voice", operation = "speak" }, revision = actual - 1 });
        settings.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var fresh = await _client.PutAsJsonAsync($"{chatUrl}/threads/focus", new { threadId = (string?)null, revision = actual });
        fresh.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(fresh)).GetProperty("revision").GetInt64().Should().Be(actual + 1);
    }

    [Fact]
    public async Task Состояние_несёт_нити_каталог_и_префы()
    {
        var chatUrl = Chat(await ProjectChat());
        await OpenFile(chatUrl);
        (await _client.PutAsJsonAsync($"{Root}/prefs/music", new { operation = "song", count = 2 })).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await _client.PutAsJsonAsync($"{Root}/prefs/music", new { count = 9 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var state = await Json(await _client.GetAsync($"{chatUrl}/state"));

        state.GetProperty("threads").GetProperty("threads").GetArrayLength().Should().Be(1);
        state.GetProperty("catalog").GetProperty("providers").EnumerateArray()
            .Should().Contain(p => p.GetProperty("key").GetString() == "fake" && p.GetProperty("available").GetBoolean());
        state.GetProperty("prefs").GetProperty("music").GetProperty("count").GetInt32().Should().Be(2);
    }

    // ── Отдача файла версии ────────────────────────────────────────────────────

    [Fact]
    public async Task Файл_версии_отдаётся_с_Range_и_Content_Type()
    {
        var (chatUrl, threadId, versionId) = await ProjectVersion();
        var url = $"{chatUrl}/threads/{threadId}/versions/{versionId}/files/main";

        var whole = await _client.GetAsync(url);
        whole.StatusCode.Should().Be(HttpStatusCode.OK);
        whole.Content.Headers.ContentType!.MediaType.Should().Be("audio/mpeg");
        whole.Headers.AcceptRanges.Should().Contain("bytes");
        (await whole.Content.ReadAsByteArrayAsync()).Should().Equal(Mp3);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(10, 19);
        var part = await _client.SendAsync(request);
        part.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        part.Content.Headers.ContentRange!.ToString().Should().Be($"bytes 10-19/{Mp3.Length}");
        (await part.Content.ReadAsByteArrayAsync()).Should().Equal(Mp3[10..20]);

        // Исходник — файл проекта; роли, которой нет у версии, — 404
        (await _client.GetAsync($"{chatUrl}/threads/{threadId}/versions/origin/files/main")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync($"{chatUrl}/threads/{threadId}/versions/{versionId}/files/midi")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    // ── Сохранение в проект ────────────────────────────────────────────────────

    [Fact]
    public async Task Следующая_версия_пишется_рядом_с_исходником_всеми_файлами_и_не_затирает()
    {
        var (chatUrl, threadId, versionId) = await ProjectVersion();

        var first = await _client.PostAsJsonAsync($"{chatUrl}/threads/{threadId}/save", new { versionId });
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var body = await Json(first);
        body.GetProperty("path").GetString().Should().Be("intro.v2.mp3");
        body.GetProperty("files").EnumerateArray().Select(f => f.GetString()).Should().BeEquivalentTo("intro.v2.mp3", "intro.v2.abc");
        File.ReadAllBytes(Path.Combine(_projectRoot, "intro.v2.mp3")).Should().Equal(Mp3);
        File.ReadAllBytes(Path.Combine(_projectRoot, "intro.v2.abc")).Should().Equal(Abc);

        var second = await Json(await _client.PostAsJsonAsync($"{chatUrl}/threads/{threadId}/save", new { versionId }));
        second.GetProperty("path").GetString().Should().Be("intro.v3.mp3", "занятое имя не перезаписывается — берётся следующий номер");
        File.ReadAllBytes(Path.Combine(_projectRoot, "intro.mp3")).Should().Equal([9, 9, 9], "исходник не тронут");
    }

    [Fact]
    public async Task Стемы_сохраняются_папкой()
    {
        var (chatUrl, threadId, versionId) = await ProjectVersion("process", "separate");

        var resp = await _client.PostAsJsonAsync($"{chatUrl}/threads/{threadId}/save", new { versionId });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Json(resp)).GetProperty("files").EnumerateArray().Select(f => f.GetString())
            .Should().BeEquivalentTo("intro.v2.stems/vocals.mp3", "intro.v2.stems/drums.mp3");
        File.ReadAllBytes(Path.Combine(_projectRoot, "intro.v2.stems", "vocals.mp3")).Should().Equal(Vocals);
        File.ReadAllBytes(Path.Combine(_projectRoot, "intro.v2.stems", "drums.mp3")).Should().Equal(Drums);
    }

    [Fact]
    public async Task Сохранить_как_на_занятое_имя_409_с_подсказкой()
    {
        var (chatUrl, threadId, versionId) = await ProjectVersion();
        File.WriteAllBytes(Path.Combine(_projectRoot, "theme.abc"), [7]);

        var taken = await _client.PostAsJsonAsync($"{chatUrl}/threads/{threadId}/save",
            new { versionId, mode = "as", fileName = "theme.mp3" });

        taken.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await Json(taken);
        body.GetProperty("code").GetString().Should().Be(AudioEditErrorCodes.NameTaken, "занят файл группы — партитура");
        body.GetProperty("suggestion").GetString().Should().Be("theme.v2.mp3");
        File.ReadAllBytes(Path.Combine(_projectRoot, "theme.abc")).Should().Equal([7]);
        File.Exists(Path.Combine(_projectRoot, "theme.mp3")).Should().BeFalse("группа пишется целиком или никак");

        var free = await _client.PostAsJsonAsync($"{chatUrl}/threads/{threadId}/save",
            new { versionId, mode = "as", fileName = "outro" });
        (await Json(free)).GetProperty("path").GetString().Should().Be("outro.mp3");
    }

    [Theory]
    [InlineData("../outside", null)]
    [InlineData("/etc", null)] // абсолютный путь: подменяется на существующую папку снаружи
    [InlineData(null, "../evil.mp3")]
    public async Task Путь_вне_проекта_отказ_до_записи(string? folder, string? fileName)
    {
        var (chatUrl, threadId, versionId) = await ProjectVersion();
        // Папка снаружи существует и доступна на запись: отказ обязан прийти от границы проекта,
        // а не от «папка не найдена»
        var outside = Path.Combine(_factory.TempDir, "outside");
        Directory.CreateDirectory(outside);
        var before = Directory.GetFileSystemEntries(_factory.TempDir).Length;

        var resp = await _client.PostAsJsonAsync($"{chatUrl}/threads/{threadId}/save",
            new { versionId, mode = "as", folder = folder?.Replace("/etc", outside), fileName = fileName ?? "x" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(resp)).GetProperty("code").GetString().Should().Be(AudioEditErrorCodes.InvalidRequest);
        Directory.GetFileSystemEntries(_factory.TempDir).Length.Should().Be(before, "вне проекта ничего не появилось");
        Directory.GetFileSystemEntries(outside).Should().BeEmpty("в папку снаружи ничего не записано");
    }

    [Fact]
    public async Task Путь_через_ссылку_и_вне_проекта_в_нити_и_запуске_отказ()
    {
        var chatUrl = Chat(await ProjectChat());
        var outside = Path.Combine(_factory.TempDir, "secret.mp3");
        File.WriteAllBytes(outside, [6]);
        try { File.CreateSymbolicLink(Path.Combine(_projectRoot, "link.mp3"), outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        foreach (var file in new[] { "../secret.mp3", outside, "link.mp3" })
        {
            var resp = await _client.PostAsJsonAsync($"{chatUrl}/threads", new { file, revision = await Revision(chatUrl) });
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, file);
        }

        var quote = await Json(await _client.PostAsJsonAsync($"{Root}/quote",
            new { mode = "voice", operation = "speak", provider = "fake", model = "fake-model" }));
        using var form = new MultipartFormDataContent
        {
            { new StringContent(quote.GetProperty("quoteId").GetString()!), "quoteId" },
            { new StringContent("../secret.mp3"), "referencePath" },
        };
        (await _client.PostAsync($"{Root}/jobs", form)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Личный чат ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Личный_чат_генерирует_и_отдаёт_файл_но_не_сохраняет_в_проект()
    {
        var chat = await Sessions.CreateChatAsync(_ownerId, ClaudeMode.AcceptEdits, name: "Личный");
        var personal = $"/api/audio-editor/chats/{chat.Id}";

        (await _client.PostAsJsonAsync($"{personal}/threads", new { file = "intro.mp3", revision = 0 })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "файлов проекта у личного чата нет");
        var opened = await _client.PostAsJsonAsync($"{personal}/threads", new { draftFolder = "", revision = 0 });
        opened.StatusCode.Should().Be(HttpStatusCode.OK, await opened.Content.ReadAsStringAsync());
        var threadId = (await Json(opened)).GetProperty("focus").GetString()!;

        var versionId = await Generate(personal, chat.Id, threadId);
        var download = await _client.GetAsync($"{personal}/threads/{threadId}/versions/{versionId}/files/main?download=true");
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        (await download.Content.ReadAsByteArrayAsync()).Should().Equal(Mp3);

        var save = await _client.PostAsJsonAsync($"{personal}/threads/{threadId}/save", new { versionId });
        save.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await save.Content.ReadAsStringAsync()).Should().BeEmpty("сохранения у личного чата нет по построению — 404 маршрутизации");

        var quote = await Json(await _client.PostAsJsonAsync($"{personal}/quote",
            new { mode = "voice", operation = "speak", provider = "fake", model = "fake-model" }));
        using var form = new MultipartFormDataContent
        {
            { new StringContent(quote.GetProperty("quoteId").GetString()!), "quoteId" },
            { new StringContent("intro.mp3"), "referencePath" },
        };
        (await _client.PostAsync($"{personal}/jobs", form)).StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "путь в проекте у личного чата отвергается до диска");
    }

    [Fact]
    public async Task Личная_ручка_не_открывает_чат_проекта_и_чужой_личный_чат()
    {
        var projectChat = await ProjectChat();
        (await _client.GetAsync($"/api/audio-editor/chats/{projectChat}/state")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var secondId = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
        var strangers = await Sessions.CreateChatAsync(secondId, ClaudeMode.AcceptEdits, name: "Чужой личный");
        (await _client.GetAsync($"/api/audio-editor/chats/{strangers.Id}/state")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
