using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Журнал истории медиа-генераций (шаг 2): единственный писатель. События enqueued/started/
// finished в data/local-media-history/YYYY-MM-DD.jsonl (v:1), дозапись под lock. Тесты — на
// фейковых часах (ManualTime) и во временном каталоге: склейка по promptId, битая строка,
// interrupted, обрезка промпта/ошибки, карта из снимка /ccs/stats, сбой не роняет
public class ComfyRunHistoryTests : IDisposable
{
    private const string Prompt = "prompt-1";

    private readonly string _dataDir;
    private readonly ManualTime _time;

    public ComfyRunHistoryTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "ccs-runhist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        _time = new ManualTime();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
    }

    private static LocalJobOrigin Origin() =>
        new("owner-1", "session-1", "image", "generate", Heavy: false,
            ProjectId: "proj-1", Source: LocalJobOrigin.SourceLocalMedia, SourceJobId: "lm_1");

    private ComfyRunHistory Build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dataDir, "projects.json"),
        }).Build();
        return new ComfyRunHistory(NullServices.Instance, _time, config, NullLogger<ComfyRunHistory>.Instance);
    }

    private string HistoryFile() =>
        Path.Combine(_dataDir, ComfyRunHistory.DirName, _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd") + ".jsonl");

    // Читает строки файла: валидные — JsonNode, битые — null (пропуск)
    private List<JsonNode?> ReadLines()
    {
        var result = new List<JsonNode?>();
        if (!File.Exists(HistoryFile())) return result;
        foreach (var line in File.ReadAllLines(HistoryFile()))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { result.Add(JsonNode.Parse(line)); }
            catch (Exception) { result.Add(null); }
        }
        return result;
    }

    private static JsonNode? FindEvent(List<JsonNode?> lines, string promptId, string type) =>
        lines.FirstOrDefault(l => l is not null && l!["id"]?.ToString() == promptId && l!["t"]?.ToString() == type);

    // ─── События: запись и склейка по promptId ─────────────────────────────────

    [Fact]
    public void Enqueued_Started_Finished_ПишутТриСобытия_СклейкаПоPromptId()
    {
        var history = Build();
        var details = new LocalRunDetails("qwen-image", "кот на диване", new LocalRunParams(Width: 1024, Height: 1024, Count: 1));
        history.Enqueued(Prompt, Origin(), details);
        _time.Advance(TimeSpan.FromSeconds(5));
        history.Started(Prompt, Origin());
        _time.Advance(TimeSpan.FromSeconds(30));
        history.Finished(Prompt, Origin(), "completed", runSeconds: 28, error: null);

        var lines = ReadLines();
        lines.Should().HaveCount(3, "три события для одного prompt_id");

        // Склейка: все три события имеют один promptId и идут в порядке постановки → старта → завершения
        var enqueued = FindEvent(lines, Prompt, "enqueued")!;
        var started = FindEvent(lines, Prompt, "started")!;
        var finished = FindEvent(lines, Prompt, "finished")!;
        enqueued.Should().NotBeNull();
        started.Should().NotBeNull();
        finished.Should().NotBeNull();

        // Поля v:1 и происхождение
        enqueued!["v"]!.GetValue<int>().Should().Be(1);
        enqueued!["origin"]!["source"]?.ToString().Should().Be("local-media");
        enqueued!["origin"]!["projectId"]?.ToString().Should().Be("proj-1");
        enqueued!["origin"]!["sourceJobId"]?.ToString().Should().Be("lm_1");

        // Детали: модель, промпт, параметры (белый список)
        enqueued!["model"]?.ToString().Should().Be("qwen-image");
        enqueued!["prompt"]?.ToString().Should().Be("кот на диване");
        enqueued!["params"]!["width"]!.GetValue<int>().Should().Be(1024);
        enqueued!["params"]!["count"]!.GetValue<int>().Should().Be(1);

        // Исход и длительность
        finished!["outcome"]?.ToString().Should().Be("completed");
        finished!["runSeconds"]!.GetValue<int>().Should().Be(28);
    }

    [Fact]
    public void Started_БезMonitor_КартаNull()
    {
        var history = Build();
        history.Enqueued(Prompt, Origin(), null);
        history.Started(Prompt, Origin());

        var started = FindEvent(ReadLines(), Prompt, "started")!;
        started!["card"].Should().BeNull("монитора нет — карта null");
    }

    [Fact]
    public void Started_СMonitor_КартаИзСнимкаCcs()
    {
        // Монитор с Ccs: карта пишется из снимка /ccs/stats
        var ccs = new ComfyCcsStats(
            LoadedModels: ["qwen-image"],
            Gpus: [new ComfyGpuInfo(Index: 0, Uuid: "", Name: "RTX 4090", UtilPerc: 42,
                MemUsedMb: 1024, MemTotalMb: 24576, TempC: 55, PowerW: 120, PowerLimitW: null)],
            Reason: null);
        var monitor = new ComfyQueueMonitor(null!, null!, null!, NullLogger<ComfyQueueMonitor>.Instance);
        typeof(ComfyQueueMonitor).GetProperty("Ccs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(monitor, ccs); // setter private
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dataDir, "projects.json"),
        }).Build();
        var history = new ComfyRunHistory(new MonitorServices(monitor), _time,
            config, NullLogger<ComfyRunHistory>.Instance);
        history.Enqueued(Prompt, Origin(), null);
        history.Started(Prompt, Origin());

        var started = FindEvent(ReadLines(), Prompt, "started")!;
        started!["card"]!["loadedModels"]!.AsArray().Select(n => n!.ToString()).Should().Contain("qwen-image");
        started!["card"]!["gpus"]![0]!["name"]?.ToString().Should().Be("RTX 4090");
    }

    // ─── Обрезка: промпт до 4 КБ, ошибка до 500 ─────────────────────────────────

    [Fact]
    public void Промпт_Длиннее4Кб_Обрезается()
    {
        var history = Build();
        var longPrompt = new string('x', LocalRunDetails.MaxPromptLength + 500);
        history.Enqueued(Prompt, Origin(), new LocalRunDetails("m", longPrompt));

        var enqueued = FindEvent(ReadLines(), Prompt, "enqueued")!;
        enqueued!["prompt"]!.GetValue<string>().Length.Should().Be(LocalRunDetails.MaxPromptLength);
    }

    [Fact]
    public void Ошибка_Длиннее500_Обрезается()
    {
        var history = Build();
        history.Enqueued(Prompt, Origin(), null);
        var longError = new string('e', ComfyRunHistory.MaxErrorLength + 200);
        history.Finished(Prompt, Origin(), "failed", runSeconds: null, error: longError);

        var finished = FindEvent(ReadLines(), Prompt, "finished")!;
        finished!["error"]!.GetValue<string>().Length.Should().Be(ComfyRunHistory.MaxErrorLength);
    }

    // ─── Битая строка: читатель пропускает, валидные целы ─────────────────────

    [Fact]
    public void БитаяСтрока_Пропускается_ВалидныеЦелы()
    {
        var history = Build();
        history.Enqueued(Prompt, Origin(), null);
        history.Finished(Prompt, Origin(), "completed", runSeconds: 10, error: null);

        // Вставляем битую строку между валидными
        var file = HistoryFile();
        var lines = File.ReadAllLines(file).ToList();
        lines.Insert(1, "{битая строка: не json");
        File.WriteAllLines(file, lines);

        var read = ReadLines();
        read.Should().HaveCount(3);
        read[1].Should().BeNull("битая строка не парсится");
        read[0]!["t"]?.ToString().Should().Be("enqueued");
        read[2]!["t"]?.ToString().Should().Be("finished");
    }

    // ─── Interrupted: был enqueued, нет finished, после рестарта не в очереди ──

    [Fact]
    public void Interrupted_БылEnqueued_НетFinished_ПослеРестарта()
    {
        var history = Build();
        history.Enqueued(Prompt, Origin(), null);
        // «Рестарт»: prompt_id не найден после MaxNullHistoryTicks — монитор пишет interrupted
        history.Finished(Prompt, Origin(), "interrupted", runSeconds: null, error: null);

        var lines = ReadLines();
        var enqueued = FindEvent(lines, Prompt, "enqueued")!;
        var finished = FindEvent(lines, Prompt, "finished")!;
        enqueued.Should().NotBeNull("постановка записана");
        finished.Should().NotBeNull("завершение записано");
        finished!["outcome"]?.ToString().Should().Be("interrupted", "прервано рестартом, не сбой графа");
    }

    // Сбой графа (история есть, Failed=true) — исход failed, не interrupted; ошибка в журнал
    [Fact]
    public void СбойГрафа_ОстаётсяFailed_НеInterrupted()
    {
        var history = Build();
        history.Enqueued(Prompt, Origin(), null);
        history.Finished(Prompt, Origin(), "failed", runSeconds: 10, error: "OOM");

        var finished = FindEvent(ReadLines(), Prompt, "finished")!;
        finished!["outcome"]?.ToString().Should().Be("failed", "сбой графа — failed, не interrupted");
        finished!["error"]?.ToString().Should().Be("OOM", "текст ошибки в журнале");
    }

    // ─── Исключение не выходит: сбой записи не роняет ───────────────────────────

    [Fact]
    public void СбойЗаписи_НеРоняет()
    {
        // Каталог данных — файл: CreateDirectory бросит, Write ловит
        var fileAsDir = Path.Combine(_dataDir, "file-as-dir");
        File.Create(fileAsDir).Dispose();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(fileAsDir, "projects.json"),
        }).Build();
        var history = new ComfyRunHistory(NullServices.Instance, _time, config, NullLogger<ComfyRunHistory>.Instance);

        var act = () => history.Enqueued(Prompt, Origin(), null);
        act.Should().NotThrow("сбой записи не выходит из BackgroundService");
    }

    // IServiceProvider с монитором (для карты) или пустой (карта null)
    private sealed class MonitorServices(ComfyQueueMonitor monitor) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ComfyQueueMonitor) ? monitor : null;
    }

    private sealed class NullServices : IServiceProvider
    {
        public static readonly NullServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }

    // Фейковые часы: время двигает тест, а не сон
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
