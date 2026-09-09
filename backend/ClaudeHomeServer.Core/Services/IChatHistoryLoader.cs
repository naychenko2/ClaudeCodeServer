using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Узкий шов ChatHistoryService для выноса Spend (Этап 5). Spend использует только
// `LoadAsync` — backfill читает историю сообщений сессии при первичном наполнении
// стора расхода. Полный ChatHistoryService содержит методы записи, восстановления
// и подписки — Spend-у нужен только этот. virtual в ChatHistoryService — на случай
// подмены в тестах; интерфейс сохраняет ту же свободу, позволяя мокать зависимость
// без наследования от конкретного типа.
public interface IChatHistoryLoader
{
    // Загружает историю сообщений сессии по её claudeSessionId.
    Task<List<StoredMessage>> LoadAsync(string claudeSessionId);
}
