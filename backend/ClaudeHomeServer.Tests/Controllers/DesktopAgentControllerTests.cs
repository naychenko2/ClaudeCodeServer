using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Tests.Services.Desktop;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Харнес-контракт грани десктопа (ADR-008): GET /api/devices/agent/list и
/// POST /api/devices/agent/call. Отказ гейта — 409 { outcome, message }, а чат-вызыватель
/// берётся ИЗ capability-токена: заголовок X-Caller-Session-Id в решении не участвует.
///
/// Контроллер собирается руками, без WebApplicationFactory: проверяется именно поведение
/// эндпоинта, а не проводка DI.
/// </summary>
public class DesktopAgentControllerTests
{
    private readonly DesktopTestTime _time = new(new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
    private readonly DesktopFakeChats _chats = new();
    private readonly DesktopFakeDevices _devices = new();
    private readonly DesktopHandsSessionService _hands;
    private readonly DesktopAccessGate _gate;
    private readonly EchoDevice _sender = new();
    private readonly DesktopCallRouter _router;

    public DesktopAgentControllerTests()
    {
        _hands = new DesktopHandsSessionService(_chats, new DesktopFakeNotifier(), new DesktopFakeCanceller(),
            NullLogger<DesktopHandsSessionService>.Instance, _time);
        _gate = new DesktopAccessGate(_chats, _devices, _hands);
        _router = new DesktopCallRouter(_sender, [], NullLogger<DesktopCallRouter>.Instance);
        _sender.Router = _router;
        _devices.Add("u1", "d1", "home");
        _devices.Add("u1", "d2", "work");
    }

    /// <summary>Устройство-эхо: подтверждает приём и по go сразу присылает результат.</summary>
    private sealed class EchoDevice : IDeviceCommandSender
    {
        public DesktopCallRouter? Router;
        public string ConnectionId = "conn";
        public List<DesktopCallCommand> Calls { get; } = [];

        public Task SendCallAsync(string connectionId, DesktopCallCommand command, CancellationToken ct = default)
        {
            Calls.Add(command);
            Router!.Ack(command.CallId, ConnectionId);
            return Task.CompletedTask;
        }

        public Task SendGoAsync(string connectionId, DesktopGoCommand go, CancellationToken ct = default)
        {
            Router!.TryAcceptResult(go.CallId, "u1", "d1",
                new DesktopCallResult(go.CallId, DesktopOutcomes.Ok, 1, "кадр снят"));
            return Task.CompletedTask;
        }

        public Task SendCancelAsync(string connectionId, DesktopCancelCommand cancel, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    // Принципал схемы DesktopCapability: владелец в sub, чат-вызыватель в sid.
    private static ClaimsPrincipal Token(string chatId, string ownerId = "u1") =>
        new(new ClaimsIdentity([
            new Claim(JwtRegisteredClaimNames.Sub, ownerId),
            new Claim(DesktopCaller.SessionClaim, chatId)
        ], DesktopCapabilityAuthHandler.SchemeName));

    private DesktopAgentController Controller(ClaimsPrincipal? user, string? spoofedCallerHeader = null)
    {
        var http = new DefaultHttpContext { User = user ?? new ClaimsPrincipal(new ClaimsIdentity()) };
        if (spoofedCallerHeader is not null) http.Request.Headers["X-Caller-Session-Id"] = spoofedCallerHeader;

        return new DesktopAgentController(_gate, _hands, _devices, _router)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private async Task OnlineAsync(string deviceId = "d1", string connectionId = "conn")
    {
        _router.RegisterConnection(connectionId, "u1", deviceId);
        await _router.HelloAsync(connectionId, new DeviceHello(DesktopProtocol.Version, ["click", "type"], "1.0.0"));
    }

    private static (string Outcome, string Message) Refusal(IActionResult result)
    {
        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        var value = conflict.Value!;
        var type = value.GetType();
        return ((string)type.GetProperty("outcome")!.GetValue(value)!,
                (string)type.GetProperty("message")!.GetValue(value)!);
    }

    [Fact]
    public async Task ВызовБезСеансаРук_409СИсходомИТекстом()
    {
        _chats.Add("c1");

        var response = await Controller(Token("c1")).Call(new DesktopAgentCallRequest(null, DesktopCallKinds.Act), default);

        var (outcome, message) = Refusal(response);
        outcome.Should().Be(DesktopGateOutcomes.NoHandsSession);
        message.Should().Contain("Сеанс рук не начат");
    }

    [Fact]
    public async Task ПодделанныйЗаголовокВызывателя_НеДаётЧужихРук()
    {
        _chats.Add("c1");                       // чат хода — без сеанса
        _chats.Add("c2", chatName: "Чат с руками");
        await _hands.StartAsync("u1", "d1", "home", "c2");
        await OnlineAsync();

        // Ход подставляет в заголовок чат, у которого руки есть. Решение принимает токен.
        var response = await Controller(Token("c1"), spoofedCallerHeader: "c2")
            .Call(new DesktopAgentCallRequest(null, DesktopCallKinds.Act), default);

        Refusal(response).Outcome.Should().Be(DesktopGateOutcomes.NoHandsSession);
        _sender.Calls.Should().BeEmpty("на устройство не должно уйти ничего");
    }

    [Fact]
    public async Task ГраньВыключенаВПроекте_409БезВызоваУстройства()
    {
        _chats.Add("c1", projectFacet: false);
        await OnlineAsync();

        var response = await Controller(Token("c1")).Call(new DesktopAgentCallRequest(null, DesktopCallKinds.Screen), default);

        Refusal(response).Outcome.Should().Be(DesktopGateOutcomes.FacetOff);
        _sender.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ЧужоеУстройствоВАргументе_409DeviceMismatch()
    {
        _chats.Add("c1");
        await _hands.StartAsync("u1", "d1", "home", "c1");
        await OnlineAsync();

        var response = await Controller(Token("c1")).Call(new DesktopAgentCallRequest("work", DesktopCallKinds.Ui), default);

        Refusal(response).Outcome.Should().Be(DesktopGateOutcomes.DeviceMismatch);
    }

    [Fact]
    public async Task РазрешённыйВызов_УезжаетНаУстройствоИВозвращаетИсход()
    {
        _chats.Add("c1");
        await _hands.StartAsync("u1", "d1", "home", "c1");
        await OnlineAsync();

        var response = await Controller(Token("c1"))
            .Call(new DesktopAgentCallRequest("home", DesktopCallKinds.Screen), default);

        var result = response.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<DesktopCallResult>().Subject;
        result.Outcome.Should().Be(DesktopOutcomes.Ok);
        result.LastAppliedStep.Should().Be(1);

        var command = _sender.Calls.Should().ContainSingle().Subject;
        command.SessionId.Should().Be("c1");
        // Кадр внутри сеанса уходит без отдельного нажатия, а действие — только с ним.
        command.RequiresConfirmation.Should().BeFalse();
    }

    [Fact]
    public async Task Действие_ТребуетПодтвержденияЧеловека()
    {
        _chats.Add("c1");
        await _hands.StartAsync("u1", "d1", "home", "c1");
        await OnlineAsync();
        var controller = Controller(Token("c1"));

        // Устройство подтверждение не спрашивает, а сразу подтверждает — нас интересует флаг команды.
        _ = controller.Call(new DesktopAgentCallRequest("home", DesktopCallKinds.Act,
            JsonDocument.Parse("""{"steps":[]}""").RootElement), default);
        await WaitForCallAsync();

        _sender.Calls.Single().RequiresConfirmation.Should().BeTrue();
    }

    [Fact]
    public async Task НеизвестныйВидВызова_400()
    {
        _chats.Add("c1");

        var response = await Controller(Token("c1")).Call(new DesktopAgentCallRequest(null, "делай красиво"), default);

        response.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void БезCapabilityТокена_401()
    {
        Controller(user: null).List().Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Список_ОтдаётИменаУстройствИСтатусРук()
    {
        _chats.Add("c1");
        await _hands.StartAsync("u1", "d1", "home", "c1");

        var response = Controller(Token("c1")).List().Should().BeOfType<OkObjectResult>().Subject.Value!;

        var json = JsonSerializer.Serialize(response);
        json.Should().Contain("home").And.Contain("work");
        json.Should().Contain("\"handsHere\":true").And.Contain("\"device\":\"home\"");
    }

    // Команда уходит из фонового вызова — ждём событие, а не спим (CI слабее машины разработчика).
    private async Task WaitForCallAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_sender.Calls.Count == 0 && DateTime.UtcNow < deadline) await Task.Yield();
        _sender.Calls.Should().NotBeEmpty();
    }
}
