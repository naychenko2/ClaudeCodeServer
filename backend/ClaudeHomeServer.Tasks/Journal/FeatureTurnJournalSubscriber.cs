using System.Threading.Channels;
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
// Контракт для Б5 (timeline): TurnCompleted публикует ТОЛЬКО CLI-адаптер. Ходы локальной
// модели (ILocalLlmClient мимо CLI) и устройства в журнал НЕ попадают → timeline по таким
// ходам приблизителен (их нет в журнале, и интервалы считаются только по ходам CLI).
//
// Создание: IHostedService (Tasks, не Main — граница подсистем держится), регистрация
// явная в TasksSubsystem.Register (AddGatedHostedService) — НЕ побочный эффект создания
// TaskSchedulerService (тот был хрупкой связью: убрали параметр — журнал ходов молча пуст).
//
// Запись диска — в фоне: обработчик хода только enqueue'ит запись в очередь, фоновый
// потребитель (один поток) делает Append. Порядок записей = порядок enqueue (один
// потребитель); идемпотентность — дедуп при чтении (FeatureJournalStore.DedupTurns), не здесь.
public sealed class FeatureTurnJournalSubscriber : IHostedService
{
    private readonly TaskManager _tasks;
    private readonly FeatureJournalStore _journal;
    private readonly ILogger<FeatureTurnJournalSubscriber>? _log;

    // Очередь записей: синхронная запись диска (Append: чтение+append) уходит с потока
    // завершения хода. Unbounded — не блокируем ход; один потребитель → порядок сохраняется.
    private readonly Channel<QueuedWrite> _queue = Channel.CreateUnbounded<QueuedWrite>();
    // Счётчик «в работе» (enqueue'd, ещё не записан) + сигнал «очередь опустела» для
    // WaitForDrainAsync (тесты: дождаться, пока фоновая запись дошла до диска).
    private readonly object _drainGate = new();
    private int _inFlight;
    private TaskCompletionSource? _drained;
    // Задача фонового потребителя: StopAsync дожидается её, чтобы очередь дописалась
    // до диска при остановке хоста (ApplicationStopping), а не жила до конца процесса.
    private Task? _consumeLoop;

    private sealed record QueuedWrite(string OwnerId, string FeatureId, FeatureJournalEntry Entry);

    public FeatureTurnJournalSubscriber(TaskManager tasks, FeatureJournalStore journal,
        ITurnEventBus bus, ILogger<FeatureTurnJournalSubscriber>? log = null)
    {
        _tasks = tasks;
        _journal = journal;
        _log = log;
        // Подписка в конструкторе: экземпляр создаётся при старте хоста (IHostedService),
        // и к первому завершённому ходу подписчик уже на шине.
        bus.OnNotification<TurnCompleted>(HandleTurnCompleted, "Tasks.FeatureTurnJournal");
        // Фоновый потребитель: один поток, порядок записей = порядок enqueue.
        _consumeLoop = Task.Run(ConsumeLoopAsync);
    }

    // IHostedService: создание при старте хоста (подписка + очередь развёрнуты в конструкторе).
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Остановка хоста: завершаем очередь (ReadAllAsync дописывает остаток и выходит) и
    // дожидаемся фонового потребителя — принятые записи дойдут до диска, цикл не живёт
    // до конца процесса. TryComplete (не Complete): повторный StopAsync (двойная остановка
    // хоста) идемпотентен и не бросает ChannelClosedException.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        if (_consumeLoop is not null)
        {
            try { await _consumeLoop; }
            catch { /* сбой потребителя уже залогирован внутри цикла */ }
        }
    }

    // Дождаться, пока все принятые записи дойдут до диска (тесты). Возвращает Task,
    // завершающийся, когда очередь пуста и текущая запись сделана.
    public Task WaitForDrainAsync()
    {
        lock (_drainGate)
        {
            if (_inFlight == 0) return Task.CompletedTask;
            return (_drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    // Один завершённый ход → одна запись в очередь (disk write — в фоне, не здесь).
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
            var entry = new FeatureJournalEntry
            {
                // Момент события — конец хода; у хода без собранного паспорта — сейчас.
                At = passport?.EndedAt ?? DateTime.UtcNow,
                Kind = "turn",
                TaskId = task.Id,
                SessionId = sessionId,
                // turnSeq — из контекста хода (в паспорте поля нет); у TurnCompleted
                // всегда реальный номер поданного хода.
                TurnSeq = e.Turn.TurnSeq,
                StartedAt = passport?.StartedAt,
                EndedAt = passport?.EndedAt,
                // Outcome/ErrorClass: паспорт и событие несут одно значение; паспорт
                // первичен, событие — страховка, когда паспорт не собран.
                Outcome = passport?.Outcome ?? e.Outcome,
                ErrorClass = passport?.LastErrorClass ?? e.ErrorClass,
                Model = passport?.FinalModel,
            };

            // Счётчик «в работе» — ДО TryWrite: очередь unbounded (TryWrite не падает),
            // а consumer мог бы decrement'нуть раньше инкремента → отрицательный счёт.
            lock (_drainGate)
            {
                _inFlight++;
            }
            // TryWrite false — очередь уже завершена (StopAsync): потребитель запись не
            // прочитает, поэтому уменьшаем счётчик сами, иначе WaitForDrainAsync зависнет
            // на «в работе» (запись не дойдёт до потребителя).
            if (!_queue.Writer.TryWrite(new QueuedWrite(task.OwnerId ?? "", featureId, entry)))
            {
                lock (_drainGate)
                {
                    _inFlight--;
                }
            }
        }
        catch (Exception ex)
        {
            // Сбой не роняет подписчика/шину: Notification гасит, но держим и свой код.
            _log?.LogWarning(ex, "Журнал фичи: не удалось принять ход {Session}", e.Turn.SessionId);
        }
        return Task.CompletedTask;
    }

    // Фоновый потребитель: читает очередь и пишет в хранилище. Один поток → порядок.
    private async Task ConsumeLoopAsync()
    {
        await foreach (var write in _queue.Reader.ReadAllAsync())
        {
            try
            {
                _journal.Append(write.OwnerId, write.FeatureId, write.Entry);
            }
            catch (Exception ex)
            {
                // Сбой одной записи не роняет цикл: остальные ходы всё равно пишутся.
                _log?.LogWarning(ex, "Журнал фичи: не удалось записать ход {Feature}", write.FeatureId);
            }
            finally
            {
                TaskCompletionSource? toComplete = null;
                lock (_drainGate)
                {
                    _inFlight--;
                    if (_inFlight == 0 && _drained is not null)
                    {
                        toComplete = _drained;
                        _drained = null;
                    }
                }
                toComplete?.SetResult();
            }
        }
    }
}
