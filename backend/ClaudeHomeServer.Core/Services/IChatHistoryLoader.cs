using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Узкий шов ChatHistoryService для выноса Spend (Этап 5). Spend использовал только
// `LoadAsync` — backfill читает историю сообщений сессии при первичном наполнении
// стора расхода. Полный ChatHistoryService содержит методы записи, восстановления
// и подписки — потребителям нужен узкий набор.
//
// Этап 5 (Turn): добавился `LastWriteUtc` — PersonaRecallContributor использует его
// как кеш-ключ «якоря прошлого хода» (ADR-004 §5): кеш держится по last-write времени
// истории и инвалидируется при изменении. Расширение того же интерфейса — лучше
// второго шва: оба члена живут на одном объекте истории, контракт «доступ к
// истории сессии» остаётся одним. virtual в ChatHistoryService — на случай подмены
// в тестах; интерфейс сохраняет ту же свободу, позволяя мокать зависимость без
// наследования от конкретного типа.
public interface IChatHistoryLoader
{
    // Загружает историю сообщений сессии по её claudeSessionId.
    Task<List<StoredMessage>> LoadAsync(string claudeSessionId);

    // Возвращает время последней записи в истории сессии или null, если истории
    // нет. Используется как метка кеша «якоря прошлого хода» — кеш инвалидируется
    // при изменении истории.
    DateTime? LastWriteUtc(string claudeSessionId);
}
