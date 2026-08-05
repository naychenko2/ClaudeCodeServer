using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Проверка формы правки B: смена публичной сигнатуры MemoryWriteResolver.ResolveAsync —
// ownerId доезжает до cheap.RunAsync. Фейк раннера фиксирует полученный ownerId; этого
// достаточно — правка механическая, остальные вызовы ловит компилятор.
public class MemoryWriteResolverConsumerTests
{
    // Перехватывающий раннер: отвечает пустым JSON и фиксирует ownerId вызова.
    private sealed class CapturingRunner : ICheapTextRunner
    {
        public string? LastOwnerId;
        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "claude";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            LastOwnerId = ownerId;
            return Task.FromResult("{}");
        }
        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            LastOwnerId = ownerId;
            return Task.FromResult(new OneShotResult("{}", null, 0));
        }
    }

    [Fact]
    public async Task ResolveAsync_ПробрасываетOwnerIdВладельцаВРаннер()
    {
        // Нужен хотя бы один кандидат — иначе ранний возврат ADD без вызова LLM.
        var candidates = new List<MemoryWriteCandidate> { new("c1", "соседний факт") };
        var runner = new CapturingRunner();
        var sut = new MemoryWriteResolver(runner, new ConfigurationBuilder().Build(),
            NullLogger<MemoryWriteResolver>.Instance);

        await sut.ResolveAsync("u1", "новый факт", "semantic", candidates);

        Assert.Equal("u1", runner.LastOwnerId);
    }
}
