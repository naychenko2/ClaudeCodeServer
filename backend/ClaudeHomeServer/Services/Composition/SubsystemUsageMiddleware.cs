using System.Reflection;
using ClaudeHomeServer.Services.Mcp.Http;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace ClaudeHomeServer.Services.Composition;

// Карта «сборка → ключ подсистемы» (шаг S1b). Строится один раз на старте из активных
// подсистем (SubsystemStateStore: и обычных, и динамических модулей) и IMcpToolset: тип обработчика (контроллер или тулсет) → сборка → ключ.
// Если в сборке несколько подсистем, выбирается та, чей namespace — самый длинный префикс
// namespace типа обработчика; не нашлось — обращение неатрибутируемо.
// Сборки Main и Core — ядро: обращения к ним не считаются никогда.
public sealed class SubsystemUsageAttribution
{
    private static readonly Assembly[] CoreAssemblies =
        [typeof(SubsystemUsageAttribution).Assembly, typeof(SubsystemUsageStore).Assembly];

    private readonly Dictionary<Assembly, List<(string Key, string Namespace)>> _byAssembly = new();
    private readonly Dictionary<string, string> _mcpKeys = new(StringComparer.Ordinal);

    public SubsystemUsageAttribution(SubsystemStateStore subsystems, IEnumerable<IMcpToolset> toolsets)
    {
        foreach (var (key, type) in subsystems.ActiveTypes())
        {
            if (Array.IndexOf(CoreAssemblies, type.Assembly) >= 0) continue;
            if (!_byAssembly.TryGetValue(type.Assembly, out var list))
                _byAssembly[type.Assembly] = list = [];
            list.Add((key, type.Namespace ?? ""));
        }

        foreach (var t in toolsets)
        {
            var key = KeyOf(t.GetType());
            if (key is not null) _mcpKeys[t.Name] = key;
        }
    }

    // Ключи подсистем, к которым обращения вообще можно отнести: набор для SubsystemUsageStore.
    public IReadOnlyCollection<string> ObservableKeys =>
        _byAssembly.Values.SelectMany(l => l.Select(e => e.Key)).ToList();

    public string? KeyOf(Type handler)
    {
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
// Считаем ПОСЛЕ next — по итоговому статусу: 401/403 (до обработчика запрос не дошёл) не в счёт.
// SignalR-хаб и ручки ядра неатрибутируемы и не считаются. MCP — по сегменту /mcp/{name}.
public sealed class SubsystemUsageMiddleware(
    RequestDelegate next, SubsystemUsageAttribution attribution, SubsystemUsageStore store)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        await next(ctx);

        var status = ctx.Response.StatusCode;
        if (status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden) return;

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
