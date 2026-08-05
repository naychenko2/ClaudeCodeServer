using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Memory;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Пре-фильтр резолвера записи памяти (Memory-диета): локальный keyword-скор (MemoryFulltext)
// перед LLM — кандидаты без пересечения по словам не идут в LLM, отвечаем ADD сразу.
public class MemoryWriteResolverPrefilterTests
{
    private sealed class CountingRunner : ICheapTextRunner
    {
        public int Calls;
        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "claude";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult("""{"op":"ADD"}""");
        }
        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new OneShotResult("""{"op":"ADD"}""", null, 0));
        }
    }

    [Fact]
    public async Task НетПересеченияПоСловам_LLMНеВызывается_РезультатAdd()
    {
        var candidates = new List<MemoryWriteCandidate>
        {
            new("c1", "Прод крутится на naychenko.me"),
        };
        var runner = new CountingRunner();
        var sut = new MemoryWriteResolver(runner, new ConfigurationBuilder().Build(),
            NullLogger<MemoryWriteResolver>.Instance);

        var decision = await sut.ResolveAsync("u1", "Пользователя зовут Андрей", "semantic", candidates);

        runner.Calls.Should().Be(0);
        decision.Op.Should().Be(MemoryWriteOp.Add);
    }

    [Fact]
    public async Task ЕстьПересечениеПоСловам_LLMВызывается()
    {
        var candidates = new List<MemoryWriteCandidate>
        {
            new("c1", "соседний факт"),
        };
        var runner = new CountingRunner();
        var sut = new MemoryWriteResolver(runner, new ConfigurationBuilder().Build(),
            NullLogger<MemoryWriteResolver>.Instance);

        await sut.ResolveAsync("u1", "новый факт", "semantic", candidates);

        runner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ПорогНастраиваетсяКонфигом()
    {
        // Одно общее слово из двух терминов → скор 0.5. Порог 0.6 отсекает LLM-вызов.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Memory:ResolveKeywordThreshold"] = "0.6" })
            .Build();
        var candidates = new List<MemoryWriteCandidate> { new("c1", "соседний факт") };
        var runner = new CountingRunner();
        var sut = new MemoryWriteResolver(runner, config, NullLogger<MemoryWriteResolver>.Instance);

        var decision = await sut.ResolveAsync("u1", "новый факт", "semantic", candidates);

        runner.Calls.Should().Be(0);
        decision.Op.Should().Be(MemoryWriteOp.Add);
    }
}
