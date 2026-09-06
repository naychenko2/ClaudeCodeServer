using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Team;

// Координатор режима «Командная реализация» (этап 4, шаг 2г-3а плана выноса штаба,
// docs/research/session-core-split-2026-09.md). Сейчас здесь живут автономные утилиты
// планирования (тексты отказа и широковещания о старте/финише планировщика). Это
// минимальный заводимый блок, чтобы следующие шаги 2г-3б/3в/3г наращивали класс
// дальше, оставляя в SessionManager тонкие обёртки-делегаты.
//
// Owning-паттерн: экземпляр создаётся в конструкторе SessionManager, а не через DI
// (TeamPlanningService приходит из SessionManager). Так разорван цикл «SessionManager
// хочет TeamCoordinator, TeamCoordinator хочет SessionManager» без Lazy<T> и без новых
// швов на этом шаге. Когда придёт шаг 2г-4, регистрация переедет в Program.cs, а
// owning-обёртки в SessionManager будут сняты.
//
// На этом шаге швов нет: TeamCoordinator использует собственную зависимость SignalR
// (как TeamWaveService), а статические утилиты не ссылаются на состояние ядра.
internal sealed class TeamCoordinator
{
    private readonly IHubContext<SessionHub> _hub;

    internal TeamCoordinator(IHubContext<SessionHub> hub)
    {
        _hub = hub;
    }

    // Текст причины отказа для карточки (по Failure): разные советы под разные корни —
    // обрыв по токенам не то же, что «уточните задачу», и таймаут не вина человека.
    public static string PlannerFailureReason(TeamPlanningService.Failure f) => f switch
    {
        TeamPlanningService.Failure.TimedOut => TeamPlanningService.PlannerTimeoutReason,
        TeamPlanningService.Failure.Truncated => TeamPlanningService.PlannerTruncatedReason,
        TeamPlanningService.Failure.InvalidJson => TeamPlanningService.PlannerInvalidJsonReason,
        _ => "Планировщик не смог построить план — уточните задачу",
    };

    // Заголовок и тело карточки отказа планировщика: для СВЕЖЕЙ вводной (не правки).
    // reason — строковый ключ причины (PlannerTimeoutReason / PlannerTruncatedReason /
    // PlannerInvalidJsonReason / fallback «Планировщик не смог…»). У каждой причины —
    // своё название и свой совет: таймаут «не ваша вина, повторите», обрыв «план не
    // уместился в лимит, попробуйте короче», невалидный JSON «повторите».
    public static (string Title, string Details) FreshFailureText(string request, string? reason) =>
        reason switch
        {
            TeamPlanningService.PlannerTimeoutReason => (
                "План не построился: планировщик не уложился во время",
                TeamImplementPrompts.PlanTimeoutDetails(request)),
            TeamPlanningService.PlannerTruncatedReason => (
                "План не построился: планировщик не уместил план в лимит вывода",
                TeamImplementPrompts.PlanTruncatedDetails(request)),
            TeamPlanningService.PlannerInvalidJsonReason => (
                "План не построился: планировщик вернул неразборчивый план",
                TeamImplementPrompts.PlanInvalidJsonDetails(request)),
            _ => (
                "План по вашей вводной не построился",
                TeamImplementPrompts.PlanFailedDetails(request, reason)),
        };

    // Заголовок и тело карточки отказа для ПРАВКИ: «Изменить план» отдельно от
    // первоначальной вводной, потому что старая карточка уже погашена.
    public static (string Title, string Details) EditFailureText(string feedback, string? reason) =>
        reason switch
        {
            TeamPlanningService.PlannerTimeoutReason => (
                "План не пересобрался: планировщик не уложился во время",
                TeamImplementPrompts.PlanEditTimeoutDetails(feedback)),
            TeamPlanningService.PlannerTruncatedReason => (
                "План не пересобрался: планировщик не уместил правку в лимит вывода",
                TeamImplementPrompts.PlanEditTruncatedDetails(feedback)),
            _ => (
                "Правка не привела к новой версии плана",
                TeamImplementPrompts.PlanEditFailedDetails(feedback, reason)),
        };

    // Событие «планировщик запущен» для ленты. Контракт (для Киры):
    //  • start=true  — планировщик запущен, фронт рисует «Штаб планирует…» и блокирует
    //                   кнопки повтора. Остальные поля диагностические (для логов).
    //  • start=false — планировщик закончил: Success=true → SubtaskCount/WaveCount/Route;
    //                   Success=false → Failure (тот же текст, что в карточке отказа).
    // Событие ТРАНЗИТНОЕ: в историю не пишется (карточка плана или карточка отказа уже там,
    // дублировать не надо), и при рестарте сервера не восстанавливается — спиннер просто
    // не показывается, карточка подтянется через /api/.../history.
    public Task BroadcastTeamPlanningStartedAsync(string sessionId, TeamPlanningService.Result r, string? plannerPersonaId) =>
        _hub.Clients.Group(sessionId).SendAsync("message", new TeamPlanningMessage(
            Start: true,
            Success: false,
            SubtaskCount: 0,
            WaveCount: 0,
            ElapsedMs: 0,
            Route: r.Route?.Model,
            Failure: null,
            PersonaId: plannerPersonaId,
            PromptChars: r.PromptChars,
            ResponseChars: 0) with { SessionId = sessionId });

    public Task BroadcastTeamPlanningFinishedAsync(string sessionId, TeamPlanningService.Result r, string? plannerPersonaId) =>
        _hub.Clients.Group(sessionId).SendAsync("message", new TeamPlanningMessage(
            Start: false,
            Success: r.Plan is not null,
            SubtaskCount: r.Plan?.Subtasks.Count ?? 0,
            WaveCount: r.Plan?.WaveCount ?? 0,
            ElapsedMs: (long)r.Elapsed.TotalMilliseconds,
            Route: r.Route?.Model,
            Failure: r.Plan is null ? PlannerFailureReason(r.Failure) : null,
            PersonaId: plannerPersonaId,
            PromptChars: r.PromptChars,
            ResponseChars: r.ResponseChars) with { SessionId = sessionId });
}
