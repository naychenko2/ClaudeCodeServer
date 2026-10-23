using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

/// <summary>Исход отправки resume-хода: Sent=false — отказ с причиной для лога.</summary>
public sealed record TurnResumeSendResult(bool Sent, string? Reason = null);

/// <summary>
/// Шов между фоновым сервисом и ядром (SessionManager и соседи). Вертикаль TurnAutoResume
/// не вправе ссылаться на корень Services (сторож SubsystemBoundaryTests), поэтому всё
/// нужное сервису — через этот интерфейс; реализация живёт в Main (SessionManagerTurnResumeHost).
/// </summary>
public interface ITurnAutoResumeHost
{
    /// <summary>Маркеры обрыва ходов записаны в историю (иначе карточка/«Продолжи» обгонят маркер).</summary>
    Task AbortMarkersWritten { get; }

    /// <summary>Одноразовая выдача id кандидатов; повторный вызов — пусто.</summary>
    IReadOnlyList<string> TakeCandidates();

    Session? GetSession(string sessionId);

    Project? GetProject(string projectId);

    /// <summary>
    /// Флаг turn-auto-resume владельца чата. Владелец резолвится через ResolveOwnerId:
    /// у проектной сессии OwnerId пустой — владелец берётся из проекта (ADR-033, дефект:
    /// проверка по пустому OwnerId отсеивала все проектные чаты).
    /// </summary>
    bool IsEnabledFor(Session session);

    /// <summary>Готово ли устройство локального проекта принять ход (серверный проект — всегда да).</summary>
    bool IsDeviceOnline(Project project);

    /// <summary>Хвост транскрипта: последний tool_use без result — deploy_start/deploy_rollback.</summary>
    bool LastTurnWasDeployOrRollback(Session session, Project? project);

    Task<TurnResumeSendResult> ResumeAsync(string sessionId);

    /// <summary>
    /// Разобрать очереди отложенных сообщений, восстановленные с диска, у чатов без живого хода
    /// (после цикла возобновления: возобновлённый ход разберёт очередь по своему концу).
    /// </summary>
    Task DrainRestoredQueuesAsync();
}

