using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Models;

/// <summary>
/// Где работает группа подсистем проекта (ADR-016 §4): на сервере, на устройстве (в агенте)
/// или нигде. Строки, а не enum: в DTO едут как есть, фронт сравнивает их литералами.
/// </summary>
public static class CapabilityHost
{
    public const string Server = "server";
    public const string Device = "device";
    public const string Off = "off";
}

/// <summary>Состояние одной группы подсистем у конкретного проекта.</summary>
/// <param name="Host">Где группа работает — <see cref="CapabilityHost"/>.</param>
/// <param name="Available">Можно ли пользоваться группой прямо сейчас.</param>
/// <param name="Reason">Почему недоступна — готовый текст для человека; null, если доступна.</param>
/// <param name="Features">Состав группы — ключи <see cref="ProjectFeatures"/>.</param>
public sealed record ProjectCapabilityGroup(string Host, bool Available, string? Reason, IReadOnlyList<string> Features);

/// <summary>Можно ли запускать ход проекта прямо сейчас.</summary>
public sealed record ProjectExecCapability(bool Available, string? Reason);

/// <summary>Вердикт для фоновой работы по проекту (ADR-016, план §5).</summary>
public enum ProjectBackgroundVerdict
{
    /// <summary>Запускать можно: серверный проект или готовое устройство.</summary>
    Ready,
    /// <summary>Устройство не готово (офлайн, нет exec, харнес) — ждать его выхода в онлайн.</summary>
    WaitDevice,
    /// <summary>Устройства нет или оно отозвано — ждать нечего.</summary>
    DeviceGone,
}

/// <summary>
/// Вердикт фоновой работы с устройством проекта. <see cref="DeviceId"/> — устройство
/// локального проекта (null у серверного), <see cref="Reason"/> — текст для человека.
/// </summary>
public sealed record ProjectBackgroundGate(ProjectBackgroundVerdict Verdict, string? DeviceId, string? Reason)
{
    public static readonly ProjectBackgroundGate Ready = new(ProjectBackgroundVerdict.Ready, null, null);

    public bool IsReady => Verdict == ProjectBackgroundVerdict.Ready;
    public bool MustWait => Verdict == ProjectBackgroundVerdict.WaitDevice;
}

/// <summary>
/// Ключи подсистем проекта для матрицы (ADR-016 §4). Фронт скрывает панели по группе, в
/// которую входит ключ, а не по <c>deviceId</c> проекта.
/// </summary>
public static class ProjectFeatures
{
    // Привязаны к файлам проекта
    public const string Files = "files";
    public const string Diff = "diff";
    public const string Git = "git";
    public const string FileWatcher = "fileWatcher";
    public const string Terminal = "terminal";
    public const string DevServers = "devServers";
    public const string Skills = "skills";
    public const string Attachments = "attachments";

    // Платформа
    public const string Chat = "chat";
    public const string History = "history";
    public const string Tasks = "tasks";
    public const string Memory = "memory";
    public const string Personas = "personas";
    public const string Notes = "notes";
    public const string Costs = "costs";
    public const string Tts = "tts";

    // Нужен контент проекта на сервере
    public const string Knowledge = "knowledge";
    public const string CodeGraph = "codeGraph";
    public const string Dossiers = "dossiers";
    public const string Docs = "docs";
    public const string MapHygiene = "mapHygiene";

    // Нужен транскрипт CLI на сервере (ADR-016: у локального проекта он живёт на устройстве)
    public const string LiveSubagents = "liveSubagents";
    public const string WorkflowView = "workflowView";
    public const string ChatBranch = "chatBranch";

    public static readonly IReadOnlyList<string> FileBound =
        [Files, Diff, Git, FileWatcher, Terminal, DevServers, Skills, Attachments];

    public static readonly IReadOnlyList<string> Platform =
        [Chat, History, Tasks, Memory, Personas, Notes, Costs, Tts];

    public static readonly IReadOnlyList<string> ServerContent =
        [Knowledge, CodeGraph, Dossiers, Docs, MapHygiene];

    public static readonly IReadOnlyList<string> Transcript =
        [LiveSubagents, WorkflowView, ChatBranch];
}

/// <summary>
/// Матрица возможностей проекта (ADR-016 §4) — ЕДИНСТВЕННАЯ точка правды о том, серверный
/// проект или локальный и что из этого следует, для фронта и серверных guard'ов. Инлайновых
/// проверок локальности (<c>DeviceId != null</c>, <c>IsLocal</c>) вне этого файла не заводим:
/// их ловит сторож G10 (<c>ProjectCapabilitiesGuardTests</c>).
///
/// Четыре группы: <see cref="Files"/> — подсистемы, привязанные к файлам (у локального проекта —
/// в агенте устройства), <see cref="Platform"/> — всегда на сервере, <see cref="ServerContent"/>
/// — нужен контент проекта на сервере (у локального выключены), <see cref="Transcript"/> —
/// механики, читающие транскрипт CLI с диска сервера (у локального выключены).
/// </summary>
public sealed record ProjectCapabilities(
    string Host,
    string? DeviceId,
    ProjectCapabilityGroup Files,
    ProjectCapabilityGroup Platform,
    ProjectCapabilityGroup ServerContent,
    ProjectCapabilityGroup Transcript,
    ProjectExecCapability Exec)
{
    public const string DeviceMissingReason = "Устройство проекта не найдено или отозвано";
    public const string DeviceOfflineReason = "Устройство проекта не в сети";
    public const string NoExecReason = "На устройстве нет агента AI Home для локальных проектов: чаты проекта не могут работать";
    public const string NoFilesReason = "Агент устройства пока не открывает файлы проекта: обновите агента";
    public const string NoRelayReason = "Агент устройства не умеет показывать файлы проекта на других устройствах: обновите агента";
    public const string NotDeviceBoundReason = "Проект не локальный: его файлы на сервере";
    public const string ServerContentOffReason = "У локального проекта недоступно: его файлы лежат на устройстве, а не на сервере";
    public const string TranscriptOnDeviceReason =
        "У локального проекта недоступно: подробная история чата хранится на его устройстве";

    /// <summary>Проект привязан к устройству. Единственная проверка локальности во всём коде.</summary>
    public static bool IsDeviceBound(Project project) => project.DeviceId is not null;

    /// <summary>Файлы проекта лежат на диске сервера — вход серверной файловой подсистемы допустим.</summary>
    public static bool FilesOnServer(Project project) => !IsDeviceBound(project);

    /// <summary>Работает ли у проекта группа «нужен контент на сервере» (Dify, CodeGraph, досье, Docs, уборка карты).</summary>
    public static bool ServerContentEnabled(Project project) => !IsDeviceBound(project);

    /// <summary>
    /// Транскрипты CLI (<c>.jsonl</c>) чатов проекта лежат на диске сервера. У локального проекта
    /// они живут только на устройстве: сервер их не ищет, не копирует и не удаляет — промах
    /// поиска по своему пути он принял бы за «транскрипта нет».
    /// </summary>
    public static bool TranscriptOnServer(Project project) => !IsDeviceBound(project);

    /// <summary>
    /// Ключ папки проекта — пара «устройство + путь» (ADR-016 §1): одна и та же строка пути на
    /// сервере и на устройстве даёт разные ключи. У серверного проекта ключ совпадает с
    /// прежним <see cref="PathNormalizer.NormalizePath"/> — сторы на диске не мигрируют.
    /// </summary>
    public static string FolderKey(Project project) => FolderKey(project.DeviceId, project.RootPath);

    public static string FolderKey(string? deviceId, string rootPath) =>
        deviceId is null
            ? PathNormalizer.NormalizePath(rootPath)
            // Путь чужой машины: GetFullPath сервера его исказил бы (Windows-путь на Linux),
            // поэтому только разделители, хвост и регистр
            : "device:" + deviceId + ":" + NormalizeDevicePath(rootPath).ToLowerInvariant();

    /// <summary>
    /// Ключ датасета знаний проекта. null — у проекта нет серверного датасета вовсе (локальный:
    /// группа «контент на сервере» выключена), поэтому одноимённая серверная папка не делит с
    /// ним базу знаний.
    /// </summary>
    public static string? KnowledgeRoot(Project project) =>
        ServerContentEnabled(project) ? project.RootPath : null;

    /// <summary>
    /// Путь на устройстве в каноничной форме: абсолютный (корень диска Windows или «/»), без
    /// сегментов «.»/«..», единый разделитель по виду пути, без хвостового разделителя. На
    /// сервере не существует — проверять его наличие некому, кроме агента.
    /// </summary>
    public static string NormalizeDevicePath(string rootPath)
    {
        var raw = rootPath.Trim();
        var windows = raw.Length >= 3 && char.IsAsciiLetter(raw[0]) && raw[1] == ':' && raw[2] is '\\' or '/';
        if (!windows && !raw.StartsWith('/'))
            throw new ArgumentException("Путь на устройстве должен быть абсолютным: «C:\\папка» или «/папка»");
        var sep = windows ? '\\' : '/';
        var segments = raw.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is "." or ".."))
            throw new ArgumentException("Путь на устройстве не должен содержать «.» и «..»");
        return windows
            ? segments.Length == 1 ? segments[0] + sep : string.Join(sep, segments)
            : "/" + string.Join(sep, segments);
    }

    /// <summary>
    /// Можно ли привязать проект к устройству: флаг <c>local-projects</c> владельца и возможность
    /// <c>exec</c> у устройства. null — можно, иначе готовый текст отказа. Онлайн не требуется:
    /// возможность хранится у устройства и в офлайне.
    /// </summary>
    public static string? BindRefusal(bool localProjectsEnabled, DeviceExecStatus? device)
    {
        if (!localProjectsEnabled) return "Локальные проекты выключены (экспериментальная функция «Локальные проекты»)";
        if (device is null) return DeviceMissingReason;
        if (!device.HasCapability(DeviceCapabilities.Exec)) return NoExecReason;
        return null;
    }

    /// <summary>
    /// Потолок ожидания устройства для разовой фоновой работы (ADR-016, вариант А плана §5):
    /// то же окно, что у автозапуска задач по сроку. Истёк — отказ с уведомлением, а не тишина.
    /// </summary>
    public static readonly TimeSpan DeviceWaitCeiling = TimeSpan.FromHours(24);

    /// <summary>
    /// Можно ли фоновой работе (исполнитель задачи, волна штаба, очередь чата, автоматизация,
    /// опрос сторожа) запускать ход проекта прямо сейчас. Серверный проект — всегда да.
    /// Локальный: устройство готово — да; устройства нет (отозвано) — ждать нечего, отказ;
    /// иначе (офлайн, нет exec, харнес не готов) — ждать выхода устройства в онлайн.
    /// </summary>
    public static ProjectBackgroundGate BackgroundGate(Project project, DeviceExecStatus? device)
    {
        if (!IsDeviceBound(project)) return ProjectBackgroundGate.Ready;
        if (device is null)
            return new ProjectBackgroundGate(ProjectBackgroundVerdict.DeviceGone, project.DeviceId, DeviceMissingReason);
        var exec = For(project, device).Exec;
        return exec.Available
            ? new ProjectBackgroundGate(ProjectBackgroundVerdict.Ready, project.DeviceId, null)
            : new ProjectBackgroundGate(ProjectBackgroundVerdict.WaitDevice, project.DeviceId, exec.Reason);
    }

    /// <summary>
    /// Чтение файлов проекта с другого устройства через ретранслятор (ADR-016 §5): только у
    /// локального проекта и только пока его устройство онлайн и объявило ретранслятор.
    /// null — можно, иначе причина для человека.
    /// </summary>
    public static string? RelayRefusal(Project project, DeviceExecStatus? device) =>
        !IsDeviceBound(project) ? NotDeviceBoundReason
        : device is null ? DeviceMissingReason
        : !device.Online ? DeviceOfflineReason
        : !device.HasCapability(DeviceCapabilities.Relay) ? NoRelayReason
        : null;

    /// <summary>
    /// Матрица для проекта. <paramref name="device"/> — состояние устройства проекта из шва
    /// <see cref="IDeviceExecChannel"/>; у серверного проекта не нужен, у локального null
    /// означает «устройства нет» (отозвано или канал недоступен).
    /// </summary>
    public static ProjectCapabilities For(Project project, DeviceExecStatus? device)
    {
        var platform = new ProjectCapabilityGroup(CapabilityHost.Server, true, null, ProjectFeatures.Platform);
        if (!IsDeviceBound(project))
            return new ProjectCapabilities(
                CapabilityHost.Server, null,
                new ProjectCapabilityGroup(CapabilityHost.Server, true, null, ProjectFeatures.FileBound),
                platform,
                new ProjectCapabilityGroup(CapabilityHost.Server, true, null, ProjectFeatures.ServerContent),
                new ProjectCapabilityGroup(CapabilityHost.Server, true, null, ProjectFeatures.Transcript),
                new ProjectExecCapability(true, null));

        string? filesReason =
            device is null ? DeviceMissingReason
            : !device.Online ? DeviceOfflineReason
            : !device.HasCapability(DeviceCapabilities.Files) ? NoFilesReason
            : null;

        string? execReason =
            device is null ? DeviceMissingReason
            : !device.Online ? DeviceOfflineReason
            : !device.HasCapability(DeviceCapabilities.Exec) ? NoExecReason
            : !device.HarnessReady ? device.HarnessProblem ?? "Харнес устройства не готов"
            : null;

        return new ProjectCapabilities(
            CapabilityHost.Device, project.DeviceId,
            new ProjectCapabilityGroup(CapabilityHost.Device, filesReason is null, filesReason, ProjectFeatures.FileBound),
            platform,
            new ProjectCapabilityGroup(CapabilityHost.Off, false, ServerContentOffReason, ProjectFeatures.ServerContent),
            new ProjectCapabilityGroup(CapabilityHost.Off, false, TranscriptOnDeviceReason, ProjectFeatures.Transcript),
            new ProjectExecCapability(execReason is null, execReason));
    }
}
