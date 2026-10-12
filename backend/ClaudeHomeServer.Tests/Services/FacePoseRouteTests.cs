using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition.Llm;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Маршрут face-pose (ADR-027 §3): разбор значения, дефолт места ракурса, страховка раннера.
public class FacePoseRouteTests
{
    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class FakeOneShot : IOneShotRunner
    {
        public string? LastModel;
        public string? NormalizeModel(string? model) => model;
        public Task<string> RunAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null)
        {
            LastModel = model;
            return Task.FromResult("ok");
        }
        public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default, string? ownerId = null,
            string? effort = null, string? label = null)
            => Task.FromResult(new OneShotResult("ok", null, 0));
    }

    private static ILocalImageMedia Media(bool configured)
    {
        var m = new Mock<ILocalImageMedia>();
        m.SetupGet(x => x.Configured).Returns(configured);
        return m.Object;
    }

    private static (LocalActionRouter Router, LocalActionOverridesStore Store, IConfiguration Config) Stand(
        ILocalImageMedia? media)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = TestConfig.Build(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dir, "projects.json"),
        });
        var store = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        var ollama = new OllamaClient(new NullHttpFactory(), config, NullLogger<OllamaClient>.Instance);
        var router = new LocalActionRouter(ollama, store, config, NullLogger<LocalActionRouter>.Instance,
            localMedia: media);
        return (router, store, config);
    }

    [Fact]
    public void Parse_FacePose_ДаётВидFacePose()
    {
        var route = LocalActionRouter.Parse(LocalActionOverridesStore.FacePoseRoute, RouteSource.Admin);
        route.Kind.Should().Be(RouteKind.FacePose);
        route.Source.Should().Be(RouteSource.Admin);
        LocalActionOverridesStore.FacePoseRoute.Should().Be("face-pose");
    }

    [Fact]
    public void ДефолтМестаРакурса_ПриLocalMedia_FacePose()
    {
        var (router, _, _) = Stand(Media(configured: true));
        var route = router.Resolve(LocalActionCatalog.CharacterPhotoAngle);
        route.Kind.Should().Be(RouteKind.FacePose);
        route.Source.Should().Be(RouteSource.Default);
    }

    [Fact]
    public void ДефолтМестаРакурса_БезLocalMedia_РазворачиваетсяВСлот()
    {
        foreach (var media in new[] { null, Media(configured: false) })
        {
            var (router, _, _) = Stand(media);
            var route = router.Resolve(LocalActionCatalog.CharacterPhotoAngle);
            route.Kind.Should().Be(RouteKind.Tier);
            route.Source.Should().Be(RouteSource.Default);
        }
    }

    [Fact]
    public void ДефолтМестаЭмоции_ПриLocalMedia_ОстаётсяСлотом()
    {
        var (router, _, _) = Stand(Media(configured: true));
        router.Resolve(LocalActionCatalog.CharacterPhotoEmotion).Kind.Should().Be(RouteKind.Tier);
    }

    [Fact]
    public void ЯвныйВыборАдмина_НеПодменяется()
    {
        // face-pose без local-media остаётся face-pose (фото ждут), слот при local-media остаётся слотом
        var (noMedia, store1, _) = Stand(Media(configured: false));
        store1.Set(LocalActionCatalog.CharacterPhotoAngle, LocalActionOverridesStore.FacePoseRoute);
        noMedia.Resolve(LocalActionCatalog.CharacterPhotoAngle).Kind.Should().Be(RouteKind.FacePose);

        var (withMedia, store2, _) = Stand(Media(configured: true));
        store2.Set(LocalActionCatalog.CharacterPhotoAngle, "tier:strong");
        var route = withMedia.Resolve(LocalActionCatalog.CharacterPhotoAngle);
        route.Kind.Should().Be(RouteKind.Tier);
        route.Source.Should().Be(RouteSource.Admin);
    }

    [Fact]
    public async Task CheapTextRunner_ПолучивFacePose_ИдётПоДефолтномуСлотуИПишетWarning()
    {
        var (router, store, config) = Stand(Media(configured: true));
        // стор правили руками: face-pose на текстовом месте (гард мест с картинками сюда не доходит)
        store.Set(LocalActionCatalog.NotesTags, LocalActionOverridesStore.FacePoseRoute);

        var oneShot = new FakeOneShot();
        var log = new RecordingLogger<CheapTextRunner>();
        var ollama = new OllamaClient(new NullHttpFactory(), config, NullLogger<OllamaClient>.Instance);
        var cloud = new CloudCheapClient(new NullHttpFactory(), config, new LlmProviderRegistry(config),
            NullLogger<CloudCheapClient>.Instance);
        var runner = new CheapTextRunner(router, ollama, cloud, oneShot, log);

        var text = await runner.RunAsync(LocalActionCatalog.NotesTags, "p", fallbackModel: "fallback-model");

        text.Should().Be("ok");
        // слот не настроен → модель действия, а не «face-pose» и не пусто
        oneShot.LastModel.Should().Be("fallback-model");
        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("face-pose"));
        runner.DescribeRoute(LocalActionCatalog.NotesTags, "fallback-model")
            .Should().Be("tier=weak");
    }

    [Fact]
    public async Task CheapTextRunner_МестоСКартинками_ОтказВслепуюНеМетит()
    {
        var (router, _, config) = Stand(Media(configured: true));
        var oneShot = new FakeOneShot();
        var log = new RecordingLogger<CheapTextRunner>();
        var ollama = new OllamaClient(new NullHttpFactory(), config, NullLogger<OllamaClient>.Instance);
        var cloud = new CloudCheapClient(new NullHttpFactory(), config, new LlmProviderRegistry(config),
            NullLogger<CloudCheapClient>.Instance);
        var runner = new CheapTextRunner(router, ollama, cloud, oneShot, log);

        foreach (var key in new[] { LocalActionCatalog.CharacterPhotoAngle, LocalActionCatalog.CharacterPhotoEmotion })
        {
            await runner.Invoking(r => r.RunAsync(key, "p")).Should().ThrowAsync<InvalidOperationException>();
            await runner.Invoking(r => r.RunDetailedAsync(key, "p")).Should().ThrowAsync<InvalidOperationException>();
            (await runner.RunFreeAsync(key, "p")).Should().BeNull();
        }

        oneShot.LastModel.Should().BeNull();
        log.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("картинки")).Should().Be(6);

        // текстовое место не затронуто
        (await runner.RunAsync(LocalActionCatalog.NotesTags, "p")).Should().Be("ok");
    }
}
