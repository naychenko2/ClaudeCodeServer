using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.ImageEditor;

// Ручка правок без ИИ POST …/image-editor/transform и шаги истории (ADR-018 §9): флаг гейтит
// её так же, как остальные; путь базы — только внутри проекта и не через ссылку; в проект она
// не пишет никогда; dryRun шага не создаёт; шаги складываются в ленту с родителем.
public class ImageEditorTransformTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private string Root(string projectId) => $"/api/projects/{projectId}/image-editor";

    private (string ProjectId, string Dir) CreateProject(bool enableFlag = true)
    {
        var users = _factory.Services.GetRequiredService<UserStore>();
        var user = users.FindByUsername(TestWebApplicationFactory.TestUsername)!;
        if (enableFlag) users.SetFeatureFlag(user.Id, FeatureFlagKeys.ImageEditor, true).Should().BeTrue();
        var dir = Path.Combine(_factory.TempDir, "img_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var id = _factory.Services.GetRequiredService<ProjectManager>()
            .Create("img-" + Guid.NewGuid().ToString("N")[..6], dir, user.Id, TestWebApplicationFactory.TestUsername).Id;
        return (id, dir);
    }

    private static object Rotate(object @base) => new
    {
        @base,
        ops = new object[] { new { type = "rotate", degrees = 90 } },
    };

    [Fact]
    public async Task Флаг_выключен_transform_и_шаг_404()
    {
        var (projectId, dir) = CreateProject(enableFlag: false);
        await File.WriteAllBytesAsync(Path.Combine(dir, "hero.png"), Png(40, 20));
        var client = _factory.CreateAuthenticatedClient();

        var transform = await client.PostAsJsonAsync($"{Root(projectId)}/transform", Rotate(new { path = "hero.png" }));
        var dry = await client.PostAsJsonAsync($"{Root(projectId)}/transform?dryRun=true", Rotate(new { path = "hero.png" }));
        var step = await client.GetAsync($"{Root(projectId)}/steps/{new string('a', 32)}");

        transform.StatusCode.Should().Be(HttpStatusCode.NotFound);
        dry.StatusCode.Should().Be(HttpStatusCode.NotFound);
        step.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("images/../../outside.png")]
    [InlineData("/etc/passwd")]
    [InlineData("leak.png")]
    [InlineData("refs/secret.png")]
    public async Task Путь_базы_через_выход_из_проекта_или_ссылку_400(string path)
    {
        var (projectId, dir) = CreateProject();
        var outside = Path.Combine(_factory.TempDir, "outside_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(outside);
        await File.WriteAllBytesAsync(Path.Combine(outside, "secret.png"), Png(8, 8));
        await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(dir)!, "outside.png"), Png(8, 8));
        File.CreateSymbolicLink(Path.Combine(dir, "leak.png"), Path.Combine(outside, "secret.png"));
        Directory.CreateSymbolicLink(Path.Combine(dir, "refs"), outside);
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.PostAsJsonAsync($"{Root(projectId)}/transform", Rotate(new { path }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(resp)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.InvalidRequest);
        StepFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_не_пишет_в_проект_и_складывает_шаги_в_ленту()
    {
        var (projectId, dir) = CreateProject();
        var file = Path.Combine(dir, "hero.png");
        await File.WriteAllBytesAsync(file, Png(40, 20));
        var before = Hash(file);
        var client = _factory.CreateAuthenticatedClient();

        var dry = await client.PostAsJsonAsync($"{Root(projectId)}/transform?dryRun=true",
            new { @base = new { path = "hero.png" }, ops = Array.Empty<object>(), encode = new { format = "webp", quality = 60 } });
        dry.StatusCode.Should().Be(HttpStatusCode.OK);
        var dryBody = await Json(dry);
        dryBody.GetProperty("stepId").ValueKind.Should().Be(JsonValueKind.Null);
        dryBody.GetProperty("bytes").GetInt64().Should().BeGreaterThan(0);
        StepFiles().Should().BeEmpty("dryRun шага не пишет");

        var first = await client.PostAsJsonAsync($"{Root(projectId)}/transform", Rotate(new { path = "hero.png" }));
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var firstBody = await Json(first);
        var firstId = firstBody.GetProperty("stepId").GetString()!;
        (firstBody.GetProperty("width").GetInt32(), firstBody.GetProperty("height").GetInt32()).Should().Be((20, 40));

        var second = await client.PostAsJsonAsync($"{Root(projectId)}/transform", new
        {
            @base = new { stepId = firstId },
            ops = new object[] { new { type = "crop", rect = new { x = 0, y = 0, width = 0.5, height = 0.5 } } },
        });
        second.StatusCode.Should().Be(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
        var secondId = (await Json(second)).GetProperty("stepId").GetString()!;

        var steps = _factory.Services.GetRequiredService<ImageEditSteps>();
        var userId = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var chained = steps.Open(userId, projectId, secondId)!.Value.Step;
        chained.Parent.Should().Be(firstId);
        chained.EditId.Should().Be(steps.Open(userId, projectId, firstId)!.Value.Step.EditId, "одна лента правки");
        chained.SourcePath.Should().Be("hero.png");

        var image = await client.GetAsync($"{Root(projectId)}/steps/{secondId}");
        image.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var bitmap = SKBitmap.Decode(await image.Content.ReadAsByteArrayAsync()))
            (bitmap.Width, bitmap.Height).Should().Be((10, 20));

        Hash(file).Should().Be(before, "оригинал в проекте не меняется");
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Should().Equal(file);
    }

    [Fact]
    public async Task Неизвестный_шаг_404_и_недопустимая_операция_400()
    {
        var (projectId, dir) = CreateProject();
        await File.WriteAllBytesAsync(Path.Combine(dir, "hero.png"), Png(40, 20));
        var client = _factory.CreateAuthenticatedClient();

        var missing = await client.PostAsJsonAsync($"{Root(projectId)}/transform", Rotate(new { stepId = new string('b', 32) }));
        var badAngle = await client.PostAsJsonAsync($"{Root(projectId)}/transform", new
        {
            @base = new { path = "hero.png" },
            ops = new object[] { new { type = "rotate", degrees = 45 } },
        });
        var twoBases = await client.PostAsJsonAsync($"{Root(projectId)}/transform",
            Rotate(new { path = "hero.png", stepId = new string('c', 32) }));

        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(missing)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.StepNotFound);
        badAngle.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        twoBases.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Каталог_отдаёт_порог_тяжёлого_файла()
    {
        var (projectId, _) = CreateProject();
        var client = _factory.CreateAuthenticatedClient();

        var catalog = await Json(await client.GetAsync($"{Root(projectId)}/catalog"));

        catalog.GetProperty("limits").GetProperty("heavyFileMb").GetInt32().Should().Be(5);
    }

    private string[] StepFiles()
    {
        var root = _factory.Services.GetRequiredService<ImageEditWorkspace>().Root;
        return Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => f.Contains($"{Path.DirectorySeparatorChar}{ImageEditWorkspace.StepsDirName}{Path.DirectorySeparatorChar}"))
                .ToArray()
            : [];
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

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
