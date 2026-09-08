using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Узкий шов DailyBriefingService для выноса Tasks (Этап 5, волна 1).
// TaskSchedulerService на каждом тике зовёт MaybeRunScheduledAsync — единственный
// метод, нужный вертикали Tasks. On-demand GenerateAsync остаётся в Main
// (BriefingController) и в шов не входит — Tasks им не пользуется.
public interface IDailyBriefingRunner
{
    Task MaybeRunScheduledAsync(User user, TimeZoneInfo tz, DateTime nowUtc, CancellationToken ct = default);
}