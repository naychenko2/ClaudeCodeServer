using System.Reflection;
using ClaudeHomeServer.Services.Mcp.Http;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace ClaudeHomeServer.Services.Composition;

// Явная привязка обработчика (контроллера или тулсета) к ключу подсистемы (шаг S1b).
// Вертикальные контроллеры и тулсеты живут в сборке Main, поэтому по сборке их не отнести;
// обработчик без атрибута (и не из сборки вертикали) — ядро и не считается никогда.
[AttributeUsage(AttributeTargets.Class)]
public sealed class SubsystemUsageAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}

// Атрибуция обращений: тип обработчика (контроллер или тулсет) → ключ подсистемы.
// Сначала явный [SubsystemUsage]; иначе — карта «сборка → ключ» для обработчиков, лежащих
// в сборке самой вертикали (там самый длинный префикс namespace). Сборки Main и Core
// ядром не считаются: без атрибута обращение неатрибутируемо.
// Ключи атрибутов не из числа активных подсистем (выключена или не загружена) игнорируются.
public sealed class SubsystemUsageAttribution
{
    private static readonly Assembly[] CoreAssemblies =
        [typeof(SubsystemUsageAttribution).Assembly, typeof(SubsystemUsageStore).Assembly];

    private readonly Dictionary<Assembly, List<(string Key, string Namespace)>> _byAssembly = new();
    private readonly Dictionary<string, string> _mcpKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeKeys;
    private readonly HashSet<string> _observable = new(StringComparer.Ordinal);

    public SubsystemUsageAttribution(SubsystemStateStore subsystems, IEnumerable<IMcpToolset> toolsets)
    {
        _activeKeys = subsystems.ActiveTypes().Select(t => t.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var (key, type) in subsystems.ActiveTypes())
        {
            if (Array.IndexOf(CoreAssemblies, type.Assembly) >= 0) continue;
            if (!_byAssembly.TryGetValue(type.Assembly, out var list))
                _byAssembly[type.Assembly] = list = [];
            list.Add((key, type.Namespace ?? ""));
            _observable.Add(key);
        }

        // Контроллеры ядра-сборки с явной привязкой: их ключи тоже наблюдаемы
        foreach (var type in typeof(SubsystemUsageAttribution).Assembly.GetTypes())
            if (type.GetCustomAttribute<SubsystemUsageAttribute>(inherit: false)?.Key is { } k
                && _activeKeys.Contains(k))
                _observable.Add(k);

        foreach (var t in toolsets)
        {
            var key = KeyOf(t.GetType());
            if (key is not null) { _mcpKeys[t.Name] = key; _observable.Add(key); }
        }
    }

    // Ключи подсистем, к которым обращения вообще можно отнести: набор для SubsystemUsageStore.
    public IReadOnlyCollection<string> ObservableKeys => _observable.ToList();

    public string? KeyOf(Type handler)
    {
        var explicitKey = handler.GetCustomAttribute<SubsystemUsageAttribute>()?.Key;
        if (explicitKey is not null) return _activeKeys.Contains(explicitKey) ? explicitKey : null;

        if (!_byAssembly.TryGetValue(handler.Assembly, out var list)) return null;
        if (list.Count == 1) return list[0].Key;

        var ns = handler.Namespace ?? "";
        string? best = null;
        var bestLen = -1;
        foreach (var (key, prefix) in list)
        {
            var matches = ns.Equals(prefix, StringComparison.Ordinal)
                || ns.StartsWith(prefix + ".", StringComparison.Ordinal);
            if (matches && prefix.Length > bestLen) { best = key; bestLen = prefix.Length; }
        }
        return best;
    }

    public string? KeyOfMcp(string name) => _mcpKeys.GetValueOrDefault(name);
}

// Мидлвар учёта обращений: после UseRouting у конечной точки видно действие контроллера.
// Считаем ПОСЛЕ next — по итоговому статусу: 401/403/404 (до обработчика запрос не дошёл или маршрут не найден) не в счёт.
// SignalR-хаб и ручки ядра неатрибутируемы и не считаются. MCP — по сегменту /mcp/{name}.
public sealed class SubsystemUsageMiddleware(
    RequestDelegate next, SubsystemUsageAttribution attribution, SubsystemUsageStore store)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        await next(ctx);

        var status = ctx.Response.StatusCode;
        if (status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden
            or StatusCodes.Status404NotFound) return;

        try
        {
            var key = Resolve(ctx);
            if (key is not null) store.Hit(key, DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            // Счётчик — наблюдение, а не часть запроса: сбой атрибуции не должен ломать ответ.
        }
    }

    private string? Resolve(HttpContext ctx)
    {
        if (ctx.Request.Path.StartsWithSegments("/mcp", out var rest))
        {
            var name = rest.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return name is null ? null : attribution.KeyOfMcp(name);
        }

        var action = ctx.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        return action is null ? null : attribution.KeyOf(action.ControllerTypeInfo);
    }
}

public static class SubsystemUsageMiddlewareExtensions
{
    public static IApplicationBuilder UseSubsystemUsage(this IApplicationBuilder app) =>
        app.UseMiddleware<SubsystemUsageMiddleware>();
}
