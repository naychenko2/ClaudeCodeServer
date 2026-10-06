using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.ChatContext;

// Отражение фокуса вертикали в стор контекста (ADR-023 §5). Собственный Focus вертикали остаётся в её файле
// только как «до/после» для различения смены и для засева контекста чата без файла; единственный источник
// правды о выбранном объекте — основной объект контекста, DTO вертикали отдаёт его проекцию. Вертикаль зовёт
// этот Core-класс, а не другую вертикаль. Сбой зеркала фокус вертикали не откатывает: запись вертикали уже сделана.
public sealed class ChatContextFocusMirror(
    IChatContextStore store,
    ILogger<ChatContextFocusMirror> log)
{
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
        if (before == after && !(claim && after is not null)) return;
        try
        {
            // Агент не трогает основной объект, который уже тот же вида и нити: версию, закреплённую человеком,
            // и метку By он не стирает. Сверка — с основным объектом стора, а не с сырым before вертикали:
            // выбор человека пишется только в стор, сырой Focus при этом может быть пустым
            if (after is not null && by == ContextActor.Agent
                && store.Get(ownerId, sessionId).Primary is { } same && same.Kind == kind && ThreadOf(same, refKey) == after)
                return;
            if (before == after)
            {
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

    // Запуск дал нить новую версию (ADR-023, «результат запуска становится объектом»): если основной объект —
    // эта нить, закреплённая на версии-основе запуска, он переезжает на новую версию. Метка того, кто его
    // поставил (By), и id элемента сохраняются. Закреплённую на другой версии не трогаем: человек выбрал её
    // сам, пока запуск шёл. Без закрепления (versionId нет) двигать нечего — читатели берут текущую версию нити.
    // Сбой зеркала запись вертикали не откатывает
    public void AdvanceVersion(string ownerId, string sessionId, string kind, string threadId, string? baseVersionId,
        string newVersionId, string refKey = ThreadKey)
    {
        try
        {
            if (store.Get(ownerId, sessionId).Primary is not { } p || p.Kind != kind || ThreadOf(p, refKey) != threadId) return;
            if (p.Ref["versionId"] is not JsonValue v || !v.TryGetValue<string>(out var pinned) || pinned != baseVersionId) return;
            var next = (JsonObject)p.Ref.DeepClone();
            next["versionId"] = newVersionId;
            store.SetPrimary(ownerId, sessionId, p with { Ref = next }, null);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Основной объект {Kind} чата {SessionId} не перешёл на версию {VersionId}", kind, sessionId, newVersionId);
        }
    }

    // Нить удалена: убрать её из основного и референсов
    public void Forget(string ownerId, string sessionId, string kind, string threadId, string refKey = ThreadKey)
    {
        try { store.Forget(ownerId, sessionId, i => i.Kind == kind && ThreadOf(i, refKey) == threadId); }
        catch (Exception ex) { log.LogWarning(ex, "Нить {ThreadId} не убрана из контекста чата {SessionId}", threadId, sessionId); }
    }

    // Фокус вертикали для её DTO: из основного объекта контекста своего вида (и только если такая нить ещё
    // есть); own — собственное поле, на него падаем лишь при сбое чтения стора
    public string? ProjectFocus(string ownerId, string sessionId, string kind, string? own, Func<string, bool> threadExists,
        string refKey = ThreadKey)
    {
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
