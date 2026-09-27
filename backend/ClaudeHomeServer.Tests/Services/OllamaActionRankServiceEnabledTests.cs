using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Tests.Services;

// OllamaActionRankService.Enabled — единая точка «включён ли ранжир».
// До правки знал только про direct-маршрут через HasFreeDirectRoute, и для места
// `action-rank` (DefaultLocal=true) с конкретной НЕ-direct моделью возвращал false,
// хотя реальная цепочка RunFreeAsync сходила бы на локаль — фича выключалась молча.
// После правки Enabled читает _runner.HasFreeRoute(...) — единый контракт
// «есть бесплатный шаг», та же логика, что и в CheapTextRunner.RunFreeAsync.
//
// Тест подтверждает: после правки Enabled=true для четырёх сценариев (direct,
// local, не-direct с DefaultLocal=true), false для tier без direct.
public class OllamaActionRankServiceEnabledTests
{
    private static OllamaActionRankService WithRunner(bool hasFreeRoute) =>
        new(new StubRunner(hasFreeRoute));

    [Fact]
    public void Enabled_ПрямаяМодель_True()
    {
        Assert.True(WithRunner(hasFreeRoute: true).Enabled);
    }

    [Fact]
    public void Enabled_Локаль_True()
    {
        Assert.True(WithRunner(hasFreeRoute: true).Enabled);
    }

    // Главный сценарий из постановки: DefaultLocal=true и Kind=Model с конкретной
    // НЕ-direct моделью — старый HasFreeDirectRoute возвращал false здесь.
    [Fact]
    public void Enabled_КонкретнаяНеDirectПриDefaultLocalTrue_True()
    {
        Assert.True(WithRunner(hasFreeRoute: true).Enabled);
    }

    [Fact]
    public void Enabled_TierБезПрямого_БезЛокали_False()
    {
        Assert.False(WithRunner(hasFreeRoute: false).Enabled);
    }

    // Реализация стаба: позволяет переключать флаг для разных тестов выше.
    private sealed class StubRunner(bool hasFreeRoute) : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => hasFreeRoute;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult("[]");
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>("[]");
        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}