namespace ClaudeHomeServer.Services.Llm;

// Шлюз «медиа-задание после хода»: локальный LLM-провайдер с YieldsGpuToMedia делит видеокарту с
// ComfyUI, и арбитр GPU выгружает модель при любом задании ComfyUI — даже посреди хода чата на ней
// (01.11.2026: чат на Gemma попросил нарисовать локально и оборвал сам себя). Драйвер локальных
// картинок перед отправкой в ComfyUI ждёт, пока такие ходы закончатся.
public interface IMediaGpuYieldGate
{
    // Идёт ли прямо сейчас ход на провайдере, уступающем GPU медиа-заданиям.
    bool HasActiveTurns { get; }

    // Ждать, пока такие ходы закончатся, но не дольше maxWait. true — ходов нет; false — срок вышел
    // (вызывающий запускает задание всё равно: картинка важнее вечной очереди).
    Task<bool> WaitForQuietAsync(TimeSpan maxWait, CancellationToken ct);
}
