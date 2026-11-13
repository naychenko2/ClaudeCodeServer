using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Prefs;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// Личный маршрут редактора api/image-editor/chats/{sessionId} на собранном приложении (разрез
// docs/research/image-editor-personal-chats-cut-2026-09.md): черновик → запуск → версии → байты шага,
// чужой личный чат и чат проекта неотличимы, у личного чата нет записи в проект и файлов проекта
// (персонажи — в папке «Личного», PersonalCharacterEndpointsTests).
[Trait("Category", "Host")]
public class PersonalChatEndpointTests : IClassFixture<PersonalChatEndpointTests.Hosts>
{
    /// <summary>
    /// Хост на весь класс (IClassFixture), а не на тест: подмена исполнителя задач и
    /// поставщика зарегистрирована один раз. Состояние фейка (счётчик запусков, журнал
    /// задач) поднимает заново конструктор класса перед каждым тестом. Личные prefs —
    /// единственное состояние хоста, ключенное владельцем, а не чатом: его файл сносит
    /// конструктор класса. Всё остальное (нити, черновики, версии) ключено парой
    /// (владелец, чат), а чат каждый тест создаёт свой.
    /// </summary>
    public sealed class Hosts : TestWebApplicationFactory
    {
        internal readonly PersonalJobs Jobs = new();

        public Hosts()
        {
            ExtraServices = services =>
            {
                services.AddSingleton<IImageEditJobs>(Jobs);
                services.AddSingleton<IImageEditor>(new FakeImageEditor("fal", models: FakeImageEditor.Model("m")));
            };
        }
    }

    private readonly TestWebApplicationFactory _factory;
    private readonly PersonalJobs _jobs;
    private readonly HttpClient _client;
    private readonly string _ownerId;
    private readonly string _projectId;

    public PersonalChatEndpointTests(Hosts hosts)
    {
        _factory = hosts;
        _jobs = hosts.Jobs;
        _jobs.Reset();
        (_projectId, _) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.TestUsername);
        _ownerId = OwnerOf(TestWebApplicationFactory.TestUsername);
        _client = _factory.CreateAuthenticatedClient();
        // Личная область ключена владельцем, а не чатом: чужой сохранённый выбор не должен
        // дойти до черновика этого теста (личная ручка наследует prefs при создании нити)
        var personalPrefs = _factory.Services.GetRequiredService<ImageProjectPrefsStore>().PathOf(_ownerId, ImageEditScope.Personal);
        if (File.Exists(personalPrefs)) File.Delete(personalPrefs);
    }

    // Задачи в памяти, ключ — область: у личного чата это personal. Вариант 1 у каждой
    internal sealed class PersonalJobs : IImageEditJobs
    {
        public int Started;
        private readonly Dictionary<string, (string Owner, string Scope, ImageEditJobInput Input)> _jobs = [];

        // Сброс перед каждым тестом: общий хост помнит запущенные задачи и счётчик чужих тестов
        public void Reset()
        {
            Started = 0;
            lock (_jobs) _jobs.Clear();
        }

        public Task<ImageEditCallResult<ImageEditQuoteDto>> QuoteAsync(
            string ownerId, string projectId, ImageEditQuoteRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ImageEditCallResult<ImageEditJobCreatedDto>> StartAsync(
            string ownerId, string projectId, ImageEditJobInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref Started);
            var id = Guid.NewGuid().ToString("N");
            lock (_jobs) _jobs[id] = (ownerId, projectId, input);
            return Task.FromResult(ImageEditCallResult<ImageEditJobCreatedDto>.Ok(new ImageEditJobCreatedDto(id)));
        }

        public ImageEditJobDto? Get(string ownerId, string projectId, string jobId)
        {
            lock (_jobs)
                return _jobs.TryGetValue(jobId, out var j) && j.Owner == ownerId && j.Scope == projectId
                    ? new ImageEditJobDto(jobId, projectId, ImageEditJobStatus.Completed, "fal", "m", [1], null,
                        EditOutcome.Ok, true, null, null, DateTime.UtcNow, j.Input.ChatSessionId, j.Input.Initiator,
                        Count: 1, ThreadId: j.Input.ThreadId)
                    : null;
        }

        public Task<ImageEditJobDto?> CancelAsync(string ownerId, string projectId, string jobId, CancellationToken ct) =>
            Task.FromResult<ImageEditJobDto?>(null);

        public EditedImage? OpenVariant(string ownerId, string projectId, string jobId, int variant) =>
            Get(ownerId, projectId, jobId) is not null && variant == 1 ? new EditedImage(Png(40, 20), "image/png") : null;
    }

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private ImageThreadStore Store => _factory.Services.GetRequiredService<ImageThreadStore>();

    private static string Personal(string sessionId) => $"/api/image-editor/chats/{sessionId}";

    private string OwnerOf(string username) =>
        _factory.Services.GetRequiredService<UserStore>().FindByUsername(username)!.Id;

    private Task<Session> PersonalChat(string? ownerId = null) =>
        Sessions.CreateChatAsync(ownerId ?? _ownerId, ClaudeMode.AcceptEdits, name: "Личный чат");

    private long Revision(string chatId) => Store.Get(_ownerId, chatId).Revision;

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private async Task<string> OpenDraft(string chatId)
    {
        var resp = await _client.PostAsJsonAsync($"{Personal(chatId)}/threads", new { draftFolder = "", revision = Revision(chatId) });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await Json(resp)).GetProperty("focus").GetString()!;
    }

    [Fact]
    public async Task Черновик_запуск_и_версии_в_личном_чате_а_шаг_отдаёт_байты()
    {
        var chat = await PersonalChat();
        var threadId = await OpenDraft(chat.Id);

        var started = await _client.PostAsync($"{Personal(chat.Id)}/jobs", JobForm(chat.Id, threadId));

        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        var jobId = (await Json(started)).GetProperty("jobId").GetString()!;
        (await _client.GetAsync($"{Personal(chat.Id)}/jobs/{jobId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var job = _jobs.Get(_ownerId, ImageEditScope.Personal, jobId);
        job.Should().NotBeNull("задача ключуется личной областью");

        await _factory.Services.GetRequiredService<ImageThreadService>().OnJobFinishedAsync(_ownerId, job!);

        var threads = await _client.GetAsync($"{Personal(chat.Id)}/threads");
        threads.StatusCode.Should().Be(HttpStatusCode.OK);
        var version = (await Json(threads)).GetProperty("threads")[0].GetProperty("versions").EnumerateArray()
            .Should().ContainSingle(v => v.GetProperty("jobId").ValueKind == JsonValueKind.String).Subject;
        var stepId = version.GetProperty("currentStepId").GetString()!;
        var step = await _client.GetAsync($"{Personal(chat.Id)}/steps/{stepId}");
        step.StatusCode.Should().Be(HttpStatusCode.OK);
        step.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await step.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();

        var rotated = await _client.PostAsJsonAsync($"{Personal(chat.Id)}/transform",
            new { @base = new { stepId }, ops = new object[] { new { type = "rotate", degrees = 90 } } });
        rotated.StatusCode.Should().Be(HttpStatusCode.OK, "правка без ИИ от шага работает и вне проекта");
    }

    [Fact]
    public async Task Чужой_личный_чат_чат_проекта_и_несуществующий_404_одним_телом()
    {
        var foreign = await PersonalChat(OwnerOf(TestWebApplicationFactory.SecondUsername));
        var projectChat = await Sessions.CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Чат проекта");

        var bodies = new List<string>();
        foreach (var id in new[] { foreign.Id, projectChat.Id, "нет-такого" })
        {
            foreach (var resp in new[]
                     {
                         await _client.GetAsync($"{Personal(id)}/threads"),
                         await _client.PostAsJsonAsync($"{Personal(id)}/threads", new { draftFolder = "", revision = 0 }),
                         await _client.GetAsync($"{Personal(id)}/catalog"),
                         await _client.GetAsync($"{Personal(id)}/prefs"),
                     })
            {
                resp.StatusCode.Should().Be(HttpStatusCode.NotFound, id);
                bodies.Add(await resp.Content.ReadAsStringAsync());
            }
        }

        bodies.Distinct().Should().ContainSingle("чужой, проектный и несуществующий чат неотличимы");
        (await Json(await _client.GetAsync($"{Personal(projectChat.Id)}/threads"))).GetProperty("code").GetString()
            .Should().Be(ImageEditErrorCodes.ChatNotFound);
        Store.Get(_ownerId, projectChat.Id).Threads.Should().BeEmpty("личная ручка не дошла до нитей чата проекта");
    }

    [Fact]
    public async Task Проверка_имени_без_папки_Личного_свободна_а_персонаж_не_найден()
    {
        var chat = await PersonalChat();

        // Сохранение в «Личное» (Ф2-3): проверка имени — GET, папку не создаёт; без папки имя свободно
        (await _client.GetAsync($"{Personal(chat.Id)}/save/check?name=a&format=png"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        // Персонажи личной области — в папке «Личного» (ADR-040, Ф2-4); героя в ней нет
        (await _client.GetAsync($"{Personal(chat.Id)}/characters/hero"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Файлы_проекта_у_личного_чата_400()
    {
        var chat = await PersonalChat();

        var transform = await _client.PostAsJsonAsync($"{Personal(chat.Id)}/transform",
            new { @base = new { path = "images/hero.png" }, ops = new object[] { new { type = "rotate", degrees = 90 } } });
        var byFile = await _client.PostAsJsonAsync($"{Personal(chat.Id)}/threads", new { file = "images/hero.png", revision = 0 });
        var inFolder = await _client.PostAsJsonAsync($"{Personal(chat.Id)}/threads", new { draftFolder = "images", revision = 0 });

        transform.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(transform)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.InvalidRequest);
        byFile.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        inFolder.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Store.Get(_ownerId, chat.Id).Threads.Should().BeEmpty();
    }

    [Fact]
    public async Task Выбор_человека_личного_чата_хранит_персонажа_как_null()
    {
        var chat = await PersonalChat();

        var put = await _client.PutAsJsonAsync($"{Personal(chat.Id)}/prefs",
            new { provider = "fal", model = "m", count = 3, matchSourceSize = false, characterSlug = "hero" });

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        (await Json(put)).GetProperty("characterSlug").ValueKind.Should().Be(JsonValueKind.Null);
        var stored = _factory.Services.GetRequiredService<ImageProjectPrefsStore>().Get(_ownerId, ImageEditScope.Personal);
        stored.Should().Be(new ImageProjectPrefs("fal", "m", 3, false, null));
        var get = await Json(await _client.GetAsync($"{Personal(chat.Id)}/prefs"));
        get.GetProperty("count").GetInt32().Should().Be(3);
    }

    private static MultipartFormDataContent JobForm(string sessionId, string threadId) => new()
    {
        { new StringContent("q-1"), "quoteId" },
        { new StringContent("нарисовать кота"), "prompt" },
        { new StringContent(sessionId), "sessionId" },
        { new StringContent(threadId), "threadId" },
    };

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
