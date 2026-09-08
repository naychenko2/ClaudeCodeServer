using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Реализация IDailyBriefingRunner (Core) поверх DailyBriefingService (Main).
// Тонкий форвардер: вертикаль Tasks через шов зовёт утренний бриф, ничего не зная
// о Llm/Notes/Projects/Users/Personas/Push/NotificationStore, которые сам бриф собирает.
// On-demand GenerateAsync остаётся за BriefingController в Main и в шов не входит.
public sealed class DailyBriefingRunnerAdapter(DailyBriefingService briefing) : IDailyBriefingRunner
{
    public Task MaybeRunScheduledAsync(User user, TimeZoneInfo tz, DateTime nowUtc, CancellationToken ct = default) =>
        briefing.MaybeRunScheduledAsync(user, tz, nowUtc, ct);
}