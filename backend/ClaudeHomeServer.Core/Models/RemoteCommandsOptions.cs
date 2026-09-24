namespace ClaudeHomeServer.Models;

// Пульт удалённых команд — секция «RemoteCommands». Тот же приём, что у PowerControlOptions:
// главный замок — конфигурация, а не отсутствие кода. Реестр действий живёт ТОЛЬКО в файле на
// диске (appsettings.Local.json вне git): веб-морда торчит наружу, и поле произвольной команды
// в ней было бы прямым RCE. По HTTP ездит только ключ действия.
//
// "RemoteCommands": {
//   "Enabled": true,
//   "Actions": [
//     {
//       "Key": "vscode-tunnel",
//       "Title": "Туннель VS Code",
//       "Mode": "oneshot",
//       "Start": "code tunnel service install --accept-server-license-terms",
//       "Stop": "code tunnel kill",
//       "Status": "code tunnel status",
//       "StatusRunningPattern": "\"tunnel\":\"Connected\"",
//       "Url": "https://vscode.dev/tunnel/grisha-home",
//       "TimeoutSeconds": 30
//     }
//   ]
// }
public class RemoteCommandsOptions
{
    public const string Section = "RemoteCommands";

    // Главный рубильник. Выключено — пункта меню нет, GET отвечает 200 с enabled:false,
    // а мутирующие эндпоинты — 404.
    public bool Enabled { get; set; }

    // Белый список действий. Кривые записи сервис отбрасывает при старте с WARN и живёт на
    // оставшихся — одна опечатка в конфиге не должна гасить пульт целиком.
    public List<RemoteCommandAction> Actions { get; set; } = [];
}

/// <summary>
/// Режим действия. <c>oneshot</c> — все три команды короткие, состояние держит система
/// (службы Windows, docker, планировщик); <c>daemon</c> — <c>Start</c> порождает
/// долгоживущий процесс, которым владеет CCS.
/// </summary>
public enum RemoteCommandMode
{
    Oneshot,
    Daemon,
}

/// <summary>Одно объявленное действие пульта.</summary>
public class RemoteCommandAction
{
    /// <summary>Идентификатор действия: slug <c>[a-z0-9-]{1,64}</c>. Единственное, что ходит по HTTP.</summary>
    public string Key { get; set; } = "";

    /// <summary>Подпись в интерфейсе.</summary>
    public string Title { get; set; } = "";

    // Режим строкой, а не enum: биндер конфигурации на незнакомом значении бросает исключение
    // при чтении IOptions.Value, и опечатка «oneshoot» уронила бы весь пульт вместо отброса
    // одной записи с WARN. Разбор — ParseMode ниже, значение по умолчанию — oneshot.
    public string Mode { get; set; } = "oneshot";

    /// <summary>Команда запуска. Обязательна.</summary>
    public string Start { get; set; } = "";

    /// <summary>Команда остановки. Для daemon опциональна (тогда ребёнок гасится kill'ом дерева).</summary>
    public string? Stop { get; set; }

    /// <summary>Команда проверки состояния. Обязательна для oneshot; у daemon без неё состояние честно unknown.</summary>
    public string? Status { get; set; }

    /// <summary>
    /// Подстрока в выводе <c>Status</c>: задана — «запущен» это exit 0 И вхождение подстроки;
    /// не задана — «запущен» это exit 0. Подстрока, а не regex: выразительности хватает,
    /// сюрпризов меньше. Держать ASCII — нативные утилиты Windows печатают в OEM.
    /// </summary>
    public string? StatusRunningPattern { get; set; }

    /// <summary>
    /// Ссылка действия, которую карточка пульта показывает кнопкой «Открыть» (например,
    /// https://vscode.dev/tunnel/имя для туннеля VS Code). Это НЕ команда — строка не
    /// исполняется на сервере, фронт просто открывает её в новой вкладке. Годятся только
    /// http/https; кривая схема молча отбрасывается с WARN, как опечатка в Mode.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>Рабочая папка команд; пусто — домашняя папка пользователя сервера.</summary>
    public string? WorkingDir { get; set; }

    /// <summary>
    /// Потолок ожидания ЗАВЕРШАЮЩИХСЯ команд: в oneshot — всех трёх, в daemon — только
    /// <c>Stop</c> и <c>Status</c>. По истечении — kill дерева команды, состояние unknown.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Таймаут в разумных границах: минимум секунда, потолок в час от откровенной опечатки.</summary>
    public int EffectiveTimeoutSeconds => Math.Clamp(TimeoutSeconds, 1, 3600);

    /// <summary>Разбор режима: незнакомое значение — не «молча oneshot», а отказ (запись отбросят с WARN).</summary>
    public static bool TryParseMode(string? value, out RemoteCommandMode mode)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "":
            case "oneshot": mode = RemoteCommandMode.Oneshot; return true;
            case "daemon": mode = RemoteCommandMode.Daemon; return true;
            default: mode = RemoteCommandMode.Oneshot; return false;
        }
    }
}
