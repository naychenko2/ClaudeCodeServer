using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Tests.Services.Desktop;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Веб-половина сеанса рук (ADR-008): GET /api/devices/hands/chat/{id} и
/// POST /api/devices/hands/chat/{id}/request под обычным JWT владельца.
///
/// Зачем статус отдельной ручкой: событие desktop_session эфемерное — перезагрузив
/// страницу, шапка чата о живом сеансе больше ниоткуда не узнает, и бейдж «руки на home»
/// погас бы поверх работающих рук.
///
/// Главный инвариант заявки: веб может ТОЛЬКО попросить. Сеанс стартует лишь с самого
/// устройства, поэтому ни одна ветка этих эндпоинтов не смеет создавать сеанс.
/// </summary>
public class DesktopHandsWebEndpointsTests
{
    private readonly DesktopTestTime _time = new(new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
    private readonly DesktopFakeChats _chats = new();
    private readonly DesktopFakeDevices _devices = new();
    private readonly DesktopHandsSessionService _hands;

    public DesktopHandsWebEndpointsTests()
    {
        _hands = new DesktopHandsSessionService(_chats, new DesktopFakeNotifier(), new DesktopFakeCanceller(),
            NullLogger<DesktopHandsSessionService>.Instance, _time);
        _devices.Add("u1", "d1", "home");
    }

    private DeviceSessionsController Controller(string userId = "u1")
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId)], "jwt"));
        return new DeviceSessionsController(_hands, _devices, _chats)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
        };
    }

    private static T Field<T>(object value, string name) =>
        (T)value.GetType().GetProperty(name)!.GetValue(value)!;

    private static object Body(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Subject.Value!;

    [Fact]
    public void СтатусБезСеанса_ОтвечаетЧестнымНет()
    {
        _chats.Add("c1");

        var body = Body(Controller().ChatStatus("c1"));

        Field<bool>(body, "active").Should().BeFalse();
        Field<object?>(body, "session").Should().BeNull();
        Field<DateTime?>(body, "requestedAt").Should().BeNull();
        Field<string?>(body, "facetRefusal").Should().BeNull("грань чату выдана");
    }

    [Fact]
    public async Task СтатусЖивогоСеанса_ПереживаетПерезагрузкуСтраницы()
    {
        _chats.Add("c1");
        await _hands.StartAsync("u1", "d1", "home", "c1");

        // Ровно то, что делает шапка чата после F5: событие ленты уже не повторится
        var body = Body(Controller().ChatStatus("c1"));

        Field<bool>(body, "active").Should().BeTrue();
        var session = Field<object?>(body, "session")!;
        Field<string>(session, "device").Should().Be("home");
        Field<DateTime>(session, "expiresAt").Should().BeAfter(_time.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public void ЧужойЧат_404АНеЧужойСтатус()
    {
        _chats.Add("c1", ownerId: "u2");

        Controller().ChatStatus("c1").Should().BeOfType<NotFoundResult>();
        Controller().RequestFromChat("c1").Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void ИсчезнувшийЧат_НеотличимОтЧужого()
    {
        Controller().ChatStatus("нет-такого").Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void Заявка_СтановитсяВОчередьУстройства_НоСеансаНеСоздаёт()
    {
        _chats.Add("c1", chatName: "Десктопный чат");

        var body = Body(Controller().RequestFromChat("c1"));

        Field<bool>(body, "requested").Should().BeTrue();
        // Веб только просит: начать сеанс может лишь человек у машины
        _hands.ForChat("c1").Should().BeNull();
        _hands.RequestsFor("u1").Should().ContainSingle()
            .Which.ChatSessionId.Should().Be("c1");
    }

    [Fact]
    public void ЗаявкаВиднаВСтатусе_ЧтобыБейджОбъяснилОжидание()
    {
        _chats.Add("c1");
        Controller().RequestFromChat("c1");

        var body = Body(Controller().ChatStatus("c1"));

        Field<bool>(body, "active").Should().BeFalse();
        Field<DateTime?>(body, "requestedAt").Should().Be(_time.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public void ЗаявкаПриВыключеннойГрани_409СПричиной()
    {
        _chats.Add("c1", projectFacet: false);

        var conflict = Controller().RequestFromChat("c1").Should().BeOfType<ConflictObjectResult>().Subject;
        var value = conflict.Value!;

        Field<string>(value, "outcome").Should().Be(DesktopGateOutcomes.FacetOff);
        Field<string>(value, "message").Should().Contain("выключена в проекте");
        _hands.RequestsFor("u1").Should().BeEmpty("просить нечего: грань не выдана");
    }

    [Fact]
    public async Task ЗаявкаПриИдущемСеансе_ОтвечаетСостоянием_АНеВторойЗаявкой()
    {
        _chats.Add("c1");
        await _hands.StartAsync("u1", "d1", "home", "c1");

        var body = Body(Controller().RequestFromChat("c1"));

        Field<bool>(body, "requested").Should().BeFalse();
        Field<bool>(body, "active").Should().BeTrue();
        _hands.RequestsFor("u1").Should().BeEmpty();
    }

    [Fact]
    public void ГраньВыключена_СтатусНазываетПричину()
    {
        _chats.Add("c1", desktopChat: false);

        var body = Body(Controller().ChatStatus("c1"));

        Field<string?>(body, "facetRefusal").Should().Contain("не десктопный");
    }
}
