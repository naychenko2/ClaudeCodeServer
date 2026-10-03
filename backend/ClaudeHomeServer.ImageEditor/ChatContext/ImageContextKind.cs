using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Threads;

namespace ClaudeHomeServer.Services.ImageEditor.ChatContext;

// Вид «image» контекста чата (ADR-023): Ref = {threadId, versionId?} — нить картинки этого чата.
// Только Validate и Describe: референсы картинка пока не принимает, «Чем» не описывает. Он же засевает
// контекст чата без файла из фокуса картинки — с приоритетом над звуком.
public sealed class ImageContextKind(ImageThreadStore store) : IContextKindProvider, IChatContextSeedSource
{
    public const string Kind = "image";

    public IReadOnlyList<string> Kinds { get; } = [Kind];

    public int SeedPriority => 0;

    // Нить обязана быть в хранилище этого владельца и этого чата — чужая нить недостижима по построению
    public string? Validate(ContextScope scope, string kind, JsonObject reference)
    {
        if (kind != Kind) return $"Вид «{kind}» не принадлежит редактору картинок";
        if (Text(reference, "threadId") is not { } threadId) return "Не указана нить картинки";
        if (Find(scope, threadId) is not { } thread) return "Нить картинки не найдена в этом чате";
        if (Text(reference, "versionId") is { } versionId && thread.Version(versionId) is null)
            return "У картинки нет такой версии";
        return null;
    }

    public ContextItemSummary Describe(ContextScope scope, ContextItem item)
    {
        if (Text(item.Ref, "threadId") is not { } threadId || Find(scope, threadId) is not { } thread)
            return new ContextItemSummary("картинка недоступна", null, null, true);
        var version = (Text(item.Ref, "versionId") is { } v ? thread.Version(v) : null) ?? thread.CurrentVersion;
        return new ContextItemSummary(ImageThreadService.Name(thread),
            version is { IsOrigin: false } ? $"v{version.Number}" : null, null, false);
    }

    public IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op) => [];

    public string? DescribeExecutor(ContextScope scope, ContextItem primary) => null;

    public ContextItem? SeedPrimary(ContextScope scope)
    {
        var state = store.Get(scope.OwnerId, scope.Session.Id);
        if (state.Focus is not { } focus || state.Threads.All(t => t.Id != focus)) return null;
        return new ContextItem("seed_image", Kind, new JsonObject { ["threadId"] = focus }, null,
            ContextActor.Human, scope.Session.CreatedAt);
    }

    private ImageThread? Find(ContextScope scope, string threadId) =>
        store.Get(scope.OwnerId, scope.Session.Id).Threads.FirstOrDefault(t => t.Id == threadId);

    private static string? Text(JsonObject reference, string name) =>
        reference[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
