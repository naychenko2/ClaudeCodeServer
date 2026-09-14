namespace ClaudeHomeServer.Services;

// Шов для слота модели в global-слое AppSettings (TierModel). Llm нужен только
// фактический id модели по уровню — без знаний о сторах, секциях конфига и
// источниках (конфиг vs файл vs override). Полный AppSettingsService к этому
// шву отношения не имеет: за чтение глобальных слотов отвечает узкая функция,
// а весь класс с Get()/Save()/ClaudeBilling остаётся в Main.
//
// Прецедент «один метод — один шов» уже есть: IKnowledgeNotificationDispatcher
// отрезает Knowledge от NotificationService тем же приёмом (узкая обёртка в Core,
// адаптер в Main поверх существующего сервиса).
public interface ITierModelResolver
{
    // Модель глобального слота инстанса для уровня (ADR-007 §2). null — слот
    // не задан, резолвер падает дальше (в личный слот владельца / место).
    string? TierModel(ModelTier tier);
}
