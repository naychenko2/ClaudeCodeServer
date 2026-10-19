using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services;

// Адаптер шва IChatFeed над SessionManager (ADR-019 §2): модули пишут в ленту чата через него,
// не видя ядра сессий
public sealed class ChatFeed(SessionManager sessions) : IChatFeed
{
    public Task<bool> AppendRecordAsync(string sessionId, StoredModuleRecord record, CancellationToken ct = default) =>
        sessions.AppendModuleRecordAsync(sessionId, record);

    // Идемпотентность якорей: скан истории чата по модулю/типу и (опционально) Data.
    public async Task<bool> HasRecordAsync(string sessionId, string module, string recordType,
        Func<JsonElement, bool>? match = null, CancellationToken ct = default)
    {
        var history = await sessions.GetHistoryAsync(sessionId);
        return history.OfType<StoredModuleRecord>()
            .Any(r => r.Module == module && r.RecordType == recordType
                && (match is null || (r.Data is { } data && match(data))));
    }
}
