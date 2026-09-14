using ClaudeHomeServer.Services.Desktop;

namespace ClaudeHomeServer.Tests.Services.Desktop;

// Общие фейки грани десктопа. Живут в Main.Tests, а не в тестах вертикали (Этап 5,
// вынос Desktop): их делят ДВЕ тестовые сборки — ClaudeHomeServer.Desktop.Tests
// (сеанс рук, гейт исполнения) и ClaudeHomeServer.Tests (тесты контроллеров канала,
// которые остались в Main вместе с самими контроллерами). Ссылка идёт в одну сторону,
// Desktop.Tests → Main.Tests, поэтому общий код — здесь.

/// <summary>Управляемые часы — сеансы гаснут по срокам, а тесты не спят.</summary>
internal sealed class DesktopTestTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Реестр чатов на память: чат исчезает — значит, его удалили или он истёк.</summary>
internal sealed class DesktopFakeChats : IDesktopChatDirectory
{
    private readonly Dictionary<string, DesktopChatInfo> _chats = [];

    public DesktopChatInfo Add(string chatId, string ownerId = "u1", string? projectId = "p1",
        bool desktopChat = true, bool projectFacet = true, bool flag = true, string? chatName = "Десктопный чат")
    {
        var chat = new DesktopChatInfo(chatId, ownerId, projectId, chatName, "Проект", "Денис",
            desktopChat, projectFacet, flag);
        _chats[chatId] = chat;
        return chat;
    }

    public void Remove(string chatId) => _chats.Remove(chatId);

    public void SetProjectFacet(string chatId, bool enabled) =>
        _chats[chatId] = _chats[chatId] with { ProjectFacetEnabled = enabled };

    public DesktopChatInfo? Find(string chatSessionId) => _chats.GetValueOrDefault(chatSessionId);
}

/// <summary>Реестр устройств на память.</summary>
internal sealed class DesktopFakeDevices : IDesktopDeviceDirectory
{
    private readonly List<(string OwnerId, DesktopDeviceInfo Device)> _devices = [];

    public DesktopDeviceInfo Add(string ownerId, string id, string name, bool online = true)
    {
        var device = new DesktopDeviceInfo(id, name, online);
        _devices.RemoveAll(d => d.Device.Id == id);
        _devices.Add((ownerId, device));
        return device;
    }

    public void SetOnline(string id, bool online)
    {
        var index = _devices.FindIndex(d => d.Device.Id == id);
        _devices[index] = (_devices[index].OwnerId, _devices[index].Device with { Online = online });
    }

    public IReadOnlyList<DesktopDeviceInfo> List(string ownerId) =>
        _devices.Where(d => d.OwnerId == ownerId).Select(d => d.Device).ToList();

    public DesktopDeviceInfo? FindByName(string ownerId, string name) =>
        List(ownerId).FirstOrDefault(d => string.Equals(d.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    public DesktopDeviceInfo? FindById(string ownerId, string deviceId) =>
        List(ownerId).FirstOrDefault(d => d.Id == deviceId);
}

/// <summary>Записывает рассылку статуса — по ней видно, что бейдж узнал о погасании.</summary>
internal sealed class DesktopFakeNotifier : IDesktopHandsNotifier
{
    public List<(string ChatId, bool Active, string? Reason)> Events { get; } = [];

    public Task StatusAsync(DesktopHandsSession session, bool active, string? reason, CancellationToken ct = default)
    {
        Events.Add((session.ChatSessionId, active, reason));
        return Task.CompletedTask;
    }
}

/// <summary>Записывает рассылку cancel по вызовам погасшего сеанса.</summary>
internal sealed class DesktopFakeCanceller : IDesktopCallCanceller
{
    public List<(string ChatId, string Reason)> Cancelled { get; } = [];

    public Task CancelChatCallsAsync(string chatSessionId, string reason, CancellationToken ct = default)
    {
        Cancelled.Add((chatSessionId, reason));
        return Task.CompletedTask;
    }
}
