using System.IdentityModel.Tokens.Jwt;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChatUserState;

namespace ClaudeHomeServer.Services;

// Личное состояние чата (прочитано / закреплён / заглушён) участника общего проекта, S1-7.
// Поля Session — значения владельца проекта, они остаются как были; участник (редактор, читатель)
// видит и пишет СВОЁ состояние в оверлее ChatUserStateStore. Владелец проекта и чат вне проекта
// оверлея не используют: у них по-прежнему поля Session (формат и все потребители целы).
// Нет записи у участника — «не задано»: чат не закреплён, не заглушён, прочитанность пуста
// (значения владельца ему не достаются — иначе его отметки читал бы чужой LastReadAt).
public sealed class ChatUserStateService(ChatUserStateStore store, ProjectManager projects, IHttpContextAccessor http)
{
    private const string CacheKey = "ccs.ChatUserState";

    // Состояние для того, кто СЕЙЧАС читает ответ (текущий HTTP-запрос): null — оверлея нет и
    // отдаются поля Session (владелец, чат вне проекта, нет запроса или пользователя).
    // Зовёт конвертер Session на границе сериализации, поэтому на каждую сессию списка нужны
    // дешёвые проверки; файл участника читается один раз на запрос и кладётся в HttpContext.Items.
    public UserChatState? ViewFor(Session session)
    {
        if (session.ProjectId is null || http.HttpContext is not { } ctx) return null;
        var viewer = ctx.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!IsMember(session, viewer)) return null;

        if (ctx.Items[CacheKey] is not Dictionary<string, UserChatState> cache)
            ctx.Items[CacheKey] = cache = new(store.GetAll(viewer!));
        var own = cache.GetValueOrDefault(session.Id);
        return new UserChatState(own?.LastReadAt, own?.IsPinned ?? false, own?.NotificationsMuted ?? false);
    }

    // Участник общего проекта (не владелец) — только он живёт на оверлее
    public bool IsMember(Session session, string? userId) =>
        session.ProjectId is not null
        && ProjectAccess.Check(projects.GetById(session.ProjectId), userId) is ProjectRole.Editor or ProjectRole.Reader;

    // Идемпотентно, как SessionManager.MarkRead: уже прочитано после последней активности — записи нет
    public void MarkRead(string userId, Session session)
    {
        if (store.Get(userId, session.Id)?.LastReadAt >= session.UpdatedAt) return;
        store.MarkRead(userId, session.Id);
    }

    public void SetPinned(string userId, string sessionId, bool pinned) => store.SetPinned(userId, sessionId, pinned);

    public void SetMuted(string userId, string sessionId, bool muted) => store.SetMuted(userId, sessionId, muted);
}
