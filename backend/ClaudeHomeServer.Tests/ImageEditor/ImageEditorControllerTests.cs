using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.ImageEditor;

// Ручки редактора картинок api/projects/{id}/image-editor/* (ADR-017, разделы 1, 7, 8):
// флаг гейтит сервер, ненастроенный поставщик скрыт и не даёт 500, запуск — 202 с id или
// понятный отказ, чужая задача неотличима от несуществующей, выключенная подсистема
// картинок не роняет хост.
public class ImageEditorControllerTests : IDisposable
{
    private readonly List<TestWebApplicationFactory> _factories = [];

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
        GC.SuppressFinalize(this);
    }

    // Исполнитель задач в памяти: владение сверяется по паре (владелец, проект), как
    // обязан делать настоящий ImageEditJobService
    private sealed class FakeJobs : IImageEditJobs
    {
        public readonly ConcurrentDictionary<string, (string Owner, string Project, bool Cancelled)> Jobs = new();
        public ImageEditJobInput? LastInput;

        public Task<ImageEditCallResult<ImageEditQuoteDto>> QuoteAsync(
            string ownerId, string projectId, ImageEditQuoteRequest request, CancellationToken ct) =>
            Task.FromResult(ImageEditCallResult<ImageEditQuoteDto>.Ok(new ImageEditQuoteDto(
                "q-1", request.Provider, "fal-ai/nano-banana-2/edit",
                new ImageEditEstimateDto(0.08 * request.Count, ImageEditPriceUnits.Usd, false, ImageEditEstimateSources.Catalog),
                DateTime.UtcNow.AddMinutes(10), 20)));

        public Task<ImageEditCallResult<ImageEditJobCreatedDto>> StartAsync(
            string ownerId, string projectId, ImageEditJobInput input, CancellationToken ct)
        {
            LastInput = input;
            if (input.QuoteId != "q-1")
                return Task.FromResult(ImageEditCallResult<ImageEditJobCreatedDto>.Fail(
                    ImageEditErrorCodes.QuoteNotFound, "Котировка устарела"));
            var id = Guid.NewGuid().ToString("N");
            Jobs[id] = (ownerId, projectId, false);
            return Task.FromResult(ImageEditCallResult<ImageEditJobCreatedDto>.Ok(new ImageEditJobCreatedDto(id)));
        }

        public ImageEditJobDto? Get(string ownerId, string projectId, string jobId) =>
            Jobs.TryGetValue(jobId, out var j) && j.Owner == ownerId && j.Project == projectId
                ? Dto(jobId, j.Project, j.Cancelled)
                : null;

        public Task<ImageEditJobDto?> CancelAsync(string ownerId, string projectId, string jobId, CancellationToken ct)
        {
            if (!Jobs.TryGetValue(jobId, out var j) || j.Owner != ownerId || j.Project != projectId)
                return Task.FromResult<ImageEditJobDto?>(null);
            Jobs[jobId] = j with { Cancelled = true };
            return Task.FromResult<ImageEditJobDto?>(Dto(jobId, projectId, true));
        }

        // Готовый вариант для ручки save; null — варианта нет
        public EditedImage? Variant;

        public EditedImage? OpenVariant(string ownerId, string projectId, string jobId, int variant) => Variant;

        private static ImageEditJobDto Dto(string id, string projectId, bool cancelled) =>
            new(id, projectId, cancelled ? ImageEditJobStatus.Cancelled : ImageEditJobStatus.Running,
                "fal", "fal-ai/nano-banana-2/edit", [], null, null, null, null, null, DateTime.UtcNow);
    }

    // Запись результата: только фиксирует вызов — путь вне проекта до неё дойти не должен
    private sealed class FakeSaver : IImageEditSaver
    {
        public readonly ConcurrentQueue<ImageEditSaveRequest> Calls = new();

        public ImageEditCallResult<ImageEditSaveResultDto> Save(
            string projectRoot, ImageEditSaveRequest request, EditedImage image)
        {
            Calls.Enqueue(request);
            return ImageEditCallResult<ImageEditSaveResultDto>.Ok(new ImageEditSaveResultDto("hero.v2.png"));
        }

        public ImageEditCallResult<ImageEditSaveResultDto> SaveAs(
            string projectRoot, string? folder, string? fileName, EditedImage image)
        {
            Calls.Enqueue(new ImageEditSaveRequest(null, 0, null, folder, fileName, ImageEditSaveModes.As));
            return ImageEditCallResult<ImageEditSaveResultDto>.Ok(new ImageEditSaveResultDto("hero.png"));
        }

        public ImageEditCallResult<SaveCheckResponse> Check(
            string projectRoot, string? folder, string? fileName, string extension) =>
            ImageEditCallResult<SaveCheckResponse>.Ok(new SaveCheckResponse("hero.png", false, null));
    }

    private sealed class ImagesDisabledFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Subsystems:images:Enabled", "false");
        }
    }

    private TestWebApplicationFactory Factory(IImageEditor[]? editors = null, FakeJobs? jobs = null,
        bool imagesDisabled = false, FakeSaver? saver = null)
    {
        TestWebApplicationFactory factory = imagesDisabled ? new ImagesDisabledFactory() : new TestWebApplicationFactory();
        factory.ExtraServices = services =>
        {
            foreach (var editor in editors ?? []) services.AddSingleton(editor);
            if (jobs is not null) services.AddSingleton<IImageEditJobs>(jobs);
            if (saver is not null) services.AddSingleton<IImageEditSaver>(saver);
        };
        _factories.Add(factory);
        return factory;
    }

    private static string EnableFlag(TestWebApplicationFactory factory, string username)
    {
        var users = factory.Services.GetRequiredService<UserStore>();
        var user = users.FindByUsername(username)!;
        users.SetFeatureFlag(user.Id, FeatureFlagKeys.ImageEditor, true).Should().BeTrue();
        return user.Id;
    }

    private static string CreateProject(TestWebApplicationFactory factory, string username)
    {
        var users = factory.Services.GetRequiredService<UserStore>();
        var user = users.FindByUsername(username)!;
        var dir = Path.Combine(factory.TempDir, "img_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return factory.Services.GetRequiredService<ProjectManager>()
            .Create("img-" + Guid.NewGuid().ToString("N")[..6], dir, user.Id, username).Id;
    }

    private static MultipartFormDataContent JobForm(string quoteId = "q-1")
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(quoteId), "quoteId" },
            { new StringContent("убрать провод"), "prompt" },
        };
        var png = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);
        png.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(png, "source", "hero.png");
        return form;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private static object Quote(string provider) => new
    {
        provider, model = "auto", mode = "fast", op = "edit", count = 2,
        hasMask = false, references = 0, hasCharacter = false, width = 1024, height = 768,
    };

    [Fact]
    public async Task Флаг_выключен_все_ручки_404()
    {
        var jobs = new FakeJobs();
        var factory = Factory([new FakeImageEditor("fal", models: FakeImageEditor.Model("m"))], jobs);
        var projectId = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        // Флаг включён по умолчанию — выключаем override'ом пользователя
        var users = factory.Services.GetRequiredService<UserStore>();
        users.SetFeatureFlag(users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id, FeatureFlagKeys.ImageEditor, false)
            .Should().BeTrue();
        var client = factory.CreateAuthenticatedClient();
        var root = $"/api/projects/{projectId}/image-editor";

        var responses = new[]
        {
            await client.GetAsync($"{root}/catalog"),
            await client.PostAsJsonAsync($"{root}/quote", Quote("fal")),
            await client.PostAsync($"{root}/jobs", JobForm()),
            await client.GetAsync($"{root}/jobs/any"),
            await client.DeleteAsync($"{root}/jobs/any"),
            await client.GetAsync($"{root}/jobs/any/variants/1"),
            await client.PostAsJsonAsync($"{root}/save", new { jobId = "any", variant = 1 }),
        };

        responses.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.NotFound);
        jobs.Jobs.Should().BeEmpty("при выключенном флаге запуск не должен дойти до исполнителя");
    }

    [Fact]
    public async Task Ненастроенный_поставщик_отсутствует_в_каталоге_и_котировка_409()
    {
        var factory = Factory(
        [
            new FakeImageEditor("higgsfield", enabled: false, models: FakeImageEditor.Model("nano_banana_2")),
            new FakeImageEditor("fal", models: FakeImageEditor.Model("fal-ai/nano-banana-2/edit")),
        ], new FakeJobs());
        EnableFlag(factory, TestWebApplicationFactory.TestUsername);
        var projectId = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();

        var catalog = await client.GetAsync($"/api/projects/{projectId}/image-editor/catalog");
        catalog.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(catalog);
        body.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("key").GetString())
            .Should().Equal("fal");
        body.GetProperty("default").GetProperty("provider").GetString().Should().Be("fal");
        body.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("providers")[0].GetProperty("models")[0].GetProperty("modes")[0].GetString()
            .Should().Be("fast", "enum'ы уходят строками в camelCase — на это рассчитывает фронт");

        var quote = await client.PostAsJsonAsync($"/api/projects/{projectId}/image-editor/quote", Quote("higgsfield"));
        quote.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(quote)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.ProviderUnavailable);
    }

    [Fact]
    public async Task Без_драйверов_каталог_пуст_а_запуск_понятный_отказ()
    {
        var factory = Factory();
        EnableFlag(factory, TestWebApplicationFactory.TestUsername);
        var projectId = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();

        var catalog = await client.GetAsync($"/api/projects/{projectId}/image-editor/catalog");
        catalog.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(catalog);
        body.GetProperty("providers").GetArrayLength().Should().Be(0);
        body.GetProperty("default").GetProperty("provider").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("reason").GetString().Should().Be(ImageEditCatalogReasons.NoProviderConfigured);

        var start = await client.PostAsync($"/api/projects/{projectId}/image-editor/jobs", JobForm());
        start.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await Json(start);
        error.GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.ProviderUnavailable);
        error.GetProperty("error").GetString().Should().Contain("не настроен");
    }

    // Хотфикс 2026-09-26: Higgsfield доступен всем — обычный пользователь (не админ) получает
    // котировку без цены и запускается настоящим исполнителем задач
    [Fact]
    public async Task Higgsfield_НеАдмин_БезЦены_КотировкаИЗапуск202()
    {
        var factory = Factory([new FakeImageEditor("higgsfield", models: FakeImageEditor.Model("nano_banana_2"))]);
        EnableFlag(factory, TestWebApplicationFactory.SecondUsername);
        var projectId = CreateProject(factory, TestWebApplicationFactory.SecondUsername);
        var client = factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername,
            TestWebApplicationFactory.SecondPassword);
        var root = $"/api/projects/{projectId}/image-editor";

        var quote = await client.PostAsJsonAsync($"{root}/quote", Quote("higgsfield"));
        quote.StatusCode.Should().Be(HttpStatusCode.OK, await quote.Content.ReadAsStringAsync());
        var quoteBody = await Json(quote);
        quoteBody.GetProperty("estimate").GetProperty("amount").ValueKind.Should().Be(JsonValueKind.Null);

        var start = await client.PostAsync($"{root}/jobs", JobForm(quoteBody.GetProperty("quoteId").GetString()!));
        start.StatusCode.Should().Be(HttpStatusCode.Accepted, await start.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Запуск_по_котировке_202_с_id_задачи()
    {
        var jobs = new FakeJobs();
        var factory = Factory([new FakeImageEditor("fal", models: FakeImageEditor.Model("fal-ai/nano-banana-2/edit"))], jobs);
        EnableFlag(factory, TestWebApplicationFactory.TestUsername);
        var projectId = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        var root = $"/api/projects/{projectId}/image-editor";

        var quote = await client.PostAsJsonAsync($"{root}/quote", Quote("fal"));
        quote.StatusCode.Should().Be(HttpStatusCode.OK);
        var quoteBody = await Json(quote);
        quoteBody.GetProperty("estimate").GetProperty("unit").GetString().Should().Be("usd");

        var start = await client.PostAsync($"{root}/jobs", JobForm(quoteBody.GetProperty("quoteId").GetString()!));
        start.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = (await Json(start)).GetProperty("jobId").GetString();
        jobId.Should().NotBeNullOrEmpty();
        jobs.Jobs.Should().ContainKey(jobId!);

        var state = await client.GetAsync($"{root}/jobs/{jobId}");
        state.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(state)).GetProperty("status").GetString().Should().Be("running");

        var stale = await client.PostAsync($"{root}/jobs", JobForm("q-old"));
        stale.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(stale)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.QuoteNotFound);
    }

    [Fact]
    public async Task DELETE_чужой_задачи_отклоняется()
    {
        var jobs = new FakeJobs();
        var factory = Factory([new FakeImageEditor("fal", models: FakeImageEditor.Model("m"))], jobs);
        EnableFlag(factory, TestWebApplicationFactory.TestUsername);
        EnableFlag(factory, TestWebApplicationFactory.SecondUsername);
        var ownProject = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var foreignProject = CreateProject(factory, TestWebApplicationFactory.SecondUsername);
        var owner = factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername,
            TestWebApplicationFactory.SecondPassword);
        var stranger = factory.CreateAuthenticatedClient();

        var start = await owner.PostAsync($"/api/projects/{foreignProject}/image-editor/jobs", JobForm());
        start.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = (await Json(start)).GetProperty("jobId").GetString()!;

        // Чужой проект — 404 на гейте проекта; свой проект с чужим jobId — 404 у исполнителя
        (await stranger.DeleteAsync($"/api/projects/{foreignProject}/image-editor/jobs/{jobId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.DeleteAsync($"/api/projects/{ownProject}/image-editor/jobs/{jobId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"/api/projects/{ownProject}/image-editor/jobs/{jobId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"/api/projects/{foreignProject}/image-editor/catalog"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "чужой проект не открывается ни одной ручкой");
        jobs.Jobs[jobId].Cancelled.Should().BeFalse("чужой DELETE не должен отменить задачу");

        var own = await owner.DeleteAsync($"/api/projects/{foreignProject}/image-editor/jobs/{jobId}");
        own.StatusCode.Should().Be(HttpStatusCode.OK);
        jobs.Jobs[jobId].Cancelled.Should().BeTrue();
    }

    [Fact]
    public async Task Подсистема_картинок_выключена_хост_жив_и_ручки_без_500()
    {
        var factory = Factory(imagesDisabled: true);
        EnableFlag(factory, TestWebApplicationFactory.TestUsername);
        var projectId = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        var root = $"/api/projects/{projectId}/image-editor";

        var catalog = await client.GetAsync($"{root}/catalog");
        catalog.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(catalog)).GetProperty("reason").GetString().Should().Be(ImageEditCatalogReasons.SubsystemDisabled);
        (await client.PostAsJsonAsync($"{root}/quote", Quote("fal"))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsync($"{root}/jobs", JobForm())).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.DeleteAsync($"{root}/jobs/any")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync($"{root}/save", new { jobId = "any", variant = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ── Path traversal (ADR-017 §8): пути из запроса обязаны проходить SafePath.Join ──

    private static readonly string[] EscapePaths = ["../etc/passwd", "/etc/passwd", "images/../../escape.png"];

    private static TheoryData<string, string> Cross(params string[] fields)
    {
        var data = new TheoryData<string, string>();
        foreach (var field in fields)
            foreach (var path in EscapePaths)
                data.Add(field, path);
        return data;
    }

    public static TheoryData<string, string> SaveFields() => Cross("sourcePath", "folder");
    public static TheoryData<string, string> JobFields() => Cross("sourcePath", "referencePaths");

    [Theory]
    [MemberData(nameof(SaveFields))]
    public async Task Save_путь_вне_проекта_400_и_запись_не_вызывается(string field, string path)
    {
        var jobs = new FakeJobs { Variant = new EditedImage([0x89, 0x50, 0x4E, 0x47], "image/png") };
        var saver = new FakeSaver();
        var factory = Factory([new FakeImageEditor("fal", models: FakeImageEditor.Model("m"))], jobs, saver: saver);
        EnableFlag(factory, TestWebApplicationFactory.TestUsername);
        var projectId = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();

        var body = new Dictionary<string, object?>
        {
            ["jobId"] = "any", ["variant"] = 1, ["fileName"] = "escape.png", [field] = path,
        };
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/image-editor/save", body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await Json(resp);
        error.GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.InvalidRequest);
        error.GetProperty("error").GetString().Should().Contain("вне папки проекта");
        saver.Calls.Should().BeEmpty("путь вне проекта не должен дойти до записи файла");
    }

    [Fact]
    public async Task Запуск_пропорции_из_формы_доходят_до_исполнителя()
    {
        var jobs = new FakeJobs();
        var factory = Factory([new FakeImageEditor("fal", models: FakeImageEditor.Model("m"))], jobs);
        EnableFlag(factory, TestWebApplicationFactory.TestUsername);
        var projectId = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();

        var form = JobForm();
        form.Add(new StringContent("16:9"), "aspectRatio");
        var resp = await client.PostAsync($"/api/projects/{projectId}/image-editor/jobs", form);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        jobs.LastInput!.AspectRatio.Should().Be("16:9");
    }

    [Theory]
    [InlineData("4:3")]
    [InlineData("16:9; drop")]
    public async Task Запуск_незнакомые_пропорции_400_и_задача_не_создаётся(string ratio)
    {
        var jobs = new FakeJobs();
        var factory = Factory([new FakeImageEditor("fal", models: FakeImageEditor.Model("m"))], jobs);
        EnableFlag(factory, TestWebApplicationFactory.TestUsername);
        var projectId = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();

        var form = JobForm();
        form.Add(new StringContent(ratio), "aspectRatio");
        var resp = await client.PostAsync($"/api/projects/{projectId}/image-editor/jobs", form);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(resp)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.InvalidRequest);
        jobs.Jobs.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(JobFields))]
    public async Task Запуск_путь_вне_проекта_400_и_задача_не_создаётся(string field, string path)
    {
        var jobs = new FakeJobs();
        var factory = Factory([new FakeImageEditor("fal", models: FakeImageEditor.Model("m"))], jobs);
        EnableFlag(factory, TestWebApplicationFactory.TestUsername);
        var projectId = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        // Приманка рядом с папкой проекта: ровно туда ведёт images/../../escape.png
        File.WriteAllBytes(Path.Combine(factory.TempDir, "escape.png"), [0x89, 0x50, 0x4E, 0x47]);
        var client = factory.CreateAuthenticatedClient();

        var form = JobForm();
        form.Add(new StringContent(path), field);
        var resp = await client.PostAsync($"/api/projects/{projectId}/image-editor/jobs", form);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await Json(resp);
        error.GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.InvalidRequest);
        error.GetProperty("error").GetString().Should().Contain("вне папки проекта",
            "отказ обязан прийти от проверки пути, а не от того, что файла случайно нет");
        jobs.Jobs.Should().BeEmpty("файл вне проекта не должен уйти поставщику");
    }

    // ── Символические ссылки (ADR-017 §8): SafePath.Join их не видит ─────────────────

    public static TheoryData<string, string> LinkCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var field in new[] { "sourcePath", "referencePaths" })
            foreach (var kind in new[] { "file", "dir", "dangling" })
                data.Add(field, kind);
        return data;
    }

    [Theory]
    [MemberData(nameof(LinkCases))]
    public async Task Запуск_путь_через_ссылку_наружу_400_и_задача_не_создаётся(string field, string kind)
    {
        var jobs = new FakeJobs();
        var factory = Factory([new FakeImageEditor("fal", models: FakeImageEditor.Model("m"))], jobs);
        EnableFlag(factory, TestWebApplicationFactory.TestUsername);
        var projectId = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var root = factory.Services.GetRequiredService<ProjectManager>().GetById(projectId)!.RootPath;
        var outside = Path.Combine(factory.TempDir, "outside_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "secret.png"), [0x89, 0x50, 0x4E, 0x47]);

        string path;
        try
        {
            switch (kind)
            {
                case "file":
                    File.CreateSymbolicLink(Path.Combine(root, "leak.png"), Path.Combine(outside, "secret.png"));
                    path = "leak.png";
                    break;
                case "dir":
                    Directory.CreateSymbolicLink(Path.Combine(root, "refs"), outside);
                    path = "refs/secret.png";
                    break;
                default:
                    File.CreateSymbolicLink(Path.Combine(root, "dangling.png"), Path.Combine(outside, "nope.png"));
                    path = "dangling.png";
                    break;
            }
        }
        // Windows без прав на ссылки — проверка идёт в CI на Linux
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; }
        var client = factory.CreateAuthenticatedClient();

        var form = JobForm();
        form.Add(new StringContent(path), field);
        var resp = await client.PostAsync($"/api/projects/{projectId}/image-editor/jobs", form);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await Json(resp);
        error.GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.InvalidRequest);
        error.GetProperty("error").GetString().Should().Contain("символическую ссылку",
            "отказ обязан прийти от проверки ссылки, а не от того, что файла нет");
        jobs.Jobs.Should().BeEmpty("файл хоста через ссылку не должен уйти поставщику");
    }
}
