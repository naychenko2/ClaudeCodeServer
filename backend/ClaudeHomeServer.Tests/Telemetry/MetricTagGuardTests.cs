using System.Diagnostics.Metrics;
using ClaudeHomeServer.Telemetry;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Ограничение кардинальности ЗНАЧЕНИЙ тегов (<see cref="MetricTagGuard"/>).
///
/// Контекст: allowlist ServerMetrics.AllowedTags стережёт только имена тегов. Значения
/// приходили снаружи без всякой проверки:
/// <list type="bullet">
/// <item><c>tool_name</c> — если MCP-сервер не прислал X-Mcp-Tool, вместо имени инструмента
///   в тег уезжал путь запроса с GUID проекта и именем файла;</item>
/// <item><c>model</c> — свободная строка из тела PUT /api/projects/{id}/sessions/{sid}.</item>
/// </list>
/// Каждое новое значение — вечный ряд в ClickHouse до конца retention.
/// </summary>
public class MetricTagGuardTests
{
    // ── Форма значения ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Tool_Missing_IsUnnamed(string? raw)
    {
        // Безымянный вызов должен отличаться от мусора: это штатная ситуация
        // (старая версия MCP-сервера в песочнице), а не подозрительное значение
        MetricTagGuard.Tool(raw).Should().Be(MetricTagGuard.Unnamed);
    }

    [Theory]
    [InlineData("tasks_list")]
    [InlineData("notes_semantic_search")]
    [InlineData("codegraph_find")]
    [InlineData("personas_bindings_list")]
    public void Tool_RealNames_PassThrough(string name)
    {
        // Настоящие имена инструментов не должны схлопываться — иначе метрика бесполезна
        MetricTagGuard.Tool(name).Should().Be(name);
    }

    [Theory]
    // Ровно то, что уезжало в тег до ограничителя
    [InlineData("(без имени) /api/projects/3f2b1c9e-0a44-4f7e-9c31-2b8d5e6a7f10/files/read")]
    [InlineData("/api/tasks/8c1d2e3f-4a5b-6c7d-8e9f-0a1b2c3d4e5f")]
    [InlineData("tool with spaces")]
    [InlineData("инструмент")]
    public void Tool_PathsAndJunk_MapToOther(string raw)
    {
        MetricTagGuard.Tool(raw).Should().Be(MetricTagGuard.Overflow);
    }

    [Fact]
    public void Tool_OverlongValue_MapsToOther()
    {
        // Длина ограничена и сама по себе: имён инструментов длиннее 64 символов не бывает,
        // а вот склеенный путь легко перевалит за них, не содержа ни одного запретного символа
        MetricTagGuard.Tool(new string('a', 65)).Should().Be(MetricTagGuard.Overflow);
        MetricTagGuard.Tool(new string('a', 64)).Should().Be(new string('a', 64));
    }

    [Theory]
    [InlineData("claude-sonnet-4-5-20250929")]
    [InlineData("glm-4.6")]
    [InlineData("qwen2.5:7b")]                  // локальная модель Ollama
    [InlineData("direct:openai/gpt-4o-mini")]   // прямой маршрут OpenRouter
    public void Model_RealIds_PassThrough(string model)
    {
        MetricTagGuard.Model(model).Should().Be(model);
    }

    [Theory]
    [InlineData("моя любимая модель")]
    [InlineData("model with spaces")]
    [InlineData("<script>alert(1)</script>")]
    public void Model_FreeText_MapsToOther(string raw)
    {
        // Поле Session.Model — свободный пользовательский ввод, а не выбор из списка
        MetricTagGuard.Model(raw).Should().Be(MetricTagGuard.Overflow);
    }

    // ── Лимит различных значений ─────────────────────────────────────────────

    [Fact]
    public void Limiter_CapsDistinctValues()
    {
        // Форму можно пройти и оставаясь бесконечным множеством (генерируемые идентификаторы),
        // поэтому вторая ступень — потолок на число РАЗНЫХ значений
        var limiter = new TagValueLimiter(3);

        limiter.Limit("a", _ => true).Should().Be("a");
        limiter.Limit("b", _ => true).Should().Be("b");
        limiter.Limit("c", _ => true).Should().Be("c");
        limiter.Limit("d", _ => true).Should().Be(MetricTagGuard.Overflow, "лимит исчерпан");

        // Уже известные значения продолжают проходить — счётчики по ним не рвутся
        limiter.Limit("b", _ => true).Should().Be("b");
        limiter.Count.Should().Be(3);
    }

    [Fact]
    public void Limiter_ShapeFailure_DoesNotConsumeBudget()
    {
        // Иначе поток мусора съедал бы места, отведённые настоящим значениям
        var limiter = new TagValueLimiter(2);

        limiter.Limit("junk-1", _ => false).Should().Be(MetricTagGuard.Overflow);
        limiter.Limit("junk-2", _ => false).Should().Be(MetricTagGuard.Overflow);
        limiter.Count.Should().Be(0);

        limiter.Limit("real", _ => true).Should().Be("real");
    }

    // ── Интеграция: что реально попадает в измерение ─────────────────────────

    private sealed record Sample(string Instrument, IReadOnlyDictionary<string, object?> Tags);

    private static List<Sample> Capture(Action action, string instrumentName)
    {
        var samples = new List<Sample>();

        void Add(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            if (instrument.Name != instrumentName) return;
            var dict = new Dictionary<string, object?>();
            foreach (var t in tags) dict[t.Key] = t.Value;
            lock (samples) samples.Add(new Sample(instrument.Name, dict));
        }

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ServerMetrics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((i, _, t, _) => Add(i, t));
        listener.SetMeasurementEventCallback<long>((i, _, t, _) => Add(i, t));
        listener.Start();

        action();

        return samples;
    }

    [Fact]
    public void RecordMcpCall_DoesNotLeakRequestPath()
    {
        // Главная регрессия: путь с идентификатором проекта не должен доезжать до метрики.
        // Проверяем именно измерение, а не чистую функцию — правило легко обойти,
        // передав значение мимо ограничителя.
        var path = "(без имени) /api/projects/3f2b1c9e-0a44-4f7e-9c31-2b8d5e6a7f10/files/read";

        var samples = Capture(() => ServerMetrics.RecordMcpCall(path, "success"), "ccs.mcp.calls");

        samples.Should().NotBeEmpty();
        samples.Select(s => s.Tags.GetValueOrDefault("tool_name") as string)
            .Should().NotContain(path, "путь запроса в теге — это и кардинальность, и PII");
        samples.Select(s => s.Tags.GetValueOrDefault("tool_name") as string)
            .Should().Contain(MetricTagGuard.Overflow);
    }

    [Fact]
    public void RecordLlmDuration_DoesNotLeakFreeFormModel()
    {
        // provider уникален на тест — отсекает измерения параллельных тестов
        var provider = "test-" + Guid.NewGuid().ToString("N")[..8];
        var junk = @"C:\Users\depec\models\моя модель";

        var samples = Capture(
            () => ServerMetrics.RecordLlmDuration(100, provider, junk),
            "ccs.llm.duration");

        var mine = samples.Where(s => s.Tags.GetValueOrDefault("provider") as string == provider).ToList();
        mine.Should().ContainSingle();
        mine[0].Tags["model"].Should().Be(MetricTagGuard.Overflow);
    }
}
