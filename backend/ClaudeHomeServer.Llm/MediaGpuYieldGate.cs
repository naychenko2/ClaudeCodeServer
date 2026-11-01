namespace ClaudeHomeServer.Services.Llm;

// Реализация IMediaGpuYieldGate: ходы считает тот же LocalEngineBusyTracker, что пускает их к
// локальному движку (ключ — ключ провайдера), а «какие провайдеры уступают GPU» — флаг
// YieldsGpuToMedia в каталоге. Ожидание — опрос раз в PollInterval: ход длится десятки секунд и
// минуты, точность в секунду здесь не нужна, а подписка на конец хода тянула бы сюда шину событий.
public sealed class MediaGpuYieldGate(Func<IEnumerable<string>> yieldingKeys, LocalEngineBusyTracker tracker,
    TimeProvider? time = null) : IMediaGpuYieldGate
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public MediaGpuYieldGate(LlmProviderRegistry providers, LocalEngineBusyTracker tracker)
        : this(() => providers.All.Where(p => p.IsLocal && p.YieldsGpuToMedia).Select(p => p.Key), tracker) { }

    public bool HasActiveTurns => yieldingKeys().Any(k => tracker.ActiveFor(k) > 0);

    public async Task<bool> WaitForQuietAsync(TimeSpan maxWait, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + maxWait;
        while (HasActiveTurns)
        {
            if (_time.GetUtcNow() >= deadline) return false;
            await Task.Delay(PollInterval, _time, ct);
        }
        return true;
    }
}
