using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services;

// Адаптер шва IChatFeed над SessionManager (ADR-019 §2): модули пишут в ленту чата через него,
// не видя ядра сессий
public sealed class ChatFeed(SessionManager sessions) : IChatFeed
{
    public Task<bool> AppendRecordAsync(string sessionId, StoredModuleRecord record, CancellationToken ct = default) =>
        sessions.AppendModuleRecordAsync(sessionId, record);
}
