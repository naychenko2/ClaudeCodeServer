using System.Reflection;
using ClaudeHomeServer.Services.Mcp.Http;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace ClaudeHomeServer.Services.Composition;

// Явная привязка обработчика (контроллера или тулсета) к ключу подсистемы (шаг S1b).
// Вертикальные контроллеры и тулсеты живут в сборке Main, поэтому по сборке их не отнести;
// обработчик без атрибута (и не из сборки вертикали) — ядро и не считается никогда.
// Атрибут на методе (S2b-1) — для смешанных ручек: часть эндпоинтов ядро, часть — вертикаль
// (PersonasController: аватарные эндпоинты → images, остальное — ядро). Методовой атрибут
// приоритетнее классового; на классе смешанной ручки его ставить нельзя — он приписал бы
// вертикали все её эндпоинты.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
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

        // Контроллеры ядра-сборки с явной привязкой: их ключи тоже наблюдаемы.
        // Методовые атрибуты смешанных ручек (S2b-1) — тот же набор наблюдаемых:
        // обращение к аватарной ручке персо — это обращение к вертикали images.
        // Тот же обход по домашним сборкам самих вертикалей: без него ключ, который
        // вернула атрибуция в такой сборке (в том числе строка `core:`), не попал бы
        // в набор стора и его обращения молча потерялись (R-S2b-1). Граница скана —
        // граница наблюдаемости: атрибут в Main или в домашней сборке активной
        // вертикали. Атрибут в любой другой сборке (например, выключенной вертикали)
        // ключа наблюдаемым не делает: KeyOf его вернуть может (ключ активен), но стор
        // игнорирует счёт вне набора (SubsystemUsageStore.cs:85); расширять скан на
        // такие сборки — решение постановщика, не этот шаг.
        // Main здесь уже не дублируется: CoreAssemblies исключены из _byAssembly выше.
        var attributedAssemblies = new List<Assembly> { typeof(SubsystemUsageAttribution).Assembly };
        attributedAssemblies.AddRange(_byAssembly.Keys);
        foreach (var assembly in attributedAssemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.GetCustomAttribute<SubsystemUsageAttribute>(inherit: false)?.Key is { } k
                    && _activeKeys.Contains(k))
                    _observable.Add(k);
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (m.GetCustomAttribute<SubsystemUsageAttribute>()?.Key is { } mk
                        && _activeKeys.Contains(mk))
                        _observable.Add(mk);
            }
        }

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

    // Ключ по ЭНДПОИНТУ: методовой атрибут приоритетнее классового (смешанная ручка
    // относится к вертикали ровно своими вертикальными эндпоинтами), дальше — обычный
    // путь KeyOf (классовый атрибут или карта «сборка → ключ»).
    // Методовой атрибут УТОЧНЯЕТ классовой, а не отменяет его: если его ключ не активен
    // (вертикаль выключена), откат к KeyOf — классовая атрибуция сохраняется, иначе
    // счётчик вертикали тихо теряется на выключенной смежной вертикали (R-S2b-1).
    public string? KeyOfAction(ControllerActionDescriptor action)
    {
        var methodKey = action.MethodInfo.GetCustomAttribute<SubsystemUsageAttribute>()?.Key;
        if (methodKey is not null && _activeKeys.Contains(methodKey)) return methodKey;
        return KeyOf(action.ControllerTypeInfo);
    }

    public string? KeyOfMcp(string name) => _mcpKeys.GetValueOrDefault(name);
}

// Мидлвар учёта обращений: после UseRouting у конечной точки видно действие контроллера.
// Считаем ПОСЛЕ next — по итоговому статусу: 401/403/404 (до обработчика запрос не дошёл или маршрут не найден) не в счёт.
// SignalR-хаб и ручки ядра неатрибутируемы и не считаются. MCP — по сегменту mcp в любой
// позиции пути: прямой /mcp/{name} и через шлюз ходов /gw/t/{turnId}/mcp/{name}.
public sealed class SubsystemUsageMiddleware(
    RequestDelegate next, SubsystemUsageAttribution attribution, SubsystemUsageStore store,
    ILogger<SubsystemUsageMiddleware> log)
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
        catch (Exception ex)
        {
            // Счётчик — наблюдение, а не часть запроса: сбой атрибуции не должен ломать ответ.
            // Но и молча теряться не должен: без лога отказ счётчика не виден нигде (R-S1b, п.6).
            log.LogWarning(ex, "Сбой атрибуции обращения к подсистеме: {Path}", ctx.Request.Path);
        }
    }

    private string? Resolve(HttpContext ctx)
    {
        // Сегмент mcp ищется в любой позиции пути: вызовы через шлюз ходов /gw/t/{turnId}/mcp/{name}
        // относятся к тому же тулсету, что и прямые /mcp/{name} (R-S1b, п.7). Сравнение сегмента
        // регистронезависимо, как прежний StartsWithSegments: /MCP/{name} атрибутировался и раньше.
        var segments = ctx.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is not null)
        {
            var mcpIndex = Array.FindIndex(segments, s => s.Equals("mcp", StringComparison.OrdinalIgnoreCase));
            if (mcpIndex >= 0 && mcpIndex + 1 < segments.Length)
                return attribution.KeyOfMcp(segments[mcpIndex + 1]);
        }

        var action = ctx.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        return action is null ? null : attribution.KeyOfAction(action);
    }
}

public static class SubsystemUsageMiddlewareExtensions
{
    public static IApplicationBuilder UseSubsystemUsage(this IApplicationBuilder app) =>
        app.UseMiddleware<SubsystemUsageMiddleware>();
}
