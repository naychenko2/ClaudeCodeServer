using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;

namespace ClaudeHomeServer.Models;

/// <summary>
/// Ссылка на чат, связанный с задачей (К5, разрез Александра §3; шаг S2d-4b). Две формы:
/// видимый чат — id и название; чужой приватный — форма без id и названия: «Приватный чат
/// участника», только признак и то, чей чат.
///
/// В ответ форма лежит РЯДОМ с id-полем: id видимого чата остаётся в своём поле
/// (LinkedSessionId и др.), отдельного объекта нет — форма ответа для видимых чатов не меняется;
/// чужой приватный — id-поле обнулено, рядом стоит эта запись. Для фронта это наименьшее
/// изменение: существующие чтения id не тронуты, новый ключ появляется только там, где id закрыт.
/// </summary>
public sealed record ChatRef
{
    /// <summary>Подпись формы-заглушки (Title здесь — то, что показывает фронт вместо названия:
    /// для видимого чата это его Name, для закрытого — этот текст). Держится одной строкой, чтобы
    /// форма не разводилась на два места.</summary>
    public const string PrivateFormTitle = "Приватный чат участника";

    public string? SessionId { get; init; }
    public string? Title { get; init; }

    /// <summary>true — ни id, ни названия: этот чат данной аудитории не виден (чаще всего — чужой
    /// приватный; см. <see cref="ChatRefs.For"/>).</summary>
    public bool Private { get; init; }

    /// <summary>Чей чат: автор приватности (<c>PrivateToUserId</c>), иначе владелец чата. Нужен
    /// фронту, чтобы сказать «чужой»; сам по себе id чата и его названия не открывает.</summary>
    public string? AuthorUserId { get; init; }
}

/// <summary>Кому показываем чат задачи. Две аудитории — потому что две формы рассылки:
/// • <see cref="Viewer"/> — один человек (вызывающий REST, владелец карточки в WS): видимость
///   решает <see cref="SessionAccess"/> по нему;
/// • <see cref="Group"/> — вся группа project_{id} ОДНИМ payload'ом: отдать можно только то, что
///   видно всем, т.е. общий чат этого проекта; приватный чат участника закрыт и для его автора —
///   свой чат он видит точковыми путями (список и карточка REST).</summary>
public sealed record ChatAudience
{
    public string? ViewerUserId { get; init; }
    public string? GroupProjectId { get; init; }

    public static ChatAudience Viewer(string? viewerUserId) => new() { ViewerUserId = viewerUserId };
    public static ChatAudience Group(string projectId) => new() { GroupProjectId = projectId };
}

/// <summary>
/// Единая точка решения «что видно об чате, связанном с задачей» (К5): через неё идут список и
/// карточка REST (TaskView) и событие доски (копия группе и копия владельца в ToTaskBoard).
/// Второй точки правды нет. Путь MCP (TasksToolset) и карточка доклада — следующий шаг S2d-4b2.
///
/// Несуществующий чат (в каталоге его нет) не маскируется: называть приватным чат, которого нет,
/// — ложь, а id удалённого чата ничего не открывает (ни названия, ни содержимого). Тот же исход,
/// когда каталог не передан: резолвера нет — решения нет, поведение прежнее. Точки ToTaskBoard
/// без резолвера — список в отчёте S2d-4b, их подключение — следующий шаг.
/// </summary>
public static class ChatRefs
{
    public static ChatRef? For(ISessionDirectory? sessions, IProjectManager? projects,
        string? sessionId, ChatAudience audience)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        if (sessions?.GetById(sessionId) is not { } session) return null;

        if (audience.GroupProjectId is { } group)
            return session.ProjectId == group && session.PrivateToUserId is null
                ? Visible(session)
                : Hidden(session);

        if (audience.ViewerUserId is { } viewer)
        {
            var project = session.ProjectId is { } pid ? projects?.GetById(pid) : null;
            return SessionAccess.CanSee(session, project, viewer) ? Visible(session) : Hidden(session);
        }

        return null;
    }

    private static ChatRef Visible(Session s) => new() { SessionId = s.Id, Title = s.Name };

    private static ChatRef Hidden(Session s) => new()
    {
        Private = true,
        Title = ChatRef.PrivateFormTitle,
        AuthorUserId = s.PrivateToUserId ?? s.OwnerId,
    };
}
