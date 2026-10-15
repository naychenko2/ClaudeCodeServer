using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Детали прогона (шаг 7, Minor 1): каждая точка постановки передаёт LocalRunDetails (модель,
// промпт, параметры) в Register → enqueued. Мутация «забыли передать details» в SubmitAsync →
// enqueued без модели/промпта/параметров → красный. По одному тесту на вид: картинка, звук,
// видео, LoRA. На фейковом ComfyUI и во временном каталоге
public class LocalRunDetailsTests : IDisposable
{
    private static readonly string Owner = Guid.NewGuid().ToString();
    private const string ProjectId = "proj-1";

    private readonly string _tempDir;
    private readonly string _dataDir;
    private readonly FakeComfy _comfy = new();
    private readonly FakeProjectAccess _projects = new();
    private readonly ManualTime _time = new();

    public LocalRunDetailsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "run_details_" + Guid.NewGuid().ToString("N"));
        _dataDir = Directory.CreateDirectory(Path.Combine(_tempDir, "data")).FullName;
        _projects.Roots[(Owner, ProjectId)] = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
    }

    public void Dispose()
    {
        TestFs.DeleteDirectoryResilient(_tempDir);
        GC.SuppressFinalize(this);
    }

    private static string ConfigDataPath(string dataDir) => Path.Combine(dataDir, "projects.json");

    // Журнал + реестр в временном каталоге
    private (ComfyRunHistory History, ComfyPromptLedger Ledger) BuildHistory()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = ConfigDataPath(_dataDir),
        }).Build();
        var history = new ComfyRunHistory(NullServices.Instance, _time, config, NullLogger<ComfyRunHistory>.Instance);
        return (history, new ComfyPromptLedger(_time, history));
    }

    // Читает enqueued-событие из файла журнала по prompt_id
    private JsonNode? FindEnqueued(ComfyRunHistory history, string promptId)
    {
        var date = _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd");
        var file = Path.Combine(history.Dir, date + ".jsonl");
        if (!File.Exists(file)) return null;
        foreach (var line in File.ReadAllLines(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (JsonNode.Parse(line) is { } node && node["id"]?.ToString() == promptId && node["t"]?.ToString() == "enqueued")
                return node;
        }
        return null;
    }

    // ─── Картинка: модель (Qwen-Image), промпт, параметры (число, сид) ──────────────────────

    [Fact]
    public async Task Картинка_Enqueued_Содержит_Модель_Промпт_Параметры()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = "4",
            ["DataPath"] = ConfigDataPath(_dataDir),
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var (history, ledger) = BuildHistory();
        var adapter = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance,
            null, null, ledger);

        var request = new LocalImageRequest(LocalImageOp.Generate, "кот на диване",
            Images: [], Aspect: null, Count: 2, Model: LocalImageModels.Quality, Seed: 42);
        var result = await adapter.SubmitAsync(request, default);
        result.Ticket.Should().NotBeNull("задача принята");

        var enqueued = FindEnqueued(history, result.Ticket!);
        enqueued.Should().NotBeNull("enqueued записан");
        enqueued!["model"]?.ToString().Should().Be(LocalImageModels.Quality, "модель — Qwen-Image");
        enqueued!["prompt"]?.ToString().Should().Be("кот на диване", "промпт сохранён");
        enqueued!["params"]!["count"]!.GetValue<int>().Should().Be(2, "число картинок");
        enqueued!["params"]!["seed"]!.GetValue<long>().Should().Be(42, "сид");
    }

    // ─── Звук: модель (операция), промпт, сид ─────────────────────────────────────────────────

    [Fact]
    public async Task Звук_Enqueued_Содержит_Модель_Промпт_Сид()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:AudioEnabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = "4",
            ["LocalMedia:MaxAudioInputSeconds"] = "600",
            ["DataPath"] = ConfigDataPath(_dataDir),
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var (history, ledger) = BuildHistory();
        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        var adapter = new LocalAudioMediaAdapter(client, config, images, NullLogger<LocalAudioMediaAdapter>.Instance, ledger);

        var request = new LocalAudioRequest(LocalAudioOp.MusicGenerate, Prompt: "весенняя мелодия", Seed: 7);
        var result = await adapter.SubmitAsync(request, default);
        result.Ticket.Should().NotBeNull("задача принята");

        var enqueued = FindEnqueued(history, result.Ticket!);
        enqueued.Should().NotBeNull("enqueued записан");
        enqueued!["model"]?.ToString().Should().Be(LocalMediaOps.MusicGenerate, "модель — операция");
        enqueued!["prompt"]?.ToString().Should().Be("весенняя мелодия", "промпт сохранён");
        enqueued!["params"]!["seed"]!.GetValue<long>().Should().Be(7, "сид");
    }

    // ─── Видео: модель (операция), промпт, параметры (длительность, сид) ──────────────────────

    [Fact]
    public async Task Видео_Enqueued_Содержит_Модель_Промпт_Параметры()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = "4",
            ["DataPath"] = ConfigDataPath(_dataDir),
        }).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var (history, ledger) = BuildHistory();
        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        var adapter = new LocalVideoMediaAdapter(client, config, images, NullLogger<LocalVideoMediaAdapter>.Instance,
            null, _time, null, ledger);

        var request = new LocalVideoRequest("кот бежит по дивану", LocalMediaTestImages.Png(64, 64),
            Seconds: 5, Seed: 99);
        var result = await adapter.SubmitAsync(request, default);
        result.Ticket.Should().NotBeNull("задача принята");

        var enqueued = FindEnqueued(history, result.Ticket!);
        enqueued.Should().NotBeNull("enqueued записан");
        enqueued!["model"]?.ToString().Should().Be("image_to_video", "модель — операция");
        enqueued!["prompt"]?.ToString().Should().Be("кот бежит по дивану", "промпт сохранён");
        enqueued!["params"]!["durationSec"]!.GetValue<int>().Should().Be(5, "длительность");
        enqueued!["params"]!["seed"]!.GetValue<long>().Should().Be(99, "сид");
    }

    // ─── LoRA: модель (операция), промпт (slug персонажа) ─────────────────────────────────────

    [Fact]
    public async Task Lora_Enqueued_Содержит_Модель_И_Slug()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:LoraTrainEnabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxComfyQueue"] = "4",
            ["DataPath"] = ConfigDataPath(_dataDir),
        }).Build();
        var store = new LocalMediaJobStore(config);
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var (history, ledger) = BuildHistory();
        var service = new LocalMediaService(client, store, _projects, config,
            NullLogger<LocalMediaService>.Instance, null, null, _time, ledger);

        var root = _projects.Roots[(Owner, ProjectId)];
        var photos = Enumerable.Range(1, 20)
            .Select(i =>
            {
                var name = $"{i:x16}.png";
                var path = Path.Combine(root, name);
                File.WriteAllBytes(path, LocalMediaTestImages.Png(64, 64));
                return new LoraTrainPhoto(name, path);
            })
            .ToList();
        var request = new LoraTrainStart(Owner, ProjectId, "test-char", "abcdef123456",
            LoraTrainPresets.Standard, "ccsandrey", photos);
        var result = await service.StartLoraTrainAsync(request, default);
        result.JobId.Should().NotBeNull("обучение поставлено");

        var job = store.Get(result.JobId!, Owner)!;
        var enqueued = FindEnqueued(history, job.PromptId);
        enqueued.Should().NotBeNull("enqueued записан");
        enqueued!["model"]?.ToString().Should().Be(LocalMediaOps.LoraTrain, "модель — операция");
        enqueued!["prompt"]?.ToString().Should().Be("test-char", "промпт — slug персонажа");
    }

    // Фейковые часы: время двигает тест, а не сон
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class NullServices : IServiceProvider
    {
        public static readonly NullServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
