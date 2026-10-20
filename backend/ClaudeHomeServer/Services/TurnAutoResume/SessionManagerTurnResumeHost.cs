using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm.Claude;

// Namespace корня Services намеренно: адаптер знает SessionManager/ProjectManager, а вертикаль
// ClaudeHomeServer.Services.TurnAutoResume на них ссылаться не вправе (SubsystemBoundaryTests).
namespace ClaudeHomeServer.Services;

/// <summary>
/// Боевая реализация шва <see cref="ITurnAutoResumeHost"/>: тонкая обёртка над
/// SessionManager, ProjectManager, флагами и шлюзом устройства проекта.
/// </summary>
public sealed class SessionManagerTurnResumeHost(
    SessionManager sessions,
    ProjectManager projects,
    FeatureFlagService flags,
    IProjectDeviceGate deviceGate) : ITurnAutoResumeHost
{
    public Task AbortMarkersWritten => sessions.AbortMarkersWritten;

    public IReadOnlyList<string> TakeCandidates() => sessions.TakeRestartResumeCandidates();

    public Session? GetSession(string sessionId) => sessions.GetSessionInfo(sessionId);

    public Project? GetProject(string projectId) => projects.GetById(projectId);

    // Владелец через ResolveOwnerId: у проектной сессии OwnerId пустой — владелец берётся
    // из проекта (ADR-033). null (чат/проект без владельца) — флаг считается выключенным.
    public bool IsEnabledFor(Session session)
    {
        var owner = sessions.ResolveOwnerId(session);
        return owner is not null && flags.IsEnabled(owner, FeatureFlagKeys.TurnAutoResume);
    }

    // Серверный проект шлюз считает готовым; локальный — по состоянию устройства (матрица ADR-016)
    public bool IsDeviceOnline(Project project) => deviceGate.Check(project).IsReady;

    public bool LastTurnWasDeployOrRollback(Session session, Project? project)
    {
        // Транскрипт локального проекта живёт на устройстве — сервер его не читает (ADR-016)
        if (project is not null && !ProjectCapabilities.TranscriptOnServer(project)) return false;
        if (session.ClaudeSessionId is not { } csid) return false;
        var cwd = session.WorktreePath ?? project?.RootPath ?? "";
        var transcript = TranscriptProbe.FindMainTranscript(cwd, csid);
        return TranscriptProbe.LastToolUseIsDeployOrRollback(transcript);
    }

    public async Task<TurnResumeSendResult> ResumeAsync(string sessionId)
    {
        var r = await sessions.ResumeAfterRestartAsync(sessionId);
        return new TurnResumeSendResult(r.Sent, r.Reason);
    }
}
