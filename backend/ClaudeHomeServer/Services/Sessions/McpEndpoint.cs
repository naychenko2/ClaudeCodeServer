using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Sessions;

// Адрес и сервисный токен бэкенда для MCP-прокси (S3-4, docs/research/session-manager-split-2026-11.md):
// базовый URL API, замена wildcard-хоста на localhost, пригоден ли адрес для http-транспорта,
// единый per-owner JWT. Вынесено из SessionManager дословно; фасад держит делегирующие
// обёртки — на них сидят внешние вызовы (McpBackendAccess, контексты хода).
// Зависимостей пять: config, IServer, JwtService, ILauncherFactory (в таблице S3-0 §2 их три —
// строка 757 исходника берёт override адреса из среды владельца, то есть launchers).
// Класс public ТОЛЬКО потому, что он параметр публичного конструктора SessionManager (CS0051) и DI нужен
// публичный конструктор; обход фасада закрывает сторож формы ядра (правило 4: на типы Services.Sessions
// ссылаются лишь SessionManager, сами Services.Sessions и тесты).
public sealed class McpEndpoint
{
    private readonly IConfiguration _config;
    private readonly Microsoft.AspNetCore.Hosting.Server.IServer _server;
    private readonly JwtService _jwt;
    private readonly Execution.ILauncherFactory _launchers;
    private readonly ILogger<McpEndpoint> _log;

    // Предупреждение о неhttp-адресе — один раз на адрес: состояние постоянное, ход идёт каждую минуту.
    private string? _httpMcpWarnedFor;

    public McpEndpoint(IConfiguration config,
        Microsoft.AspNetCore.Hosting.Server.IServer server,
        JwtService jwt,
        Execution.ILauncherFactory launchers,
        ILogger<McpEndpoint> log)
    {
        _config = config;
        _server = server;
        _jwt = jwt;
        _launchers = launchers;
        _log = log;
    }

    // Базовый URL API для MCP-сервера: среда владельца (из песочницы Kestrel виден
    // как host.docker.internal) → конфиг → адрес Kestrel → дефолт.
    // 0.0.0.0/[::] заменяем на localhost — MCP-сервер ходит с той же машины.
    // Среди адресов Kestrel предпочитаем http: MCP-серверы на node ходят обычным
    // fetch, а боевой серт выписан на внешний домен — по https://localhost они
    // упираются в ERR_TLS_CERT_ALTNAME_INVALID (localhost/127.0.0.1 нет в SAN).
    // Если http-адреса нет вообще, поднимите локальный http-эндпоинт и пропишите
    // McpTasksApiUrl явно — иначе все MCP-прокси (tasks/notes/memory/wsp) отвалятся.
    internal string ResolveTasksApiUrl(string? ownerId = null)
    {
        if (ownerId is not null && _launchers.ForOwner(ownerId).McpApiUrlOverride is { } sandboxUrl)
            return sandboxUrl.TrimEnd('/');

        var fromConfig = _config["McpTasksApiUrl"];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig.TrimEnd('/');

        var addresses = _server.Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?
            .Addresses;
        var addr = addresses?.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                   ?? addresses?.FirstOrDefault();
        if (string.IsNullOrEmpty(addr)) return "http://localhost:5000";
        return LoopbackApiUrl(addr);
    }

    // Wildcard-хост адреса прослушивания → localhost. Kestrel и хост отдают в
    // IServerAddressesFeature и сырые формы конфига: ASPNETCORE_HTTP_PORTS/URLS дают
    // http://*:порт или http://+:порт, а такой адрес не разбирается в Uri (UriFormatException
    // у шлюза MCP и у всех MCP-конфигов хода).
    private static readonly Regex WildcardHost =
        new(@"^(?<scheme>[a-z][a-z0-9+.-]*://)(?:\*|\+|0\.0\.0\.0|\[::\])(?=[:/]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string LoopbackApiUrl(string address) =>
        WildcardHost.Replace(address, "${scheme}localhost").TrimEnd('/');

    // Единый сервисный токен владельца для MCP-серверов (tasks/notes/memory/personas/…):
    // per-owner JWT с перевыпуском за сутки до истечения (сервер может жить дольше срока токена).
    internal string GetServiceToken(string ownerId) =>
        _serviceTokens.AddOrUpdate(ownerId,
            id => (_jwt.IssueServiceToken(id), DateTime.UtcNow),
            (id, old) => DateTime.UtcNow - old.IssuedAt > JwtService.ServiceTokenLifetime - TimeSpan.FromDays(1)
                ? (_jwt.IssueServiceToken(id), DateTime.UtcNow)
                : old).Token;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Token, DateTime IssuedAt)>
        _serviceTokens = new();

    // Пригоден ли адрес для MCP-over-HTTP. Рубильник Mcp:HttpTransport сюда НЕ входит —
    // он живой, читается провайдером на каждый ход (HttpMcpEnabledProvider); здесь только
    // форма адреса.
    internal bool HttpEndpointUsable(string apiUrl)
    {
        var http = Services.Mcp.Http.McpHttpTransport.Usable(apiUrl, enabled: true);
        // Предупреждаем один раз на адрес: состояние постоянное, ход идёт каждую минуту.
        // Узнаёт и хозяин выключенного рубильника: включённый транспорт этот адрес всё равно
        // не поднимет — инстанс останется на stdio.
        if (!http && Interlocked.Exchange(ref _httpMcpWarnedFor, apiUrl) != apiUrl)
            _log.LogWarning("MCP-over-HTTP невозможен: адрес бэкенда «{Url}» не http — "
                + "продуктовые серверы объявляются ходу по-старому, через stdio", apiUrl);
        return http;
    }
}
