using ClaudeHomeServer.Services.Desktop;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Desktop;

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
