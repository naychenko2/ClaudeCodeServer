using System.Text.Json;

namespace ClaudeHomeServer.Protocol;

// ---------- руки локального проекта (ADR-016, раздел «Руки»; план docs/research/hands-plan-2026-09.md, Ш1) ----------
//
// Контракт без поведения: типы и константы, от которых отталкиваются сервер (Ш4), мост (Ш2),
// агент (Ш3) и трей (Ш7). Модель угроз — docs/architecture/device-agent-local-api.md, раздел «Руки».

/// <summary>
/// Что сервер обязан запретить в ходе с руками и что агент сверяет перед подключением моста
/// (сквозные решения 2 и 3). Одно определение на обе стороны: сервер пишет его в
/// <c>--disallowedTools</c>, агент отказывает ходу, если в spawn есть маркер рук, а полного
/// набора в аргументах нет. Правила — в синтаксисе правил разрешений CLI.
/// </summary>
public static class HandsTurnRules
{
    /// <summary>
    /// Shell закрыт целиком, allow-list отвергнут. <c>Task</c> — пока не доказано, что
    /// сабагент наследует <c>--disallowedTools</c>; <c>Agent</c> — то же имя инструмента в
    /// новых версиях CLI (в потоке встречаются оба).
    /// </summary>
    public static readonly IReadOnlyList<string> DisallowedTools =
        ["Bash", "PowerShell", "Monitor", "BashOutput", "KillShell", "Task", "Agent"];

    /// <summary>
    /// Отложенный запуск кода через файлы проекта: хуки из <c>.claude/settings*.json</c> и
    /// MCP-серверы из <c>.mcp.json</c> исполнились бы при следующем запуске CLI на устройстве.
    /// </summary>
    public static readonly IReadOnlyList<string> DisallowedWriteRules =
        ["Edit(.claude/**)", "Write(.claude/**)", "Edit(.mcp.json)", "Write(.mcp.json)"];

    /// <summary>Полный набор для <c>--disallowedTools</c> хода с руками.</summary>
    public static readonly IReadOnlyList<string> All = [.. DisallowedTools, .. DisallowedWriteRules];
}

/// <summary>Файлы и каталоги рук на машине. Сервер их не видит и не правит.</summary>
public static class HandsFiles
{
    /// <summary>
    /// Белый список программ — в каталоге конфига агента, рядом с <c>roots.json</c>. Правит только
    /// команда машины <c>ai-home-agent hands allow-app/deny-app</c> (и трей через неё же).
    /// Формат — <see cref="HandsAppsFile"/>.
    /// </summary>
    public const string AppsFileName = "hands-apps.json";

    /// <summary>
    /// Состояние сеанса — в каталоге данных агента. Формат — <see cref="HandsSessionState"/>.
    /// Файла нет или он битый — сеанса нет (закрыто по умолчанию).
    /// </summary>
    public const string SessionFileName = "hands-session.json";

    /// <summary>Каталог компонента в каталоге данных агента: <c>%LOCALAPPDATA%\AiHomeAgent\hands\</c>.</summary>
    public const string ComponentDirectory = "hands";

    /// <summary>Исполняемый файл моста (форк sbroenne/mcp-windows, win-x64 single-file).</summary>
    public const string BridgeExe = "HandsBridge.exe";
}

/// <summary>
/// <c>hands-apps.json</c>. <see cref="Version"/> — версия формата; незнакомая версия или битый
/// файл читаются как пустой список. Пути — полные, нормализованные, сравнение точное без
/// учёта регистра (Windows); поиска по имени и по <c>PATH</c> нет.
/// </summary>
public sealed record HandsAppsFile(int Version, IReadOnlyList<HandsAppEntry> Apps)
{
    public const int CurrentVersion = 1;
}

/// <param name="Path">Полный путь к <c>.exe</c> программы.</param>
/// <param name="AddedAt">Когда человек разрешил программу.</param>
public sealed record HandsAppEntry(string Path, DateTimeOffset AddedAt);

/// <summary>
/// Программы, которые мост не запускает никогда, даже если человек внёс их в белый список:
/// каждая исполняет произвольные команды. Сравнение по имени файла без учёта регистра;
/// <c>python*</c> — по префиксу. Команда <c>allow-app</c> отказывает им сразу.
/// </summary>
public static class HandsForbiddenApps
{
    public static readonly IReadOnlyList<string> Names =
    [
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe",
        "rundll32.exe", "regsvr32.exe", "node.exe", "explorer.exe",
    ];

    public static readonly IReadOnlyList<string> NamePrefixes = ["python"];
}

/// <summary>
/// Сеанс рук: окно времени, которое человек открыл на машине (CLI или трей). С сервера сеанс не
/// стартует никогда — такой команды в протоколе устройства нет. Потолок и простой — константы кода.
/// </summary>
public static class HandsSession
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(2);

    /// <summary>Сеанс кончается, если столько времени не было ни одного вызова моста.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
}

/// <summary>Кто открыл сеанс.</summary>
public static class HandsSessionSource
{
    public const string Cli = "cli";
    public const string Tray = "tray";
}

/// <summary>
/// <c>hands-session.json</c>. Сеанс активен, пока сейчас раньше <see cref="ExpiresAt"/> и
/// с <see cref="LastActivityAt"/> прошло меньше <see cref="HandsSession.IdleTimeout"/>.
/// </summary>
public sealed record HandsSessionState(
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset LastActivityAt,
    string Source);

/// <summary>
/// Почему кончился сеанс или отцепились руки хода. Агент гасит дерево хода (<c>KillTree</c>) и
/// сообщает серверу эту причину; трей показывает её человеку.
/// </summary>
public static class HandsEndReason
{
    public const string Expired = "expired";
    public const string Idle = "idle";
    public const string StoppedFromTray = "tray-stop";
    public const string StoppedFromCli = "cli-stop";
    public const string HandsDisabled = "disabled";
    public const string AgentStopping = "agent-stopping";
}

/// <summary>
/// Pipe «агент ↔ трей»: <c>NamedPipeServerStream</c> на стороне агента с ACL «только текущий
/// пользователь», без localhost-API. Кадр — одна строка UTF-8 JSON (<see cref="HandsPipeMessage"/>),
/// конец строки — <c>\n</c>, не длиннее <see cref="MaxMessageBytes"/>. Трей шлёт запросы, агент
/// отвечает и сам шлёт события; «Стоп» работает без сервера.
/// </summary>
public static class HandsPipe
{
    public const int Version = 1;

    public const int MaxMessageBytes = 64 * 1024;

    /// <summary>
    /// Имя pipe: одно на пользователя ОС — у каждого пользователя машины свой агент и свой трей.
    /// ACL при этом обязателен: имя не секрет.
    /// </summary>
    public static string Name(string userSid) => $"AiHomeAgent.Tray.{userSid}";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>Типы кадров pipe. Незнакомый тип получатель пропускает, а не рвёт соединение.</summary>
public static class HandsPipeTypes
{
    // трей → агент
    /// <summary>Первый кадр трея: <see cref="HandsPipeMessage.Version"/>. Агент отвечает <see cref="Status"/>.</summary>
    public const string Hello = "hello";
    public const string GetStatus = "get-status";
    /// <summary>Открыть сеанс на <see cref="HandsPipeMessage.Minutes"/> (не больше потолка).</summary>
    public const string SessionStart = "session-start";
    /// <summary>Закрыть сеанс; идущие ходы с руками гасятся.</summary>
    public const string SessionStop = "session-stop";
    /// <summary>Погасить ход <see cref="HandsPipeMessage.TurnId"/> (null — все ходы с руками); сеанс остаётся.</summary>
    public const string TurnStop = "turn-stop";

    // агент → трей
    /// <summary>Снимок <see cref="HandsPipeMessage.Status"/>: ответ на запрос и рассылка при любой перемене.</summary>
    public const string Status = "status";
    /// <summary>Руки подключились к ходу <see cref="HandsPipeMessage.Turn"/> — повод для уведомления.</summary>
    public const string HandsActive = "hands-active";
    /// <summary>Руки хода отцепились: <see cref="HandsPipeMessage.TurnId"/>, <see cref="HandsPipeMessage.Reason"/>.</summary>
    public const string HandsEnded = "hands-ended";
    /// <summary>Запрос трея не выполнен: <see cref="HandsPipeMessage.Error"/> — текст для человека.</summary>
    public const string Error = "error";
}

/// <summary>Кадр pipe. Поля, не относящиеся к <see cref="Type"/>, — null.</summary>
public sealed record HandsPipeMessage(
    string Type,
    int? Version = null,
    int? Minutes = null,
    string? TurnId = null,
    HandsTrayStatus? Status = null,
    HandsActiveTurn? Turn = null,
    string? Reason = null,
    string? Error = null);

/// <summary>Что показывает трей.</summary>
/// <param name="Installed">Компонент установлен и SHA-256 сверен.</param>
/// <param name="Session">Активный сеанс; null — сеанса нет.</param>
/// <param name="ActiveTurns">Ходы, к которым сейчас подключены руки.</param>
/// <param name="ServerOnline">Связь агента с сервером — «Стоп» работает и без неё.</param>
public sealed record HandsTrayStatus(
    bool Installed,
    HandsSessionState? Session,
    IReadOnlyList<HandsActiveTurn> ActiveTurns,
    bool ServerOnline);

/// <summary>Ход с руками: «ИИ управляет компьютером в чате X».</summary>
/// <param name="TurnId">Ход исполнения на устройстве (<c>DeviceExecControl.TurnId</c>).</param>
/// <param name="ChatTitle">Название чата для человека; сервер может его не прислать.</param>
/// <param name="ProjectName">Проект чата для человека.</param>
public sealed record HandsActiveTurn(string TurnId, string? ChatTitle, string? ProjectName, DateTimeOffset StartedAt);
