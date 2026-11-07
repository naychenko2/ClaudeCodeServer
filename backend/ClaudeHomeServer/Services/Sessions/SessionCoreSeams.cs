using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Sessions;

// Швы ядра сессий (S3-0 §3, docs/research/session-manager-split-2026-11.md): именованные
// разрывы циклов между ролями, вынесенными из SessionManager. Пять допустимых аргументов
// Lazy<T> держит SessionCoreShapeGuardTests (правило 3); здесь — объявления швов.
// Реализация связывается в AddSessionCore как Lazy<T> от синглтона; до шага, где вынесен
// настоящий владелец, интерфейс реализует SessionManager (переходный режим).

// «Начать ход»: роль очереди при постановке сообщения может форсировать разбор очереди
// (переход 0→1 при свободном чате) — это рекурсия «очередь → приём хода»; добивание
// оборванного сабагента (TurnPassports) поднимает ход тем же путём. С3-4 объявляет шов
// целиком, по всем точкам, которыми сейчас пользуется SessionManager; владелец станет
// отдельной ролью приёма хода в S3-7.
//
// Две оговорки к «сигнатурам как у SessionManager»:
// — параметры без значений по умолчанию: их не принимает интерфейс; делегирующая реализация
//   на фасаде подставляет те же значения, что были у вызовов;
// — AddWorkLoopStoppedNoticeAsync и AppendServerRestartCardAsync берут sessionId, а не
//   SessionEntry: тип записи internal, а шов публичный (CS0051), entry реализуется на фасаде.
// public ТОЛЬКО потому, что Lazy<ITurnStarter> — параметр публичного конструктора
// PendingQueueService (CS0051: менее доступный тип в параметре доступного конструктора);
// публичной поверхности у шва нет.
public interface ITurnStarter
{
    Task<SessionManager.SendUserOutcome> SendMessageAsync(string sessionId, string text,
        IReadOnlyList<string> attachedPaths, string? mode, bool systemDirective, bool auto,
        string? senderPersonaId, bool suppressTasksExecute, string? senderOrigin,
        string? senderConnectionId, string? staffNote, SessionManager.DeliveryCause cause,
        int agentDepth, string? senderUserId);

    Task<bool> SendOrEnqueueAsync(string sessionId, string text, string? senderPersonaId,
        string? senderOrigin, bool silent, bool suppressTasksExecute, string? staffNote,
        SessionManager.PendingKind kind, string? senderUserId);

    Task DrainNextPendingAsync(string sessionId);

    Task ContinueWorkLoopAsync(string sessionId);

    Task NudgeTruncatedSubagentAsync(string sessionId, SubagentRunPassport run, int attempt);

    Task AddWorkLoopStoppedNoticeAsync(string sessionId, string reason, string text);

    Task AppendServerRestartCardAsync(string sessionId);
}
