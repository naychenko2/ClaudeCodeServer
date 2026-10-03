using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.ChatContext;

// Двойная запись фокуса вертикали (ADR-023 §5, фаза 1). Вертикаль по-прежнему пишет свой Focus (старые
// полосы, флаг выключен), а при флаге владельца зеркалит смену в стор контекста; чтение DTO вертикали при
// флаге — проекция из контекста. Без флага (или без стора) всё как раньше. Вертикаль зовёт этот Core-класс,
// а не другую вертикаль. Сбой зеркала фокус вертикали не откатывает: запись вертикали уже сделана.
public sealed class ChatContextFocusMirror(
    IChatContextStore store,
    IFeatureFlagGate flags,
    ILogger<ChatContextFocusMirror> log)
{
    public bool Enabled(string ownerId) => flags.IsEnabled(ownerId, FeatureFlagKeys.ComposerContextRow);

    // Фокус вертикали сменился с before на after: after != null — нить становится основным объектом;
    // after == null — снимаем основной, только если он был именно этой нитью (звук не должен снять картинку)
    // claim — явный выбор агента (image_focus и т.п.): фокус вертикали мог не измениться, потому что человек
    // снял объект в контексте (✕), а нить и так в фокусе у вертикали; тогда, если основной — не эта нить,
    // агент возвращает её основным (✦). Текущий выбор человека на эту нить не затирается: уже основная — не трогаем
    // refKey — имя поля Ref, где вид хранит идентификатор объекта (threadId у картинки и звука, sceneId у сцены
    // видео, filmPath у фильма)
    public void Sync(string ownerId, string sessionId, string kind, string? before, string? after, ContextActor by,
        bool claim = false, string refKey = ThreadKey)
    {
        if (!Enabled(ownerId)) return;
        if (before == after && !(claim && after is not null)) return;
        try
        {
            if (before == after)
            {
                var cur = store.Get(ownerId, sessionId);
                if (cur.Primary is { } cp && cp.Kind == kind && ThreadOf(cp, refKey) == after) return;
                store.SetPrimary(ownerId, sessionId, NewItem(kind, after!, by, null, refKey), null);
                return;
            }
            if (after is not null)
            {
                store.SetPrimary(ownerId, sessionId, NewItem(kind, after, by, null, refKey), null);
                return;
            }
            var current = store.Get(ownerId, sessionId);
            if (current.Revision == 0)
                // Файла нет: основной — засев из оставшихся фокусов (или пусто); закрепляем его записью,
                // чтобы фронт получил событие о смене
                store.SetPrimary(ownerId, sessionId, current.Primary, null);
            else if (current.Primary is { } p && p.Kind == kind && ThreadOf(p, refKey) == before)
                store.SetPrimary(ownerId, sessionId, null, null);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Фокус {Kind} чата {SessionId} не отражён в контексте", kind, sessionId);
        }
    }

    // Нить удалена: убрать её из основного и референсов
    public void Forget(string ownerId, string sessionId, string kind, string threadId, string refKey = ThreadKey)
    {
        if (!Enabled(ownerId)) return;
        try { store.Forget(ownerId, sessionId, i => i.Kind == kind && ThreadOf(i, refKey) == threadId); }
        catch (Exception ex) { log.LogWarning(ex, "Нить {ThreadId} не убрана из контекста чата {SessionId}", threadId, sessionId); }
    }

    // Фокус вертикали для её DTO: при флаге — из основного объекта контекста своего вида (и только
    // если такая нить ещё есть), иначе собственное поле как есть
    public string? ProjectFocus(string ownerId, string sessionId, string kind, string? own, Func<string, bool> threadExists,
        string refKey = ThreadKey)
    {
        if (!Enabled(ownerId)) return own;
        try
        {
            return store.Get(ownerId, sessionId).Primary is { } p && p.Kind == kind
                && ThreadOf(p, refKey) is { } id && threadExists(id) ? id : null;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Проекция фокуса {Kind} чата {SessionId} недоступна", kind, sessionId);
            return own;
        }
    }

    public const string ThreadKey = "threadId";

    public static string? ThreadOf(ContextItem item, string refKey = ThreadKey) =>
        item.Ref[refKey] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static ContextItem NewItem(string kind, string threadId, ContextActor by, string? versionId = null,
        string refKey = ThreadKey)
    {
        var reference = new JsonObject { [refKey] = threadId };
        if (versionId is not null) reference["versionId"] = versionId;
        return new ContextItem("ci_" + Guid.NewGuid().ToString("N")[..12], kind, reference, null, by, DateTime.UtcNow);
    }
}
