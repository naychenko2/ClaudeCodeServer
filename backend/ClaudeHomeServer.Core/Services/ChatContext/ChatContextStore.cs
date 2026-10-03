using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.ChatContext;

// Стор контекста чата (ADR-023 §1): data/chat-context/{ownerId}/{sessionId}.json.
// Корень отдельный от sessions.json намеренно: правка контекста — настройка чата, она не трогает
// Session (UpdatedAt, архив, сортировку). Каталог лежит в data/ и попадает в бэкап сам, формат аддитивный.
//
// Ревизия поднимается на каждую запись, поменявшую состояние. Запись человека несёт ревизию, от
// которой он считал (чужая — ChatContextConflictException со свежим состоянием); null — без сверки
// (так пишет агент и сервер). Запись без изменений ревизию не двигает и не рассылается.
public sealed class ChatContextStore(
    string root,
    ContextKindRegistry registry,
    IChatContextNotifier? notifier = null,
    TimeProvider? time = null) : IChatContextStore
{
    public const string DirName = "chat-context";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string RootFromConfig(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return Path.Combine(Path.GetDirectoryName(dataPath)!, DirName);
    }

    public ChatContextState Get(string ownerId, string sessionId)
    {
        lock (_gate) return Read(ownerId, sessionId);
    }

    // item == null — снять основной объект. Основной один на чат поперёк вертикалей, и тот же объект
    // из референсов уходит. Роль у основного не хранится
    public ChatContextState SetPrimary(string ownerId, string sessionId, ContextItem? item, long? revision) =>
        Write(ownerId, sessionId, revision, current =>
        {
            if (item is null)
                return current.Primary is null ? current : current with { Primary = null };
            RequireKnown(item.Kind);
            if (current.Primary is { } p && SameObject(p, item)) return current;
            return current with
            {
                Primary = item with { Role = null },
                Refs = [.. current.Refs.Where(r => !SameObject(r, item))],
            };
        });

    // Дубль «тот же Kind + Ref + Role» не добавляется; объект, уже ставший основным, референсом не
    // становится (иначе один объект стоял бы в двух частях «С чем» сразу)
    public ChatContextState AddRef(string ownerId, string sessionId, ContextItem item, long? revision) =>
        Write(ownerId, sessionId, revision, current =>
        {
            RequireKnown(item.Kind);
            if (current.Primary is { } p && SameObject(p, item)) return current;
            if (current.Refs.Any(r => SameObject(r, item) && r.Role == item.Role)) return current;
            if (current.Refs.Count >= ChatContextErrors.MaxRefs)
                throw new ChatContextException(ChatContextErrors.RefsLimit,
                    $"Референсов не больше {ChatContextErrors.MaxRefs}");
            return current with { Refs = [.. current.Refs, item] };
        });

    public ChatContextState RemoveRef(string ownerId, string sessionId, string itemId, long? revision) =>
        Write(ownerId, sessionId, revision, current =>
            current.Refs.Any(r => r.Id == itemId)
                ? current with { Refs = [.. current.Refs.Where(r => r.Id != itemId)] }
                : current);

    public ChatContextState Clear(string ownerId, string sessionId, long? revision) =>
        Write(ownerId, sessionId, revision, current =>
            current.Primary is null && current.Refs.Count == 0 ? current : current with { Primary = null, Refs = [] });

    // Объект исчез: убрать отовсюду без ревизии — запись сервера не должна проигрывать правке человека
    public void Forget(string ownerId, string sessionId, Func<ContextItem, bool> match) =>
        Write(ownerId, sessionId, null, current =>
        {
            var primary = current.Primary is { } p && match(p) ? null : current.Primary;
            var refs = current.Refs.Where(r => !match(r)).ToList();
            return primary == current.Primary && refs.Count == current.Refs.Count
                ? current
                : current with { Primary = primary, Refs = refs };
        });

    public DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private ChatContextState Write(string ownerId, string sessionId, long? revision,
        Func<ChatContextState, ChatContextState> apply)
    {
        ChatContextState next;
        bool changed;
        lock (_gate)
        {
            var current = Read(ownerId, sessionId);
            if (revision is { } r && r != current.Revision) throw new ChatContextConflictException(current);
            var result = apply(current);
            changed = !ReferenceEquals(result, current);
            next = changed ? result with { Revision = current.Revision + 1 } : current;
            if (changed) JsonFileStore.Save(PathFor(ownerId, sessionId), next, Json);
        }
        if (changed) Notify(ownerId, sessionId, next);
        return next;
    }

    private void Notify(string ownerId, string sessionId, ChatContextState state)
    {
        if (notifier is null) return;
        try { notifier.Changed(ownerId, sessionId, state); }
        catch { /* потерянное событие фронт догоняет GET контекста */ }
    }

    private void RequireKnown(string kind)
    {
        if (!registry.IsRegistered(kind))
            throw new ChatContextException(ChatContextErrors.KindUnknown, $"Вид контекста «{kind}» не зарегистрирован");
    }

    private static bool SameObject(ContextItem a, ContextItem b) =>
        a.Kind == b.Kind && JsonNode.DeepEquals(a.Ref, b.Ref);

    private ChatContextState Read(string ownerId, string sessionId)
    {
        var state = JsonFileStore.Load<ChatContextState>(PathFor(ownerId, sessionId), Json);
        return state is null ? new ChatContextState(0, null, []) : state with { Refs = state.Refs ?? [] };
    }

    private string PathFor(string ownerId, string sessionId)
    {
        foreach (var segment in new[] { ownerId, sessionId })
            if (string.IsNullOrWhiteSpace(segment) || segment.IndexOfAny(['/', '\\']) >= 0 || segment.Contains(".."))
                throw new ArgumentException("Недопустимый идентификатор владельца или сессии");
        return SafePath.Join(root, Path.Combine(ownerId, sessionId + ".json"));
    }
}
