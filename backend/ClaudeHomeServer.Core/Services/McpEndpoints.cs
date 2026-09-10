namespace ClaudeHomeServer.Services;

// Единая точка построения URL MCP-сервера и перечня их wire-имён. Core-примитив: имя
// сервера и арифметика пути стабильны между ходами, должны доезжать до Llm (ранее
// тянулись из `Services/Mcp.Http` как из чужой вертикали).
//
// Сами тулсеты (`TasksToolset`, `NotesToolset`, …) остаются в Main: у них
// прикладная логика списка инструментов и регистрация в DI. Здесь — только
// то, что ходит через границу вертикалей: имена в URL и сам URL.
public static class McpEndpoints
{
    // Имена серверов в URL — совпадают с литералами в `mcp.json` клиента Claude.
    // Списком держать в Core: фронт рисует рубрикатор по этому же перечню,
    // и Main не должен быть источником истины для шинного контракта.
    public const string TasksName = "tasks";
    public const string NotesName = "notes";
    public const string MemoryName = "memory";
    public const string PersonasName = "personas";
    public const string NotificationsName = "notifications";
    public const string WatchName = "watch";
    public const string WebSearchName = "websearch";
    public const string CodeGraphName = "codegraph";
    public const string DifyName = "dify";
    public const string WidgetsName = "widgets";
    public const string WorkspaceName = "wsp";

    /// <summary>
    /// Адрес эндпоинта сервера: базовый URL владельца плюс маршрут контроллера. Строится из
    /// РАЗОБРАННОГО адреса, а не конкатенацией сырой строки — гейт судит по нормализованному
    /// Uri, и адрес в конфиге хода обязан с ним соглашаться.
    ///
    /// Вынесено из `McpHttpTransport.EndpointFor` (этап 5, волна 4 переноса спины):
    /// ClaudeSession собирает конфиг хода и зовёт формулу 14 раз через границу
    /// вертикали Llm ↔ Mcp, без Core-примитива Llm не мог бы стоять в отдельной сборке.
    /// </summary>
    public static string EndpointFor(string apiUrl, string server) =>
        Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/') + "/mcp/" + server
            : apiUrl.TrimEnd('/') + "/mcp/" + server;
}
