using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services.ChatContext;

// Ручки контекста чата (ADR-023 §2.1) на собранном приложении: пять ручек и saved-files, 409 со
// свежим DTO, 404 на чужую сессию, засев из фокусов вертикалей без записи на диск, двойная запись
// фокуса и проекция фокуса в DTO вертикали, событие в хаб. Детская часть (Б20c, ADR-041 ред. 8,
// решение 78): ребёнку — только чтение контекста и сохранённых файлов своего урока, правки —
// 403 child_mode до тела ручки (метки нет, гейт И1).
[Trait("Category", "Host")]
public class ChatContextControllerTests : IClassFixture<ChatContextControllerTests.Hosts>
{
    // Хост на класс: каждый тест заводит свой проект в уникальном каталоге и свой чат, поэтому
    // состояние хоста между тестами не пересекается
    public sealed class Hosts : IDisposable
    {
        public TestWebApplicationFactory Factory { get; } = new();

        public void Dispose() => Factory.Dispose();
    }

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _ownerId;
    private readonly string _projectId;
    private readonly string _projectRoot;

    public ChatContextControllerTests(Hosts hosts)
    {
        _factory = hosts.Factory;
        var users = _factory.Services.GetRequiredService<UserStore>();
        _ownerId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        _projectRoot = Path.Combine(_factory.TempDir, "ctx_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectRoot);
        _projectId = _factory.Services.GetRequiredService<ProjectManager>()
            .Create("ctx", _projectRoot, _ownerId, TestWebApplicationFactory.TestUsername).Id;
        _client = _factory.CreateAuthenticatedClient();
    }

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private ImageThreadStore Images => _factory.Services.GetRequiredService<ImageThreadStore>();
    private AudioThreadStore Audios => _factory.Services.GetRequiredService<AudioThreadStore>();

    private Task<Session> Chat() => Sessions.CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Чат");

    private static string Ctx(Session chat) => $"/api/chats/{chat.Id}/context";
    private string ImageThreads(Session chat) => $"/api/projects/{_projectId}/image-editor/sessions/{chat.Id}/threads";

    private string StateFile(Session chat) => Path.Combine(_factory.TempDir, "chat-context", _ownerId, chat.Id + ".json");

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private string NewImageThread(Session chat, bool focus = true) =>
        Images.Create(_ownerId, chat.Id, null, "", focus).Thread.Id;

    private string NewAudioThread(Session chat) =>
        Audios.Open(_ownerId, chat.Id, null, "", null, null).Thread!.Id;

    [Fact]
    public async Task Пустой_чат_отдаёт_пустой_контекст_без_файла()
    {
        var chat = await Chat();

        var resp = await _client.GetAsync(Ctx(chat));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Json(resp);
        body.GetProperty("revision").GetInt64().Should().Be(0);
        body.GetProperty("primary").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("refs").GetArrayLength().Should().Be(0);
        File.Exists(StateFile(chat)).Should().BeFalse();
    }

    [Fact]
    public async Task Засев_берёт_картинку_и_ничего_не_пишет_на_диск()
    {
        var chat = await Chat();
        var imageId = NewImageThread(chat);
        NewAudioThread(chat);

        var body = await Json(await _client.GetAsync(Ctx(chat)));

        body.GetProperty("revision").GetInt64().Should().Be(0);
        var primary = body.GetProperty("primary");
        primary.GetProperty("kind").GetString().Should().Be("image", "картинка важнее звука");
        primary.GetProperty("ref").GetProperty("threadId").GetString().Should().Be(imageId);
        primary.GetProperty("label").GetString().Should().Be("Новая картинка · черновик");
        primary.GetProperty("missing").GetBoolean().Should().BeFalse();
        File.Exists(StateFile(chat)).Should().BeFalse("файл появляется на первой записи, а не на чтении");
    }

    [Fact]
    public async Task Засев_берёт_звук_когда_у_картинки_нет_фокуса()
    {
        var chat = await Chat();
        NewImageThread(chat, focus: false);
        var audioId = NewAudioThread(chat);

        var primary = (await Json(await _client.GetAsync(Ctx(chat)))).GetProperty("primary");

        primary.GetProperty("kind").GetString().Should().Be("audio");
        primary.GetProperty("ref").GetProperty("threadId").GetString().Should().Be(audioId);
    }

    [Fact]
    public async Task Первая_запись_создаёт_файл_поверх_засеянного()
    {
        var chat = await Chat();
        var imageId = NewImageThread(chat);
        File.WriteAllText(Path.Combine(_projectRoot, "notes.md"), "x");

        var resp = await _client.PostAsJsonAsync($"{Ctx(chat)}/refs", new { kind = "project-file", @ref = new { path = "notes.md" } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Json(resp);
        body.GetProperty("revision").GetInt64().Should().Be(1);
        body.GetProperty("primary").GetProperty("ref").GetProperty("threadId").GetString().Should().Be(imageId,
            "засеянный основной объект не теряется на первой записи референса");
        body.GetProperty("refs")[0].GetProperty("label").GetString().Should().Be("notes.md");
        File.Exists(StateFile(chat)).Should().BeTrue();
    }

    [Fact]
    public async Task Основной_объект_ставится_и_снимается_ручкой()
    {
        var chat = await Chat();
        var imageId = NewImageThread(chat, focus: false);

        var set = await _client.PutAsJsonAsync($"{Ctx(chat)}/primary", new { kind = "image", @ref = new { threadId = imageId }, revision = 0 });
        set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
        var setBody = await Json(set);
        setBody.GetProperty("revision").GetInt64().Should().Be(1);
        setBody.GetProperty("primary").GetProperty("by").GetString().Should().Be("human");

        var clear = await _client.PutAsJsonAsync($"{Ctx(chat)}/primary", new { kind = (string?)null, revision = 1 });
        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(clear)).GetProperty("primary").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Чужая_сессия_и_несуществующая_дают_404()
    {
        var chat = await Chat();
        var other = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        (await other.GetAsync(Ctx(chat))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.PutAsJsonAsync($"{Ctx(chat)}/primary", new { kind = (string?)null })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.DeleteAsync(Ctx(chat))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.GetAsync($"{Ctx(chat)}/saved-files")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/api/chats/net-takogo/context")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Чужая_ревизия_409_со_свежим_DTO()
    {
        var chat = await Chat();
        var imageId = NewImageThread(chat, focus: false);
        (await _client.PutAsJsonAsync($"{Ctx(chat)}/primary", new { kind = "image", @ref = new { threadId = imageId }, revision = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var stale = await _client.DeleteAsync($"{Ctx(chat)}?revision=0");

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await Json(stale);
        body.GetProperty("error").GetString().Should().Be("context_changed");
        body.GetProperty("context").GetProperty("revision").GetInt64().Should().Be(1);
        body.GetProperty("context").GetProperty("primary").GetProperty("kind").GetString().Should().Be("image");
    }

    [Fact]
    public async Task Отказы_записи_400_с_кодом()
    {
        var chat = await Chat();
        var imageId = NewImageThread(chat, focus: false);

        async Task<string> Error(HttpResponseMessage r)
        {
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest, await r.Content.ReadAsStringAsync());
            return (await Json(r)).GetProperty("error").GetString()!;
        }

        (await Error(await _client.PutAsJsonAsync($"{Ctx(chat)}/primary", new { kind = "hologram", @ref = new { id = "s" } })))
            .Should().Be("kind_unknown");
        (await Error(await _client.PutAsJsonAsync($"{Ctx(chat)}/primary", new { kind = "image", @ref = new { threadId = "чужая" } })))
            .Should().Be("ref_invalid");
        (await Error(await _client.PostAsJsonAsync($"{Ctx(chat)}/refs", new { kind = "project-file", @ref = new { path = "../escape.txt" } })))
            .Should().Be("ref_invalid");
        (await Error(await _client.PostAsJsonAsync($"{Ctx(chat)}/refs",
                new { kind = "image", @ref = new { threadId = imageId }, role = "style" })))
            .Should().Be("role_not_accepted");

        // Validate стоит ДО записи: ни один из отказов не создал файл и не поднял ревизию
        (await Json(await _client.GetAsync(Ctx(chat)))).GetProperty("revision").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task Персонаж_голос_и_файл_основными_не_бывают_400()
    {
        var chat = await Chat();
        File.WriteAllText(Path.Combine(_projectRoot, "a.md"), "x");

        var resp = await _client.PutAsJsonAsync($"{Ctx(chat)}/primary", new { kind = "project-file", @ref = new { path = "a.md" } });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Json(resp)).GetProperty("error").GetString().Should().Be("kind_not_primary");
        (await Json(await _client.GetAsync(Ctx(chat)))).GetProperty("revision").GetInt64().Should().Be(0);
    }

    // Роль спрашивается у владельца ОСНОВНОГО объекта: звук картинку с ролью style не принимает,
    // а картинка персонажа с ролью character — принимает
    [Fact]
    public async Task Роль_референса_проверяется_по_основному_объекту()
    {
        var chat = await Chat();
        var audioId = NewAudioThread(chat);
        var imageId = NewImageThread(chat, focus: false);

        (await _client.PutAsJsonAsync($"{Ctx(chat)}/primary", new { kind = "audio", @ref = new { threadId = audioId } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var refused = await _client.PostAsJsonAsync($"{Ctx(chat)}/refs",
            new { kind = "image", @ref = new { threadId = imageId }, role = "style" });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, await refused.Content.ReadAsStringAsync());
        (await Json(refused)).GetProperty("error").GetString().Should().Be("role_not_accepted");

        (await _client.PutAsJsonAsync($"{Ctx(chat)}/primary", new { kind = "image", @ref = new { threadId = imageId } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var form = new MultipartFormDataContent { { new StringContent("Аня"), "name" } };
        for (var i = 1; i <= 3; i++)
        {
            var photo = new ByteArrayContent(TestImages.Jpeg((byte)i));
            photo.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            form.Add(photo, "photos", $"IMG_{i}.jpg");
        }
        var created = await _client.PostAsync($"/api/projects/{_projectId}/image-editor/characters", form);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var slug = (await Json(created)).GetProperty("slug").GetString();
        var accepted = await _client.PostAsJsonAsync($"{Ctx(chat)}/refs",
            new { kind = "image-character", @ref = new { slug }, role = "character" });
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());

        // Файл проекта сам референсов не принимает, но роль «style» у основной картинки он получает
        File.WriteAllText(Path.Combine(_projectRoot, "s.png"), "x");
        var file = await _client.PostAsJsonAsync($"{Ctx(chat)}/refs",
            new { kind = "project-file", @ref = new { path = "s.png" }, role = "style" });
        file.StatusCode.Should().Be(HttpStatusCode.OK, await file.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Референс_добавляется_и_убирается_а_очистка_сбрасывает_всё()
    {
        var chat = await Chat();
        File.WriteAllText(Path.Combine(_projectRoot, "a.md"), "x");

        var added = await Json(await _client.PostAsJsonAsync($"{Ctx(chat)}/refs", new { kind = "project-file", @ref = new { path = "a.md" } }));
        var itemId = added.GetProperty("refs")[0].GetProperty("id").GetString()!;
        added.GetProperty("refs")[0].GetProperty("usedBy").GetArrayLength().Should().Be(0);

        var removed = await _client.DeleteAsync($"{Ctx(chat)}/refs/{itemId}?revision=1");
        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(removed)).GetProperty("refs").GetArrayLength().Should().Be(0);

        await _client.PostAsJsonAsync($"{Ctx(chat)}/refs", new { kind = "project-file", @ref = new { path = "a.md" } });
        var cleared = await Json(await _client.DeleteAsync(Ctx(chat)));
        cleared.GetProperty("refs").GetArrayLength().Should().Be(0);
        cleared.GetProperty("primary").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Удалённый_файл_референса_приходит_с_missing()
    {
        var chat = await Chat();
        var path = Path.Combine(_projectRoot, "gone.md");
        File.WriteAllText(path, "x");
        await _client.PostAsJsonAsync($"{Ctx(chat)}/refs", new { kind = "project-file", @ref = new { path = "gone.md" } });
        File.Delete(path);

        var refs = (await Json(await _client.GetAsync(Ctx(chat)))).GetProperty("refs");

        refs[0].GetProperty("missing").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Saved_files_отвечает_списком()
    {
        var chat = await Chat();

        var resp = await _client.GetAsync($"{Ctx(chat)}/saved-files");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(resp)).ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Saved_files_склеивает_сохранённые_файлы_картинок_и_звуков_чата_по_времени()
    {
        var chat = await Chat();
        var other = await Chat();
        var image = NewImageThread(chat, focus: false);
        var audio = NewAudioThread(chat);
        Images.MoveToFile(_ownerId, chat.Id, image, "images/hero.png");
        Audios.MoveToFile(_ownerId, chat.Id, audio, "audio/intro.mp3");
        var foreign = NewImageThread(other, focus: false);
        Images.MoveToFile(_ownerId, other.Id, foreign, "images/other.png");

        var resp = await _client.GetAsync($"{Ctx(chat)}/saved-files");

        var items = (await Json(resp)).EnumerateArray().ToList();
        items.Select(i => (i.GetProperty("path").GetString(), i.GetProperty("threadKind").GetString()))
            .Should().Equal(("images/hero.png", "image"), ("audio/intro.mp3", "audio"));
        foreach (var item in items) item.TryGetProperty("savedAt", out _).Should().BeTrue("контракт ручки: {path, threadKind, savedAt}");
    }

    [Fact]
    public async Task Локальный_проект_читается_пустым_а_запись_отказывает_400()
    {
        var local = _factory.Services.GetRequiredService<ProjectManager>()
            .CreateLocal("local", "/home/dev/proj", _ownerId, "dev-1");
        var chat = await Sessions.CreateAsync(local.Id, ClaudeMode.AcceptEdits, name: "Локальный");

        var get = await _client.GetAsync(Ctx(chat));
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(get)).GetProperty("revision").GetInt64().Should().Be(0);

        var put = await _client.PostAsJsonAsync($"{Ctx(chat)}/refs", new { kind = "project-file", @ref = new { path = "a.md" } });
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(put)).GetProperty("error").GetString().Should().Be("project_local_unsupported");
    }

    // ── Двойная запись фокуса ─────────────────────────────────────────────────

    [Fact]
    public async Task Выбор_картинки_в_старой_полосе_виден_в_контексте()
    {
        var chat = await Chat();
        var imageId = NewImageThread(chat, focus: false);
        var state = Images.Get(_ownerId, chat.Id);

        var resp = await _client.PutAsJsonAsync($"{ImageThreads(chat)}/focus", new { threadId = imageId, revision = state.Revision });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        var primary = (await Json(await _client.GetAsync(Ctx(chat)))).GetProperty("primary");
        primary.GetProperty("kind").GetString().Should().Be("image");
        primary.GetProperty("ref").GetProperty("threadId").GetString().Should().Be(imageId);
        File.Exists(StateFile(chat)).Should().BeTrue("смена фокуса записана в стор");
    }

    [Fact]
    public async Task Фокус_в_DTO_нитей_берётся_из_контекста()
    {
        var chat = await Chat();
        var imageId = NewImageThread(chat);
        var audioId = NewAudioThread(chat);

        // Собственное поле картинки хранит фокус, а основным в контексте выбран звук
        (await _client.PutAsJsonAsync($"{Ctx(chat)}/primary", new { kind = "audio", @ref = new { threadId = audioId } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        Images.Get(_ownerId, chat.Id).Focus.Should().Be(imageId);
        (await Json(await _client.GetAsync(ImageThreads(chat)))).GetProperty("focus").ValueKind.Should().Be(JsonValueKind.Null,
            "фокус картинки — проекция: основной объект не картинка");

        (await _client.PutAsJsonAsync($"{Ctx(chat)}/primary", new { kind = "image", @ref = new { threadId = imageId } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(await _client.GetAsync(ImageThreads(chat)))).GetProperty("focus").GetString().Should().Be(imageId);

        (await _client.PutAsJsonAsync($"{Ctx(chat)}/primary", new { kind = "audio", @ref = new { threadId = audioId } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(await _client.GetAsync(ImageThreads(chat)))).GetProperty("focus").ValueKind.Should().Be(JsonValueKind.Null,
            "основной объект снова звук — проекция фокуса картинки пуста");
    }

    [Fact]
    public async Task Удаление_нити_убирает_её_из_контекста()
    {
        var chat = await Chat();
        var imageId = NewImageThread(chat, focus: false);
        var state = Images.Get(_ownerId, chat.Id);
        await _client.PutAsJsonAsync($"{ImageThreads(chat)}/focus", new { threadId = imageId, revision = state.Revision });
        (await Json(await _client.GetAsync(Ctx(chat)))).GetProperty("primary").ValueKind.Should().Be(JsonValueKind.Object);
        var revision = Images.Get(_ownerId, chat.Id).Revision;

        var del = await _client.DeleteAsync($"{ImageThreads(chat)}/{imageId}?revision={revision}");
        del.StatusCode.Should().Be(HttpStatusCode.OK, await del.Content.ReadAsStringAsync());

        (await Json(await _client.GetAsync(Ctx(chat)))).GetProperty("primary").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Запись_контекста_долетает_событием_в_хаб()
    {
        var chat = await Chat();
        var imageId = NewImageThread(chat, focus: false);
        var token = _factory.GetToken(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword);
        await using var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/session"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        conn.HandshakeTimeout = TimeSpan.FromSeconds(60);
        var got = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<JsonElement>("message", m =>
        {
            if (m.TryGetProperty("type", out var t) && t.GetString() == "chat_context_changed"
                && m.TryGetProperty("sessionId", out var s) && s.GetString() == chat.Id)
                got.TrySetResult(m);
        });
        await conn.StartAsync();
        // Чат проектный: рассылка идёт в группу проекта (ToProjectOrOwner), а не в личную
        await conn.InvokeAsync("JoinProject", _projectId);

        (await _client.PutAsJsonAsync($"{Ctx(chat)}/primary", new { kind = "image", @ref = new { threadId = imageId } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(got.Task,
            "chat_context_changed должен долететь до группы проекта");
        var msg = await got.Task;
        // Сигнал с ревизией без карточек (решение CONSULT-CONTEXT-SUBJECT): на проводе ровно три поля —
        // ни путей, ни Missing, ни Ref, приватность через рассылку не утекает.
        msg.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(new[] { "type", "sessionId", "revision" });
        msg.GetProperty("revision").GetInt64().Should().Be(1);
    }

    // ── Ребёнку: только чтение контекста своего урока (Б20c, ADR-041 ред. 8, решение 78) ──────
    // Видно: основной объект, «чем», референсы, сохранённые файлы — своего ChildSpace-чата.
    // Нельзя: PUT основного объекта, POST/DELETE референсов и сброс — [ChildAllowed] на них нет,
    // их отсекает гейт И1 до тела ручки (403 child_mode). Чужой чат, свой без ChildSpace и
    // несуществующий — тот же 404, что для взрослого: существование чата не раскрываем.

    private const string KidPassword = "b20c-kid-pw-123456";

    // Ребёнок без профиля на момент создания (обычный пользователь): CreateChatAsync стартует
    // сессию, а детская сверка хода отказывает ребёнку «старый урок» вне проекта (B9a) — фикстура
    // в неё не входит (образец ClearChild в ChildChatReadTests). Профиль ставит SetChild.
    private (string Id, string Username) Kid(string tag)
    {
        var user = _factory.Services.GetRequiredService<UserStore>().Add($"b20c-kid-{tag}", KidPassword, "user");
        return (user.Id, user.Username);
    }

    // Валидный профиль (тот же снимок, что SetChild в ChildModeEndpointGuardTests): живая
    // IChildModePolicy читает его по стору — токен проходит гейт И1, а детская проверка
    // Scope видит ребёнка.
    private void SetChild(string kidId) =>
        _factory.Services.GetRequiredService<UserStore>().GetById(kidId)!.ChildMode = new ChildProfile
        {
            Enabled = true,
            TutorPersonaId = "tutor",
            ProviderKey = "key",
            Model = "sonnet",
        };

    private HttpClient KidClient(string username) => _factory.CreateAuthenticatedClient(username, KidPassword);

    // Свой детский чат (ChildSpace проставляется напрямую — создание урока штатным POST относится
    // к Б9b и здесь не нужен). Профиль ребёнка ставится ПОСЛЕ создания: детская сверка хода
    // не задевает тестовую фикстуру (образец ChildChatReadTests).
    private async Task<Session> KidChatAsync(string kidId, string? childSpace)
    {
        var chat = await Sessions.CreateChatAsync(kidId, ClaudeMode.Auto, name: "Урок");
        chat.ChildSpace = childSpace;
        Sessions.SaveSessions();
        return chat;
    }

    // Проект-предмета ребёнка и живой урок в нём — форма Б9b: маркер ChildSubjectId при создании
    // проекта (то же поле, что пишет провижер предмета, Б8b) + чат с ChildSpace/ChildSubject
    // внутри него (та же фабрика, что у POST …/spaces/lessons/chat). Штатный POST здесь не идёт:
    // он требует провижна с домом контейнера и живой развёртки модели — это фикстура
    // ChildChatReadTests.NewSubjectChildAsync, для границы Scope достаточно формы урока.
    // Профиль — ПОСЛЕ создания (тот же приём, что KidChatAsync).
    private async Task<(Project Project, Session Lesson)> KidSubjectLessonAsync(string kidId, string kidName)
    {
        var root = Path.Combine(_factory.TempDir, "subj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var project = _factory.Services.GetRequiredService<ProjectManager>()
            .Create("russian", root, kidId, kidName, childSubjectId: "russian");
        var lesson = await Sessions.CreateAsync(project.Id, ClaudeMode.Auto, name: "Урок в предмете",
            childSpace: ChildSpaces.Lessons, childSubject: "russian");
        return (project, lesson);
    }

    [Fact]
    public async Task РебёнокЧитаетКонтекстИФайлыСвоегоУрока_200()
    {
        var (kidId, kidName) = Kid("read");
        var chat = await KidChatAsync(kidId, ChildSpaces.Lessons);
        SetChild(kidId);
        using var kid = KidClient(kidName);

        var resp = await kid.GetAsync(Ctx(chat));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Json(resp);
        body.GetProperty("revision").GetInt64().Should().Be(0);
        body.GetProperty("primary").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("refs").GetArrayLength().Should().Be(0);

        var files = await kid.GetAsync($"{Ctx(chat)}/saved-files");
        files.StatusCode.Should().Be(HttpStatusCode.OK, "сохранённые файлы своего урока — открыты (решение 78)");
        (await Json(files)).ValueKind.Should().Be(JsonValueKind.Array);
    }

    // Живой урок в проекте-предмете (Б9b, хвост ревью R-Б20c п.1): граница — ChildSpace,
    // ProjectId в ней не участвует (тот же смысл, что у ChatsController, B9d). Мутация
    // «граница по ProjectId» («ребёнку только чаты вне проекта») красит этот кейс, а чтение
    // беспроектного урока под ней остаётся зелёным — разрыв закрывает именно урок в проекте.
    [Fact]
    public async Task РебёнокЧитаетКонтекстУрокаВПроектеПредмета_200()
    {
        var (kidId, kidName) = Kid("subject");
        var (project, lesson) = await KidSubjectLessonAsync(kidId, kidName);
        SetChild(kidId);
        using var kid = KidClient(kidName);

        // Sanity: урок — чат проекта-предмета этого ребёнка (форма Б9b), а не чат вне проекта
        lesson.ProjectId.Should().Be(project.Id);
        lesson.ChildSpace.Should().Be(ChildSpaces.Lessons);

        var resp = await kid.GetAsync(Ctx(lesson));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "урок в проекте-предмете — свой ChildSpace-чат, 200");
        var body = await Json(resp);
        body.GetProperty("revision").GetInt64().Should().Be(0);
        body.GetProperty("primary").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("refs").GetArrayLength().Should().Be(0);
    }

    // Граница «сохранённых файлов» (Х-W4-53, хвост ревью Х-W4-51): след сохранения пишет вертикаль
    // медиа (ImageThreadStore.MoveToFile), а она ребёнку закрыта гейтом И1 — поэтому след кладёт
    // сама фикстура, ровно так, как он появился бы, если бы редактор ребёнку открыли. Ручка судит
    // эти пути ChildPathPolicy (та же точка правил, что у контекста урока): «Для репетитора/»,
    // служебное и всё вне папки предмета отсекаются; обычные файлы и «Материалы/» остаются, копия
    // судится по своему оригиналу (решение 91).
    [Fact]
    public async Task РебёнкуСохранённыеФайлыУрока_ТолькоРазрешённыеChildPathPolicy()
    {
        var (kidId, kidName) = Kid("saved");
        var (project, lesson) = await KidSubjectLessonAsync(kidId, kidName);
        SetChild(kidId);
        using var kid = KidClient(kidName);

        var outside = Path.Combine(_factory.TempDir, "outside.png"); // вне папки предмета — Deny
        foreach (var path in new[]
        {
            "Заметки/картинка.png",                        // обычный файл предмета — Allow
            "Материалы/лист.png",                         // «Материалы» — ребёнку чтение
            ".ccs-text/Заметки/картинка.png.txt",         // копия разрешённого оригинала — Allow
            "Для репетитора/ключ.png",                    // Hidden
            "CLAUDE.md",                                  // Hidden (защищённый сегмент)
            ".ccs-text/Для репетитора/ключ.png.txt",      // копия скрытого оригинала — Hidden
            outside,
        })
        {
            var thread = Images.Create(kidId, lesson.Id, null, "", false).Thread.Id;
            Images.MoveToFile(kidId, lesson.Id, thread, path);
        }

        var resp = await kid.GetAsync($"{Ctx(lesson)}/saved-files");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        var paths = (await Json(resp)).EnumerateArray().Select(i => i.GetProperty("path").GetString()).ToList();
        paths.Should().BeEquivalentTo(new[] { "Заметки/картинка.png", "Материалы/лист.png", ".ccs-text/Заметки/картинка.png.txt" },
            "за пределами ChildPathPolicy ребёнку ничего не отдаём (мутация «без фильтра» красит этот список до семи)");
    }

    [Fact]
    public async Task РебёнкуЧужойЧатСвойБезChildSpaceИНетТакого_404_НаОбеихGET()
    {
        var (kidId, kidName) = Kid("foreign");
        var foreign = await Chat(); // чат админа в его проекте — ребёнку не свой
        var notChild = await KidChatAsync(kidId, null); // свой, но не урок
        SetChild(kidId);
        using var kid = KidClient(kidName);

        foreach (var url in new[] { Ctx(foreign), $"{Ctx(foreign)}/saved-files" })
            (await kid.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound, $"{url}: чужой");
        foreach (var url in new[] { Ctx(notChild), $"{Ctx(notChild)}/saved-files" })
            (await kid.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound, $"{url}: свой без ChildSpace");
        (await kid.GetAsync("/api/chats/net-takogo/context"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "несуществующий неотличим");
    }

    [Fact]
    public async Task РебёнкуВсеЗаписиКонтекста_403_ChildMode()
    {
        var (kidId, kidName) = Kid("write");
        var chat = await KidChatAsync(kidId, ChildSpaces.Lessons);
        SetChild(kidId);
        using var kid = KidClient(kidName);

        var put = await kid.PutAsJsonAsync($"{Ctx(chat)}/primary", new { kind = (string?)null });
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden, "PUT основного объекта — метки нет");
        (await put.Content.ReadAsStringAsync()).Should().Contain("child_mode");

        var post = await kid.PostAsJsonAsync($"{Ctx(chat)}/refs", new { kind = "project-file", @ref = new { path = "a.md" } });
        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await post.Content.ReadAsStringAsync()).Should().Contain("child_mode");

        var del = await kid.DeleteAsync($"{Ctx(chat)}/refs/ci_x");
        del.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await del.Content.ReadAsStringAsync()).Should().Contain("child_mode");

        var clear = await kid.DeleteAsync(Ctx(chat));
        clear.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await clear.Content.ReadAsStringAsync()).Should().Contain("child_mode");

        // Ни одна запись не оставила следа: контекст своего урока пуст и без файла
        (await Json(await kid.GetAsync(Ctx(chat)))).GetProperty("revision").GetInt64().Should().Be(0);
        File.Exists(StateFile(chat)).Should().BeFalse();
    }

    // Х-W4-51: файлы проекта в контексте урока судит ChildPathPolicy. Скрытое ребёнку («Для репетитора/»,
    // скрытые сегменты) в ответ не попадает вовсе, «Материалы/» и обычные файлы видны обычным чипом.
    private static ContextItem FileRef(string path) => new("ci_" + Guid.NewGuid().ToString("N")[..12],
        ProjectFileContextKind.Kind, new System.Text.Json.Nodes.JsonObject { ["path"] = path }, null,
        ContextActor.Human, DateTime.UtcNow);

    private static readonly string[] ВсеФайлы =
        ["Для репетитора/конспект.pdf", "Материалы/лист.pdf", "урок.txt", ".скрытое/x.txt"];

    private void SeedFiles(string ownerId, Session chat, string root)
    {
        foreach (var rel in ВсеФайлы)
        {
            var full = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }
        var store = _factory.Services.GetRequiredService<IChatContextStore>();
        foreach (var rel in ВсеФайлы) store.AddRef(ownerId, chat.Id, FileRef(rel), null);
    }

    private static string[] Paths(JsonElement body) => body.GetProperty("refs").EnumerateArray()
        .Select(r => r.GetProperty("ref").GetProperty("path").GetString()!).ToArray();

    [Fact]
    public async Task РебёнкуСкрытыеЕгоГраницейФайлыВОтветеКонтекстаНеОтдаются()
    {
        var (kidId, kidName) = Kid("hidden-files");
        var (project, lesson) = await KidSubjectLessonAsync(kidId, kidName);
        SeedFiles(kidId, lesson, project.RootPath);
        SetChild(kidId);
        using var kid = KidClient(kidName);

        var resp = await kid.GetAsync(Ctx(lesson));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Json(resp);
        Paths(body).Should().BeEquivalentTo(["Материалы/лист.pdf", "урок.txt"],
            "«Для репетитора/» и скрытые сегменты ребёнку не видны, «Материалы/» и обычный файл — видны");
        body.GetProperty("refs").EnumerateArray().Should().OnlyContain(r => !r.GetProperty("missing").GetBoolean());
    }

    [Fact]
    public async Task ВзрослыйВидитВсеФайлыКонтекстаКакРаньше()
    {
        var chat = await Chat();
        SeedFiles(_ownerId, chat, _projectRoot);

        var body = await Json(await _client.GetAsync(Ctx(chat)));

        Paths(body).Should().BeEquivalentTo(ВсеФайлы, "граница — только для детей, взрослому ответ не режется");
    }

    [Fact]
    public async Task ВзрослыйЧитаетКонтекстСвоегоЧатаБезChildSpace_КакРаньше()
    {
        var chat = await Chat();

        var resp = await _client.GetAsync(Ctx(chat));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "детская проверка — только для детей");
        (await Json(resp)).GetProperty("revision").GetInt64().Should().Be(0);
    }
}
