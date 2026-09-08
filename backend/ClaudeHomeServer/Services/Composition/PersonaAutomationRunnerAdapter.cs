using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Реализация IPersonaAutomationRunner (Core) поверх PersonaAutomationService (Main).
// Тонкий форвардер: вертикаль Tasks через шов зовёт тик проактивности, ничего не зная
// о rules-движке, источниках-триггерах и сторах состояний, которые сама проактивность
// собирает. TestAsync остаётся в Main и в шов не входит.
public sealed class PersonaAutomationRunnerAdapter(PersonaAutomationService automation)
    : IPersonaAutomationRunner
{
    public Task MaybeRunAutomationsAsync(User user, TimeZoneInfo tz, DateTime nowUtc, CancellationToken ct) =>
        automation.MaybeRunAutomationsAsync(user, tz, nowUtc, ct);
}