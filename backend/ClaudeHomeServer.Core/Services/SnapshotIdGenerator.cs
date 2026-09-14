namespace ClaudeHomeServer.Services;

// Генератор id снимков промпта — {unixMs}-{seq}, лексикографически сортируемый по времени.
// Вынесен из `PromptSnapshotStore` (волна 6, Llm) — ClaudeSession зовёт его ДО шинной
// публикации (PromptSnapshotMessage в UI синхронно, шина — Notification, возврата не даёт).
// Прецедент SafePath/ExecutableResolver: stateless-примитив в Core, нулевая зависимость
// от BCL-плюс, вызывается и из Core, и из Main (форвардер в PromptSnapshotStore.NewPublicId
// сохранён ради существующего call-site в SessionManager).
public static class SnapshotIdGenerator
{
    // Единственный экземпляр счётчика в процессе. PromptSnapshotStore.NewId (private)
    // и PromptSnapshotStore.NewPublicId (forward) ОБА ходят через этот же генератор,
    // иначе вернутся коллизии id в одной миллисекунде (проверено ревью 5б волны 5: при
    // двух независимых счётчиках на каждый чих мы получали повтор через 5–12 снимков).
    private static int _seq;

    /// <summary>
    /// Генератор id для снимков промпта. Контракт совпадает с прежним
    /// <c>PromptSnapshotStore.NewId()</c>, и счётчик общий — без коллизий.
    /// </summary>
    public static string NewPublicId() =>
        $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Interlocked.Increment(ref _seq) & 0xFFFFF:x5}";
}
