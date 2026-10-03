using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChatContext;

namespace ClaudeHomeServer.Tests.Services.ChatContext;

// Провайдер-заглушка для тестов спины: виды заданы списком, Describe отдаёт Ref как подпись
internal sealed class FakeKindProvider(params string[] kinds) : IContextKindProvider
{
    public IReadOnlyList<string> Kinds { get; } = kinds;
    public bool CanBePrimary(string kind) => true;
    public IReadOnlyList<ContextRoleSpec> Accepted { get; set; } = [];
    public string? Validate(ContextScope scope, string kind, JsonObject reference) => null;
    public ContextItemSummary Describe(ContextScope scope, ContextItem item) =>
        new(item.Ref.ToJsonString(), null, null, false);
    public IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op) => Accepted;
    public string? DescribeExecutor(ContextScope scope, ContextItem primary) => null;
}

internal sealed class RecordingNotifier : IChatContextNotifier
{
    public List<(string Owner, string Session, long Revision)> Calls { get; } = [];
    public void Changed(string ownerId, string sessionId, ChatContextState state) =>
        Calls.Add((ownerId, sessionId, state.Revision));
}

internal static class ChatContextTestData
{
    public static string TempRoot() => Path.Combine(Path.GetTempPath(), "chat-context-" + Guid.NewGuid().ToString("N"));

    public static ContextItem Item(string kind, string key, string? role = null, string? id = null,
        ContextActor by = ContextActor.Human) =>
        new(id ?? Guid.NewGuid().ToString("N"), kind, new JsonObject { ["key"] = key }, role, by,
            new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc));

    public static ContextKindRegistry Registry(params string[] kinds) =>
        new([new FakeKindProvider(kinds)]);
}
