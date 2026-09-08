using System.Collections.Concurrent;
using ClaudeHomeServer.Core.Services;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Tests.Helpers;

// Тестовая реализация ISessionBroadcaster для мест, где раньше тесты мокали
// IHubContext<SessionHub>. Собирает все сообщения в списки по адресу — тесты
// проверяют содержимое и количество, как раньше через mock IHubClients.Group(...)
// .SendCoreAsync(...). Списки изолированы по адресу, что важно для тестов
// мульти-адресной рассылки (например WatchdogNotifier: owner + session + project).
//
// Хранилище — Lock+List, а не ConcurrentBag: порядок рассылки дельт в
// VoiceLocalTurnTests критичен (тест ожидает «Первое.», «Второе.» именно в
// этом порядке), а Bag перетасовывает при конкурентных добавлениях. List под
// lock — медленнее, но тесты этого не замечают, а порядок гарантирован.
public sealed class TestSessionBroadcaster : ISessionBroadcaster
{
    private readonly object _lock = new();
    private readonly List<(string OwnerId, ServerMessage Message)> _owner = new();
    private readonly List<(string SessionId, ServerMessage Message)> _session = new();
    private readonly List<(string ProjectId, ServerMessage Message)> _project = new();
    private readonly List<(string ProjectId, string ServiceId, ServerMessage Message)> _previewLog = new();

    public IReadOnlyList<(string OwnerId, ServerMessage Message)> Owner
    {
        get { lock (_lock) return _owner.ToArray(); }
    }
    public IReadOnlyList<(string SessionId, ServerMessage Message)> Session
    {
        get { lock (_lock) return _session.ToArray(); }
    }
    public IReadOnlyList<(string ProjectId, ServerMessage Message)> Project
    {
        get { lock (_lock) return _project.ToArray(); }
    }
    public IReadOnlyList<(string ProjectId, string ServiceId, ServerMessage Message)> PreviewLog
    {
        get { lock (_lock) return _previewLog.ToArray(); }
    }

    public TestSessionBroadcaster Clear()
    {
        lock (_lock)
        {
            _owner.Clear();
            _session.Clear();
            _project.Clear();
            _previewLog.Clear();
        }
        return this;
    }

    public Task ToSession(string sessionId, ServerMessage message)
    {
        lock (_lock) _session.Add((sessionId, message));
        return Task.CompletedTask;
    }

    public Task ToOwner(string ownerId, ServerMessage message)
    {
        lock (_lock) _owner.Add((ownerId, message));
        return Task.CompletedTask;
    }

    public Task ToProject(string projectId, ServerMessage message)
    {
        lock (_lock) _project.Add((projectId, message));
        return Task.CompletedTask;
    }

    public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message)
    {
        lock (_lock) _previewLog.Add((projectId, serviceId, message));
        return Task.CompletedTask;
    }
}
