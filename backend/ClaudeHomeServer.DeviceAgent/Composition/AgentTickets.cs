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
/// процесс, перебирающий билеты, не превращал агента в усилитель запросов к серверу.
/// Сервер недоступен — билет не принят: закрыто по умолчанию.
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
