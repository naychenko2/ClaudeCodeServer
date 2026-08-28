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

/// <summary>
/// Вход ревизии: имя записи каталога и версия, зашедшая в запись при импорте
/// (импортированной может не быть — тогда сверка версий молча не проводится).
/// </summary>
public sealed record McpCatalogRevisionQuery(string Name, string? ImportedVersion);

/// <summary>
/// Итог сверки одной импортированной записи с живым реестром. «Отозван» (Deprecated)
/// ставится ТОЛЬКО по явному status: deprecated/deleted в разобранном ответе; любая
/// беда проверки — CheckFailed с причиной в Error, и это НЕ «отозван»: preview-сервис
/// имеет полное право лежать, и в этот момент человек не должен гасить рабочие серверы.
/// </summary>
public sealed record McpCatalogRevisionItem(
    string Name, string? Status, bool Deprecated, bool HasNewerVersion,
    string? LatestVersion, bool CheckFailed, string? Error);
