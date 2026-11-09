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
// в сборке самой вертикали: она однозначна ровно тогда, когда подсистема в ней одна
// (по факту — одна подсистема на сборку). По namespace относить нельзя: namespace подсистемы
// ClaudeHomeServer.Services.X не является префиксом namespace её контроллера
// ClaudeHomeServer.X.Controllers — несколько подсистем в одной сборке атрибутуются только
// явно (R-S1b, m2). Сборки Main и Core ядром не считаются: без атрибута обращение
// неатрибутируемо.
// Ключи атрибутов не из числа активных подсистем (выключена или не загружена) игнорируются.
public sealed class SubsystemUsageAttribution
{
    private static readonly Assembly[] CoreAssemblies =
        [typeof(SubsystemUsageAttribution).Assembly, typeof(SubsystemUsageStore).Assembly];

    private readonly Dictionary<Assembly, List<string>> _byAssembly = new();
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
            list.Add(key);
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
        // Оговорка про путь тулсетов (ниже, цикл по toolsets): он этой границы скана
        // НЕ имеет — KeyOf типа тулсета с явным атрибутом не проверяет сборку, и
        // активный ключ попал бы в _observable напрямую, из любой сборки. Случай
        // недостижим в текущей составке: тулсет регистрирует только активная вертикаль
        // (выключенная не зовёт Register, IAppSubsystem.cs:253-257; динамический модуль
        // не грузится вовсе, ModuleLoader.cs:64) или Main — поэтому формулировка выше
        // верна для всех реальных случаев, но как утверждение о коде она широка.
        // Держать ту же проверку сборки в пути тулсетов — решение постановщика, не этот
        // шаг (хвост e071b37b по ревью R-S2b-1).
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

        // Своя сборка вертикали: относится ровно тогда, когда подсистема в ней одна.
        // Несколько подсистем в одной сборке по сборке не отнести: прежняя ветка «самого
        // длинного префикса namespace» не срабатывала никогда — namespace подсистемы
        // (ClaudeHomeServer.Services.X) не является префиксом namespace её контроллера
        // (ClaudeHomeServer.X.Controllers) и давала бы null для обеих; такой случай
        // требует явного атрибута на контроллере/тулсете (R-S1b, m2).
        return _byAssembly.TryGetValue(handler.Assembly, out var list) && list.Count == 1
            ? list[0]
            : null;
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
// Считаем ПОСЛЕ next — по итоговому статусу И по смонтированному эндпоинту:
// 401 — отказ аутентификации ДО обработчика (UseAuthentication стоит после UseRouting,
// эндпоинт к этому моменту уже смонтирован): обращения к подсистеме не было — не в счёт
// при любом эндпоинте. 403/404 — по источнику: без эндпоинта маршрута нет (его снял гейт
// подсистемы или его нет вовсе) — обращения не было; с эндпоинтом ответил сам обработчик
// вертикали («заметка не найдена», «доступа нет») — это состоявшееся обращение (R-S1b, m1).
// SignalR-хаб и ручки ядра неатрибутируемы и не считаются. MCP — по сегменту mcp в любой
// позиции пути (прямой /mcp/{name}), КРОМЕ внешней ноги шлюза ходов /gw/t/{turnId}/mcp/{name}:
// она не есть обращение к тулсету — до тулсета доходит внутренний запрос шлюза, он и даёт +1
// (инвариант: один вызов тулсета = ровно +1; R-S1b, M1).
public sealed class SubsystemUsageMiddleware(
    RequestDelegate next, SubsystemUsageAttribution attribution, SubsystemUsageStore store,
    ILogger<SubsystemUsageMiddleware> log)
{
    // Префикс маршрутов шлюза ходов: /gw/t/{turnId}/… (McpGatewayEndpoints.RoutePattern,
    // LlmGatewayEndpoints.Route, EgressGatewayEndpoints.RoutePattern, OcrGatewayEndpoints.Route)
    private const string GatewaySegment = "gw";
    public async Task InvokeAsync(HttpContext ctx)
    {
        await next(ctx);

        var status = ctx.Response.StatusCode;
        if (status == StatusCodes.Status401Unauthorized) return;

        var endpoint = ctx.GetEndpoint();
        if (endpoint is null
            && status is StatusCodes.Status403Forbidden or StatusCodes.Status404NotFound) return;

        try
        {
            var key = Resolve(ctx, endpoint);
            if (key is not null) store.Hit(key, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            // Счётчик — наблюдение, а не часть запроса: сбой атрибуции не должен ломать ответ.
            // Но и молча теряться не должен: без лога отказ счётчика не виден нигде (R-S1b, п.6).
            log.LogWarning(ex, "Сбой атрибуции обращения к подсистеме: {Path}", ctx.Request.Path);
        }
    }

    private string? Resolve(HttpContext ctx, Endpoint? endpoint)
    {
        // Сегмент mcp ищется в любой позиции пути (кроме внешней ноги шлюза): прямой /mcp/{name}
        // и его хвосты относятся к тулсету. Сравнение сегмента регистронезависимо, как прежний
        // StartsWithSegments: /MCP/{name} атрибутировался и раньше.
        var segments = ctx.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is not null && !IsGatewayOuter(segments))
        {
            var mcpIndex = Array.FindIndex(segments, s => s.Equals("mcp", StringComparison.OrdinalIgnoreCase));
            if (mcpIndex >= 0 && mcpIndex + 1 < segments.Length)
                return attribution.KeyOfMcp(segments[mcpIndex + 1]);
        }

        var action = endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>();
        return action is null ? null : attribution.KeyOfAction(action);
    }

    // Внешняя нога шлюза ходов: /gw/t/{turnId}/mcp/{name}/{хвост}. Не считается: шлюз проксирует её
    // на /mcp/{name}/{хвост} своего же Kestrel (McpGatewayEndpoints.cs:94 → McpBackendAccess.cs:8 →
    // SessionManager.ResolveTasksApiUrl), и тот запрос проходит этот же мидлвар (Program.cs:1116) —
    // он единственный дошёл до тулсета и он же счёт. Считать обе ноги = +2 за один вызов (R-S1b, M1).
    // Отличать внутренний запрос по X-Caller-Session-Id и считать только внешний — другой контракт
    // счётчика для всех MCP-вызовов: решение постановщика, не этот шаг.
    private static bool IsGatewayOuter(string[] segments) =>
        segments.Length > 0 && segments[0].Equals(GatewaySegment, StringComparison.OrdinalIgnoreCase);
}

public static class SubsystemUsageMiddlewareExtensions
{
    public static IApplicationBuilder UseSubsystemUsage(this IApplicationBuilder app) =>
        app.UseMiddleware<SubsystemUsageMiddleware>();
}
