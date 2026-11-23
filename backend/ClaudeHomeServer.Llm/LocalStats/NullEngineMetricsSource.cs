namespace ClaudeHomeServer.Services.Llm;

/// <summary>
/// Заглушка источника метрик для llama-server и Ollama: движки не отдают
/// Prometheus-метрики, поэтому все числовые поля снимка null, а
/// <see cref="EngineSnapshot.Unsupported"/> — все блоки, которые vLLM-адаптер
/// умеет заполнять. Полные адаптеры (собственные метрики llama.cpp/Ollama) —
/// вне этой фичи.
/// </summary>
public sealed class NullEngineMetricsSource : IEngineMetricsSource
{
    public Task<EngineSnapshot?> ReadAsync(CancellationToken ct = default)
    {
        // Все блоки vLLM-адаптера: для заглушки они все «неподдержаны».
        // Имена полей (канонические) — единый источник правды (VllmMetricsSource.CanonicalFields).
        var snapshot = new EngineSnapshot
        {
            Unsupported = VllmMetricsSource.CanonicalFields,
        };
        return Task.FromResult<EngineSnapshot?>(snapshot);
    }
}
