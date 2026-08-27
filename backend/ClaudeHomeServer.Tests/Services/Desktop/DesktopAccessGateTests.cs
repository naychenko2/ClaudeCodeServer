using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Desktop;

/// <summary>
/// Гейт исполнения грани десктопа (ADR-008, «Два уровня, которые нельзя смешивать»):
/// право чата проверяется на КАЖДЫЙ вызов, чат берётся из capability-токена, а сеанс рук
/// гейтит и чтение, и действия.
/// </summary>
public class DesktopAccessGateTests
{
    private readonly DesktopTestTime _time = new(new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
    private readonly DesktopFakeChats _chats = new();
    private readonly DesktopFakeDevices _devices = new();
    private readonly DesktopFakeNotifier _notifier = new();
    private readonly DesktopFakeCanceller _calls = new();
    private readonly DesktopHandsSessionService _hands;
    private readonly DesktopAccessGate _sut;

    public DesktopAccessGateTests()
    {
        _hands = new DesktopHandsSessionService(_chats, _notifier, _calls,
            NullLogger<DesktopHandsSessionService>.Instance, _time);
        _sut = new DesktopAccessGate(_chats, _devices, _hands);
        _devices.Add("u1", "d1", "home");
        _devices.Add("u1", "d2", "work");
    }

    private static DesktopCaller Caller(string chatId = "c1", string ownerId = "u1") =>
        new(ownerId, chatId, null);

    [Fact]
    public async Task БезСеансаРук_Отказ_ЗаявкаВОчереди()
    {
        _chats.Add("c1");

        var decision = _sut.EvaluateCall(Caller(), deviceName: null);

        decision.Allowed.Should().BeFalse();
        decision.Outcome.Should().Be(DesktopGateOutcomes.NoHandsSession);
        decision.Message.Should().Contain("home").And.Contain("work");
        _hands.RequestsFor("u1").Should().ContainSingle(r => r.ChatSessionId == "c1");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ЧатТокенаНеСовпалСЧатомСеанса_Отказ()
    {
        _chats.Add("c1");
        _chats.Add("c2");
        // Руки отданы чату c2, а токен принёс c1 — вызов проходить не должен.
        await _hands.StartAsync("u1", "d1", "home", "c2");

        var decision = _sut.EvaluateCall(Caller("c1"), deviceName: null);

        decision.Allowed.Should().BeFalse();
        decision.Outcome.Should().Be(DesktopGateOutcomes.NoHandsSession);
        _hands.ForChat("c2").Should().NotBeNull("чужой вызов не гасит и не крадёт чужой сеанс");
    }

    [Fact]
    public async Task ЧужойВладелецВТокене_Отказ()
    {
        _chats.Add("c1", ownerId: "u1");
        await _hands.StartAsync("u1", "d1", "home", "c1");

        var decision = _sut.EvaluateCall(Caller("c1", ownerId: "u2"), deviceName: null);

        decision.Allowed.Should().BeFalse();
        decision.Outcome.Should().Be(DesktopGateOutcomes.ChatGone);
    }

    [Fact]
    public async Task ГраньВыключилиВПроекте_ОтказДажеПриЖивомСеансе()
    {
        _chats.Add("c1");
        await _hands.StartAsync("u1", "d1", "home", "c1");

        _chats.SetProjectFacet("c1", enabled: false);
        var decision = _sut.EvaluateCall(Caller(), deviceName: null);

        decision.Allowed.Should().BeFalse();
        decision.Outcome.Should().Be(DesktopGateOutcomes.FacetOff);
    }

    [Fact]
    public void НеДесктопныйЧат_Отказ()
    {
        _chats.Add("c1", desktopChat: false);

        _sut.EvaluateCall(Caller(), deviceName: null).Outcome.Should().Be(DesktopGateOutcomes.FacetOff);
        _sut.EvaluateFacet(Caller()).Outcome.Should().Be(DesktopGateOutcomes.FacetOff);
    }

    [Fact]
    public void ФлагВыключен_Отказ()
    {
        _chats.Add("c1", flag: false);

        _sut.EvaluateCall(Caller(), deviceName: null).Outcome.Should().Be(DesktopGateOutcomes.FacetOff);
    }

    [Fact]
    public void ЧатВнеПроекта_ГраньНеВыдаётся()
    {
        _chats.Add("c1", projectId: null!);

        _sut.EvaluateCall(Caller(), deviceName: null).Outcome.Should().Be(DesktopGateOutcomes.FacetOff);
    }

    [Fact]
    public void ЧатаНет_Отказ()
    {
        var decision = _sut.EvaluateCall(Caller("исчез"), deviceName: null);

        decision.Allowed.Should().BeFalse();
        decision.Outcome.Should().Be(DesktopGateOutcomes.ChatGone);
    }

    [Fact]
    public async Task ИмяУстройстваНеСовпало_ОтказСИменемУстройстваСеанса()
    {
        _chats.Add("c1");
        await _hands.StartAsync("u1", "d1", "home", "c1");

        var decision = _sut.EvaluateCall(Caller(), deviceName: "work");

        decision.Allowed.Should().BeFalse();
        decision.Outcome.Should().Be(DesktopGateOutcomes.DeviceMismatch);
        decision.Message.Should().Contain("home");
    }

    [Fact]
    public async Task НеизвестноеИмяУстройства_ОтказСоСписком()
    {
        _chats.Add("c1");
        await _hands.StartAsync("u1", "d1", "home", "c1");

        var decision = _sut.EvaluateCall(Caller(), deviceName: "ноутбук");

        decision.Outcome.Should().Be(DesktopGateOutcomes.UnknownDevice);
        decision.Message.Should().Contain("home");
    }

    [Fact]
    public async Task УстройствоОфлайн_ОтказСоСпискомОнлайн()
    {
        _chats.Add("c1");
        await _hands.StartAsync("u1", "d1", "home", "c1");
        _devices.SetOnline("d1", online: false);

        var decision = _sut.EvaluateCall(Caller(), deviceName: null);

        decision.Outcome.Should().Be(DesktopOutcomes.DeviceOffline);
        decision.Message.Should().Contain("home").And.Contain("work");
    }

    [Fact]
    public async Task ИмяУстройстваОпущено_БерётсяУстройствоСеанса()
    {
        _chats.Add("c1");
        await _hands.StartAsync("u1", "d2", "work", "c1");

        var decision = _sut.EvaluateCall(Caller(), deviceName: null);

        decision.Allowed.Should().BeTrue();
        decision.Device!.Name.Should().Be("work");
    }

    [Fact]
    public async Task РазрешённыйВызов_ПродлеваетОкноПростоя()
    {
        _chats.Add("c1");
        await _hands.StartAsync("u1", "d1", "home", "c1");
        _time.Advance(TimeSpan.FromMinutes(10));

        _sut.EvaluateCall(Caller(), deviceName: "home").Allowed.Should().BeTrue();

        _hands.ForChat("c1")!.LastCallAt.Should().Be(_time.Now.UtcDateTime);
    }

    [Fact]
    public void СписокУстройств_РаботаетБезСеанса()
    {
        _chats.Add("c1");

        // desktop_devices как раз и рассказывает, что сеанса нет и на чём его начать,
        // поэтому сеанса он не требует — в отличие от чтения экрана и действий.
        _sut.EvaluateFacet(Caller()).Allowed.Should().BeTrue();
        _sut.EvaluateCall(Caller(), deviceName: null).Allowed.Should().BeFalse();
    }
}
