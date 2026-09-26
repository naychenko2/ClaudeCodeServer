using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.ImageEditor;

// Ручки POST …/image-editor/save и GET …/save/check (ADR-018 §5): источник — ровно одно из
// «вариант задачи» и «шаг истории», пустой запрос — 400, а не «задача не найдена»; «Сохранить
// как…» на занятое имя — 409 name_taken с suggestion и без перезаписи; проверка ничего не пишет;
// шаг истории сохраняется без генерации.
public class ImageEditorSaveTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly OneVariantJobs _jobs = new();

    public ImageEditorSaveTests() =>
        _factory.ExtraServices = services => services.AddSingleton<IImageEditJobs>(_jobs);

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    // Одна задача «job-1» с единственным вариантом 1 у владельца — остальное неотличимо от отсутствующего
    private sealed class OneVariantJobs : IImageEditJobs
    {
        public const string JobId = "job-1";
        public byte[] Bytes = Png(12, 8);

        public Task<ImageEditCallResult<ImageEditQuoteDto>> QuoteAsync(
            string ownerId, string projectId, ImageEditQuoteRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ImageEditCallResult<ImageEditJobCreatedDto>> StartAsync(
            string ownerId, string projectId, ImageEditJobInput input, CancellationToken ct) =>
            throw new NotSupportedException();

        public ImageEditJobDto? Get(string ownerId, string projectId, string jobId) => null;

        public Task<ImageEditJobDto?> CancelAsync(string ownerId, string projectId, string jobId, CancellationToken ct) =>
            Task.FromResult<ImageEditJobDto?>(null);

        public EditedImage? OpenVariant(string ownerId, string projectId, string jobId, int variant) =>
            jobId == JobId && variant == 1 ? new EditedImage(Bytes, "image/png") : null;
    }

    private string Root(string projectId) => $"/api/projects/{projectId}/image-editor";

    private (string ProjectId, string Dir) CreateProject()
    {
        var users = _factory.Services.GetRequiredService<UserStore>();
        var user = users.FindByUsername(TestWebApplicationFactory.TestUsername)!;
        users.SetFeatureFlag(user.Id, FeatureFlagKeys.ImageEditor, true).Should().BeTrue();
        var dir = Path.Combine(_factory.TempDir, "img_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "images"));
        var id = _factory.Services.GetRequiredService<ProjectManager>()
            .Create("img-" + Guid.NewGuid().ToString("N")[..6], dir, user.Id, TestWebApplicationFactory.TestUsername).Id;
        return (id, dir);
    }

    private static string[] Files(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).Order().ToArray();

    // ── Источник сохранения (major ревью шагов 1–2) ─────────────────────────────────

    [Fact]
    public async Task Без_источника_400_invalid_request_а_не_job_not_found()
    {
        var (projectId, dir) = CreateProject();
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.PostAsJsonAsync($"{Root(projectId)}/save",
            new { variant = 1, mode = "as", folder = "images", fileName = "cat" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(resp)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.InvalidRequest);
        Files(dir).Should().BeEmpty();
    }

    [Fact]
    public async Task Оба_источника_сразу_400()
    {
        var (projectId, _) = CreateProject();
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.PostAsJsonAsync($"{Root(projectId)}/save",
            new { jobId = OneVariantJobs.JobId, variant = 1, stepId = new string('a', 32), mode = "as", fileName = "cat" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Нет_задачи_404_job_not_found_нет_шага_404_step_not_found()
    {
        var (projectId, _) = CreateProject();
        var client = _factory.CreateAuthenticatedClient();

        var job = await client.PostAsJsonAsync($"{Root(projectId)}/save",
            new { jobId = "nope", variant = 1, mode = "as", fileName = "cat" });
        var step = await client.PostAsJsonAsync($"{Root(projectId)}/save",
            new { stepId = new string('a', 32), mode = "as", fileName = "cat" });

        job.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(job)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.JobNotFound);
        step.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(step)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.StepNotFound);
    }

    [Fact]
    public async Task Неизвестный_режим_400()
    {
        var (projectId, _) = CreateProject();
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.PostAsJsonAsync($"{Root(projectId)}/save",
            new { jobId = OneVariantJobs.JobId, variant = 1, mode = "overwrite", fileName = "cat" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── «Сохранить как…» ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Сохранить_как_из_варианта_пишет_ровно_выбранное_имя()
    {
        var (projectId, dir) = CreateProject();
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.PostAsJsonAsync($"{Root(projectId)}/save",
            new { jobId = OneVariantJobs.JobId, variant = 1, mode = "as", folder = "images", fileName = "hero.png" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(resp)).GetProperty("path").GetString().Should().Be("images/hero.png");
        File.ReadAllBytes(Path.Combine(dir, "images", "hero.png")).Should().Equal(_jobs.Bytes);
    }

    [Fact]
    public async Task Занятое_имя_409_name_taken_с_подсказкой_и_файл_не_тронут()
    {
        var (projectId, dir) = CreateProject();
        var existing = Path.Combine(dir, "images", "hero.png");
        var old = Png(3, 3);
        File.WriteAllBytes(existing, old);
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.PostAsJsonAsync($"{Root(projectId)}/save",
            new { jobId = OneVariantJobs.JobId, variant = 1, mode = "as", folder = "images", fileName = "hero" });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await Json(resp);
        body.GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.NameTaken);
        body.GetProperty("suggestion").GetString().Should().Be("images/hero.v2.png");
        File.ReadAllBytes(existing).Should().Equal(old);
        Files(dir).Should().Equal("images/hero.png");
    }

    [Theory]
    [InlineData("images", "a/b.png")]
    [InlineData("images", "../x.png")]
    [InlineData("../", "x")]
    [InlineData("/etc", "x")]
    [InlineData("images/../../", "x")]
    [InlineData("missing", "x")]
    public async Task Имя_или_папка_мимо_проекта_400_и_ничего_не_создаётся(string folder, string name)
    {
        var (projectId, dir) = CreateProject();
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.PostAsJsonAsync($"{Root(projectId)}/save",
            new { jobId = OneVariantJobs.JobId, variant = 1, mode = "as", folder, fileName = name });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Files(dir).Should().BeEmpty();
        Directory.Exists(Path.Combine(dir, "missing")).Should().BeFalse();
    }

    [Fact]
    public async Task Папка_ссылка_наружу_400()
    {
        var (projectId, dir) = CreateProject();
        var outside = Path.Combine(_factory.TempDir, "outside_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(outside);
        try { Directory.CreateSymbolicLink(Path.Combine(dir, "out"), outside); }
        // Windows без прав на ссылки — проверка идёт в CI на Linux
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; }
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.PostAsJsonAsync($"{Root(projectId)}/save",
            new { jobId = OneVariantJobs.JobId, variant = 1, mode = "as", folder = "out", fileName = "x" });
        var check = await client.GetAsync($"{Root(projectId)}/save/check?folder=out&name=x&format=png");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        check.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Directory.EnumerateFiles(outside).Should().BeEmpty();
    }

    [Fact]
    public async Task Сохранение_шага_истории_без_генерации()
    {
        var (projectId, dir) = CreateProject();
        await File.WriteAllBytesAsync(Path.Combine(dir, "images", "hero.png"), Png(40, 20));
        var client = _factory.CreateAuthenticatedClient();
        var transform = await client.PostAsJsonAsync($"{Root(projectId)}/transform", new
        {
            @base = new { path = "images/hero.png" },
            ops = new object[] { new { type = "rotate", degrees = 90 } },
            encode = new { format = "jpeg", quality = 80 },
        });
        transform.StatusCode.Should().Be(HttpStatusCode.OK);
        var stepId = (await Json(transform)).GetProperty("stepId").GetString()!;

        var resp = await client.PostAsJsonAsync($"{Root(projectId)}/save",
            new { stepId, mode = "as", folder = "images", fileName = "hero-rotated.png" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(resp)).GetProperty("path").GetString().Should().Be("images/hero-rotated.jpg",
            "расширение ставится по фактическому формату шага, вписанное .png срезается");
        using var saved = SKBitmap.Decode(Path.Combine(dir, "images", "hero-rotated.jpg"));
        (saved.Width, saved.Height).Should().Be((20, 40));
    }

    [Fact]
    public async Task Сохранение_с_перекодированием_ставит_расширение_по_новому_формату()
    {
        var (projectId, dir) = CreateProject();
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.PostAsJsonAsync($"{Root(projectId)}/save", new
        {
            jobId = OneVariantJobs.JobId, variant = 1, mode = "as", folder = "images", fileName = "hero",
            encode = new { format = "webp", quality = 80 },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(resp)).GetProperty("path").GetString().Should().Be("images/hero.webp");
        Files(dir).Should().Equal("images/hero.webp");
    }

    // ── GET save/check ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Check_ничего_не_пишет_и_отвечает_формой_path_taken_suggestion()
    {
        var (projectId, dir) = CreateProject();
        File.WriteAllBytes(Path.Combine(dir, "images", "hero.png"), Png(3, 3));
        var client = _factory.CreateAuthenticatedClient();

        var taken = await Json(await client.GetAsync($"{Root(projectId)}/save/check?folder=images&name=hero.png&format=png"));
        var free = await Json(await client.GetAsync($"{Root(projectId)}/save/check?folder=images&name=hero&format=webp"));

        taken.GetProperty("path").GetString().Should().Be("images/hero.png");
        taken.GetProperty("taken").GetBoolean().Should().BeTrue();
        taken.GetProperty("suggestion").GetString().Should().Be("images/hero.v2.png");
        free.GetProperty("path").GetString().Should().Be("images/hero.webp");
        free.GetProperty("taken").GetBoolean().Should().BeFalse();
        free.GetProperty("suggestion").ValueKind.Should().Be(JsonValueKind.Null);
        Files(dir).Should().Equal("images/hero.png");
    }

    [Theory]
    [InlineData("")]
    [InlineData("bmp")]
    public async Task Check_неизвестный_формат_400(string format)
    {
        var (projectId, _) = CreateProject();
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.GetAsync($"{Root(projectId)}/save/check?folder=images&name=hero&format={format}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Флаг_выключен_save_check_404()
    {
        var (projectId, _) = CreateProject();
        var users = _factory.Services.GetRequiredService<UserStore>();
        users.SetFeatureFlag(users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id, FeatureFlagKeys.ImageEditor, false);
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.GetAsync($"{Root(projectId)}/save/check?folder=images&name=hero&format=png");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private static byte[] Png(int w, int h)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(SKColors.Teal);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
