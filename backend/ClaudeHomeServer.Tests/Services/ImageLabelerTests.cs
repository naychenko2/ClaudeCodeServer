using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Шов разметки картинок (ADR-027 §4): маршрут без зрения — отказ, а не ответ;
// форма stream-json сообщения с image-блоком.
public class ImageLabelerTests
{
    private const string Action = LocalActionCatalog.CharacterPhotoEmotion;

    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    // Провайдер glm без зрения; claude-модели картинки видят
    private static (ImageLabeler Labeler, LocalActionOverridesStore Store, RecordingLogger<ImageLabeler> Log) Make()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = TestConfig.Build(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dir, "projects.json"),
            ["LlmProviders:blind:AnthropicBaseUrl"] = "https://blind.example/anthropic",
            ["LlmProviders:blind:ApiKey"] = "sk-test",
            ["LlmProviders:blind:SupportsImages"] = "false",
            ["LlmProviders:blind:Models:0:Id"] = "blind-1",
        });
        var store = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        var ollama = new OllamaClient(new NullHttpFactory(), config, NullLogger<OllamaClient>.Instance);
        var router = new LocalActionRouter(ollama, store, config, NullLogger<LocalActionRouter>.Instance);
        var providers = new LlmProviderRegistry(config);
        var runner = new OneShotClaudeRunner(providers, TestLauncherFactory.Instance, config);
        var log = new RecordingLogger<ImageLabeler>();
        return (new ImageLabeler(router, providers, runner, log), store, log);
    }

    private static ImageLabelRequest Request() => new(Action, "owner-1", "опиши позу",
        [new ImageLabelInput([1, 2, 3], "image/jpeg")]);

    [Fact]
    public async Task МаршрутБезЗрения_ОтказомНеОтветом()
    {
        var (labeler, store, log) = Make();
        store.Set(Action, "blind-1");

        labeler.RouteOf(Action, "owner-1").Kind.Should().Be(ImageLabelRouteKind.None);
        var result = await labeler.LabelAsync(Request(), CancellationToken.None);

        result.Text.Should().BeNull();
        result.Failure.Should().Be(ImageLabelFailure.NoVision);
        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void МодельСоЗрением_ДаётVision_ЯвныйВыборАдминаПомечен()
    {
        var (labeler, store, _) = Make();
        store.Set(Action, "claude-haiku-4-5");

        var route = labeler.RouteOf(Action, "owner-1");
        route.Kind.Should().Be(ImageLabelRouteKind.Vision);
        route.Explicit.Should().BeTrue();
        route.Model.Should().Be("haiku"); // стор канонизирует id
    }

    [Fact]
    public async Task МестоБезКартинокНаВходе_НеОбслуживается()
    {
        var (labeler, _, _) = Make();
        var result = await labeler.LabelAsync(
            Request() with { ActionKey = LocalActionCatalog.CharacterSceneParse }, CancellationToken.None);
        result.Failure.Should().Be(ImageLabelFailure.NoVision);
    }

    [Fact]
    public async Task FacePose_ЭтимШвомНеОбслуживается()
    {
        var (labeler, store, _) = Make();
        store.Set(Action, LocalActionOverridesStore.FacePoseRoute);
        var result = await labeler.LabelAsync(Request(), CancellationToken.None);
        result.Failure.Should().Be(ImageLabelFailure.Unavailable);
    }

    [Fact]
    public void StreamJson_ЕстьImageБлок_Base64_МедиаТип()
    {
        byte[] bytes = [1, 2, 3, 250];
        var line = OneShotClaudeRunner.BuildStreamInput("промпт", [new ImageLabelInput(bytes, "image/png")]);

        line.Should().EndWith("\n");
        using var doc = JsonDocument.Parse(line);
        var msg = doc.RootElement.GetProperty("message");
        doc.RootElement.GetProperty("type").GetString().Should().Be("user");
        var content = msg.GetProperty("content");
        content[0].GetProperty("type").GetString().Should().Be("text");
        content[0].GetProperty("text").GetString().Should().Be("промпт");
        var src = content[1].GetProperty("source");
        content[1].GetProperty("type").GetString().Should().Be("image");
        src.GetProperty("type").GetString().Should().Be("base64");
        src.GetProperty("media_type").GetString().Should().Be("image/png");
        Convert.FromBase64String(src.GetProperty("data").GetString()!).Should().Equal(bytes);
    }

    [Fact]
    public void Аргументы_СКартинками_StreamJsonНаВходеИВыходе()
    {
        var args = OneShotClaudeRunner.BuildArgs([], safeMode: true, persistSessions: false,
            model: null, effort: null, streamInput: true);
        args.Should().ContainInOrder("--input-format", "stream-json");
        args.Should().ContainInOrder("--output-format", "stream-json");
        args.Should().Contain("--verbose");
    }

    [Fact]
    public void ПоследняяСтрокаResult_ВыбираетсяИзПотока()
    {
        var stdout = "{\"type\":\"system\"}\n{\"type\":\"assistant\"}\n{\"type\":\"result\",\"result\":\"ok\"}\n";
        OneShotClaudeRunner.LastResultLine(stdout).Should().Contain("\"result\":\"ok\"");
    }
}
