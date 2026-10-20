using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.Tasks.Journal;

// Подписчик ходов фичи (волна 3 «Обзор фич», Б8c) — «Источник 2» журнала (разрез
// Александра §3). По завершении хода сессии, привязанной к задаче фичи, пишет в
// FeatureJournalStore строку kind=turn. Источник 1 (переходы задач) пишет TaskManager.
//
// Правила:
// - Задача находится по sessionId через TaskManager.GetBySession (LinkedSessionId),
//   без SessionManager. Чат без привязки (общий оркестраторский/ключевой/куратор) —
//   нет записи.
// - Пишется только если у задачи есть фича: featureId = (Role==feature ? Id : FeatureId).
//   Задача без фичи — нет записи.
// - Поля — из паспорта TurnRunPassport: startedAt/endedAt/outcome/errorClass/model(FinalModel).
//   turnSeq — из контекста хода (в паспорте поля нет), taskId — id задачи.
// - Сбой не роняет подписчика/шину: Notification-шина сама гасит исключения подписчиков,
//   страховка try/catch держит ещё и наш собственный код (GetBySession, сборка записи).
//
// Создание при старте: plain singleton (не IHostedService — фоновых циклов нет),
// подписка — в конструкторе. Гарантированное создание при старте — через зависимость
// TaskSchedulerService (hosted, стартует первым); см. TasksSubsystem.Register.
public sealed class FeatureTurnJournalSubscriber
{
    private readonly TaskManager _tasks;
    private readonly FeatureJournalStore _journal;
    private readonly ILogger<FeatureTurnJournalSubscriber>? _log;

    public FeatureTurnJournalSubscriber(TaskManager tasks, FeatureJournalStore journal,
        ITurnEventBus bus, ILogger<FeatureTurnJournalSubscriber>? log = null)
    {
        _tasks = tasks;
        _journal = journal;
        _log = log;
        // Подписка в конструкторе: экземпляр создаётся при старте (hosted-зависимость),
        // и к первому завершённому ходу подписчик уже на шине.
        bus.OnNotification<TurnCompleted>(HandleTurnCompleted, "Tasks.FeatureTurnJournal");
    }

    // Один завершённый ход → одна строка kind=turn в журнале фичи.
    private Task HandleTurnCompleted(TurnCompleted e)
    {
        try
        {
            var sessionId = e.Turn.SessionId;

            // Задача, к которой привязана сессия (Claude-исполнитель). Чат без привязки
            // (общий оркестраторский/ключевой/куратор) → нет записи.
            var task = _tasks.GetBySession(sessionId);
            if (task is null) return Task.CompletedTask;

            // featureId: у эпика (Role=feature) — его собственный id, у дочерней — FeatureId.
            // Задача без фичи → нет записи.
            var featureId = task.Role == TaskRoles.Feature ? task.Id : task.FeatureId;
            if (featureId is null) return Task.CompletedTask;

            var passport = e.Passport;
            _journal.Append(task.OwnerId ?? "", featureId, new FeatureJournalEntry
            {
                // Момент события — конец хода; у хода без собранного паспорта — сейчас.
                At = passport?.EndedAt ?? DateTime.UtcNow,
                Kind = "turn",
                TaskId = task.Id,
                SessionId = sessionId,
                // turnSeq — из контекста хода (в паспорте поля нет); у TurnCompleted всегда
                // реальный номер поданного хода.
                TurnSeq = e.Turn.TurnSeq,
                StartedAt = passport?.StartedAt,
                EndedAt = passport?.EndedAt,
                // Outcome/ErrorClass: паспорт и событие несут одно значение; паспорт первичен,
                // событие — страховка, когда паспорт не собран (событие уходит и без него).
                Outcome = passport?.Outcome ?? e.Outcome,
                ErrorClass = passport?.LastErrorClass ?? e.ErrorClass,
                Model = passport?.FinalModel,
            });
        }
        catch (Exception ex)
        {
            // Сбой не роняет подписчика/шину: Notification гасит, но держим и свой код.
            _log?.LogWarning(ex, "Журнал фичи: не удалось записать ход {Session}", e.Turn.SessionId);
        }
        return Task.CompletedTask;
    }
}
