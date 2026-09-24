using System.Reflection;
using ClaudeHomeServer.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Composition;

/// <summary>
/// Группа подсистем проекта по матрице ADR-016 §4 — какой вход к какой группе относится.
/// </summary>
public enum ProjectCapabilityArea
{
    /// <summary>Платформа: работает у любого проекта, к файлам проекта на сервере не ходит.</summary>
    Platform,
    /// <summary>Привязано к файлам проекта: у локального проекта файлы на устройстве.</summary>
    FileBound,
    /// <summary>Нужен контент проекта на сервере (Dify, CodeGraph, досье, Docs, уборка карты).</summary>
    ServerContent,
}

/// <summary>
/// Отказ подсистемы для локального проекта: вход вызван, но файлов проекта на сервере нет.
/// Бросается ДО обращения к диску — сервер не трогает путь, который живёт на другой машине.
/// </summary>
public sealed class LocalProjectException(string reason) : InvalidOperationException(reason)
{
    public string Code => ProjectCapabilityGuard.Code;
}

/// <summary>
/// Guard файловой и выключенной групп (ADR-016 §4, сторож G1). Решение берётся только из
/// <see cref="ProjectCapabilities"/>: здесь ни одной собственной проверки локальности.
///
/// Входы (контроллеры, хабы) размечаются <see cref="ProjectCapabilityAttribute"/> — атрибут
/// сам и отказывает; сервисы и тулсеты, у которых проект уже на руках, зовут
/// <see cref="Refusal"/>/<see cref="EnsureAllowed"/> перед тем, как взять <c>RootPath</c>.
/// </summary>
public static class ProjectCapabilityGuard
{
    /// <summary>Машинный код отказа: фронт и модели различают его без разбора текста.</summary>
    public const string Code = "local_project";

    public const string FilesOnDeviceReason =
        "Файлы локального проекта живут на устройстве — сервер к ним не обращается";

    /// <summary>Текст отказа для группы или null, если группа у проекта работает на сервере.</summary>
    public static string? Refusal(Project project, ProjectCapabilityArea area) => area switch
    {
        ProjectCapabilityArea.FileBound when !ProjectCapabilities.FilesOnServer(project) => FilesOnDeviceReason,
        ProjectCapabilityArea.ServerContent when !ProjectCapabilities.ServerContentEnabled(project) =>
            ProjectCapabilities.ServerContentOffReason,
        _ => null,
    };

    /// <summary>Разрешена ли группа у проекта на сервере.</summary>
    public static bool Allows(Project project, ProjectCapabilityArea area) => Refusal(project, area) is null;

    /// <summary>Бросает <see cref="LocalProjectException"/>, если группа у проекта на сервере не работает.</summary>
    public static void EnsureAllowed(Project project, ProjectCapabilityArea area)
    {
        if (Refusal(project, area) is { } reason) throw new LocalProjectException(reason);
    }

    /// <summary>Корень проекта на диске сервера для группы — или отказ до обращения к диску.</summary>
    public static string ServerRoot(Project project, ProjectCapabilityArea area = ProjectCapabilityArea.FileBound)
    {
        EnsureAllowed(project, area);
        return project.RootPath;
    }

    /// <summary>Тело HTTP-отказа: 409 с кодом <see cref="Code"/> и текстом для человека.</summary>
    public static ObjectResult Denied(string reason) =>
        new(new { error = reason, code = Code }) { StatusCode = StatusCodes.Status409Conflict };

    /// <summary>Текст отказа хаба: код впереди, чтобы клиент различал его префиксом.</summary>
    public static string HubMessage(string reason) => Code + ": " + reason;
}

/// <summary>
/// Разметка входа группой матрицы. Метод перекрывает класс. На контроллере работает как
/// фильтр действия, на хабе — через <see cref="ProjectCapabilityHubFilter"/>: для локального
/// проекта вход отвечает отказом <c>local_project</c> ДО тела действия, то есть до диска.
///
/// Проект берётся из параметра <see cref="ProjectKey"/> (маршрут, запрос или аргумент метода
/// хаба). Проекта нет — вход пропускается дальше: «не найден» отвечает он сам.
/// <see cref="ProjectCapabilityArea.Platform"/> — явная отметка «вход проверен, файлов не
/// трогает»: без неё сторож G1 не пропустит вход с параметром проекта.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ProjectCapabilityAttribute(ProjectCapabilityArea area) : Attribute, IAsyncActionFilter, IOrderedFilter
{
    public ProjectCapabilityArea Area { get; } = area;

    /// <summary>Имя параметра с id проекта.</summary>
    public string ProjectKey { get; init; } = "projectId";

    // Раньше прочих фильтров действия: отказ не должен зависеть от их побочных эффектов
    public int Order => int.MinValue + 1000;

    /// <summary>Действующая разметка метода: атрибут метода, иначе класса.</summary>
    public static ProjectCapabilityAttribute? For(MethodInfo method) =>
        method.GetCustomAttribute<ProjectCapabilityAttribute>()
        ?? method.DeclaringType?.GetCustomAttribute<ProjectCapabilityAttribute>(inherit: true);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Атрибуты класса и метода — два экземпляра фильтра; решает только действующий
        var effective = context.ActionDescriptor is ControllerActionDescriptor cad ? For(cad.MethodInfo) : this;
        if (effective is not null && (effective.Area != Area || effective.ProjectKey != ProjectKey))
        {
            await next();
            return;
        }

        var projectId = context.RouteData.Values.TryGetValue(ProjectKey, out var fromRoute) ? fromRoute?.ToString()
            : context.ActionArguments.TryGetValue(ProjectKey, out var fromArgs) ? fromArgs?.ToString()
            : context.HttpContext.Request.Query.TryGetValue(ProjectKey, out var fromQuery) ? fromQuery.ToString()
            : null;
        if (Deny(context.HttpContext.RequestServices, projectId) is { } reason)
        {
            context.Result = ProjectCapabilityGuard.Denied(reason);
            return;
        }
        await next();
    }

    // Отказ для проекта по id или null. Владельца не сверяем: отказ не раскрывает содержимого,
    // а чужой проект иначе дошёл бы до тела действия и до диска раньше его проверки
    internal string? Deny(IServiceProvider services, string? projectId)
    {
        if (Area == ProjectCapabilityArea.Platform || string.IsNullOrEmpty(projectId)) return null;
        var project = services.GetRequiredService<IProjectManager>().GetById(projectId);
        return project is null ? null : ProjectCapabilityGuard.Refusal(project, Area);
    }
}

/// <summary>
/// Глобальный фильтр MVC: <see cref="LocalProjectException"/>, брошенный сервисом до диска,
/// становится тем же ответом 409 <c>local_project</c>, что и отказ атрибута.
/// </summary>
public sealed class LocalProjectExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not LocalProjectException ex) return;
        context.Result = ProjectCapabilityGuard.Denied(ex.Message);
        context.ExceptionHandled = true;
    }
}

/// <summary>
/// Исполнение <see cref="ProjectCapabilityAttribute"/> на методах хабов SignalR: MVC-фильтры
/// там не работают. Регистрируется глобально в <c>AddSignalR</c>.
/// </summary>
public sealed class ProjectCapabilityHubFilter : IHubFilter
{
    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (ProjectCapabilityAttribute.For(invocationContext.HubMethod) is { } attr)
        {
            var parameters = invocationContext.HubMethod.GetParameters();
            var index = Array.FindIndex(parameters, p => p.Name == attr.ProjectKey);
            var projectId = index >= 0 && index < invocationContext.HubMethodArguments.Count
                ? invocationContext.HubMethodArguments[index]?.ToString()
                : null;
            if (attr.Deny(invocationContext.ServiceProvider, projectId) is { } reason)
                throw new HubException(ProjectCapabilityGuard.HubMessage(reason));
        }
        return next(invocationContext);
    }
}
