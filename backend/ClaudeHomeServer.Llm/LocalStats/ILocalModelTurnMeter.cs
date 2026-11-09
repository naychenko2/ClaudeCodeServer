namespace ClaudeHomeServer.Services.Llm;

// Живой чат на локальном движке: связь сессии с владельцем, проектом, названием и
// текущей скоростью генерации. Потребители — Main (панель «Локальные модели» и
// живые события чата): направление Main → Llm через ссылку, реализация — в Llm.
public sealed record TurnChatLive(
    string SessionId,
    string OwnerId,
    string? ProjectId,
    string? Title,
    double? GenTps,
    string Phase);

// Живой вид счётчика хода (LocalModelTurnMeter): чаты, идущие на локальном движке
// прямо сейчас, и итог последнего завершённого хода сессии. Счётчик — singleton,
// этот интерфейс — его второе имя (тот же инстанс, см. регистрацию в LlmSubsystem).
public interface ILocalModelTurnMeter
{
    // Чаты с запросом в работе на локальном движке: фаза, скорость, владелец,
    // проект и название. Пусто — движок не занят (или ходы не идут через шлюз).
    IReadOnlyList<TurnChatLive> ActiveChats();

    // Итог последнего завершённого хода сессии; null — ходов ещё не было (или рестарт).
    LocalModelTurnSummary? GetLastTurn(string sessionId);
}
