using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Services.Access;

/// <summary>Откуда фильтр берёт объект доступа.</summary>
public enum ScopeSource
{
    /// <summary>Id проекта (ключ <see cref="ProjectScopeAttribute.Key"/>, по умолчанию <c>projectId</c>).</summary>
    Project,

    /// <summary>Id чата (по умолчанию <c>sessionId</c>): чат → проект → доступ.</summary>
    Session,
}

/// <summary>
/// Фильтр действия: резолвит <see cref="RequestScope"/> до тела действия и кладёт его в
/// <c>HttpContext</c> (<see cref="RequestScopeHttpContextExtensions.GetRequestScope"/>).
/// Образец — <c>ProjectCapabilityAttribute</c>; id берётся из маршрута, аргументов действия
/// или запроса. Нет id — 400, нет доступа или объекта — 404 (не раскрываем существование),
/// нет <c>sub</c> — 403. Если id приходит в теле или вход анонимный (office-токен) — явный
/// <see cref="IProjectAccess.Resolve"/> в теле действия.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ProjectScopeAttribute : Attribute, IAsyncActionFilter, IOrderedFilter
{
    public const string ErrorCode = "project_scope";

    public ScopeSource From { get; init; } = ScopeSource.Project;

    /// <summary>Имя параметра с id; не задано — <c>projectId</c> / <c>sessionId</c> по <see cref="From"/>.</summary>
    public string? Key { get; init; }

    /// <summary>Требуемое право (по умолчанию просмотр).</summary>
    public ProjectRight Right { get; init; } = ProjectRight.Read;

    // После авторизации, до прочих фильтров действия
    public int Order => int.MinValue + 2000;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var key = Key ?? (From == ScopeSource.Session ? "sessionId" : "projectId");
        var id = context.RouteData.Values.TryGetValue(key, out var fromRoute) ? fromRoute?.ToString()
            : context.ActionArguments.TryGetValue(key, out var fromArgs) ? fromArgs?.ToString()
            : context.HttpContext.Request.Query.TryGetValue(key, out var fromQuery) ? fromQuery.ToString()
            : null;
        if (string.IsNullOrEmpty(id))
        {
            context.Result = Fail(StatusCodes.Status400BadRequest, $"Не указан параметр {key}");
            return;
        }

        var caller = context.HttpContext.User.FindFirst("sub")?.Value
            ?? context.HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(caller))
        {
            context.Result = Fail(StatusCodes.Status403Forbidden, "Не определён пользователь");
            return;
        }

        var access = context.HttpContext.RequestServices.GetRequiredService<IProjectAccess>();
        var scope = From == ScopeSource.Session
            ? access.ResolveSession(caller, id, Right)
            : access.Resolve(caller, id, Right);
        if (scope is null)
        {
            context.Result = Fail(StatusCodes.Status404NotFound, "Не найдено");
            return;
        }

        context.HttpContext.SetRequestScope(scope);
        await next();
    }

    private static ObjectResult Fail(int status, string message) =>
        new(new { error = message, code = ErrorCode }) { StatusCode = status };
}
