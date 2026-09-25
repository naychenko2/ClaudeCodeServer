using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Composition;

/// <summary>Интроспекция билета у сервера. Боевая — метод хаба устройств через канал управления.</summary>
internal interface IAgentTicketIntrospector
{
    Task<AgentTicketIntrospection?> IntrospectAsync(string ticket, CancellationToken ct);
}

/// <summary>
/// Проверка билета браузера (ADR-016 §5, план §2 п. 10): своего ключа у агента нет, билет
/// спрашивается у сервера и кэшируется на свой срок, не дольше
/// <see cref="DeviceAgentApi.TicketLifetime"/>. Отказ кэшируется коротко — чтобы локальный
/// процесс, перебирающий билеты, не превращал агента в усилитель запросов к серверу; сбой
/// интроспекции кэшируется так же. Сервер недоступен — билет не принят: закрыто по умолчанию.
/// </summary>
internal sealed class AgentTicketCache(IAgentTicketIntrospector introspector, TimeProvider? time = null)
{
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(10);
    private const int MaxEntries = 4096;

    private readonly ConcurrentDictionary<string, (AgentTicketIntrospection? Grant, DateTimeOffset Until)> _cache = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Билет принят впервые (не из кэша): повод поднять ватчер проекта.</summary>
    public event Action<AgentTicketIntrospection>? Granted;

    public async Task<AgentTicketIntrospection?> ValidateAsync(string? ticket, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 128) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(key, out var hit) && hit.Until > now) return hit.Grant;

        AgentTicketIntrospection? grant;
        try { grant = await introspector.IntrospectAsync(ticket, ct); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Сбой канала — такой же отказ: иначе перебор билетов при лежащем канале бьёт в сервер каждым запросом
            if (_cache.Count >= MaxEntries) Prune(now);
            _cache[key] = (null, now + NegativeTtl);
            return null;
        }

        if (_cache.Count >= MaxEntries) Prune(now);
        var until = grant is null
            ? now + NegativeTtl
            : Min(grant.ExpiresAt, now + DeviceAgentApi.TicketLifetime);
        if (grant is not null && until <= now) return null;
        _cache[key] = (grant, until);
        if (grant is not null) Granted?.Invoke(grant);
        return grant;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (k, v) in _cache)
            if (v.Until <= now) _cache.TryRemove(k, out _);
        if (_cache.Count >= MaxEntries) _cache.Clear();
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}

/// <summary>
/// Билеты в URL — только они туда и попадают, основной билет никогда (ревью 4.5, MAJOR #4).
/// Выдаются агентом по основному билету, привязаны к его гранту (владелец, проект) и к одной
/// «сфере»: поток одного пути (<c>&lt;video src&gt;</c>), подключение к хабу (WebSocket заголовок
/// не ставит), превью дев-сервера (iframe). Многоразовые в пределах срока: плеер докачивает
/// диапазонами, клиент SignalR ходит negotiate + connect, превью грузит подресурсы.
/// </summary>
internal sealed class AgentUrlTickets(TimeProvider? time = null)
{
    public const string HubScope = "hub";
    public const string PreviewScope = "preview";

    private const int MaxEntries = 4096;

    private readonly ConcurrentDictionary<string, (AgentTicketIntrospection Grant, string Scope, DateTimeOffset Until)> _tickets = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public static string StreamScope(string path) => "stream:" + path;

    /// <summary>Билет потока одного пути: <see cref="DeviceAgentApi.StreamTicketLifetime"/>, не дольше основного.</summary>
    public (string Ticket, DateTimeOffset ExpiresAt) IssueStream(AgentTicketIntrospection grant, string path) =>
        Issue(grant, StreamScope(path), DeviceAgentApi.StreamTicketLifetime);

    /// <param name="capByGrant">Не дольше основного билета. Снимается только у превью —
    /// почему, см. <see cref="DeviceAgentApi.PreviewTicketLifetime"/>.</param>
    public (string Ticket, DateTimeOffset ExpiresAt) Issue(AgentTicketIntrospection grant, string scope, TimeSpan lifetime,
        bool capByGrant = true)
    {
        var now = _time.GetUtcNow();
        if (_tickets.Count >= MaxEntries) Prune(now);
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var until = now + lifetime;
        if (capByGrant && grant.ExpiresAt < until) until = grant.ExpiresAt;
        _tickets[Hash(ticket)] = (grant, scope, until);
        return (ticket, until);
    }

    /// <summary>Грант основного билета — только для той же сферы и в пределах срока.</summary>
    public AgentTicketIntrospection? Validate(string? ticket, string? scope)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 128 || scope is null) return null;
        var key = Hash(ticket);
        if (!_tickets.TryGetValue(key, out var entry)) return null;
        if (entry.Until <= _time.GetUtcNow())
        {
            _tickets.TryRemove(key, out _);
            return null;
        }
        return string.Equals(entry.Scope, scope, StringComparison.Ordinal) ? entry.Grant : null;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (k, v) in _tickets)
            if (v.Until <= now) _tickets.TryRemove(k, out _);
        if (_tickets.Count >= MaxEntries) _tickets.Clear();
    }

    private static string Hash(string ticket) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
}
