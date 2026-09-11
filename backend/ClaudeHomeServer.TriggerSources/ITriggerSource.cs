using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.TriggerSources;

// Событие срабатывания правила: источник детектировал, что триггер наступил.
public sealed record TriggerEvent(
    string RuleId,
    AutomationTriggerType Type,
    // Человекочитаемое описание для gate-промпта персоны («Новый коммит abc123 в project X»,
    // «Заметка «Бэклог» изменена», «Задача «Релиз» перешла Todo → Done»)
    string Summary,
    // Доп. key→value для gate-промпта (пути файлов, автор коммита, теги…) — может быть null
    IReadOnlyDictionary<string, string>? Details = null,
    // Для Mention — чат, где упомянули персону (реакция пойдёт в него, а не в закреплённый)
    string? OriginSessionId = null);

// Контекст оценки правила: источник/executor видят всё о текущем правиле и моменте.
public sealed record TriggerContext(
    User User,
    Persona Persona,
    PersonaAutomationRule Rule,
    TimeZoneInfo Tz,
    DateTime NowUtc,
    RuleRuntimeState State);

// Источник событий триггеров. Poll-источники (Timer/File/Note/GitCommit/TaskStatus) вызываются
// из тика TaskSchedulerService → PersonaAutomationService.MaybeRunAutomationsAsync; Mention —
// push-источник (подписка на SessionManager.OnUserMessage), из тика не зовётся, но реализует
// интерфейс для единообразия запуска экшена.
public interface ITriggerSource
{
    AutomationTriggerType Type { get; }
    // Детектить срабатывания для правила (0..N событий). Источник сам читает/обновляет снапшоты
    // в ctx.State; персистентность — забота вызывающего (Save после оценки).
    Task<IReadOnlyList<TriggerEvent>> EvaluateAsync(TriggerContext ctx, CancellationToken ct);
}
