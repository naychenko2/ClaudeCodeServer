using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Чистая утилита подписи персоны для логов и уведомлений: «Роль (Имя)» либо просто имя.
// Не поведение сервиса, а stateless форматирование — вынесено из PersonaManager в Core
// (Этап 5, вынос Tasks), потому что читатели (TaskManager, DailyBriefingService,
// PersonaMemoryService, ChatTurnLoggerService, командные сервисы TeamPlan/TeamDecision,
// PersonaBindingsService) — все из разных вертикалей и тянули ради этого
// `PersonaManager` целиком. Теперь зависимость — один тип в Core.
public static class PersonaLabel
{
    /// <summary>Подпись персоны: «Роль (Имя)», если задана роль, иначе просто имя.</summary>
    public static string Of(Persona p) =>
        string.IsNullOrEmpty(p.Role) ? p.Name : $"{p.Role} ({p.Name})";
}
