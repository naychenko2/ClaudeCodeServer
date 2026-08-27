using ClaudeHomeServer.Services.Desktop;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Desktop;

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

/// <summary>
/// Сеанс рук (ADR-008, «Сеанс рук и согласие»): старт только с устройства, один сеанс на
/// чат и на устройство, и все шесть поводов погасания.
/// </summary>
public class DesktopHandsSessionTests
{
    private readonly DesktopTestTime _time = new(new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
    private readonly DesktopFakeChats _chats = new();
    private readonly DesktopFakeNotifier _notifier = new();
    private readonly DesktopFakeCanceller _calls = new();
    private readonly DesktopHandsSessionService _sut;

    public DesktopHandsSessionTests()
    {
        _sut = new DesktopHandsSessionService(_chats, _notifier, _calls,
            NullLogger<DesktopHandsSessionService>.Instance, _time);
    }

    private Task<DesktopHandsStartResult> StartAsync(string chatId = "c1", string deviceId = "d1",
        string deviceName = "home", string ownerId = "u1") =>
        _sut.StartAsync(ownerId, deviceId, deviceName, chatId);

    // ---------- старт ----------

    [Fact]
    public async Task СтартСУстройства_ПоднимаетСеансИШлётСтатус()
    {
        _chats.Add("c1");

        var result = await StartAsync();

        result.Started.Should().BeTrue();
        _sut.ForChat("c1").Should().NotBeNull();
        _sut.ForDevice("u1", "d1")!.ChatSessionId.Should().Be("c1");
        _notifier.Events.Should().ContainSingle(e => e.ChatId == "c1" && e.Active);
    }

    [Fact]
    public async Task ЧужойЧат_СеансаНеДаёт()
    {
        _chats.Add("c1", ownerId: "someone-else");

        var result = await StartAsync();

        result.Started.Should().BeFalse();
        result.Outcome.Should().Be(DesktopGateOutcomes.ChatGone);
    }

    [Theory]
    // не десктопный чат, выключенная в проекте грань и снятый флаг — одинаковый отказ
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task БезГрани_СеансНеСтартует(bool desktopChat, bool projectFacet, bool flag)
    {
        _chats.Add("c1", desktopChat: desktopChat, projectFacet: projectFacet, flag: flag);

        var result = await StartAsync();

        result.Started.Should().BeFalse();
        result.Outcome.Should().Be(DesktopGateOutcomes.FacetOff);
        _sut.ForChat("c1").Should().BeNull();
    }

    [Fact]
    public async Task ДругойЧатНаТомЖеУстройстве_ОдинСеансНаУстройство()
    {
        _chats.Add("c1");
        _chats.Add("c2");
        await StartAsync("c1");

        var second = await StartAsync("c2");

        second.Started.Should().BeFalse();
        second.Outcome.Should().Be(DesktopGateOutcomes.HandsBusy);
        second.Message.Should().Contain("home");
    }

    [Fact]
    public async Task ДругоеУстройствоДляТогоЖеЧата_ОдинСеансНаЧат()
    {
        _chats.Add("c1");
        await StartAsync("c1", "d1", "home");

        var second = await StartAsync("c1", "d2", "work");

        second.Started.Should().BeFalse();
        second.Outcome.Should().Be(DesktopGateOutcomes.HandsBusy);
    }

    [Fact]
    public async Task ПовторныйСтартТогоЖеЧата_ПродлеваетСеанс()
    {
        _chats.Add("c1");
        await StartAsync();
        _time.Advance(TimeSpan.FromMinutes(10));

        var again = await StartAsync();

        again.Started.Should().BeTrue();
        _sut.ForChat("c1")!.LastCallAt.Should().Be(_time.Now.UtcDateTime);
    }

    // ---------- поводы погасания ----------

    [Fact]
    public async Task Повод1_ПятнадцатьМинутБезВызовов_ГаситСеанс()
    {
        _chats.Add("c1");
        await StartAsync();

        _time.Advance(TimeSpan.FromMinutes(14));
        await _sut.SweepAsync();
        _sut.ForChat("c1").Should().NotBeNull("14 минут — ещё не простой");

        _time.Advance(TimeSpan.FromMinutes(2));
        await _sut.SweepAsync();

        _sut.ForChat("c1").Should().BeNull();
        _notifier.Events.Should().Contain(e => !e.Active && e.Reason == DesktopHandsEndReasons.Idle);
    }

    [Fact]
    public async Task Вызов_ПродлеваетОкноПростоя()
    {
        _chats.Add("c1");
        await StartAsync();

        for (var i = 0; i < 4; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(10));
            _sut.Touch("c1").Should().BeTrue();
            await _sut.SweepAsync();
        }

        _sut.ForChat("c1").Should().NotBeNull();
    }

    [Fact]
    public async Task Повод2_ПотолокДваЧаса_ГаситДажеПодВызовами()
    {
        _chats.Add("c1");
        await StartAsync();

        for (var i = 0; i < 13; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(10));
            _sut.Touch("c1");
            await _sut.SweepAsync();
        }

        _sut.ForChat("c1").Should().BeNull();
        _notifier.Events.Should().Contain(e => !e.Active && e.Reason == DesktopHandsEndReasons.Cap);
    }

    [Fact]
    public async Task Повод3_ЗакрытиеОкнаКлиента_ГаситСеанс()
    {
        _chats.Add("c1");
        await StartAsync();

        var stopped = await _sut.StopForDeviceAsync("u1", "d1", DesktopHandsEndReasons.ClientClosed);

        stopped.Should().BeTrue();
        _sut.ForChat("c1").Should().BeNull();
        _notifier.Events.Should().Contain(e => !e.Active && e.Reason == DesktopHandsEndReasons.ClientClosed);
    }

    [Fact]
    public async Task Повод4_ЧатаБольшеНет_ГаситСеанс()
    {
        _chats.Add("c1");
        await StartAsync();

        _chats.Remove("c1");
        await _sut.SweepAsync();

        _sut.ForChat("c1").Should().BeNull();
        _notifier.Events.Should().Contain(e => !e.Active && e.Reason == DesktopHandsEndReasons.ChatGone);
    }

    [Fact]
    public async Task Повод5_РазрывСоединения_ГаситСеанс()
    {
        _chats.Add("c1");
        await StartAsync();

        await _sut.OnDeviceOfflineAsync(new DeviceConnection("conn", "u1", "d1", _time.Now));

        _sut.ForChat("c1").Should().BeNull();
        _notifier.Events.Should().Contain(e => !e.Active && e.Reason == DesktopHandsEndReasons.Disconnected);
    }

    [Fact]
    public async Task Повод6_РестартБэкенда_СеансыЖивутТолькоВПамяти()
    {
        _chats.Add("c1");
        await StartAsync();

        // «Рестарт» — это новый экземпляр службы: ничего не восстанавливается с диска.
        var afterRestart = new DesktopHandsSessionService(_chats, _notifier, _calls,
            NullLogger<DesktopHandsSessionService>.Instance, _time);

        afterRestart.ForChat("c1").Should().BeNull();
        afterRestart.ForOwner("u1").Should().BeEmpty();
    }

    [Fact]
    public async Task ВозвратУстройстваНаСвязь_СеансНеВоскрешает()
    {
        _chats.Add("c1");
        await StartAsync();
        await _sut.OnDeviceOfflineAsync(new DeviceConnection("conn", "u1", "d1", _time.Now));

        await _sut.OnDeviceOnlineAsync(new DeviceConnection("conn2", "u1", "d1", _time.Now));

        _sut.ForChat("c1").Should().BeNull();
    }

    // ---------- рубильник проекта ----------

    [Fact]
    public async Task ВыключениеГраниВПроекте_ГаситСеансыИРассылаетCancel()
    {
        _chats.Add("c1", projectId: "p1");
        await StartAsync();

        var stopped = await _sut.CancelForProjectAsync("p1");

        stopped.Should().Be(1);
        _sut.ForChat("c1").Should().BeNull();
        _calls.Cancelled.Should().ContainSingle(c => c.ChatId == "c1" && c.Reason == DesktopHandsEndReasons.FacetOff);
    }

    [Fact]
    public async Task Сторож_ЛовитВыключеннуюГраньМимоКонтроллера()
    {
        _chats.Add("c1");
        await StartAsync();

        _chats.SetProjectFacet("c1", enabled: false);
        await _sut.SweepAsync();

        _sut.ForChat("c1").Should().BeNull();
        _notifier.Events.Should().Contain(e => !e.Active && e.Reason == DesktopHandsEndReasons.FacetOff);
    }

    [Fact]
    public async Task Погасание_ОтменяетВызовыЧата()
    {
        _chats.Add("c1");
        await StartAsync();

        await _sut.StopAsync("c1", DesktopHandsEndReasons.Stopped);

        _calls.Cancelled.Should().ContainSingle(c => c.ChatId == "c1" && c.Reason == DesktopHandsEndReasons.Stopped);
    }

    [Fact]
    public async Task ПовторноеПогасание_Идемпотентно()
    {
        _chats.Add("c1");
        await StartAsync();

        (await _sut.StopAsync("c1", DesktopHandsEndReasons.Stopped)).Should().BeTrue();
        (await _sut.StopAsync("c1", DesktopHandsEndReasons.Stopped)).Should().BeFalse();
    }

    // ---------- очередь заявок ----------

    [Fact]
    public void Заявка_НесётИмяЧатаПроектаИПерсоны()
    {
        var chat = _chats.Add("c1");

        _sut.Enqueue(chat);

        var request = _sut.RequestsFor("u1").Should().ContainSingle().Subject;
        request.ChatName.Should().Be("Десктопный чат");
        request.ProjectName.Should().Be("Проект");
        request.PersonaName.Should().Be("Денис");
    }

    [Fact]
    public async Task Старт_СнимаетЗаявкуЧата()
    {
        var chat = _chats.Add("c1");
        _sut.Enqueue(chat);

        await StartAsync();

        _sut.RequestsFor("u1").Should().BeEmpty();
    }

    [Fact]
    public async Task ПротухшаяЗаявка_ИзОчередиУходит()
    {
        _sut.Enqueue(_chats.Add("c1"));

        _time.Advance(DesktopHandsSessionService.RequestTtl + TimeSpan.FromMinutes(1));
        await _sut.SweepAsync();

        _sut.RequestsFor("u1").Should().BeEmpty();
    }
}
