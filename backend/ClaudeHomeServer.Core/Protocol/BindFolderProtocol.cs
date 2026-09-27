namespace ClaudeHomeServer.Protocol;

/// <summary>
/// Выдача папки локального проекта агентом (решение владельца 2026-09-27, ADR-016 §5): при
/// создании и перепривязке проекта агент сам создаёт папку и добавляет в корни машины ровно
/// её. Канал — канал исполнения с назначением <see cref="DeviceExecPurposes.BindFolder"/>:
/// запрос — кадр Control, ответ — Info (<see cref="RelayResponseHead"/>), тело Stdout, Exit.
///
/// Запретный список путей и выключатель автовыдачи живут только на агенте: сервер их не
/// видит и не переопределяет, операции включить автовыдачу в протоколе нет.
/// </summary>
public static class BindFolderProtocol
{
    public const string Operation = "bind-project-folder";

    /// <summary>Потолок длины имени проекта: оно только подпись в <c>roots list</c>.</summary>
    public const int MaxProjectNameLength = 200;
}

/// <summary>Запрос: абсолютный путь на машине и имя проекта — только для подписи корня.</summary>
public sealed record BindFolderRequest(string Path, string? ProjectName);

/// <summary>Итоги выдачи.</summary>
public static class BindFolderOutcomes
{
    /// <summary>Папка годится: есть, каталог, под корнями (возможно, только что созданная и добавленная).</summary>
    public const string Bound = "bound";

    /// <summary>По пути лежит файл.</summary>
    public const string NotDirectory = "not-directory";

    /// <summary>Путь в запретном списке агента.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>
    /// Автовыдача выключена на машине (<c>roots auto off</c>): поля проверки заполнены, сервер
    /// отвечает прежним отказом с подсказкой команды.
    /// </summary>
    public const string AutoOff = "auto-off";

    /// <summary>Выдать не вышло: путь не абсолютный, каталог общий на запись, ошибка ФС.</summary>
    public const string Refused = "refused";
}

/// <summary>
/// Ответ агента. <see cref="Message"/> — текст отказа для человека; <see cref="Exists"/>,
/// <see cref="IsDirectory"/>, <see cref="InsideRoots"/> — вердикт проверки для <see cref="BindFolderOutcomes.AutoOff"/>.
/// </summary>
public sealed record BindFolderResult(
    string Outcome,
    bool Created = false,
    bool Added = false,
    string? Message = null,
    bool Exists = false,
    bool IsDirectory = false,
    bool InsideRoots = false);
