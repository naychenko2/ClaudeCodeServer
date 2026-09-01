using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Watchdog;

/// <summary>
/// Живые данные по активным сторожам владельца (визуализация chat-watchdogs): снимок
/// {sessions, projects} для GET /api/watchdogs и его же рассылка событием watchdogs_changed —
/// значки сторожа в списках чатов/проектов живут без поллинга. Слушатель Changed стора
/// встаёт в конструкторе — синглтон обязан быть создан к первым постановкам (прогрев в
/// Program.cs). Ничего не мутирует: статусы сторожей и DeliveredAt не трогает (ADR-013).
/// </summary>
public sealed class WatchdogNotifier : IDisposable
{
    private readonly WatchdogStore _store;
    private readonly Func<string, bool> _watchEnabled;
    private readonly IHubContext<SessionHub> _hub;
    private readonly ILogger<WatchdogNotifier>? _log;

    public WatchdogNotifier(WatchdogStore store, SessionManager sessions,
        IHubContext<SessionHub> hub, ILogger<WatchdogNotifier>? log = null)
        : this(store, sessions.WatchMcpEnabled, hub, log) { }

    // Шов под юнит-тесты: SessionManager в тест не собирают (тяжёлая сборка) — флаг
    // подаётся делегатом; прод-конструктор выше берёт его из SessionManager.WatchMcpEnabled
    internal WatchdogNotifier(WatchdogStore store, Func<string, bool> watchEnabled,
        IHubContext<SessionHub> hub, ILogger<WatchdogNotifier>? log = null)
    {
        _store = store;
        _watchEnabled = watchEnabled;
        _hub = hub;
        _log = log;
        store.Changed += OnChanged;
    }

    public void Dispose() => _store.Changed -= OnChanged;

    /// <summary>Точка для цикла сторожей: терминал (fired/timed_out/launch_failed) ставится
    /// сервисом прямой мутацией записи мимо методов стора — событие Changed там не стреляет,
    /// WatchdogService.TerminateAsync зовёт нотификатор сам после Save.</summary>
    public void NotifyChanged(string ownerId) => OnChanged(ownerId);

    private void OnChanged(string ownerId)
    {
        // Fire-and-forget: сторож-событие не задерживает вызывателя (постановка из хода).
        // BroadcastAsync ловит всё сам; внешний catch — на сбой построения снимка
        try { _ = BroadcastAsync(ownerId); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "watchdogs_changed: рассылка не началась (owner {OwnerId})", ownerId);
        }
    }

    /// <summary>
    /// Снимок активных сторожей владельца: id чатов с хотя бы одним активным (Status==Active)
    /// сторожем и id проектов, где такие чаты есть; чаты вне проектов — только в Sessions.
    /// Флаг chat-watchdogs выключен → пустой снимок (REST тоже: 200 с пустыми списками,
    /// не 403 — стор остаётся рабочим при выключении).
    /// </summary>
    internal WatchdogsChangedMessage Snapshot(string ownerId)
    {
        if (!_watchEnabled(ownerId)) return new WatchdogsChangedMessage([], []);
        var active = _store.GetByOwner(ownerId).Where(w => w.Status == WatchdogStatus.Active).ToList();
        return new WatchdogsChangedMessage(
            [.. active.Select(w => w.SessionId).Distinct()],
            [.. active.Where(w => w.ProjectId is not null).Select(w => w.ProjectId!).Distinct()]);
    }

    // Адресация как у chat_archived (BroadcastChatArchivedAsync): session-группа каждого
    // затронутого чата — имя группы это сам id сессии (JoinSession), копия с заполненным
    // SessionId для роутинга клиента; плюс project- и user-группы: значки живут в списке
    // чатов, в рельсе проектов и в списке чатов вне проектов
    private async Task BroadcastAsync(string ownerId)
    {
        try
        {
            var msg = Snapshot(ownerId);
            var sends = new List<Task> { _hub.Clients.Group("user_" + ownerId).SendAsync("message", msg) };
            foreach (var sid in msg.Sessions)
                sends.Add(_hub.Clients.Group(sid).SendAsync("message", msg with { SessionId = sid }));
            foreach (var pid in msg.Projects)
                sends.Add(_hub.Clients.Group("project_" + pid).SendAsync("message", msg));
            await Task.WhenAll(sends);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "watchdogs_changed не разослан (owner {OwnerId})", ownerId);
        }
    }
}
