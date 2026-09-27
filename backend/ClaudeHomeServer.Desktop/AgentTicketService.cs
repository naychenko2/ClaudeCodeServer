using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Desktop;

/// <summary>Выданный билет: сам билет уходит браузеру один раз и на сервере не хранится.</summary>
public sealed record AgentTicket(string Ticket, string DeviceId, DateTimeOffset ExpiresAt);

/// <summary>
/// Билеты доступа браузера к localhost-API агента (ADR-016 §5, план §2 п. 10). Билет
/// короткий (<see cref="DeviceAgentApi.TicketLifetime"/>) и привязан к тройке «владелец +
/// устройство + проект»; агент своего ключа подписи не имеет и проверяет билет
/// интроспекцией через канал управления — отвечает на неё только это устройство.
///
/// Хранилище только в памяти и только хешами: рестарт бэкенда отзывает все билеты, утечка
/// дампа памяти живых билетов не даёт.
/// </summary>
public sealed class AgentTicketService(IProjectManager projects, TimeProvider? time = null)
{
    private sealed record Grant(string OwnerId, string DeviceId, string ProjectId, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// Билет к агенту устройства проекта. null — проект не локальный, чужой или без
    /// устройства: тогда к агенту ходить незачем, файлы проекта на сервере.
    /// </summary>
    public AgentTicket? Issue(string ownerId, Project project)
    {
        if (project.OwnerId != ownerId || !ProjectCapabilities.IsDeviceBound(project)) return null;
        Prune();

        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = _time.GetUtcNow() + DeviceAgentApi.TicketLifetime;
        _grants[Hash(ticket)] = new Grant(ownerId, project.DeviceId!, project.Id, expiresAt);
        return new AgentTicket(ticket, project.DeviceId!, expiresAt);
    }

    /// <summary>
    /// Интроспекция по запросу устройства. Отвечает только устройству, к которому билет
    /// привязан, и только пока проект за ним же: перепривязанный или удалённый проект
    /// обнуляет выданные билеты.
    /// </summary>
    public AgentTicketIntrospection? Introspect(string ownerId, string deviceId, string ticket)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 128) return null;
        var key = Hash(ticket);
        if (!_grants.TryGetValue(key, out var grant)) return null;
        if (grant.ExpiresAt <= _time.GetUtcNow())
        {
            _grants.TryRemove(key, out _);
            return null;
        }
        if (grant.OwnerId != ownerId || grant.DeviceId != deviceId) return null;
        if (ProjectOnDevice(ownerId, deviceId, grant.ProjectId) is not { } project) return null;
        return new AgentTicketIntrospection(grant.OwnerId, project.Id, project.RootPath, grant.ExpiresAt);
    }

    /// <summary>Проект владельца, привязанный именно к этому устройству, — или null.</summary>
    public Project? ProjectOnDevice(string ownerId, string deviceId, string projectId)
    {
        var project = projects.GetById(projectId);
        return project is not null && project.OwnerId == ownerId && ProjectCapabilities.IsDeviceBound(project)
               && project.DeviceId == deviceId
            ? project
            : null;
    }

    private void Prune()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, grant) in _grants)
            if (grant.ExpiresAt <= now) _grants.TryRemove(key, out _);
    }

    private static string Hash(string ticket) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
}
