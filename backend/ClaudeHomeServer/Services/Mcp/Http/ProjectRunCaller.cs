using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.TestRuns;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Общая часть тулсетов, которые исполняют код проекта в рабочем дереве чата (tests: run_tests,
/// dev: build): маршрут <c>/mcp/{сервер}/{sessionId}</c>, гейты вызывателя и живые этапы на
/// карточку. Тексты отказов — у каждого тулсета свои, гейты — одни на оба.
/// </summary>
internal sealed class ProjectRunCaller(
    SessionManager sessions,
    ProjectManager projects,
    PersonaManager personas,
    ISessionBroadcaster? broadcaster)
{
    // Тексты отказов тулсета: что делает инструмент и чем его заменить
    internal sealed record Texts(string BadRoute, string ProjectOnly, string LocalProject, string ReadOnly,
        string NoBash);

    // Один сегмент — id сессии; форма как у local-media (белый список resumeSessionId)
    private static bool TryParseRoute(string? route, out string sessionId)
    {
        sessionId = "";
        if (route is null || route.Split('/').Length != 1) return false;
        if (route.Length is < 1 or > 128 || !route.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        sessionId = route;
        return true;
    }

    public bool TryResolveSession(McpToolCallContext context, Texts texts, out Session session, out string error)
    {
        session = null!;
        error = "";
        if (!TryParseRoute(context.RouteTail, out var sessionId))
        {
            error = texts.BadRoute;
            return false;
        }
        var owned = sessions.GetOwned(sessionId, context.OwnerId);
        if (owned is null)
        {
            error = "Чат-вызыватель не найден или принадлежит другому владельцу — доступ закрыт.";
            return false;
        }
        session = owned;
        return true;
    }

    // Проект чата на сервере (ADR-016 — локальный проект в v1 не поддерживаем) и персона, которой
    // не запрещён Bash: инструмент исполняет код проекта — это тот же запуск кода, что и Bash
    public bool TryResolveProject(Session session, string ownerId, Texts texts, out Project project, out string error)
    {
        project = null!;
        error = "";
        if (session.ProjectId is null)
        {
            error = texts.ProjectOnly;
            return false;
        }
        var found = projects.GetById(session.ProjectId);
        if (found is null || !string.Equals(found.OwnerId, ownerId, StringComparison.Ordinal))
        {
            error = "Проект чата не найден.";
            return false;
        }
        if (!ProjectCapabilities.FilesOnServer(found))
        {
            error = texts.LocalProject;
            return false;
        }
        if (session.PersonaId is { } personaId && personas.Get(personaId, ownerId) is { } persona
            && !PersonaAccessPolicy.AllowsBash(persona))
        {
            error = persona.Access == PersonaAccess.ReadOnly ? texts.ReadOnly : texts.NoBash;
            return false;
        }
        project = found;
        return true;
    }

    // Рабочее дерево чата: worktree, если он есть, иначе корень проекта
    public static string RootOf(Session session, Project project) =>
        session.WorktreePath is { Length: > 0 } worktree ? worktree : project.RootPath;

    public static McpToolCallResult Deny(string text) => new(text, IsError: true);

    public static string? StringArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // Шлёт фазу в чат-вызыватель мимо CLI (CLI notifications/progress в stream-json не
    // пробрасывает). Потеря события безвредна — следующая фаза пришлёт новое. Каждое событие
    // несёт полный снимок этапов; смена этапа сразу пишется и в историю вызова
    public void SendProgress(string sessionId, string? toolUseId, TestRunProgress progress, TestRunStages stages)
    {
        if (toolUseId is null) return;
        var changed = stages.Advance(progress.Stage);
        var snapshot = stages.Snapshot();
        if (changed) sessions.RecordToolStages(sessionId, toolUseId, snapshot, totals: null, persist: false);
        Broadcast(sessionId, new ToolProgressMessage(toolUseId, Stage: progress.Stage, Label: progress.Label,
            Percent: progress.Percent, Exact: progress.Exact, Stages: snapshot) { SessionId = sessionId });
    }

    // Конец вызова (в том числе обрыв): последний этап закрывается, итог уходит на карточку и
    // сразу на диск — tool_result после «Стопа» может не прийти вовсе. failed — оборвалось или
    // упало на последнем этапе; totals — счётчики тестов (у сборки их нет)
    public void SendFinal(string sessionId, string? toolUseId, TestRunStages stages, bool failed, ToolRunTotals? totals)
    {
        if (toolUseId is null || stages.Snapshot().Count == 0) return;
        var final = stages.Finish(failed);
        sessions.RecordToolStages(sessionId, toolUseId, final, totals, persist: true);
        Broadcast(sessionId, new ToolProgressMessage(toolUseId, Stages: final, Totals: totals) { SessionId = sessionId });
    }

    private void Broadcast(string sessionId, ToolProgressMessage message)
    {
        if (broadcaster is null) return;
        _ = SendAsync();

        async Task SendAsync()
        {
            try { await broadcaster.ToSession(sessionId, message); }
            catch { /* живое событие */ }
        }
    }
}
