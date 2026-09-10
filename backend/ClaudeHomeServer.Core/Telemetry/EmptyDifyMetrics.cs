namespace ClaudeHomeServer.Core.Telemetry;

// No-op реализация IDifyMetrics для юнит-тестов и опциональных зависимостей
// (Этап 5, шов IDifyMetrics). Используется в DossierStore, TeamMemoryService и
// других вертикалях, где MemoryDify.DiffSyncAsync вызывается без реальной
// метрики — синк не идёт (Available == false), а сигнатура DiffSyncAsync
// требует метрику. Без этой реализации сигнатура раздваивалась бы (nullable +
// проверка null в каждом вызове).
public sealed class EmptyDifyMetrics : IDifyMetrics
{
    public void RecordSyncError(string reason) { /* no-op */ }
}
