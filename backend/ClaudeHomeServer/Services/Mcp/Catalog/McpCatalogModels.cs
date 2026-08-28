namespace ClaudeHomeServer.Services.Mcp.Catalog;

/// <summary>
/// Поле формы заведения, которое заполняет человек при импорте из каталога:
/// переменная окружения, заголовок, переменная адреса или аргумент командной строки.
/// Секретные значения из реестра не приезжают никогда — только имя и описание.
/// </summary>
/// <param name="Target">Куда подставить: env | header | url | args.</param>
/// <param name="Name">Имя env/заголовка, переменной в адресе или плейсхолдера аргумента.</param>
public sealed record McpCatalogFieldDto(
    string Target, string Name, string? Description, bool Required, bool Secret, string? Default);

/// <summary>
/// Предзаполнение формы заведения сервера из декларации реестра. Описания полей живут
/// только в сессии импорта: в McpServerRecord они не кладутся.
/// </summary>
public sealed record McpCatalogPrefillDto(
    string Key, string? Label, string? Description, string Transport,
    string? Command, IReadOnlyList<string> Args, string? Url,
    IReadOnlyList<McpCatalogFieldDto> Fields);

/// <summary>
/// Карточка результата поиска по каталогу. Connectable=false — подключить из каталога
/// нельзя (тип пакета, аргументы, версия, окружение, отзыв), причина — в Notice.
/// </summary>
public sealed record McpCatalogCardDto(
    string Name, string? Title, string? Description, string? RepositoryUrl,
    string? Version, DateTime? PublishedAt, string Status, bool IsLatest,
    bool Connectable, string? Notice, McpCatalogPrefillDto? Prefill);

/// <summary>Страница результатов поиска по каталогу.</summary>
public sealed record McpCatalogSearchResult(IReadOnlyList<McpCatalogCardDto> Items, string? NextCursor);
