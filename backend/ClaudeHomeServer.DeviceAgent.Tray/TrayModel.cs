using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Tray;

/// <summary>Вид значка: различается формой, а не только цветом (макет Ш6, раздел 1).</summary>
internal enum TrayIconKind
{
    /// <summary>Агент на связи, руки не действуют — контур монитора.</summary>
    Idle,
    /// <summary>Ход действует руками — залитый экран акцентом.</summary>
    HandsActive,
    /// <summary>Нет связи с сервером или агент не отвечает — серый контур с косой чертой.</summary>
    Offline,
}

/// <summary>Команды пунктов меню и плашки.</summary>
internal static class TrayCommands
{
    public const string Stop = "stop";
    public const string OpenFolder = "open-folder";
    public const string OpenLog = "open-log";
    public const string Exit = "exit";
}

/// <summary>
/// Пункт меню — только то, что рисует <c>TrackPopupMenu</c>: текст, подменю, разделитель,
/// недоступный пункт, пункт по умолчанию.
/// </summary>
internal sealed record TrayMenuItem(
    string Text,
    string? Command = null,
    bool Enabled = true,
    bool IsDefault = false,
    IReadOnlyList<TrayMenuItem>? Children = null,
    string? Argument = null)
{
    public static readonly TrayMenuItem Separator = new("-", Enabled: false);

    public bool IsSeparator => ReferenceEquals(this, Separator);

    public static TrayMenuItem Label(string text) => new(text, Enabled: false);
}

/// <summary>Плашка «ИИ управляет компьютером» (макет Ш6, раздел 2).</summary>
/// <param name="Visible">Показывать ли плашку.</param>
/// <param name="Title">Первая строка.</param>
/// <param name="Project">Чей ход — строка под заголовком; null — не показывать.</param>
/// <param name="Hint">Предупреждение про фокус и окна; null — не показывать.</param>
/// <param name="StopVisible">Кнопка «Стоп».</param>
internal sealed record PlateView(bool Visible, string Title, string? Project, string? Hint, bool StopVisible)
{
    public static readonly PlateView Hidden = new(false, "", null, null, false);
}

/// <summary>Всплывающее уведомление значка (<c>NIF_INFO</c>): заголовок до 63 символов, текст до 255.</summary>
internal sealed record TrayNotification(string Title, string Text);

/// <summary>Окно подтверждения.</summary>
internal sealed record TrayConfirmation(string Title, string Text);

/// <summary>
/// Состояние трея без UI: из кадров pipe агента получаются значок, подсказка, меню, плашка и
/// уведомления; «Стоп» превращается в кадр pipe. Решения владельца 1в и 2б: сеанса на машине и
/// белого списка программ нет — пунктов «Разрешить руки», сроков и программ в меню нет. Паузы
/// агента Ш3 не завёл — пункта паузы тоже нет.
/// Не потокобезопасна: все вызовы — из потока окна.
/// </summary>
internal sealed class TrayModel
{
    public const string AppTitle = "AI Home — агент устройства";
    public const string PlateTitle = "ИИ управляет компьютером";
    public const string PlateHint = "Фокус может переключаться. Окна хода закроются вместе с ним.";
    public const string StoppedNotice = "Руки выключены. Ход прерван.";
    public const string StopText = "Стоп";
    public static readonly TrayNotification StopUndelivered =
        new(AppTitle, "Агент не отвечает — «Стоп» не доставлен. Попробуйте ещё раз через несколько секунд.");
    /// <summary>Сколько плашка держит «Руки выключены» после «Стоп».</summary>
    public static readonly TimeSpan StoppedNoticeFor = TimeSpan.FromSeconds(3);

    // Ходы, о начале которых уже сказано уведомлением: одно уведомление на ход
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);
    private bool _stoppedNotice;

    /// <summary>Pipe агента открыт.</summary>
    public bool Connected { get; private set; }

    /// <summary>Последний снимок агента; null — ещё не пришёл.</summary>
    public HandsTrayStatus? Status { get; private set; }

    /// <summary>Модель поменялась так, что UI надо перерисовать.</summary>
    public event Action? Changed;

    private IReadOnlyList<HandsActiveTurn> Active => Connected ? Status?.ActiveTurns ?? [] : [];

    public bool HandsActive => Active.Count > 0;

    public void OnConnected()
    {
        Connected = true;
        Changed?.Invoke();
    }

    /// <summary>Pipe закрылся: агент не отвечает, о руках ничего не известно.</summary>
    public void OnDisconnected()
    {
        Connected = false;
        Status = null;
        _stoppedNotice = false;
        Changed?.Invoke();
    }

    /// <summary>Кадр агента; результат — уведомление, которое надо показать, или null.</summary>
    public TrayNotification? Apply(HandsPipeMessage message)
    {
        TrayNotification? notification = null;
        switch (message.Type)
        {
            case HandsPipeTypes.Status when message.Status is { } status:
                Status = status;
                foreach (var turn in status.ActiveTurns)
                    notification ??= NotifyStart(turn);
                break;
            case HandsPipeTypes.HandsActive when message.Turn is { } turn:
                notification = NotifyStart(turn);
                break;
            case HandsPipeTypes.HandsEnded:
                if (message.Reason == HandsEndReason.StoppedFromTray) _stoppedNotice = true;
                if (message.TurnId is { } ended && Status is { } current)
                    Status = current with { ActiveTurns = current.ActiveTurns.Where(t => t.TurnId != ended).ToList() };
                break;
            case HandsPipeTypes.Error when message.Error is { Length: > 0 } error:
                notification = new TrayNotification(AppTitle, error);
                break;
            default:
                return null;
        }
        Changed?.Invoke();
        return notification;
    }

    private TrayNotification? NotifyStart(HandsActiveTurn turn)
    {
        if (!_notified.Add(turn.TurnId)) return null;
        // Новый ход с руками гасит «Руки выключены» от прошлого
        _stoppedNotice = false;
        var who = ProjectName(turn.ProjectRoot) is { } name ? $"Проект «{name}»" : "Ход";
        return new TrayNotification("ИИ начал работать с компьютером",
            $"{who} управляет окнами. Остановить — «Стоп» в углу экрана или в меню значка.");
    }

    /// <summary>Кадр «Стоп» для pipe; null — ни один ход не действует руками.</summary>
    /// <remarks>TurnId не указывается: «Стоп» гасит любой ход с руками, а не только показанный.</remarks>
    public HandsPipeMessage? StopRequest() => HandsActive ? new HandsPipeMessage(HandsPipeTypes.TurnStop) : null;

    /// <summary>Истекли <see cref="StoppedNoticeFor"/> после «Стоп»: плашка гаснет.</summary>
    public void StoppedNoticeExpired()
    {
        if (!_stoppedNotice) return;
        _stoppedNotice = false;
        Changed?.Invoke();
    }

    /// <summary>Плашка сейчас показывает «Руки выключены» — UI заводит таймер на <see cref="StoppedNoticeFor"/>.</summary>
    public bool ShowsStoppedNotice => _stoppedNotice && !HandsActive;

    public TrayIconKind Icon =>
        HandsActive ? TrayIconKind.HandsActive
        : Connected && Status is { ServerOnline: true } ? TrayIconKind.Idle
        : TrayIconKind.Offline;

    /// <summary>Подсказка значка — не длиннее 63 символов.</summary>
    public string Tooltip =>
        HandsActive ? "AI Home — ИИ управляет компьютером"
        : !Connected ? AppTitle + " · агент не отвечает"
        : Status is { ServerOnline: false } ? AppTitle + " · нет связи с сервером"
        : AppTitle + " · на связи";

    public PlateView Plate
    {
        get
        {
            if (HandsActive)
            {
                var turn = Active[0];
                var project = ProjectName(turn.ProjectRoot) is { } name ? $"Проект «{name}»" : null;
                return new PlateView(true, PlateTitle, project, PlateHint, StopVisible: true);
            }
            return ShowsStoppedNotice ? new PlateView(true, StoppedNotice, null, null, StopVisible: false) : PlateView.Hidden;
        }
    }

    public IReadOnlyList<TrayMenuItem> Menu()
    {
        var items = new List<TrayMenuItem> { TrayMenuItem.Label(AppTitle) };
        var device = Status?.Device;
        if (!Connected || Status is null)
        {
            items.Add(TrayMenuItem.Label("Агент не отвечает — значок ждёт его"));
            items.Add(TrayMenuItem.Separator);
            items.Add(new TrayMenuItem("Выйти из агента", TrayCommands.Exit));
            return items;
        }

        var host = device is null ? null : HostOf(device.Server);
        items.Add(TrayMenuItem.Label(Status.ServerOnline
            ? "На связи" + (host is null ? "" : " · " + host)
            : "Нет связи с сервером" + (host is null ? "" : " · " + host)));
        items.Add(TrayMenuItem.Separator);

        if (HandsActive)
        {
            items.Add(TrayMenuItem.Label(PlateTitle));
            if (ProjectName(Active[0].ProjectRoot) is { } name) items.Add(TrayMenuItem.Label($"Сейчас работает: проект «{name}»"));
        }
        else
        {
            items.Add(TrayMenuItem.Label(Status.Installed ? "Руки установлены · сейчас не действуют" : "Руки не установлены"));
        }
        items.Add(new TrayMenuItem("Остановить руки", TrayCommands.Stop, Enabled: HandsActive, IsDefault: HandsActive));
        items.Add(TrayMenuItem.Separator);

        if (device is not null)
        {
            items.Add(device.Roots.Count == 0
                ? TrayMenuItem.Label("Разрешённых папок нет")
                : new TrayMenuItem($"Разрешённые папки ({device.Roots.Count})",
                    Children: device.Roots.Select(r => new TrayMenuItem(r, TrayCommands.OpenFolder, Argument: r)).ToList()));
            items.Add(TrayMenuItem.Separator);
            items.Add(TrayMenuItem.Label($"Обновления: версия {device.AgentVersion}"));
            if (device.LogDirectory is { } logs)
                items.Add(new TrayMenuItem("Открыть журнал агента", TrayCommands.OpenLog, Argument: logs));
            items.Add(TrayMenuItem.Separator);
        }

        items.Add(new TrayMenuItem("Выйти из агента", TrayCommands.Exit));
        return items;
    }

    /// <summary>Окно перед «Выйти из агента».</summary>
    public TrayConfirmation ExitConfirmation()
    {
        var turn = HandsActive && ProjectName(Active[0].ProjectRoot) is { } name
            ? $"Сейчас ИИ управляет компьютером в проекте «{name}». Выход прервёт ход, руки выключатся.\n"
            : HandsActive ? "Сейчас ИИ управляет компьютером. Выход прервёт ход, руки выключатся.\n"
            : "Идущие ходы прервутся.\n";
        return new TrayConfirmation("Выйти из агента?",
            turn + "Агент запустится снова при следующем входе в Windows.");
    }

    /// <summary>Имя папки проекта из пути хода: и Windows-, и Unix-разделители.</summary>
    internal static string? ProjectName(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var trimmed = root.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return name.Length == 0 ? null : name;
    }

    private static string? HostOf(string server) =>
        Uri.TryCreate(server, UriKind.Absolute, out var uri) ? uri.Host : null;
}
