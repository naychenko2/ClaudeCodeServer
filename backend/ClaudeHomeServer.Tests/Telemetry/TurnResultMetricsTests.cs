using System.Diagnostics.Metrics;
using ClaudeHomeServer.Telemetry;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Проверка записи метрик результата хода (<see cref="TurnTelemetry.RecordTurnResult"/>)
/// через <see cref="MeterListener"/> — с реальным чтением значений и тегов,
/// а не «вызов не бросил исключение».
///
/// Контекст (регрессия): API-ошибка провайдера (напр. 429) приходит от claude CLI
/// как <c>subtype=success</c> с <c>is_error=true</c>. Раньше ClaudeSession передавал
/// в метрику только <c>subtype == "error"</c>, поэтому отказ провайдера уезжал
/// с <c>outcome=success</c>, счётчик <c>ccs.llm.errors</c> не инкрементился никогда,
/// а мгновенные отказные ходы занижали p95 длительности.
/// </summary>
public class TurnResultMetricsTests
{
    private sealed record Sample(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    /// <summary>Уникальный provider на тест — отсекает шум от тестов, идущих параллельно.</summary>
    private static string NewProvider() => "test-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Выполняет действие и возвращает измерения ServerMetrics, помеченные нашим provider'ом.
    /// Гистограмма пишет double, счётчики — long, поэтому слушаем оба типа.
    /// </summary>
    private static List<Sample> Capture(string provider, Action action)
    {
        var samples = new List<Sample>();

        void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var t in tags) dict[t.Key] = t.Value;
            // Чужие измерения (другие тесты в том же процессе) отбрасываем
            if (dict.TryGetValue("provider", out var p) && p as string == provider)
                lock (samples) samples.Add(new Sample(instrument.Name, value, dict));
        }

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ServerMetrics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((i, v, t, _) => Add(i, v, t));
        listener.SetMeasurementEventCallback<long>((i, v, t, _) => Add(i, v, t));
        listener.Start();

        action();

        return samples;
    }

    [Fact]
    public void ApiError_WithSuccessSubtype_CountsAsError()
    {
        // Сценарий 429: ClaudeSession свёл subtype=success + is_error=true → isError: true
        var provider = NewProvider();
        var samples = Capture(provider, () =>
            TurnTelemetry.RecordTurnResult(1234, provider, "glm-5.2", isError: true, apiErrorStatus: "429"));

        var duration = samples.Single(s => s.Instrument == "ccs.llm.duration");
        duration.Tags["outcome"].Should().Be("error", "отказ провайдера не должен уезжать как success");
        duration.Value.Should().Be(1234);

        var error = samples.Single(s => s.Instrument == "ccs.llm.errors");
        error.Value.Should().Be(1);
        error.Tags["error_type"].Should().Be("rate_limit");
    }

    [Fact]
    public void SuccessfulTurn_DoesNotIncrementErrors()
    {
        var provider = NewProvider();
        var samples = Capture(provider, () =>
            TurnTelemetry.RecordTurnResult(500, provider, "glm-5.2", isError: false, apiErrorStatus: null));

        samples.Single(s => s.Instrument == "ccs.llm.duration").Tags["outcome"].Should().Be("success");
        samples.Should().NotContain(s => s.Instrument == "ccs.llm.errors",
            "успешный ход не должен порождать запись в счётчик ошибок");
    }

    [Theory]
    [InlineData("429", "rate_limit")]
    [InlineData("401", "auth")]
    [InlineData("403", "auth")]
    [InlineData("503", "network")]
    [InlineData("process_exit", "process_exit")]
    [InlineData(null, "unknown")]
    public void ErrorType_IsClassifiedFromApiStatus(string? apiStatus, string expectedType)
    {
        var provider = NewProvider();
        var samples = Capture(provider, () =>
            TurnTelemetry.RecordTurnResult(10, provider, "glm-5.2", isError: true, apiErrorStatus: apiStatus));

        samples.Single(s => s.Instrument == "ccs.llm.errors").Tags["error_type"].Should().Be(expectedType);
    }

    [Fact]
    public void NullModel_FallsBackToUnknown_NotEmptyTag()
    {
        // Пустой тег в метрике сломал бы группировку в дашборде по model
        var provider = NewProvider();
        var samples = Capture(provider, () =>
            TurnTelemetry.RecordTurnResult(42, provider, model: null, isError: false, apiErrorStatus: null));

        samples.Single(s => s.Instrument == "ccs.llm.duration").Tags["model"].Should().Be("unknown");
    }

    /// <summary>
    /// Прямое покрытие регрессии: сведение двух признаков отказа. Именно эта связка
    /// была сломана — учитывался только subtype, из-за чего 429 считался успехом.
    /// </summary>
    [Theory]
    [InlineData("success", false, false)]           // обычный успешный ход
    [InlineData("error", false, true)]              // жёсткий сбой CLI
    [InlineData("success", true, true)]             // ← регрессия: 429 от провайдера
    [InlineData("error", true, true)]               // оба признака сразу
    public void IsTurnFailure_CombinesSubtypeAndIsErrorFlag(string subtype, bool isErrorFlag, bool expected)
    {
        TurnTelemetry.IsTurnFailure(subtype, isErrorFlag).Should().Be(expected);
    }

    /// <summary>
    /// Разрез «песочница или хост». Заведён после того, как выяснилось, что среда исполнения
    /// выбирается по владельцу процесса, а значит В ОДНОМ инстансе ходы идут и там, и там:
    /// без этого тега на вопрос «в контейнере медленнее?» ответить было нечем.
    /// </summary>
    [Theory]
    [InlineData(true, "docker")]
    [InlineData(false, "local")]
    public void Execution_MarksSandboxOnDurationAndErrors(bool isSandboxed, string expected)
    {
        var provider = NewProvider();
        var samples = Capture(provider, () =>
            TurnTelemetry.RecordTurnResult(1000, provider, "glm-5.2", isError: true,
                apiErrorStatus: "429", isSandboxed: isSandboxed));

        // Тег нужен на ОБЕИХ метриках: длительность отвечает «медленнее ли»,
        // счётчик ошибок — «чаще ли отваливается»
        samples.Single(s => s.Instrument == "ccs.llm.duration").Tags["execution"].Should().Be(expected);
        samples.Single(s => s.Instrument == "ccs.llm.errors").Tags["execution"].Should().Be(expected);
    }

    [Fact]
    public void ExecutionKind_SharesVocabularyWithProcessSpan()
    {
        // Спан process.start пишет тот же словарь в тег kind — если они разъедутся,
        // трейс и метрику нельзя будет сопоставить при разборе «песочница тормозит»
        TurnTelemetry.ExecutionKind(true).Should().Be("docker");
        TurnTelemetry.ExecutionKind(false).Should().Be("local");
    }

    [Fact]
    public void AllRecordedTags_AreInAllowlist()
    {
        // Страховка от «ad-hoc тега»: любой тег вне AllowedTags = взрыв кардинальности
        var provider = NewProvider();
        var samples = Capture(provider, () =>
            TurnTelemetry.RecordTurnResult(99, provider, "glm-5.2", isError: true, apiErrorStatus: "500"));

        samples.SelectMany(s => s.Tags.Keys).Distinct()
            .Should().OnlyContain(tag => ServerMetrics.AllowedTags.Contains(tag));
    }
}
