using System.Diagnostics.Metrics;
using ClaudeHomeServer.Telemetry;
using FluentAssertions;
using OpenTelemetry.Trace;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Разбор конфигурации телеметрии: сэмплер трейсов и адрес OTLP-коллектора.
///
/// Обе функции раньше молча «чинили» вход: <c>TraceSampleRatio: 0</c> превращался в дефолт
/// (то есть выключить трейсинг конфигом было нельзя), а кривой <c>OtlpEndpoint</c> уходил
/// в <c>new Uri(...)</c> без проверки и ронял приложение на старте.
/// </summary>
public class TelemetryConfigTests
{
    // ── Сэмплер ──────────────────────────────────────────────────────────────

    [Fact]
    public void Sampler_NotConfigured_KeepsEveryTrace()
    {
        // Дефолт 1.0, а не прежние 0.05/0.10: ходов единицы в минуту, и при 5%
        // нужного трейса в 19 случаях из 20 просто нет
        ObservabilityExtensions.ResolveSampler(null)
            .Should().BeOfType<AlwaysOnSampler>();
    }

    [Fact]
    public void Sampler_Zero_DisablesTracing()
    {
        // Главная регрессия: 0 попадал в проверку «<= 0 → дефолт» и трейсинг
        // продолжал писаться, хотя админ его явно выключил
        ObservabilityExtensions.ResolveSampler(0)
            .Should().BeOfType<AlwaysOffSampler>();
    }

    [Fact]
    public void Sampler_One_KeepsEveryTrace()
    {
        ObservabilityExtensions.ResolveSampler(1.0)
            .Should().BeOfType<AlwaysOnSampler>();
    }

    [Fact]
    public void Sampler_Fraction_IsParentBased()
    {
        // ParentBased обязателен: без него дочерние спаны решают судьбу сами
        // и трейс приезжает дырявым
        var sampler = ObservabilityExtensions.ResolveSampler(0.25);

        sampler.Should().BeOfType<ParentBasedSampler>();
        sampler.Description.Should().Contain("0.25");
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(42.0)]
    public void Sampler_OutOfRange_FallsBackToEveryTrace(double ratio)
    {
        // Мусор в конфиге не должен ни ронять старт, ни тихо гасить трейсы
        ObservabilityExtensions.ResolveSampler(ratio)
            .Should().BeOfType<AlwaysOnSampler>();
    }

    // ── Адрес коллектора ─────────────────────────────────────────────────────

    [Fact]
    public void Endpoint_Valid_IsParsed()
    {
        ObservabilityExtensions.ParseEndpoint("http://localhost:4318", "http://fallback:4318")
            .Should().Be(new Uri("http://localhost:4318"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Endpoint_Missing_UsesFallback(string? configured)
    {
        ObservabilityExtensions.ParseEndpoint(configured, "http://localhost:4318")
            .Should().Be(new Uri("http://localhost:4318"));
    }

    [Theory]
    [InlineData("localhost:4318")]          // без схемы — самая частая опечатка
    [InlineData("это не адрес")]
    [InlineData("ftp://localhost:4318")]    // схема, которой OTLP не понимает
    [InlineData(@"C:\signoz")]
    public void Endpoint_Invalid_DisablesExportInsteadOfCrashing(string configured)
    {
        // Раньше такое значение прилетало в new Uri(...) и роняло приложение на старте —
        // единственное место, где observability убивала продукт
        ObservabilityExtensions.ParseEndpoint(configured, "http://localhost:4318")
            .Should().BeNull();
    }

    // ── Встроенные метры .NET ────────────────────────────────────────────────

    [Fact]
    public void SystemRuntimeMeter_IsBuiltIn()
    {
        // Обоснование удаления пакета OpenTelemetry.Instrumentation.Runtime: метр
        // System.Runtime живёт в самом рантайме, пакет давал бы отдельный метр
        // OpenTelemetry.Instrumentation.Runtime — и его AddRuntimeInstrumentation()
        // не вызывался ни разу. Если метр вдруг исчезнет из рантайма, метрики GC/
        // ThreadPool пропадут молча — этот тест не даст такому случиться незаметно.
        var published = new List<string>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == "System.Runtime")
                lock (published) published.Add(instrument.Name);
        };
        listener.Start();

        published.Should().NotBeEmpty("метр System.Runtime встроен в .NET и не требует пакета");
    }
}
