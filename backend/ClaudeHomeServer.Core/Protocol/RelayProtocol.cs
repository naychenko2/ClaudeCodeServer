using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Protocol;

// Ретранслятор чтения для других устройств (ADR-016 §5, задача 5.1): проект, чьи файлы живут на
// устройстве, открыт с телефона или чужого ноутбука — сервер пересылает чтение агенту
// устройства. Записи в протоколе НЕТ по построению: перечень операций замкнут
// (RelayOperations.All), и сторож G6 сверяет его с allow-list теста.

/// <summary>
/// Операции ретранслятора — замкнутый перечень, только чтение. Новая операция без правки
/// allow-list сторожа G6 (<c>RelayProtocolGuardTests</c>) красит сборку.
/// </summary>
public static class RelayOperations
{
    public const string List = "list";
    public const string Read = "read";
    public const string Stat = "stat";
    public const string Search = "search";
    public const string GitStatus = "git-status";
    public const string GitDiff = "git-diff";
    public const string GitLog = "git-log";
    public const string GitShow = "git-show";

    public static readonly IReadOnlyList<string> All = [List, Read, Stat, Search, GitStatus, GitDiff, GitLog, GitShow];

    public static bool IsKnown(string? operation) => operation is not null && All.Contains(operation);
}

/// <summary>
/// Разновидность операции — какой маршрут сервера она обслуживает. Форма ответа у каждой
/// разновидности — ровно форма соответствующего маршрута FilesController/GitController.
/// </summary>
public static class RelayVariants
{
    /// <summary><c>list</c>: дерево вместо одного уровня (<c>files/tree</c>).</summary>
    public const string Tree = "tree";

    /// <summary><c>read</c>: байты потоком (<c>files/stream</c>) вместо вида файла (<c>files/content</c>).</summary>
    public const string Stream = "stream";

    /// <summary><c>git-diff</c>: дифф файла к HEAD (<c>files/diff</c>) вместо <c>git/diff</c>.</summary>
    public const string File = "file";

    /// <summary><c>git-show</c>: дифф файла в коммите (<c>git/commits/{sha}/diff</c>).</summary>
    public const string CommitDiff = "diff";

    /// <summary><c>git-show</c>: файл в версии коммита (<c>git/commits/{sha}/file</c>).</summary>
    public const string CommitFile = "file";
}

/// <summary>
/// Запрос ретранслятора: JSON первого кадра <see cref="DeviceExecFrameChannel.Control"/> канала
/// с назначением <see cref="DeviceExecPurposes.Relay"/>. <see cref="RootPath"/> — корень проекта
/// по данным сервера: агент сверяет его со своими разрешёнными корнями и реальным путём и
/// серверу не доверяет (сторож G7).
/// </summary>
public sealed record RelayRequest(
    string Operation,
    string ProjectId,
    string RootPath,
    string? Variant = null,
    string? Path = null,
    bool ShowHidden = false,
    string? Query = null,
    bool Staged = false,
    int? Limit = null,
    string? Branch = null,
    string? Sha = null);

/// <summary>
/// Заголовок ответа агента: кадр <see cref="DeviceExecFrameChannel.Info"/>. За ним тело кадрами
/// <see cref="DeviceExecFrameChannel.Stdout"/> и конец — кадр <see cref="DeviceExecFrameChannel.Exit"/>.
/// Отказ — код не 2xx и тело <c>{ "error": "…" }</c>.
/// </summary>
public sealed record RelayResponseHead(int Status, string? ContentType = null, long? Length = null);

/// <summary>Маршрут ретранслятора относительно <c>api/projects/{projectId}/relay/</c>: всегда GET.</summary>
public sealed record RelayRoute(string Template, string Operation, string? Variant = null)
{
    public ProjectApiRoute AsProjectRoute() => new("GET", Template);
}

public static class RelayProtocol
{
    /// <summary>
    /// Сегмент базы адреса: ретранслятор — <c>api/projects/{projectId}/relay/…</c>, дальше те же
    /// маршруты, что у сервера. Фронту меняется только база адреса.
    /// </summary>
    public const string RouteSegment = "relay";

    /// <summary>
    /// Код отказа «устройство сейчас не отдаёт файлы» (офлайн, старый агент, оборвалась связь):
    /// для фронта это состояние проекта, а не ошибка запроса.
    /// </summary>
    public const string UnavailableCode = "relay_unavailable";

    /// <summary>Сколько сервер ждёт заголовка ответа: поиск по большому дереву бывает долгим.</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Потолок простоя канала ретранслятора без соединения. У хода это минуты: там живой
    /// процесс, который жалко терять. Здесь — один запрос чтения, и честный отказ лучше
    /// висящей панели.
    /// </summary>
    public static readonly TimeSpan MaxOutage = TimeSpan.FromSeconds(15);

    /// <summary>Сколько агент ждёт кадра запроса после открытия канала.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Потолок JSON-ответа (листинг, поиск, дифф, лог): большие ответы — только потоком
    /// (<see cref="RelayVariants.Stream"/>), под собственным потолком агента.
    /// </summary>
    public const long MaxJsonBytes = 64L * 1024 * 1024;

    /// <summary>Сериализация как у MVC сервера: camelCase, перечисления строкой в camelCase.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Маршруты ретранслятора. Шаблоны — те же, что у серверных FilesController/GitController
    /// (контракт-тест сверяет форму ответов), кроме <see cref="RelayOnly"/>.
    /// </summary>
    public static readonly IReadOnlyList<RelayRoute> Routes =
    [
        new("files", RelayOperations.List),
        new("files/tree", RelayOperations.List, RelayVariants.Tree),
        new("files/content", RelayOperations.Read),
        new("files/stream", RelayOperations.Read, RelayVariants.Stream),
        new("files/stat", RelayOperations.Stat),
        new("files/search", RelayOperations.Search),
        new("files/diff", RelayOperations.GitDiff, RelayVariants.File),
        new("git/status", RelayOperations.GitStatus),
        new("git/diff", RelayOperations.GitDiff),
        new("git/log", RelayOperations.GitLog),
        new("git/commits/{sha}", RelayOperations.GitShow),
        new("git/commits/{sha}/diff", RelayOperations.GitShow, RelayVariants.CommitDiff),
        new("git/commits/{sha}/file", RelayOperations.GitShow, RelayVariants.CommitFile),
    ];

    /// <summary>Маршруты, которых у сервера нет: у серверного проекта свойства файла видны из листинга.</summary>
    public static readonly IReadOnlyList<string> RelayOnly = ["files/stat"];
}
