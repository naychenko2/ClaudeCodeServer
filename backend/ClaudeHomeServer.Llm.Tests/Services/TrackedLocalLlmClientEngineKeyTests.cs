using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Tests.Services;

// Опечатка в LocalLlm:EngineKey молча заводит второй счётчик допуска — её ловит предупреждение.
public class TrackedLocalLlmClientEngineKeyTests
{
    private static LlmProviderRegistry Registry() => new(TestConfig.Build(new Dictionary<string, string?>
    {
        ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:8080",
        ["LlmProviders:local-qwen:IsLocal"] = "true",
        ["LlmProviders:local-qwen:Models:0:Id"] = "qwen",
    }));

    private static (bool Warned, List<(LogLevel Level, string Message)> Log) Run(string? engineKey)
    {
        var values = new Dictionary<string, string?>();
        if (engineKey is not null) values["LocalLlm:EngineKey"] = engineKey;
        var logger = new RecordingLogger();
        var warned = TrackedLocalLlmClient.WarnIfEngineKeyUnknown(TestConfig.Build(values), Registry(), logger);
        return (warned, logger.Entries);
    }

    [Fact]
    public void КлючаНетВРеестре_ПредупреждениеНазываетКлючИРасхождениеСчётчика()
    {
        var (warned, log) = Run("localqwen");

        warned.Should().BeTrue();
        var entry = log.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        entry.Message.Should().Contain("localqwen").And.Contain("local-qwen").And.Contain("разойдётся");
    }

    [Fact]
    public void КлючЕстьВРеестре_ПредупрежденияНет()
    {
        var (warned, log) = Run("local-qwen");

        warned.Should().BeFalse();
        log.Should().BeEmpty();
    }

    [Fact]
    public void КлючНеЗадан_ПредупрежденияНет()
    {
        var (warned, log) = Run(null);

        warned.Should().BeFalse();
        log.Should().BeEmpty();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
