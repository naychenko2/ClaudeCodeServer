using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Chats;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// Нить картинки в обычном чате проекта на собранном приложении (ADR-019, волна 2): взять в
// работу с якорем в ленте, «Взять» вариант своей задачи и шаг правки без ИИ, откат и новая
// правка — старая стопка с якорем на прежнем месте, ручной запуск в нить с тихой строкой,
// сохранение уводит нить на новый файл, чужая сессия и чужая нить изолированы, ветвление и
// удаление чата доходят до нитей через шину.
public class ThreadFlowEndpointTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly ThreadJobs _jobs = new();
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly HttpClient _client;
    private readonly string _ownerId;
    private readonly string _projectId;
    private readonly string _root;

    public ThreadFlowEndpointTests()
    {
        _factory.ExtraServices = services =>
        {
            services.AddSingleton<IImageEditJobs>(_jobs);
            services.AddSingleton<ISessionBroadcaster>(_broadcaster);
            services.AddSingleton<IImageEditor>(new FakeImageEditor("fal", models: FakeImageEditor.Model("m")));
        };
        CharacterEndpointsTests.EnableFlag(_factory, TestWebApplicationFactory.TestUsername);
        (_projectId, _root) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.TestUsername);
        _ownerId = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        _client = _factory.CreateAuthenticatedClient();
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        File.WriteAllBytes(Path.Combine(_root, "images", "hero.png"), Png(40, 20));
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    // Задачи в памяти: запоминают вход и отдают DTO с его чатом и нитью; у каждой вариант 1
    private sealed class ThreadJobs : IImageEditJobs
    {
        public ImageEditJobInput? LastInput;
        public int Started;
        private readonly Dictionary<string, (string Owner, string Project, ImageEditJobInput Input)> _jobs = [];

        public Task<ImageEditCallResult<ImageEditQuoteDto>> QuoteAsync(
            string ownerId, string projectId, ImageEditQuoteRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ImageEditCallResult<ImageEditJobCreatedDto>> StartAsync(
            string ownerId, string projectId, ImageEditJobInput input, CancellationToken ct)
        {
            LastInput = input;
            Interlocked.Increment(ref Started);
            var id = Guid.NewGuid().ToString("N");
            lock (_jobs) _jobs[id] = (ownerId, projectId, input);
            return Task.FromResult(ImageEditCallResult<ImageEditJobCreatedDto>.Ok(new ImageEditJobCreatedDto(id)));
        }

        public ImageEditJobDto? Get(string ownerId, string projectId, string jobId)
        {
            lock (_jobs)
                return _jobs.TryGetValue(jobId, out var j) && j.Owner == ownerId && j.Project == projectId
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
    private string Editor(string? projectId = null) => $"/api/projects/{projectId ?? _projectId}/image-editor";
    private string Threads(string sessionId, string? projectId = null) => $"{Editor(projectId)}/sessions/{sessionId}/threads";

    private async Task<Session> Chat() => await Sessions.CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Обычный чат");

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private long Revision(string chatId) => Store.Get(_ownerId, chatId).Revision;

    private async Task<List<StoredModuleRecord>> Records(string chatId) =>
        (await Sessions.GetHistoryAsync(chatId)).OfType<StoredModuleRecord>().ToList();

    private async Task<string> OpenHero(string chatId)
    {
        var resp = await _client.PostAsJsonAsync(Threads(chatId), new { file = "images/hero.png", revision = Revision(chatId) });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await Json(resp)).GetProperty("focus").GetString()!;
    }

    // Шаг правки без ИИ от файла или от шага — то, что фронт потом «берёт» в нить
    private async Task<string> TransformStep(object @base)
    {
        var resp = await _client.PostAsJsonAsync($"{Editor()}/transform",
            new { @base, ops = new object[] { new { type = "rotate", degrees = 90 } } });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await Json(resp)).GetProperty("stepId").GetString()!;
    }

    private async Task<HttpResponseMessage> TakeStep(string chatId, string threadId, string stepId) =>
        await _client.PostAsJsonAsync($"{Threads(chatId)}/{threadId}/take", new { stepId, revision = Revision(chatId) });

    [Fact]
    public async Task Взять_картинку_в_работу_кладёт_якорь_стопки_в_ленту_один_раз()
    {
        var chat = await Chat();

        var threadId = await OpenHero(chat.Id);
        await OpenHero(chat.Id);

        var anchor = (await Records(chat.Id)).Should().ContainSingle("та же картинка второй раз — только фокус").Subject;
        anchor.Module.Should().Be("imageeditor");
        anchor.RecordType.Should().Be(ImageThreadService.RecordTypes.Thread);
        anchor.Data!.Value.GetProperty("threadId").GetString().Should().Be(threadId);
        anchor.Data.Value.GetProperty("stackId").GetString().Should().Be(Store.Get(_ownerId, chat.Id).Threads.Single().CurrentStackId);
        anchor.Fallback.Should().Be("Картинка: images/hero.png");
        _broadcaster.ToOwnerCalls.Select(c => c.Message).OfType<ImageThreadChangedMessage>()
            .Should().Contain(m => m.SessionId == chat.Id && m.State.Focus == threadId);
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("images/missing.png")]
    public async Task Файл_вне_проекта_или_несуществующий_400(string file)
    {
        var chat = await Chat();

        var resp = await _client.PostAsJsonAsync(Threads(chat.Id), new { file, revision = 0 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Store.Get(_ownerId, chat.Id).Threads.Should().BeEmpty();
    }

    [Fact]
    public async Task Откат_и_новая_правка_оставляют_старую_стопку_с_якорем_на_месте()
    {
        var chat = await Chat();
        var threadId = await OpenHero(chat.Id);
        var s1 = await TransformStep(new { path = "images/hero.png" });
        (await TakeStep(chat.Id, threadId, s1)).StatusCode.Should().Be(HttpStatusCode.OK);
        var s2 = await TransformStep(new { stepId = s1 });
        (await TakeStep(chat.Id, threadId, s2)).StatusCode.Should().Be(HttpStatusCode.OK);
        var firstStack = Store.Get(_ownerId, chat.Id).Threads.Single().CurrentStackId;

        var rollback = await _client.PostAsJsonAsync($"{Threads(chat.Id)}/{threadId}/rollback", new { stepId = s1, revision = Revision(chat.Id) });
        rollback.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(rollback)).GetProperty("threads")[0].GetProperty("currentStepId").GetString().Should().Be(s1);
        var s3 = await TransformStep(new { stepId = s1 });
        var taken = await TakeStep(chat.Id, threadId, s3);

        taken.StatusCode.Should().Be(HttpStatusCode.OK, await taken.Content.ReadAsStringAsync());
        var thread = Store.Get(_ownerId, chat.Id).Threads.Single();
        thread.Stacks.Single(s => s.StackId == firstStack).Should().BeEquivalentTo(
            new ImageThreadStack(firstStack!, [s1, s2], null, true));
        thread.CurrentStack!.Steps.Should().Equal(s1, s3);
        var records = await Records(chat.Id);
        records.Select(r => r.RecordType).Should().Equal(
            ImageThreadService.RecordTypes.Thread, ImageThreadService.RecordTypes.Thread, ImageThreadService.RecordTypes.StackForked);
        records[0].Data!.Value.GetProperty("stackId").GetString().Should().Be(firstStack, "якорь старой стопки остался первым");
        records[1].Data!.Value.GetProperty("stackId").GetString().Should().Be(thread.CurrentStackId);
        records[2].Fallback.Should().Be("Шаг 2 не пропал — он в старой стопке выше");
    }

    [Fact]
    public async Task Ручной_запуск_в_нить_ставит_ожидание_тихую_строку_и_берётся_вариант()
    {
        var chat = await Chat();
        var threadId = await OpenHero(chat.Id);

        var resp = await _client.PostAsync($"{Editor()}/jobs", JobForm(chat.Id, threadId));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted, await resp.Content.ReadAsStringAsync());
        var jobId = (await Json(resp)).GetProperty("jobId").GetString()!;
        _jobs.LastInput!.ThreadId.Should().Be(threadId);
        _jobs.LastInput.ChatSessionId.Should().Be(chat.Id);
        Store.Get(_ownerId, chat.Id).Threads.Single().PendingJobId.Should().Be(jobId);
        var launch = (await Records(chat.Id)).Last();
        launch.RecordType.Should().Be(ImageThreadService.RecordTypes.Launch);
        launch.Fallback.Should().StartWith("Вы запустили: «убрать провод»");

        var section = await NextTurnSection(chat);
        section.Should().Contain("Человек запустил вручную").And.Contain("варианты ждут выбора");

        var take = await _client.PostAsJsonAsync($"{Threads(chat.Id)}/{threadId}/take",
            new { jobId, variant = 1, revision = Revision(chat.Id) });

        take.StatusCode.Should().Be(HttpStatusCode.OK, await take.Content.ReadAsStringAsync());
        var thread = Store.Get(_ownerId, chat.Id).Threads.Single();
        thread.PendingJobId.Should().BeNull();
        thread.CurrentStack!.Steps.Should().ContainSingle().Which.Should().Be(thread.CurrentStepId);
        (await _client.GetAsync($"{Editor()}/steps/{thread.CurrentStepId}")).StatusCode.Should().Be(HttpStatusCode.OK,
            "взятый вариант стал шагом истории");
    }

    [Fact]
    public async Task Вариант_задачи_другой_нити_не_берётся()
    {
        var chat = await Chat();
        var threadId = await OpenHero(chat.Id);
        var other = await Chat();
        var otherThread = await OpenHero(other.Id);
        var resp = await _client.PostAsync($"{Editor()}/jobs", JobForm(other.Id, otherThread));
        var foreignJob = (await Json(resp)).GetProperty("jobId").GetString()!;

        var take = await _client.PostAsJsonAsync($"{Threads(chat.Id)}/{threadId}/take",
            new { jobId = foreignJob, variant = 1, revision = Revision(chat.Id) });

        take.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(take)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.JobNotFound);
        Store.Get(_ownerId, chat.Id).Threads.Single().HasSteps.Should().BeFalse();
    }

    [Fact]
    public async Task Запуск_в_чужую_нить_404_и_задача_не_стартует()
    {
        var chat = await Chat();
        var other = await Chat();
        var foreignThread = await OpenHero(other.Id);

        var resp = await _client.PostAsync($"{Editor()}/jobs", JobForm(chat.Id, foreignThread));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(resp)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.ThreadNotFound);
        _jobs.Started.Should().Be(0, "варианты ушли бы мимо карточки — запуск и трата не начинаются");
    }

    [Fact]
    public async Task Чужая_сессия_изолирована_на_всех_ручках_нити()
    {
        var chat = await Chat();
        var threadId = await OpenHero(chat.Id);
        var before = File.ReadAllText(Store.StatePath(_ownerId, chat.Id));
        CharacterEndpointsTests.EnableFlag(_factory, TestWebApplicationFactory.SecondUsername);
        var (otherProject, _) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.SecondUsername);
        var second = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var rev = Revision(chat.Id);

        var responses = new[]
        {
            await second.PostAsJsonAsync(Threads(chat.Id, otherProject), new { draftFolder = "", revision = rev }),
            await second.PostAsJsonAsync($"{Threads(chat.Id, otherProject)}/{threadId}/take", new { stepId = new string('a', 32), revision = rev }),
            await second.PostAsJsonAsync($"{Threads(chat.Id, otherProject)}/{threadId}/rollback", new { stepId = (string?)null, revision = rev }),
            await second.PostAsJsonAsync($"{Threads(chat.Id, otherProject)}/{threadId}/dismiss", new { jobId = "j", revision = rev }),
            await second.PutAsJsonAsync($"{Threads(chat.Id, otherProject)}/{threadId}/settings",
                new { settings = new { provider = "fal", model = "m", count = 2, matchSourceSize = true }, revision = rev }),
            await second.DeleteAsync($"{Threads(chat.Id, otherProject)}/{threadId}?revision={rev}"),
            await second.PostAsJsonAsync(Threads(chat.Id), new { draftFolder = "", revision = rev }),
        };

        foreach (var resp in responses) resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        File.ReadAllText(Store.StatePath(_ownerId, chat.Id)).Should().Be(before, "чужая запись не дошла до нитей");
        (await Records(chat.Id)).Should().ContainSingle("чужой запрос не пишет в ленту");
    }

    [Fact]
    public async Task Нить_другого_своего_чата_404_на_мутациях()
    {
        var chat = await Chat();
        await OpenHero(chat.Id);
        var other = await Chat();
        var foreign = await OpenHero(other.Id);

        var take = await TakeStep(chat.Id, foreign, await TransformStep(new { path = "images/hero.png" }));
        var remove = await _client.DeleteAsync($"{Threads(chat.Id)}/{foreign}?revision={Revision(chat.Id)}");

        take.StatusCode.Should().Be(HttpStatusCode.NotFound);
        remove.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(remove)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.ThreadNotFound);
        Store.Get(_ownerId, other.Id).Threads.Should().ContainSingle();
    }

    [Fact]
    public async Task Нить_с_шагами_не_убирается_а_пустой_черновик_убирается()
    {
        var chat = await Chat();
        var threadId = await OpenHero(chat.Id);
        (await TakeStep(chat.Id, threadId, await TransformStep(new { path = "images/hero.png" }))).EnsureSuccessStatusCode();
        var draft = await _client.PostAsJsonAsync(Threads(chat.Id), new { draftFolder = "", revision = Revision(chat.Id) });
        var draftId = (await Json(draft)).GetProperty("focus").GetString()!;

        var withSteps = await _client.DeleteAsync($"{Threads(chat.Id)}/{threadId}?revision={Revision(chat.Id)}");
        var empty = await _client.DeleteAsync($"{Threads(chat.Id)}/{draftId}?revision={Revision(chat.Id)}");

        withSteps.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        empty.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(empty);
        body.GetProperty("focus").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("threads").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Мутация_со_старой_ревизией_409()
    {
        var chat = await Chat();
        var threadId = await OpenHero(chat.Id);
        var stale = Revision(chat.Id) - 1;

        var resp = await _client.PostAsJsonAsync($"{Threads(chat.Id)}/{threadId}/rollback", new { stepId = (string?)null, revision = stale });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(resp)).GetProperty("state").GetProperty("revision").GetInt64().Should().Be(Revision(chat.Id));
    }

    [Fact]
    public async Task Сохранение_уводит_нить_на_новый_файл_с_тихой_строкой()
    {
        var chat = await Chat();
        var threadId = await OpenHero(chat.Id);
        var step = await TransformStep(new { path = "images/hero.png" });
        (await TakeStep(chat.Id, threadId, step)).EnsureSuccessStatusCode();

        var save = await _client.PostAsJsonAsync($"{Editor()}/save",
            new { stepId = step, variant = 0, sourcePath = "images/hero.png", sessionId = chat.Id, threadId });

        save.StatusCode.Should().Be(HttpStatusCode.OK, await save.Content.ReadAsStringAsync());
        var path = (await Json(save)).GetProperty("path").GetString()!;
        path.Should().NotBe("images/hero.png");
        var thread = Store.Get(_ownerId, chat.Id).Threads.Single();
        thread.File.Should().Be(path);
        thread.Lineage.Should().Equal("images/hero.png");
        var saved = (await Records(chat.Id)).Last();
        saved.RecordType.Should().Be(ImageThreadService.RecordTypes.Saved);
        saved.Fallback.Should().Be($"Сохранено как {path}");
    }

    [Fact]
    public async Task Сохранение_с_чужой_нитью_сохраняет_файл_и_нить_не_трогает()
    {
        var chat = await Chat();
        var other = await Chat();
        var foreign = await OpenHero(other.Id);
        var step = await TransformStep(new { path = "images/hero.png" });

        var save = await _client.PostAsJsonAsync($"{Editor()}/save",
            new { stepId = step, variant = 0, sourcePath = "images/hero.png", sessionId = chat.Id, threadId = foreign });

        save.StatusCode.Should().Be(HttpStatusCode.OK);
        Store.Get(_ownerId, other.Id).Threads.Single().File.Should().Be("images/hero.png");
        (await Records(chat.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Ветвление_на_шине_приложения_даёт_ветке_те_же_нити_а_удаление_чата_их_сносит()
    {
        var chat = await Chat();
        var threadId = await OpenHero(chat.Id);
        (await TakeStep(chat.Id, threadId, await TransformStep(new { path = "images/hero.png" }))).EnsureSuccessStatusCode();
        var branch = await Chat();
        var bus = _factory.Services.GetRequiredService<ITurnEventBus>();

        await bus.PublishAsync(new SessionBranched(new TurnContext(branch.Id, _ownerId, 0, 0, _projectId), chat.Id));
        await Sessions.DeleteAsync(chat.Id);

        var resp = await _client.GetAsync(Threads(branch.Id));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(resp);
        body.GetProperty("focus").GetString().Should().Be(threadId);
        body.GetProperty("threads")[0].GetProperty("currentStepId").GetString().Should().NotBeNullOrEmpty();
        File.Exists(Store.StatePath(_ownerId, chat.Id)).Should().BeFalse("нити удалённого чата снесены");

        // Ветка живёт дальше сама: её мутации не зависят от удалённого источника
        var rollback = await _client.PostAsJsonAsync($"{Threads(branch.Id)}/{threadId}/rollback",
            new { stepId = (string?)null, revision = Revision(branch.Id) });
        rollback.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<string> NextTurnSection(Session chat)
    {
        var contributor = _factory.Services.GetServices<IPromptSectionContributor>()
            .Single(c => c.Key == ImageEditorStateContributor.SectionKey);
        var context = new PromptSessionContext(Sessions.GetById(chat.Id)!, _ownerId, null, _root);
        contributor.IsEnabled(context).Should().BeTrue("в обычном чате проекта есть нить");
        var section = (await contributor.BuildAsync(context, "что дальше?"))!.Sections.Single();
        section.InTurnTail.Should().BeTrue();
        return section.Text;
    }

    private static MultipartFormDataContent JobForm(string sessionId, string threadId)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("q-1"), "quoteId" },
            { new StringContent("убрать провод"), "prompt" },
            { new StringContent(sessionId), "sessionId" },
            { new StringContent(threadId), "threadId" },
            { new StringContent("images/hero.png"), "sourcePath" },
        };
        var png = new ByteArrayContent(Png(40, 20));
        png.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(png, "source", "hero.png");
        return form;
    }

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
