using System.Security.Principal;
using System.Text.Json;

namespace ClaudeHomeServer.Protocol;

// ---------- руки локального проекта (ADR-016, раздел «Руки»; план docs/research/hands-plan-2026-09.md, Ш1) ----------
//
// Контракт без поведения: типы и константы, от которых отталкиваются сервер (Ш4), мост (Ш2),
// агент (Ш3) и трей (Ш7). Модель угроз — docs/architecture/device-agent-local-api.md, раздел «Руки».
// Решения владельца 2026-09-27 (1в 2б 3б) сняли сеанс на машине, белый список программ и запрет
// shell: руки берёт любой ход чата с включёнными руками, пока компонент стоит на устройстве.

/// <summary>
/// Правила хода с руками — одно определение на обе стороны: сервер собирает по ним аргументы
/// CLI, агент сверяет их перед подключением моста.
/// <para>
/// Решение владельца 3б (2026-09-27): shell, сабагенты и запись в <c>.claude/**</c> и
/// <c>.mcp.json</c> в ходе с руками НЕ запрещаются. Локальный проект и без рук даёт ходу shell
/// на машине с правами пользователя, а запреты Ш4 держали только границу «рук без shell».
/// Остаётся одно правило режима прав: ход с руками не идёт в <c>bypassPermissions</c> — это
/// защита от ошибок модели, а не от злоумышленника.
/// </para>
/// </summary>
public static class HandsTurnRules
{
    public const string PermissionModeFlag = "--permission-mode";

    /// <summary>Режим, в котором ход с руками не идёт никогда.</summary>
    public const string ForbiddenPermissionMode = "bypassPermissions";

    /// <summary>Флаги CLI, которые пропускают вопросы о правах мимо режима.</summary>
    public static readonly IReadOnlyList<string> ForbiddenFlags =
        ["--dangerously-skip-permissions", "--allow-dangerously-skip-permissions"];

    /// <summary>
    /// Почему аргументы хода не годятся для рук, либо null — годятся. Режим обязан стоять явно:
    /// без флага CLI взял бы <c>defaultMode</c> из <c>.claude/settings*.json</c> проекта, а там
    /// может стоять <see cref="ForbiddenPermissionMode"/>.
    /// </summary>
    public static string? PermissionRefusal(IReadOnlyList<string> args)
    {
        var modes = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (ForbiddenFlags.Contains(arg, StringComparer.Ordinal))
                return $"в ходе с руками стоит флаг {arg}";
            if (arg == PermissionModeFlag)
            {
                if (i + 1 >= args.Count) return "у флага --permission-mode нет значения";
                modes.Add(args[++i]);
            }
            else if (arg.StartsWith(PermissionModeFlag + "=", StringComparison.Ordinal))
            {
                modes.Add(arg[(PermissionModeFlag.Length + 1)..]);
            }
        }

        if (modes.Count == 0) return "у хода с руками не задан режим прав";
        return modes.Any(m => string.Equals(m, ForbiddenPermissionMode, StringComparison.OrdinalIgnoreCase))
            ? "ход с руками не идёт в режиме «Без ограничений»"
            : null;
    }

    /// <summary>
    /// Строка ленты, когда чат с руками стоит в режиме «Без ограничений»: ход с руками в
    /// <c>bypassPermissions</c> не идёт никогда, режим понижается до «Авто-правки».
    /// </summary>
    public const string BypassDowngradedText =
        "В чате с руками режим «Без ограничений» не действует: ход идёт в режиме «Авто-правки».";

    /// <summary>
    /// Ошибка хода, когда основной провайдер отказал, а в цепочке фолбэка не осталось провайдеров,
    /// которым владелец доверил руки. <paramref name="providers"/> — их имена для человека.
    /// </summary>
    public static string FallbackNowhereText(IReadOnlyList<string> providers) =>
        providers.Count == 0
            ? "Руки не разрешены ни одному провайдеру — основной провайдер недоступен, а другим руки не доверены."
            : $"Руки разрешены только для {JoinNames(providers)} — основной провайдер недоступен, а другим руки не доверены.";

    private static string JoinNames(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " и " + names[^1];
}

/// <summary>
/// Провайдеры, которым владелец доверил руки (<c>User.HandsProviders</c>): ключи реестра
/// провайдеров, родной Claude (любая подписка пула) — <see cref="Claude"/>.
/// </summary>
public static class HandsProviders
{
    public const string Claude = "claude";

    /// <summary>Разрешён ли провайдер с ключом <paramref name="providerKey"/>. Пустой список — никто.</summary>
    public static bool Allowed(IReadOnlyList<string>? allowed, string? providerKey) =>
        allowed is { Count: > 0 } && !string.IsNullOrEmpty(providerKey)
        && allowed.Contains(providerKey, StringComparer.OrdinalIgnoreCase);
}


/// <summary>
/// Состояние рук в чате — поле <c>State</c> события <c>hands_status</c> (макет Ш6, «Индикатор в чате»).
/// Эфемерное событие: в историю не пишется, начальное состояние фронт берёт запросом
/// <c>GET /api/sessions/{id}/hands-status</c>.
/// </summary>
public static class HandsChatStates
{
    /// <summary>Руки подключены к идущему ходу: «ИИ управляет компьютером».</summary>
    public const string Active = "active";
    /// <summary>Руки доступны ходам чата, сейчас ни к одному не подключены.</summary>
    public const string Allowed = "allowed";
    /// <summary>
    /// Руки у чата включены, но устройство их сейчас не даст: компонент не установлен
    /// (<c>ai-home-agent hands disable</c>) или устройство не на связи.
    /// </summary>
    public const string Unavailable = "unavailable";
    /// <summary>Руки отцепились от хода не по его концу: <c>Reason</c> — <see cref="HandsEndReason"/>.</summary>
    public const string Stopped = "stopped";
    /// <summary>Провайдеру чата руки не доверены.</summary>
    public const string ProviderNotAllowed = "provider-not-allowed";

    /// <summary>Состояния, которые присылает агент (<see cref="DeviceHandsReport"/>); прочие считает сервер.</summary>
    public static readonly IReadOnlyList<string> FromDevice = [Active, Allowed, Stopped];
}

/// <summary>Компонент рук на машине. Сервер его не видит и не правит.</summary>
public static class HandsFiles
{
    /// <summary>Каталог компонента в каталоге данных агента: <c>%LOCALAPPDATA%\AiHomeAgent\hands\</c>.</summary>
    public const string ComponentDirectory = "hands";

    /// <summary>Исполняемый файл моста (форк sbroenne/mcp-windows, win-x64 single-file).</summary>
    public const string BridgeExe = "HandsBridge.exe";
}

/// <summary>
/// Программы, которые мост не запускает никогда (решение владельца 2б, 2026-09-27): белого
/// списка нет, любая другая <c>.exe</c> по полному пути запускается. Здесь — интерпретаторы,
/// оболочки и терминалы: каждый исполняет произвольные команды, и окна их процессов тоже чужие.
/// Сравнение по имени файла без учёта регистра (и по имени без расширения); <c>python*</c> и
/// <c>pwsh*</c> — по префиксу (сборки <c>pwsh-preview.exe</c>, <c>pwsh-7.4-preview.exe</c>).
/// </summary>
public static class HandsForbiddenApps
{
    public static readonly IReadOnlyList<string> Names =
    [
        // интерпретаторы и запускалки скриптов
        "cmd.exe", "powershell.exe", "powershell_ise.exe", "pwsh.exe", "wscript.exe", "cscript.exe",
        "mshta.exe", "rundll32.exe", "regsvr32.exe", "node.exe", "perl.exe", "ruby.exe", "php.exe",
        "explorer.exe",
        // оболочки и терминалы
        "bash.exe", "sh.exe", "wsl.exe", "wslhost.exe", "mintty.exe", "git-bash.exe",
        "wt.exe", "WindowsTerminal.exe", "OpenConsole.exe", "conhost.exe",
    ];

    public static readonly IReadOnlyList<string> NamePrefixes = ["python", "pwsh"];
}

/// <summary>
/// Аргументы запуска моста — их подставляет агент в узел <see cref="DeviceExecPlaceholders.HandsServerName"/>.
/// </summary>
public static class HandsBridgeArgs
{
    /// <summary>
    /// Имя Job Object хода (<c>Local\AiHome.Turn.…</c>): окно процесса из этого Job — своё окно
    /// (решение владельца 2б). Без аргумента своих окон нет — гейт закрыт.
    /// </summary>
    public const string TurnJob = "--turn-job";

    /// <summary>Выключить инструменты моста: без зрения — <c>screenshot_control</c> (решение 7).</summary>
    public const string ExcludeTools = "--exclude-tools";

    /// <summary>Префикс имени Job хода с руками.</summary>
    public const string TurnJobPrefix = @"Local\AiHome.Turn.";
}

/// <summary>
/// Одни руки на машину: второй ход с руками на той же машине (и в том же сеансе входа)
/// получает честный отказ, а не делит мышь и клавиатуру с первым.
/// </summary>
public static class HandsMachineLock
{
    public const string Name = @"Local\AiHome.Hands.Active";

    public const string BusyText = "Руки заняты другим ходом на этом устройстве — дождись его конца или останови его из трея.";
}

/// <summary>
/// Почему руки отцепились от хода. Агент гасит дерево хода (<c>KillTree</c>) и сообщает
/// серверу причину; трей показывает её человеку.
/// </summary>
public static class HandsEndReason
{
    /// <summary>Человек нажал «Стоп» в трее: ход прерван, следующий ход снова может взять руки.</summary>
    public const string StoppedFromTray = "tray-stop";
    /// <summary>Компонент убран командой <c>ai-home-agent hands disable</c> посреди хода.</summary>
    public const string HandsDisabled = "disabled";
    public const string AgentStopping = "agent-stopping";
    /// <summary>
    /// Ход не получил руки: их держит другой ход на этой машине (<see cref="HandsMachineLock"/>).
    /// Приходит кодом отказа <see cref="DeviceExecExit.Refusal"/>, в чат — <c>Reason</c> у
    /// <see cref="HandsChatStates.Unavailable"/>.
    /// </summary>
    public const string Busy = "busy";
}

/// <summary>
/// Донесение агента о руках хода — метод хаба устройств <see cref="Method"/>. Сервер принимает
/// его только по ходу, который сам отправил этому устройству, и транслирует в чат хода.
/// </summary>
/// <param name="TurnId">Ход исполнения на устройстве (<c>DeviceExecControl.TurnId</c>).</param>
/// <param name="State">Одно из <see cref="HandsChatStates.FromDevice"/>.</param>
/// <param name="Reason"><see cref="HandsEndReason"/> у <see cref="HandsChatStates.Stopped"/>.</param>
public sealed record DeviceHandsReport(string TurnId, string State, string? Reason = null)
{
    public const string Method = "ReportHandsStatus";
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

    /// <summary>Имя pipe текущего пользователя ОС — одно у агента и трея: на Windows по SID, на Unix по имени.</summary>
    public static string NameForCurrentUser() =>
        Name(OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User!.Value : Environment.UserName);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Процесс трея <c>ai-home-agent-tray</c> (Ш7): лежит в каталоге версии рядом с агентом,
/// поднимает и перезапускает его <c>ai-home-agent supervise</c> в сеансе пользователя.
/// </summary>
public static class HandsTrayProcess
{
    public static string ExecutableName => OperatingSystem.IsWindows() ? "ai-home-agent-tray.exe" : "ai-home-agent-tray";

    /// <summary>Человек выбрал «Выйти из агента»: супервизор гасит агента и выходит сам, трей не перезапускает.</summary>
    public const int ExitAgentCode = 10;

    /// <summary>Трей этого пользователя уже запущен — второй экземпляр выходит сразу.</summary>
    public const int AlreadyRunningCode = 3;

    /// <summary>Не Windows: значка в области уведомлений нет.</summary>
    public const int UnsupportedCode = 64;
}

/// <summary>Типы кадров pipe. Незнакомый тип получатель пропускает, а не рвёт соединение.</summary>
public static class HandsPipeTypes
{
    // трей → агент
    /// <summary>Первый кадр трея: <see cref="HandsPipeMessage.Version"/>. Агент отвечает <see cref="Status"/>.</summary>
    public const string Hello = "hello";
    public const string GetStatus = "get-status";
    /// <summary>
    /// «Стоп»: погасить ход <see cref="HandsPipeMessage.TurnId"/> (null — все ходы с руками).
    /// Ход прерывается целиком, следующий ход снова может взять руки.
    /// </summary>
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
    string? TurnId = null,
    HandsTrayStatus? Status = null,
    HandsActiveTurn? Turn = null,
    string? Reason = null,
    string? Error = null);

/// <summary>Что показывает трей.</summary>
/// <param name="Installed">Компонент установлен и SHA-256 сверен.</param>
/// <param name="ActiveTurns">Ходы, к которым сейчас подключены руки (не больше одного — <see cref="HandsMachineLock"/>).</param>
/// <param name="ServerOnline">Связь агента с сервером — «Стоп» работает и без неё.</param>
/// <param name="Device">Сведения для меню трея; null — агент их не прислал.</param>
public sealed record HandsTrayStatus(
    bool Installed,
    IReadOnlyList<HandsActiveTurn> ActiveTurns,
    bool ServerOnline,
    HandsTrayDevice? Device = null);

/// <summary>Устройство глазами меню трея: только просмотр, правка — командами агента.</summary>
/// <param name="Server">Адрес сервера, с которым сопряжён агент.</param>
/// <param name="DeviceName">Имя устройства при сопряжении.</param>
/// <param name="AgentVersion">Версия работающего агента.</param>
/// <param name="Roots">Разрешённые корни проектов (<c>ai-home-agent roots</c>).</param>
/// <param name="LogDirectory">Каталог журналов агента; null — агент запущен не из установки.</param>
public sealed record HandsTrayDevice(
    string Server,
    string DeviceName,
    string AgentVersion,
    IReadOnlyList<string> Roots,
    string? LogDirectory);

/// <summary>Ход с руками: «ИИ управляет компьютером».</summary>
/// <param name="TurnId">Ход исполнения на устройстве (<c>DeviceExecControl.TurnId</c>).</param>
/// <param name="ProjectRoot">Рабочий каталог хода — чтобы человек узнал проект.</param>
public sealed record HandsActiveTurn(string TurnId, string? ProjectRoot, DateTimeOffset StartedAt);
