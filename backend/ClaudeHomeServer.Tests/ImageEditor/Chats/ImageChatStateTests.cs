using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Chats;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.ImageEditor.Chats;

// Состояние редактора на сервере и ручной запуск из чата картинки (ADR-018 §2, шаг 12 плана v2):
// PUT state с ревизией и 409 на гонку, запись без сдвига UpdatedAt, изоляция владельцев,
// рассылка image_chat_state владельцу, строка image_launch в ленте и журнал «с прошлого
// сообщения» в блоке состояния следующего хода.
public class ImageChatStateTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    private readonly TestWebApplicationFactory _factory = new();
    private readonly RecordingJobs _jobs = new();
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly HttpClient _client;
    private readonly string _ownerId;
    private readonly string _projectId;
    private readonly string _root;

    public ImageChatStateTests()
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
        File.WriteAllBytes(Path.Combine(_root, "images", "hero.png"), Png);
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    // Задачи в памяти: запоминают вход, отдают DTO с числом вариантов и оценкой котировки
    private sealed class RecordingJobs : IImageEditJobs
    {
        public ImageEditJobInput? LastInput;
        private readonly Dictionary<string, (string Owner, string Project)> _jobs = [];

        public Task<ImageEditCallResult<ImageEditQuoteDto>> QuoteAsync(
            string ownerId, string projectId, ImageEditQuoteRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ImageEditCallResult<ImageEditJobCreatedDto>> StartAsync(
            string ownerId, string projectId, ImageEditJobInput input, CancellationToken ct)
        {
            LastInput = input;
            var id = Guid.NewGuid().ToString("N");
            lock (_jobs) _jobs[id] = (ownerId, projectId);
            return Task.FromResult(ImageEditCallResult<ImageEditJobCreatedDto>.Ok(new ImageEditJobCreatedDto(id)));
        }

        public ImageEditJobDto? Get(string ownerId, string projectId, string jobId)
        {
            lock (_jobs)
                return _jobs.TryGetValue(jobId, out var j) && j.Owner == ownerId && j.Project == projectId
                    ? new ImageEditJobDto(jobId, projectId, ImageEditJobStatus.Completed, "fal", "fal-ai/flux-fill", [1, 2],
                        new EditCost(0.24, ImageEditPriceUnits.Usd), EditOutcome.Ok, true, null, null, DateTime.UtcNow,
                        LastInput?.ChatSessionId, LastInput?.Initiator ?? ImageEditInitiator.Human,
                        Count: 2, Estimate: new ImageEditEstimateDto(0.1, ImageEditPriceUnits.Usd, true, ImageEditEstimateSources.Catalog))
                    : null;
        }

        public Task<ImageEditJobDto?> CancelAsync(string ownerId, string projectId, string jobId, CancellationToken ct) =>
            Task.FromResult<ImageEditJobDto?>(null);

        public EditedImage? OpenVariant(string ownerId, string projectId, string jobId, int variant) => null;
    }

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private string Chats(string? projectId = null) => $"/api/projects/{projectId ?? _projectId}/image-editor/chats";

    private async Task<Session> CreateChat()
    {
        var resp = await _client.PostAsJsonAsync(Chats(), new { sourcePath = "images/hero.png" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        return Sessions.GetById(id)!;
    }

    private static ImageChatState State(long revision, string prompt = "убрать провод", string? canvas = null) =>
        ImageChatStateStore.Empty with { Prompt = prompt, Count = 2, Model = "m", Provider = "fal", CanvasRevision = canvas, Revision = revision };

    private static StringContent Body(ImageChatState state) =>
        new(JsonSerializer.Serialize(state, ImageChatStateStore.Json), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    // ── PUT state ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Запись_со_старой_ревизией_409_и_актуальное_состояние_в_ответе()
    {
        var chat = await CreateChat();

        var first = await _client.PutAsync($"{Chats()}/{chat.Id}/state", Body(State(0)));
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        (await Json(first)).GetProperty("revision").GetInt64().Should().Be(1);

        // Вторая вкладка считала состояние до первой записи и шлёт ревизию 0
        var stale = await _client.PutAsync($"{Chats()}/{chat.Id}/state", Body(State(0, "другой промпт")));

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await Json(stale);
        body.GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.RevisionConflict);
        body.GetProperty("state").GetProperty("revision").GetInt64().Should().Be(1);
        body.GetProperty("state").GetProperty("prompt").GetString().Should().Be("убрать провод",
            "устаревшая запись не затирает принятую");

        var get = await Json(await _client.GetAsync($"{Chats()}/{chat.Id}/state"));
        get.GetProperty("prompt").GetString().Should().Be("убрать провод");
    }

    [Fact]
    public async Task Запись_состояния_не_двигает_UpdatedAt_и_рассылается_владельцу()
    {
        var chat = await CreateChat();
        var before = Sessions.GetById(chat.Id)!.UpdatedAt;

        var resp = await _client.PutAsync($"{Chats()}/{chat.Id}/state", Body(State(0)));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        Sessions.GetById(chat.Id)!.UpdatedAt.Should().Be(before,
            "состояние редактора — настройка, а не активность: чат не поднимается и не выходит из архива");
        var message = _broadcaster.ToOwnerCalls.Where(c => c.OwnerId == _ownerId).Select(c => c.Message)
            .OfType<ImageChatStateMessage>().Should().ContainSingle().Subject;
        message.SessionId.Should().Be(chat.Id);
        message.Revision.Should().Be(1);
        message.ChangedBy.Should().Be(ImageEditInitiator.Human);
        message.Changes.Select(c => c.Field).Should().Contain(["prompt", "count", "model"]);
    }

    [Fact]
    public async Task Чужой_и_обычный_чат_в_state_404_неотличимо()
    {
        var chat = await CreateChat();
        var plain = await Sessions.CreateAsync(_projectId, ClaudeMode.AcceptEdits);

        var second = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        CharacterEndpointsTests.EnableFlag(_factory, TestWebApplicationFactory.SecondUsername);
        var (otherProject, _) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.SecondUsername);

        // Чужой чат через свой проект: чат не из этого проекта
        var foreign = await second.PutAsync($"{Chats(otherProject)}/{chat.Id}/state", Body(State(0)));
        var foreignGet = await second.GetAsync($"{Chats(otherProject)}/{chat.Id}/state");
        var ordinary = await _client.PutAsync($"{Chats()}/{plain.Id}/state", Body(State(0)));
        var missing = await _client.PutAsync($"{Chats()}/nope/state", Body(State(0)));

        foreach (var resp in new[] { foreign, foreignGet, ordinary, missing })
        {
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await Json(resp)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.ChatNotFound);
        }
        var store = _factory.Services.GetRequiredService<ImageChatStateStore>();
        store.Get(_ownerId, chat.Id).Revision.Should().Be(0, "чужая запись не дошла до состояния");
    }

    [Fact]
    public async Task Маска_едет_multipart_и_снимается_при_смене_холста_без_маски()
    {
        var chat = await CreateChat();
        var store = _factory.Services.GetRequiredService<ImageChatStateStore>();

        var form = new MultipartFormDataContent
        {
            { new StringContent(JsonSerializer.Serialize(State(0, canvas: "c1"), ImageChatStateStore.Json)), "state" },
            { new ByteArrayContent(Png), "mask", "mask.png" },
        };
        (await _client.PutAsync($"{Chats()}/{chat.Id}/state", form)).StatusCode.Should().Be(HttpStatusCode.OK);
        store.ReadMask(_ownerId, chat.Id).Should().Equal(Png);

        (await _client.PutAsync($"{Chats()}/{chat.Id}/state", Body(State(1, canvas: "c2"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        store.ReadMask(_ownerId, chat.Id).Should().BeNull("маска от прежнего холста к новому не относится");
    }

    // ── Ручной запуск из чата ──────────────────────────────────────────────────

    private static MultipartFormDataContent JobForm(string? chatSessionId)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("q-1"), "quoteId" },
            { new StringContent("убрать провод"), "prompt" },
        };
        if (chatSessionId is not null) form.Add(new StringContent(chatSessionId), "chatSessionId");
        var png = new ByteArrayContent(Png);
        png.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(png, "source", "hero.png");
        return form;
    }

    private async Task<PromptSection?> NextTurnSection(Session chat)
    {
        var contributor = _factory.Services.GetServices<IPromptSectionContributor>()
            .Single(c => c.Key == ImageEditorStateContributor.SectionKey);
        var context = new PromptSessionContext(Sessions.GetById(chat.Id)!, _ownerId, null, _root);
        contributor.IsEnabled(context).Should().BeTrue("чат картинки владельца с включённым флагом");
        return (await contributor.BuildAsync(context, "что дальше?"))?.Sections.Single();
    }

    [Fact]
    public async Task Ручной_запуск_пишет_image_launch_и_попадает_в_блок_следующего_хода()
    {
        var chat = await CreateChat();
        var before = Sessions.GetById(chat.Id)!.UpdatedAt;

        var resp = await _client.PostAsync($"/api/projects/{_projectId}/image-editor/jobs", JobForm(chat.Id));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted, await resp.Content.ReadAsStringAsync());
        var jobId = (await Json(resp)).GetProperty("jobId").GetString();
        _jobs.LastInput!.ChatSessionId.Should().Be(chat.Id);
        _jobs.LastInput.Initiator.Should().Be(ImageEditInitiator.Human);

        var launch = (await Sessions.GetHistoryAsync(chat.Id)).OfType<StoredImageLaunchMessage>().Should().ContainSingle().Subject;
        launch.By.Should().Be(SpendInitiators.Human);
        launch.Prompt.Should().Be("убрать провод");
        launch.Model.Should().Be("fal-ai/flux-fill");
        launch.Count.Should().Be(2);
        launch.JobId.Should().Be(jobId);
        launch.Estimate!.Amount.Should().Be(0.1);
        Sessions.GetById(chat.Id)!.UpdatedAt.Should().BeAfter(before, "запуск — активность человека в этом чате");
        _broadcaster.ToOwnerCalls.Select(c => c.Message).OfType<ImageChatStateMessage>()
            .Should().Contain(m => m.SessionId == chat.Id && m.State.Events.Any(e => e.JobId == jobId));

        var section = await NextTurnSection(chat);
        section!.InTurnTail.Should().BeTrue("блок состояния едет хвостом хода при любом провайдере");
        section.Text.Should().Contain("Файл: images/hero.png")
            .And.Contain("Человек запустил вручную: «убрать провод»")
            .And.Contain("итог: 2 варианта, $0.24");

        var repeat = await NextTurnSection(chat);
        repeat!.Text.Should().NotContain("запустил", "журнал «с прошлого сообщения» уже показан прошлому ходу");
    }

    [Fact]
    public async Task Чужой_чат_в_запуске_задача_идёт_без_чата_и_в_чужую_ленту_ничего()
    {
        var chat = await CreateChat();
        var second = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        CharacterEndpointsTests.EnableFlag(_factory, TestWebApplicationFactory.SecondUsername);
        var (otherProject, _) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.SecondUsername);

        var resp = await second.PostAsync($"/api/projects/{otherProject}/image-editor/jobs", JobForm(chat.Id));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted, "ответ не выдаёт существование чужого чата");
        _jobs.LastInput!.ChatSessionId.Should().BeNull("чужой чат не метит ни задачу, ни трату");
        (await Sessions.GetHistoryAsync(chat.Id)).OfType<StoredImageLaunchMessage>().Should().BeEmpty();
        _factory.Services.GetRequiredService<ImageChatStateStore>().Get(_ownerId, chat.Id).Events.Should().BeEmpty();
    }
}
