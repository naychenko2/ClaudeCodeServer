using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Composition;

// Реализация IKnowledgeHubNotifier (Core) поверх IHubContext<SessionHub>.
// Тонкая обёртка: конструирует KnowledgeChangedMessage и шлёт в группу
// user_{userId}. Логика сообщения остаётся в Protocol/ServerMessage.cs (Main) —
// Core-контракт намеренно его не видит, чтобы обратной зависимости не было.
public sealed class KnowledgeHubNotifier(IHubContext<SessionHub> hub) : IKnowledgeHubNotifier
{
    public Task BroadcastKnowledgeChangedAsync(string userId, string action, string? datasetId) =>
        hub.Clients.Group("user_" + userId)
            .SendAsync("message", new KnowledgeChangedMessage(action, datasetId));
}
