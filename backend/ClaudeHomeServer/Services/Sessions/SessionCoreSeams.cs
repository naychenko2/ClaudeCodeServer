using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Sessions;

// Швы ядра сессий (S3-0 §3, docs/research/session-manager-split-2026-11.md): именованные
// разрывы циклов между ролями, вынесенными из SessionManager. Пять допустимых аргументов
// Lazy<T> держит SessionCoreShapeGuardTests (правило 3); здесь — объявления швов.
// Реализация связывается в AddSessionCore как Lazy<T> от синглтона; до шага, где вынесен
// настоящий владелец, интерфейс реализует SessionManager (переходный режим).
// S3-7 добавляет шестой аргумент — переходный IWorkLoopContinuation (помечен в AllowedLazy
// сторожа): он живёт ровно до шага 2, когда цикл «до готово» уходит в свою роль.

// «Начать ход»: роль очереди при постановке сообщения может форсировать разбор очереди
// (переход 0→1 при свободном чате) — это рекурсия «очередь → приём хода». С3-4 объявляет
// шов целиком, по всем точкам, которыми сейчас пользуется SessionManager; с S3-7 шов
// сокращён до приёма (старт + разбор очереди) и его реализует ядро приёма TurnIntake:
// ход в сессии запускает только приём хода. Точки, которые ход не стартуют, с шва сняты:
// карточки пишет роль истории (SessionHistory), а её вызывающий (TurnRecovery,
// TurnProcessHost) идёт к роли напрямую; продолжение цикла и добивание сабагента просят
// фасад через переходный IWorkLoopContinuation ниже — их тело в SessionManager до шага 2.
//
// Оговорка к «сигнатурам как у SessionManager»: параметры без значений по умолчанию — их
// не принимает интерфейс; делегирующая реализация на фасаде подставляет те же значения,
// что были у вызовов.
// public ТОЛЬКО потому, что Lazy<ITurnStarter> — параметр публичного конструктора
// PendingQueueService (CS0051: менее доступный тип в параметре доступного конструктора);
// публичной поверхности у шва нет.
public interface ITurnStarter
{
    // Чтение признака штатной остановки (аддендум §2): проситель (TurnCompletion, цикл фасада)
    // использует его только как раннюю отсечку; писатель и решающая проверка — в ядре
    // (BeginShutdown / TrySubmitContinuationAsync под флагом ядра), иначе TOCTOU.
    bool IsStopping { get; }

    Task<SendUserOutcome> SendMessageAsync(string sessionId, string text,
        IReadOnlyList<string> attachedPaths, string? mode, bool systemDirective, bool auto,
        string? senderPersonaId, bool suppressTasksExecute, string? senderOrigin,
        string? senderConnectionId, string? staffNote, DeliveryCause cause,
        int agentDepth, string? senderUserId);

    Task<bool> SendOrEnqueueAsync(string sessionId, string text, string? senderPersonaId,
        string? senderOrigin, bool silent, bool suppressTasksExecute, string? staffNote,
        PendingKind kind, string? senderUserId);

    Task DrainNextPendingAsync(string sessionId);
}

// «Продолжить цикл „до готово“»: конец хода (TurnCompletion) по result просит следующую
// итерацию цикла, паспорт оборванного сабагента (TurnPassports) просит добивание — ни то,
// ни другое не стартует ход само и не должно видеть весь шов приёма. Тело цикла живёт на
// фасаде до шага 2 (вынос TurnContinuation), поэтому шов реализует SessionManager.
// Это НЕ шестой постоянный шов плана §3: переходный — снимается вместе с переносом цикла
// в TurnContinuation (там продолжение зовёт ядро через TrySubmitContinuation, шов не нужен).
// internal: его держат только роли ядра; конструкторы потребителей с ним — internal
// (TurnCompletion) или с internal-параметром (TurnPassports, CS0051).
internal interface IWorkLoopContinuation
{
    Task ContinueWorkLoopAsync(string sessionId);

    Task NudgeTruncatedSubagentAsync(string sessionId, SubagentRunPassport run, int attempt);
}

// «Сообщение хода в ленту»: процесс CLI (TurnProcessHost) и сборка контекста запуска
// (TurnLaunchContextFactory) отдают сообщения хода в ленту сессии — это обратный шов
// к SessionManager.OnMessageQueuedAsync, который держит разбор сообщения (end-of-turn,
// цены, карточки). S3-5 вводит шов и передаёт его процессу; перенос самого владельца
// (OnMessageQueuedAsync) — S3-6: там смена владельца правкой одной строки регистрации
// в AddSessionCore и одной строки запасной сборки в конструкторе SessionManager.
// internal: параметр сигнатуры — TurnAccumulator (internal класс ленты хода), поэтому публичной
// сигнатуры у шва быть не может (CS0051). Он нужен только ролям ядра: его держит internal-фабрика
// контекста (TurnLaunchContextFactory), а та входит в internal-конструктор хоста процесса.
internal interface ITurnMessageSink
{
    Task OnMessageQueuedAsync(string sessionId, TurnAccumulator accumulator,
        ServerMessage message, long runId);
}
