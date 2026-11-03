using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Tests.Services;

// Шлюз «медиа-задание после хода» (IMediaGpuYieldGate): ход на провайдере, делящем GPU с ComfyUI,
// держит задание картинки; ходы других провайдеров его не держат; срок ожидания конечен.
public class MediaGpuYieldGateTests
{
    private const string Gemma = "local-video-gemma";

    private static MediaGpuYieldGate Gate(LocalEngineBusyTracker tracker) =>
        new(() => [Gemma], tracker) { PollInterval = TimeSpan.FromMilliseconds(5) };

    // Мутация: HasActiveTurns всегда false — второй assert краснеет; ждать не ход, а слот без
    // освобождения — ожидание не закончится, тест упадёт по таймауту.
    [Fact]
    public async Task ХодНаУступающемПровайдере_ЗаданиеЖдётКонцаХода()
    {
        var tracker = new LocalEngineBusyTracker();
        var gate = Gate(tracker);
        Assert.False(gate.HasActiveTurns);

        var turn = tracker.TryEnter(Gemma, maxConcurrent: 2)!;
        Assert.True(gate.HasActiveTurns);
        var wait = gate.WaitForQuietAsync(TimeSpan.FromSeconds(10), default);
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);

        turn.Dispose();
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(gate.HasActiveTurns);
    }

    // Ход другого локального провайдера (Flash-Next на своих картах) видеокарту Video не занимает.
    // Мутация: считать ходы по всем ключам трекера — assert краснеет.
    [Fact]
    public void ХодДругогоПровайдера_НеДержит()
    {
        var tracker = new LocalEngineBusyTracker();
        using var turn = tracker.TryEnter("local-qwen", maxConcurrent: 4)!;
        Assert.False(Gate(tracker).HasActiveTurns);
    }

    // Срок вышел — false: вызывающий запускает задание всё равно, вечной очереди нет.
    [Fact]
    public async Task СрокВышел_False()
    {
        var tracker = new LocalEngineBusyTracker();
        using var turn = tracker.TryEnter(Gemma, maxConcurrent: 2)!;
        Assert.False(await Gate(tracker).WaitForQuietAsync(TimeSpan.FromMilliseconds(60), default));
    }
}
