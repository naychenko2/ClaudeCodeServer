using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
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

    /// <summary>Действующая разметка метода: атрибут метода, иначе класса.</summary>
    public static ProjectScopeAttribute? For(MethodInfo method) =>
        method.GetCustomAttribute<ProjectScopeAttribute>()
        ?? method.DeclaringType?.GetCustomAttribute<ProjectScopeAttribute>(inherit: true);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Атрибуты класса и метода — два экземпляра фильтра; решает только действующий
        var effective = context.ActionDescriptor is ControllerActionDescriptor cad ? For(cad.MethodInfo) : this;
        if (effective is not null && (effective.From != From || effective.Key != Key || effective.Right != Right))
        {
            await next();
            return;
        }

        var key = Key ?? (From == ScopeSource.Session ? "sessionId" : "projectId");
        var id = context.RouteData.Values.TryGetValue(key, out var fromRoute) ? fromRoute?.ToString()
            : context.ActionArguments.TryGetValue(key, out var fromArgs) ? ScalarId(fromArgs)
            : context.HttpContext.Request.Query.TryGetValue(key, out var fromQuery) ? fromQuery.ToString()
            : null;
        if (string.IsNullOrEmpty(id))
        {
            context.Result = Fail(StatusCodes.Status400BadRequest, $"Не указан параметр {key}");
            return;
        }

        var caller = context.HttpContext.User.FindFirst("sub")?.Value;
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
            // Проект виден, но права нет (читатель; в S1 и редактор) — 403, а не 404:
            // существование ему и так известно. WriteContent — то же «нет права на запись» для
            // читателя (S2c-8); RunTurn — то же «нет» для читателя на действии хода: создании чата
            // (S2d-1c): отказ различается правом, а не статусом
            if (From == ScopeSource.Project && Right is ProjectRight.Write or ProjectRight.Configure
                    or ProjectRight.WriteContent or ProjectRight.RunTurn
                && access.Resolve(caller, id, ProjectRight.Read) is not null)
            {
                context.Result = Fail(StatusCodes.Status403Forbidden, Right switch
                {
                    ProjectRight.Configure => "Только владелец проекта",
                    ProjectRight.RunTurn => "Нет права вести ход",
                    _ => "Нет права на запись",
                });
                return;
            }
            context.Result = Fail(StatusCodes.Status404NotFound, "Не найдено");
            return;
        }

        context.HttpContext.SetRequestScope(scope);
        await next();
    }

    // Составной аргумент (DTO) без id — «параметр не указан», а не ToString() с именем типа
    private static string? ScalarId(object? value) =>
        value is string or ValueType ? value.ToString() : null;

    private static ObjectResult Fail(int status, string message) =>
        new(new { error = message, code = ErrorCode }) { StatusCode = status };
}
