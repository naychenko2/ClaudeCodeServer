using Microsoft.AspNetCore.Http;

namespace ClaudeHomeServer.Services.Access;

/// <summary>Доступ к <see cref="RequestScope"/> текущего запроса.</summary>
public static class RequestScopeHttpContextExtensions
{
    private const string ItemKey = "ccs.RequestScope";

    /// <summary>
    /// Область запроса. Фильтр <see cref="ProjectScopeAttribute"/> не отработал — бросает
    /// <see cref="InvalidOperationException"/> (fail-closed): на <c>sub</c> вызывающего не
    /// откатываемся, забытая разметка проектного эндпоинта должна быть громкой, а не тихо
    /// класть данные в пространство вызывающего.
    /// </summary>
    public static RequestScope GetRequestScope(this HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is RequestScope scope
            ? scope
            : throw new InvalidOperationException(
                "RequestScope не определён: у действия нет [ProjectScope] (или фильтр не отработал) " +
                "и область не задана явно через IProjectAccess.Resolve + SetRequestScope.");

    /// <summary>Кладёт область в запрос — фильтром или явным резолвом (projectId из тела, анонимный вход).</summary>
    public static void SetRequestScope(this HttpContext context, RequestScope scope) =>
        context.Items[ItemKey] = scope;
}
