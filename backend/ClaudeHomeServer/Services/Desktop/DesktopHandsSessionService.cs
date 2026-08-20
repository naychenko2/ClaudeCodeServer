using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Desktop;

/// <summary>
/// Отчего погас сеанс рук (ADR-008, «Сеанс рук и согласие»). Поводов ровно столько,
/// сколько названо в решении, плюс явный «Стоп» человека — своих сервер не изобретает.
/// </summary>
public static class DesktopHandsEndReasons
{
    /// <summary>15 минут без вызовов.</summary>
    public const string Idle = "idle";

    /// <summary>Потолок 2 часа от старта.</summary>
    public const string Cap = "cap";

    /// <summary>Окно клиента закрыто (жизнь в трее закрытием НЕ считается — это трактовка UI).</summary>
    public const string ClientClosed = "client_closed";

    /// <summary>Чат удалён или истёк.</summary>
    public const string ChatGone = "chat_gone";

    /// <summary>Соединение с устройством разорвано.</summary>
    public const string Disconnected = "disconnected";

    /// <summary>Грань выключили в проекте — тумблер обязан быть рубильником.</summary>
    public const string FacetOff = "facet_off";

    /// <summary>Человек нажал «Стоп» (пункт трея, красная кнопка в шапке чата).</summary>
    public const string Stopped = "stopped";
}

/// <summary>
/// Сеанс рук: чат получил право действовать на КОНКРЕТНОМ устройстве. Живёт только в
/// памяти — рестарт бэкенда гасит сеанс по построению (ADR: рестарт в списке поводов).
/// </summary>
public sealed class DesktopHandsSession
{
    public required string OwnerId { get; init; }
    public required string DeviceId { get; init; }
    /// <summary>Человеческое имя устройства («home») — то же, что принимает параметр device.</summary>
    public required string DeviceName { get; init; }
    /// <summary>Чат, которому принадлежат руки. Один сеанс на чат и один на устройство.</summary>
    public required string ChatSessionId { get; init; }
    public string? ChatName { get; init; }
    /// <summary>Проект чата — по нему гасит сеансы выключенный в проекте тумблер грани.</summary>
    public string? ProjectId { get; init; }
    public required DateTime StartedAt { get; init; }
    /// <summary>Последний вызов грани: от него считаются 15 минут простоя.</summary>
    public DateTime LastCallAt { get; internal set; }

    /// <summary>Когда сеанс погаснет без вызовов.</summary>
    public DateTime IdleDeadline => LastCallAt + DesktopHandsSessionService.IdleTimeout;

    /// <summary>Предельный срок: потолок 2 часа от старта, сколько бы вызовов ни шло.</summary>
    public DateTime HardDeadline => StartedAt + DesktopHandsSessionService.MaxDuration;

    /// <summary>Ближайший из двух сроков — по нему фронт рисует отсчёт без опроса сервера.</summary>
    public DateTime ExpiresAt => IdleDeadline < HardDeadline ? IdleDeadline : HardDeadline;
}

/// <summary>
/// Заявка чата на сеанс. Безымянной кнопки «Начать сеанс» на клиенте нет: человек видит
/// очередь заявок и в ней — ИМЯ чата, проекта и персоны (ADR). Заявку ставит гейт, когда
/// вызов пришёл в чат без сеанса.
/// </summary>
public sealed record DesktopHandsRequest(
    string OwnerId,
    string ChatSessionId,
    string? ChatName,
    string? ProjectName,
    string? PersonaName,
    DateTime RequestedAt);

/// <summary>Исход попытки начать сеанс. Отказ — это ответ с причиной, а не исключение.</summary>
public sealed record DesktopHandsStartResult(bool Started, string Outcome, string Message,
    DesktopHandsSession? Session = null)
{
    public static DesktopHandsStartResult Ok(DesktopHandsSession s) =>
        new(true, DesktopOutcomes.Ok, $"Сеанс рук чата «{s.ChatName ?? s.ChatSessionId}» начат на устройстве {s.DeviceName}.", s);

    public static DesktopHandsStartResult Refused(string outcome, string message) =>
        new(false, outcome, message);
}

/// <summary>
/// Рассылка статуса сеанса (бейдж «руки на home»). Отдельный интерфейс, чтобы сеанс жил
/// без SignalR и SessionManager в тестах.
/// </summary>
public interface IDesktopHandsNotifier
{
    Task StatusAsync(DesktopHandsSession session, bool active, string? reason, CancellationToken ct = default);
}

/// <summary>Боевая рассылка: событие ленты чата через SessionManager.</summary>
public sealed class DesktopHandsNotifier(SessionManager sessions) : IDesktopHandsNotifier
{
    public Task StatusAsync(DesktopHandsSession s, bool active, string? reason, CancellationToken ct = default) =>
        sessions.BroadcastSessionMessageAsync(s.ChatSessionId, new DesktopSessionMessage(
            active, s.DeviceName, s.ChatSessionId, s.ChatName, s.StartedAt, s.ExpiresAt, reason));
}

/// <summary>
/// Отмена вызовов погасшего сеанса. Через интерфейс, а не напрямую в
/// <see cref="DesktopCallRouter"/>: сеанс подписан на наблюдателя соединений самого
/// маршрутизатора, и прямая ссылка замкнула бы граф зависимостей DI.
/// </summary>
public interface IDesktopCallCanceller
{
    Task CancelChatCallsAsync(string chatSessionId, string reason, CancellationToken ct = default);
}

/// <summary>Боевая отмена — маршрутизатор резолвится лениво, чтобы разорвать цикл DI.</summary>
public sealed class DesktopRouterCallCanceller(IServiceProvider services) : IDesktopCallCanceller
{
    public Task CancelChatCallsAsync(string chatSessionId, string reason, CancellationToken ct = default) =>
        services.GetService(typeof(DesktopCallRouter)) is DesktopCallRouter router
            ? router.CancelSessionAsync(chatSessionId, reason, ct)
            : Task.CompletedTask;
}

/// <summary>
/// Сеансы рук десктопного агента (ADR-008, «Сеанс рук и согласие»).
///
/// Договор, который держит этот класс:
/// - сеанс стартует ТОЛЬКО с устройства (эндпоинт под схемой токена устройства);
/// - один сеанс на устройство и один на чат;
/// - сеанс гейтит И чтение, И действия — без него desktop_screen/desktop_ui тоже отказывают;
/// - гаснет по: 15 минут без вызовов, потолок 2 часа, закрытие окна клиента, удаление или
///   истечение чата, разрыв соединения, рестарт бэкенда (реестр только в памяти);
/// - каждое погасание рассылает cancel по вызовам чата: живой процесс CLI иначе доработает
///   ход, будто руки на месте.
/// </summary>
public sealed class DesktopHandsSessionService : IDeviceConnectionObserver
{
    /// <summary>15 минут без вызовов — и сеанс гаснет.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Потолок сеанса — 2 часа от старта.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(2);

    /// <summary>Сколько заявка ждёт человека в очереди клиента, прежде чем протухнуть.</summary>
    public static readonly TimeSpan RequestTtl = TimeSpan.FromMinutes(15);

    /// <summary>Потолок очереди заявок на владельца — защита от разрастания реестра.</summary>
    private const int MaxRequestsPerOwner = 20;

    private readonly IDesktopChatDirectory _chats;
    private readonly IDesktopHandsNotifier _notifier;
    private readonly IDesktopCallCanceller _calls;
    private readonly ILogger<DesktopHandsSessionService> _log;
    private readonly TimeProvider _time;

    private readonly Lock _lock = new();
    // chatId → сеанс. Второго индекса по устройству нет намеренно: сеансов единицы,
    // а два словаря пришлось бы держать согласованными в каждом погасании.
    private readonly Dictionary<string, DesktopHandsSession> _sessions = [];
    // chatId → заявка на сеанс
    private readonly Dictionary<string, DesktopHandsRequest> _requests = [];

    public DesktopHandsSessionService(
        IDesktopChatDirectory chats,
        IDesktopHandsNotifier notifier,
        IDesktopCallCanceller calls,
        ILogger<DesktopHandsSessionService> log,
        TimeProvider? timeProvider = null)
    {
        _chats = chats;
        _notifier = notifier;
        _calls = calls;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ---------- чтение ----------

    /// <summary>Активный сеанс чата, либо null.</summary>
    public DesktopHandsSession? ForChat(string? chatSessionId)
    {
        if (string.IsNullOrEmpty(chatSessionId)) return null;
        lock (_lock) return _sessions.GetValueOrDefault(chatSessionId);
    }

    /// <summary>Активный сеанс устройства, либо null.</summary>
    public DesktopHandsSession? ForDevice(string ownerId, string deviceId)
    {
        lock (_lock)
            return _sessions.Values.FirstOrDefault(s => s.OwnerId == ownerId && s.DeviceId == deviceId);
    }

    /// <summary>Все сеансы владельца — бейдж «руки на home» и список устройств.</summary>
    public IReadOnlyList<DesktopHandsSession> ForOwner(string ownerId)
    {
        lock (_lock) return _sessions.Values.Where(s => s.OwnerId == ownerId).ToList();
    }

    /// <summary>Очередь заявок владельца — свежие сверху, протухшие не показываем.</summary>
    public IReadOnlyList<DesktopHandsRequest> RequestsFor(string ownerId)
    {
        var now = Now;
        lock (_lock)
            return _requests.Values
                .Where(r => r.OwnerId == ownerId && now - r.RequestedAt < RequestTtl)
                .OrderByDescending(r => r.RequestedAt)
                .ToList();
    }

    // ---------- заявка ----------

    /// <summary>
    /// Поставить заявку на сеанс: вызов пришёл в чат без рук. Заявка — единственный способ
    /// начать сеанс, потому что кнопки «начать» у агента нет: человек выбирает чат сам.
    /// </summary>
    public DesktopHandsRequest Enqueue(DesktopChatInfo chat)
    {
        var request = new DesktopHandsRequest(chat.OwnerId, chat.ChatId, chat.ChatName,
            chat.ProjectName, chat.PersonaName, Now);
        lock (_lock)
        {
            _requests[chat.ChatId] = request;
            TrimRequests(chat.OwnerId);
        }
        return request;
    }

    // ---------- старт ----------

    /// <summary>
    /// Начать сеанс. Зовётся ТОЛЬКО с устройства: у веб-морды и у агента такой двери нет.
    /// Проверяет ровно то же, что гейт исполнения: чат жив, наш, десктопный, грань в проекте
    /// включена и флаг у владельца поднят.
    /// </summary>
    public async Task<DesktopHandsStartResult> StartAsync(string ownerId, string deviceId, string deviceName,
        string chatSessionId, CancellationToken ct = default)
    {
        var chat = _chats.Find(chatSessionId);
        if (chat is null || chat.OwnerId != ownerId)
            return DesktopHandsStartResult.Refused(DesktopGateOutcomes.ChatGone,
                "Чат не найден: возможно, он удалён или истёк.");

        if (chat.FacetRefusal() is string refusal)
            return DesktopHandsStartResult.Refused(DesktopGateOutcomes.FacetOff, refusal);

        DesktopHandsSession session;
        lock (_lock)
        {
            if (_sessions.TryGetValue(chatSessionId, out var forChat))
                return forChat.DeviceId == deviceId
                    // Повторный старт того же чата на том же устройстве — не ошибка: клиент мог
                    // переподключиться. Продлеваем, а не заводим второй сеанс.
                    ? DesktopHandsStartResult.Ok(Extend(forChat))
                    : DesktopHandsStartResult.Refused(DesktopGateOutcomes.HandsBusy,
                        $"У чата «{forChat.ChatName ?? chatSessionId}» уже идёт сеанс на устройстве {forChat.DeviceName}.");

            if (_sessions.Values.FirstOrDefault(s => s.OwnerId == ownerId && s.DeviceId == deviceId) is { } forDevice)
                return DesktopHandsStartResult.Refused(DesktopGateOutcomes.HandsBusy,
                    $"На устройстве {forDevice.DeviceName} уже идёт сеанс чата «{forDevice.ChatName ?? forDevice.ChatSessionId}».");

            var now = Now;
            session = new DesktopHandsSession
            {
                OwnerId = ownerId,
                DeviceId = deviceId,
                DeviceName = deviceName,
                ChatSessionId = chatSessionId,
                ChatName = chat.ChatName,
                ProjectId = chat.ProjectId,
                StartedAt = now,
                LastCallAt = now
            };
            _sessions[chatSessionId] = session;
            _requests.Remove(chatSessionId);
        }

        _log.LogInformation("Сеанс рук начат: чат {ChatId} ← устройство {Device}", chatSessionId, deviceName);
        await NotifyAsync(session, active: true, reason: null, ct);
        return DesktopHandsStartResult.Ok(session);
    }

    /// <summary>Отметить вызов: продлевает окно простоя. false — сеанса нет.</summary>
    public bool Touch(string chatSessionId)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(chatSessionId, out var session)) return false;
            session.LastCallAt = Now;
            return true;
        }
    }

    private DesktopHandsSession Extend(DesktopHandsSession session)
    {
        session.LastCallAt = Now;
        return session;
    }

    // ---------- погасание ----------

    /// <summary>
    /// Погасить сеанс чата. Идемпотентно: гасить нечего — false. Вызовы чата отменяются
    /// всегда, даже если сеанс уже сняли: команда могла уйти на устройство до погасания.
    /// </summary>
    public async Task<bool> StopAsync(string chatSessionId, string reason, CancellationToken ct = default)
    {
        DesktopHandsSession? session;
        lock (_lock)
        {
            if (!_sessions.Remove(chatSessionId, out session)) return false;
            _requests.Remove(chatSessionId);
        }

        _log.LogInformation("Сеанс рук чата {ChatId} на устройстве {Device} погас: {Reason}",
            chatSessionId, session!.DeviceName, reason);

        await SafeAsync(() => _calls.CancelChatCallsAsync(chatSessionId, reason, ct), "отмена вызовов");
        await NotifyAsync(session, active: false, reason, ct);
        return true;
    }

    /// <summary>Погасить сеанс устройства (разрыв соединения, закрытие окна клиента, «Стоп» в трее).</summary>
    public async Task<bool> StopForDeviceAsync(string ownerId, string deviceId, string reason,
        CancellationToken ct = default)
    {
        var session = ForDevice(ownerId, deviceId);
        return session is not null && await StopAsync(session.ChatSessionId, reason, ct);
    }

    /// <summary>
    /// Грань выключили в проекте: гасим все его сеансы и рассылаем cancel. Публичный вход —
    /// зовёт контроллер проектов на снятии тумблера. Тумблер обязан быть рубильником:
    /// запущенный процесс CLI доработал бы ход со старым составом инструментов.
    /// </summary>
    public async Task<int> CancelForProjectAsync(string projectId, CancellationToken ct = default)
    {
        List<string> chats;
        lock (_lock)
            chats = _sessions.Values.Where(s => s.ProjectId == projectId).Select(s => s.ChatSessionId).ToList();

        var stopped = 0;
        foreach (var chatId in chats)
            if (await StopAsync(chatId, DesktopHandsEndReasons.FacetOff, ct)) stopped++;
        return stopped;
    }

    // ---------- уборка ----------

    /// <summary>
    /// Один проход сторожа: истёкшие по простою и по потолку сеансы, протухшие заявки,
    /// а также сеансы чатов, которых больше нет (удалён или истёк — опрос реестра чатов,
    /// а не подписка: чат удаляют из нескольких мест, и общего события у них нет).
    /// Публичный ради юнит-тестов — как TickAsync у ChatExpiryService.
    /// </summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        var now = Now;
        List<(string ChatId, string Reason)> doomed = [];

        lock (_lock)
        {
            foreach (var s in _sessions.Values)
            {
                if (now >= s.HardDeadline) doomed.Add((s.ChatSessionId, DesktopHandsEndReasons.Cap));
                else if (now >= s.IdleDeadline) doomed.Add((s.ChatSessionId, DesktopHandsEndReasons.Idle));
            }

            foreach (var stale in _requests.Values.Where(r => now - r.RequestedAt >= RequestTtl).ToList())
                _requests.Remove(stale.ChatSessionId);
        }

        // Чат мог исчезнуть — спрашиваем реестр чатов ВНЕ блокировки: обращение чужое.
        foreach (var s in Snapshot())
        {
            if (doomed.Any(d => d.ChatId == s.ChatSessionId)) continue;
            var chat = _chats.Find(s.ChatSessionId);
            if (chat is null) doomed.Add((s.ChatSessionId, DesktopHandsEndReasons.ChatGone));
            // Грань могли выключить в проекте мимо контроллера (восстановление стора,
            // правка руками) — сторож это тоже ловит, иначе сеанс переживёт рубильник.
            else if (chat.FacetRefusal() is not null) doomed.Add((s.ChatSessionId, DesktopHandsEndReasons.FacetOff));
        }

        foreach (var (chatId, reason) in doomed)
            await StopAsync(chatId, reason, ct);
    }

    private IReadOnlyList<DesktopHandsSession> Snapshot()
    {
        lock (_lock) return _sessions.Values.ToList();
    }

    // ---------- наблюдатель соединений устройств ----------

    /// <summary>
    /// Устройство вернулось на связь. Сеанс НЕ воскресает: после разрыва человек начинает
    /// его заново — иначе «разрыв гасит сеанс» перестало бы что-либо значить.
    /// </summary>
    public Task OnDeviceOnlineAsync(DeviceConnection connection, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>Разрыв соединения — один из поводов погасить сеанс (ADR).</summary>
    public Task OnDeviceOfflineAsync(DeviceConnection connection, CancellationToken ct = default) =>
        StopForDeviceAsync(connection.OwnerId, connection.DeviceId, DesktopHandsEndReasons.Disconnected, ct);

    // ---------- внутреннее ----------

    private void TrimRequests(string ownerId)
    {
        var mine = _requests.Values.Where(r => r.OwnerId == ownerId).ToList();
        if (mine.Count <= MaxRequestsPerOwner) return;
        foreach (var old in mine.OrderBy(r => r.RequestedAt).Take(mine.Count - MaxRequestsPerOwner))
            _requests.Remove(old.ChatSessionId);
    }

    private Task NotifyAsync(DesktopHandsSession session, bool active, string? reason, CancellationToken ct) =>
        SafeAsync(() => _notifier.StatusAsync(session, active, reason, ct), "рассылка статуса сеанса");

    // Погасание не должно срываться из-за упавшего соседа: реестр уже изменён, и исключение
    // наружу оставило бы систему в состоянии «сеанса нет, но об этом никто не знает».
    private async Task SafeAsync(Func<Task> action, string what)
    {
        try { await action(); }
        catch (Exception ex) { _log.LogWarning(ex, "Сеанс рук: {What} не удалась", what); }
    }
}
