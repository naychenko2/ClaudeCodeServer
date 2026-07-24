using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Сбор расхода токенов из ходов чатов. Подписывается на SessionManager.OnSessionMessage,
// по ResultMessage пишет строку в SpendLogService. Другие три источника (one-shot, fal.ai,
// ollama/openrouter-direct) интегрированы в соответствующие сервисы напрямую.
public sealed class SpendCollectorService : IHostedService
{
    private readonly SessionManager _sessions;
    private readonly SpendLogService _spend;
    private readonly LlmProviderRegistry _providers;

    public SpendCollectorService(SessionManager sessions, SpendLogService spend,
        LlmProviderRegistry providers)
    {
        _sessions = sessions;
        _spend = spend;
        _providers = providers;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _sessions.OnSessionMessage += OnMsgAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _sessions.OnSessionMessage -= OnMsgAsync;
        return Task.CompletedTask;
    }

    private Task OnMsgAsync(Session session, ServerMessage msg)
    {
        if (msg is not ResultMessage result) return Task.CompletedTask;

        var ownerId = ResolveOwnerId(session);
        if (string.IsNullOrEmpty(ownerId)) return Task.CompletedTask;

        // Прерванный ход: usage может быть null (CLI не успел отдать метрики) —
        // в этом случае запись не пишем (нет данных)
        if (result.Usage is null) return Task.CompletedTask;

        var cost = _providers.ComputeCostOrZero(session.Model, result.Usage);
        var ts = DateTime.UtcNow.ToString("O");

        _ = Task.Run(() => _spend.Append(
            ownerId: ownerId,
            projectId: session.ProjectId,
            sessionId: session.Id,
            taskId: session.TaskId,
            personaId: session.PersonaId,
            provider: session.Provider,
            model: session.Model ?? "",
            source: "chat",
            ts: ts,
            inputTokens: result.Usage.InputTokens,
            outputTokens: result.Usage.OutputTokens,
            cacheReadTokens: result.Usage.CacheReadTokens,
            cacheCreationTokens: result.Usage.CacheCreationTokens,
            costUsd: cost,
            durationMs: result.DurationMs,
            completed: result.Subtype != "error",
            entityRef: null));

        return Task.CompletedTask;
    }

    private static string? ResolveOwnerId(Session session)
    {
        if (!string.IsNullOrEmpty(session.OwnerId)) return session.OwnerId;
        // У проектных сессий OwnerId может быть не заполнен, но проект его знает
        return null; // Резолв через проект — в будущем, если понадобится
    }
}
