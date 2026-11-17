using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// П.18 и И2 (ADR-041, KM-B3b): фоновые действия над данными ребёнка выключены целиком —
// ни claude по подписке, ни локаль, ни облако. Считаем вызовы заглушек: у ребёнка их ноль,
// у взрослого цепочка работает как раньше (контроль, что тест не проходит вхолостую).
public class ChildBackgroundRouteTests
{
    private const string Kid = "kid";

    private sealed class KidPolicy : IChildModePolicy
    {
        public ChildPolicySnapshot? For(string ownerId) =>
            ownerId == Kid ? new ChildPolicySnapshot(Kid, new ChildProfile { Enabled = true }, false) : null;
    }

    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class CountingOneShot : IOneShotRunner
    {
        public int Calls;
        public string? NormalizeModel(string? model) => model;
        public Task<string> RunAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult("CLAUDE");
        }
        public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default, string? ownerId = null,
            string? effort = null, string? label = null)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new OneShotResult("CLAUDE", null, 0));
        }
    }

    private static (CheapTextRunner Runner, CountingOneShot Claude) Make()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = TestConfig.Build(new Dictionary<string, string?>
            { ["DataPath"] = Path.Combine(dir, "projects.json") });
        var ollama = new OllamaClient(new NullHttpFactory(), config, NullLogger<OllamaClient>.Instance);
        var store = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        var router = new LocalActionRouter(ollama, store, config, NullLogger<LocalActionRouter>.Instance);
        var cloud = new CloudCheapClient(new NullHttpFactory(), config, new LlmProviderRegistry(config),
            NullLogger<CloudCheapClient>.Instance);
        var claude = new CountingOneShot();
        var runner = new CheapTextRunner(router, ollama, cloud, claude, NullLogger<CheapTextRunner>.Instance,
            childPolicy: new KidPolicy());
        return (runner, claude);
    }

    [Fact]
    public async Task Контроль_ВзрослыйВладелец_ЦепочкаДоходитДоClaude()
    {
        var (runner, claude) = Make();

        var text = await runner.RunAsync(LocalActionCatalog.ChatTitle, "prompt", "haiku", ownerId: "mom");

        text.Should().Be("CLAUDE");
        claude.Calls.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_ДетскийВладелец_ПустойОтветИНиОдногоВызова()
    {
        var (runner, claude) = Make();

        var text = await runner.RunAsync(LocalActionCatalog.ChatTitle, "prompt", "haiku", ownerId: Kid);

        text.Should().BeEmpty();
        claude.Calls.Should().Be(0, "подписка ребёнку недоступна, фоновое действие выключено");
    }

    [Fact]
    public async Task RunDetailedAsync_ДетскийВладелец_ПустойРезультатИНиОдногоВызова()
    {
        var (runner, claude) = Make();

        var result = await runner.RunDetailedAsync(LocalActionCatalog.ChatTitle, "prompt", "haiku", ownerId: Kid);

        result.Text.Should().BeEmpty();
        claude.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RunLocalOrChosenAsync_ДетскийВладелец_Null()
    {
        var (runner, claude) = Make();

        var text = await runner.RunLocalOrChosenAsync(LocalActionCatalog.ChatTitle, "prompt", ownerId: Kid);

        text.Should().BeNull();
        claude.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RunOnLocalEngineDetailedAsync_ДетскийВладелец_Null()
    {
        var (runner, _) = Make();

        var result = await runner.RunOnLocalEngineDetailedAsync("local", LocalActionCatalog.ChatTitle, "prompt",
            ownerId: Kid);

        result.Should().BeNull();
    }

    // Последний рубеж: прямые потребители IOneShotRunner (персоны, разметка фото) минуют
    // CheapTextRunner — процесс CLI не должен стартовать вовсе, ни launcher, ни подписка.
    private sealed class ExplodingLaunchers : ILauncherFactory
    {
        public IProcessLauncher Local => throw new InvalidOperationException("запуск у ребёнка");
        public IProcessLauncher ForOwner(string? ownerId) => throw new InvalidOperationException("запуск у ребёнка");
        public IProcessLauncher ForProject(Project project) => throw new InvalidOperationException("запуск у ребёнка");
    }

    [Fact]
    public async Task OneShotClaudeRunner_ДетскийВладелец_ПроцессНеСтартует()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var runner = new OneShotClaudeRunner(new LlmProviderRegistry(config), new ExplodingLaunchers(), config,
            childPolicy: new KidPolicy());

        var result = await runner.RunDetailedAsync("prompt", "sonnet", ownerId: Kid);

        result.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task OneShotClaudeRunner_ВзрослыйВладелец_ДоходитДоЗапуска()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var runner = new OneShotClaudeRunner(new LlmProviderRegistry(config), new ExplodingLaunchers(), config,
            childPolicy: new KidPolicy());

        var act = () => runner.RunDetailedAsync("prompt", "sonnet", ownerId: "mom");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("запуск у ребёнка");
    }
}
