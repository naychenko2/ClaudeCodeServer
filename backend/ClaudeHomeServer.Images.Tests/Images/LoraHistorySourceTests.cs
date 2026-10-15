using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Источник результата для истории (ADR-030 §3, ADR-026 §14): обучение LoRA. Тесты — в
// temp-каталоге: превью фото обучения (успех), обход пути (грязный slug → missing), удалённые
// фото (нет каталога → missing). Путь — data/lora-training/<ownerKey>/<projectKey>/<slug>/
public class LoraHistorySourceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "lorahist_" + Guid.NewGuid().ToString("N"));
    private const string OwnerId = "11111111-1111-1111-1111-111111111111";
    private const string ProjectId = "22222222-2222-2222-2222-222222222222";

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception) { /* уборка temp — не предмет теста */ }
    }

    private LocalMediaJobStore NewStore()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "data"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
        }).Build();
        return new LocalMediaJobStore(config, NullLogger<LocalMediaJobStore>.Instance);
    }

    private LoraHistorySource NewSource(LocalMediaJobStore store)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
        }).Build();
        return new LoraHistorySource(store, config);
    }

    // Задача обучения LoRA: owner/project — Guid, slug — персонаж
    private LocalMediaJob TrainJob(string slug) => new()
    {
        Id = "job-lora",
        OwnerId = OwnerId,
        ProjectId = ProjectId,
        Op = LocalMediaOps.LoraTrain,
        PromptId = "p1",
        Status = LocalMediaStatuses.Completed,
        LoraSlug = slug,
    };

    private static MediaHistoryRef Ref() => new("p1", OwnerId, ProjectId, "job-lora", "chat-1");

    // Каталог фото: data/lora-training/<ownerKey>/<projectKey>/<slug>/
    private string PhotoDir(string slug)
    {
        var ownerKey = ClaudeHomeServer.Services.ImageEditor.LoraNamespace.OwnerKey(OwnerId);
        var projectKey = ClaudeHomeServer.Services.ImageEditor.LoraNamespace.OwnerKey(ProjectId);
        var dir = Path.Combine(_tempDir, "data", "lora-training", ownerKey, projectKey, slug);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Превью фото обучения: админ видит фото (успех, content-type по сигнатуре, variantCount=1)
    [Fact]
    public async Task ПревьюФото_Успех()
    {
        var dir = PhotoDir("alice");
        var photo = Path.Combine(dir, "0123456789abcdef.png");
        File.WriteAllBytes(photo, LocalMediaTestImages.Png(64, 64));

        var store = NewStore();
        store.Add(TrainJob("alice"));
        var src = NewSource(store);
        var res = await src.ResolveAsync(Ref(), 0, CancellationToken.None);
        res.Should().NotBeNull();
        res!.Reason.Should().BeNull();
        res.HostPath.Should().Be(photo);
        res.ContentType.Should().Be("image/png");
        res.VariantCount.Should().Be(1);
    }

    // Удалённые фото: каталога нет → 410 missing
    [Fact]
    public async Task УдалённыеФото_Missing()
    {
        var store = NewStore();
        store.Add(TrainJob("alice"));
        var src = NewSource(store);
        var res = await src.ResolveAsync(Ref(), 0, CancellationToken.None);
        res.Should().NotBeNull();
        res!.Reason.Should().Be(MediaHistoryGoneReason.Missing);
    }

    // Обход пути: slug с разделителем (a/b) невалиден → 410 missing (fail-closed)
    [Fact]
    public async Task ОбходПути_ГрязныйSlug_Missing()
    {
        var store = NewStore();
        store.Add(TrainJob("a/b"));
        var src = NewSource(store);
        var res = await src.ResolveAsync(Ref(), 0, CancellationToken.None);
        res.Should().NotBeNull();
        res!.Reason.Should().Be(MediaHistoryGoneReason.Missing);
    }

    // Запись без job_id (старые прогоны) → 410 missing
    [Fact]
    public async Task БезJobId_Missing()
    {
        var store = NewStore();
        var src = NewSource(store);
        var res = await src.ResolveAsync(new MediaHistoryRef("p1", OwnerId, ProjectId, null, "chat-1"), 0, CancellationToken.None);
        res.Should().NotBeNull();
        res!.Reason.Should().Be(MediaHistoryGoneReason.Missing);
    }
}
