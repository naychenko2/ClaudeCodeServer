using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Узкий шов PersonaAutomationService для выноса Tasks (Этап 5, волна 1).
// TaskSchedulerService на каждом тике зовёт MaybeRunAutomationsAsync — единственный
// метод, нужный вертикали Tasks. TestAsync остаётся в Main для ручного прогона
// правила через API и в шов не входит — Tasks им не пользуется.
public interface IPersonaAutomationRunner
{
    Task MaybeRunAutomationsAsync(User user, TimeZoneInfo tz, DateTime nowUtc, CancellationToken ct);
}