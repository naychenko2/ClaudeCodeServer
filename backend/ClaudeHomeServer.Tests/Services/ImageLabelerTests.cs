using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
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
    private sealed class FakeTiers(Dictionary<ModelTier, string> slots) : ITierModelResolver
    {
        public string? TierModel(ModelTier tier) => slots.GetValueOrDefault(tier);
    }

    private static (ImageLabeler Labeler, LocalActionOverridesStore Store, RecordingLogger<ImageLabeler> Log) Make(
        ImageRunFunc? run = null, Dictionary<ModelTier, string>? slots = null,
        string? presetId = null, string? presetStep = null, string? cloudTimeoutMs = null)
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
            ["Ollama:Profiles:small:CloudTimeoutMs"] = cloudTimeoutMs,
        });
        var store = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        var ollama = new OllamaClient(new NullHttpFactory(), config, NullLogger<OllamaClient>.Instance);
        var router = new LocalActionRouter(ollama, store, config, NullLogger<LocalActionRouter>.Instance);
        var providers = new LlmProviderRegistry(config);
        var runner = new OneShotClaudeRunner(providers, TestLauncherFactory.Instance, config);
        var log = new RecordingLogger<ImageLabeler>();
        ITierModelResolver? tiers = null;
        ModelAssignmentResolver? assignment = null;
        if (slots is not null)
        {
            tiers = new FakeTiers(slots);
            var specialty = TestSpecialtyStore.Create(config);
            if (presetId is not null)
                specialty.SetGlobal(new SpecialtySettingsLayer
                {
                    Presets = { new ModelRoutePreset { Id = presetId, Name = "П", Steps = [presetStep!] } },
                });
            assignment = new ModelAssignmentResolver(tiers, store, null, specialty, providers);
        }
        return (new ImageLabeler(router, providers, runner, log, tiers, null, assignment, run), store, log);
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

    private static ImageRunFunc Fake(Func<Task<OneShotResult>> body, Action<TimeSpan?>? onTimeout = null) =>
        (_, _, _, timeout, _, _, _) => { onTimeout?.Invoke(timeout); return body(); };

    private static Task<OneShotResult> Ответ(string text) =>
        Task.FromResult(new OneShotResult(text, null, 1));

    private static (ImageLabeler Labeler, int[] Calls) MakeCounting(Func<Task<OneShotResult>> body)
    {
        var calls = new int[1];
        var (labeler, store, _) = Make(Fake(() => { calls[0]++; return body(); }));
        store.Set(Action, "claude-haiku-4-5");
        return (labeler, calls);
    }

    [Fact]
    public async Task НормальныйОтвет_ВозвращаетсяТекстом()
    {
        var (labeler, calls) = MakeCounting(() => Ответ("поза: анфас"));
        var result = await labeler.LabelAsync(Request(), CancellationToken.None);
        result.Text.Should().Be("поза: анфас");
        result.Failure.Should().BeNull();
        calls[0].Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ПустойОтвет_Refused(string text)
    {
        var (labeler, _) = MakeCounting(() => Ответ(text));
        var result = await labeler.LabelAsync(Request(), CancellationToken.None);
        result.Failure.Should().Be(ImageLabelFailure.Refused);
        result.Text.Should().BeNull();
    }

    [Fact]
    public async Task LlmTimeout_Timeout()
    {
        var (labeler, _) = MakeCounting(() => throw new LlmTimeoutException("таймаут"));
        var result = await labeler.LabelAsync(Request(), CancellationToken.None);
        result.Failure.Should().Be(ImageLabelFailure.Timeout);
    }

    [Fact]
    public async Task ПрочееИсключение_Failed()
    {
        var (labeler, _) = MakeCounting(() => throw new InvalidOperationException("boom"));
        var result = await labeler.LabelAsync(Request(), CancellationToken.None);
        result.Failure.Should().Be(ImageLabelFailure.Failed);
    }

    [Fact]
    public async Task ОтменаВызывающего_НеГлотается()
    {
        var (labeler, _) = MakeCounting(() => throw new OperationCanceledException());
        var act = () => labeler.LabelAsync(Request(), CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task ТаймаутМестаНеЗадан_ВРаннерУходитФолбэк120с(string cloudTimeoutMs)
    {
        TimeSpan? seen = null;
        var (labeler, store, _) = Make(Fake(() => Ответ("ok"), t => seen = t), cloudTimeoutMs: cloudTimeoutMs);
        store.Set(Action, "claude-haiku-4-5");

        await labeler.LabelAsync(Request(), CancellationToken.None);

        seen.Should().Be(TimeSpan.FromMilliseconds(120_000));
    }

    [Fact]
    public async Task ТаймаутМестаЗадан_ВРаннерУходитОн()
    {
        TimeSpan? seen = null;
        var (labeler, store, _) = Make(Fake(() => Ответ("ok"), t => seen = t), cloudTimeoutMs: "45000");
        store.Set(Action, "claude-haiku-4-5");

        await labeler.LabelAsync(Request(), CancellationToken.None);

        seen.Should().Be(TimeSpan.FromMilliseconds(45_000));
    }

    [Fact]
    public async Task ТаймаутПриОтмененномТокенеВызывающего_ОтменаНеСтановитсяTimeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (labeler, _) = MakeCounting(() => throw new LlmTimeoutException("таймаут"));
        var act = () => labeler.LabelAsync(Request(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ЧислоКартинокСверхЛимита_FailedБезЗапускаCli()
    {
        var (labeler, calls) = MakeCounting(() => Ответ("ok"));
        var images = Enumerable.Range(0, ImageLabeler.MaxImages + 1)
            .Select(_ => new ImageLabelInput([1], "image/jpeg")).ToList();
        var result = await labeler.LabelAsync(Request() with { Images = images }, CancellationToken.None);
        result.Failure.Should().Be(ImageLabelFailure.Failed);
        calls[0].Should().Be(0);
    }

    [Fact]
    public async Task ДесятьКартинок_ВЛимите()
    {
        var (labeler, calls) = MakeCounting(() => Ответ("ok"));
        var images = Enumerable.Range(0, ImageLabeler.MaxImages)
            .Select(_ => new ImageLabelInput([1], "image/jpeg")).ToList();
        var result = await labeler.LabelAsync(Request() with { Images = images }, CancellationToken.None);
        result.Text.Should().Be("ok");
        calls[0].Should().Be(1);
    }

    [Fact]
    public async Task СуммарныйРазмерСверхЛимита_FailedБезЗапускаCli()
    {
        var (labeler, calls) = MakeCounting(() => Ответ("ok"));
        var big = new byte[(int)(ImageLabeler.MaxTotalBytes / 2) + 1];
        var images = new[] { new ImageLabelInput(big, "image/png"), new ImageLabelInput(big, "image/png") };
        var result = await labeler.LabelAsync(Request() with { Images = images }, CancellationToken.None);
        result.Failure.Should().Be(ImageLabelFailure.Failed);
        calls[0].Should().Be(0);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("application/pdf")]
    [InlineData("")]
    public async Task НеБелыйContentType_FailedБезЗапускаCli(string contentType)
    {
        var (labeler, calls) = MakeCounting(() => Ответ("ok"));
        var images = new[] { new ImageLabelInput([1, 2], contentType) };
        var result = await labeler.LabelAsync(Request() with { Images = images }, CancellationToken.None);
        result.Failure.Should().Be(ImageLabelFailure.Failed);
        calls[0].Should().Be(0);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    public async Task БелыйContentType_Проходит(string contentType)
    {
        var (labeler, calls) = MakeCounting(() => Ответ("ok"));
        var images = new[] { new ImageLabelInput([1, 2], contentType) };
        var result = await labeler.LabelAsync(Request() with { Images = images }, CancellationToken.None);
        result.Text.Should().Be("ok");
        calls[0].Should().Be(1);
    }

    [Fact]
    public void СлотTierMedium_ВТирСильный_РазворачиваетсяВМодель()
    {
        var (labeler, store, _) = Make(slots: new()
        {
            [ModelTier.Medium] = LocalActionOverridesStore.TierRoute(ModelTier.Strong),
            [ModelTier.Strong] = "claude-haiku-4-5",
        });
        store.Set(Action, "tier:medium");

        var route = labeler.RouteOf(Action, "owner-1");
        route.Model.Should().NotBeNull().And.NotStartWith("tier:").And.NotStartWith("preset:");
        route.Model.Should().Contain("haiku");
        route.Kind.Should().Be(ImageLabelRouteKind.Vision);
    }

    [Fact]
    public void СлотПресет_РазворачиваетсяВПервыйШагЦепочки()
    {
        var (labeler, store, _) = Make(slots: new() { [ModelTier.Medium] = "preset:p1" },
            presetId: "p1", presetStep: "claude-haiku-4-5");
        store.Set(Action, "tier:medium");

        var route = labeler.RouteOf(Action, "owner-1");
        route.Model.Should().NotBeNull().And.NotStartWith("tier:").And.NotStartWith("preset:");
        route.Model.Should().Contain("haiku");
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
