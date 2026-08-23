using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Controllers;

// Приём результата вызова десктопного агента (ADR-008, «Протокол канала»): владелец и
// устройство берутся из claims токена устройства, сверяются с записью вызова; приём
// одноразовый — 409 ТОЛЬКО на дубль, поздний и частичный результат принимается; «забрать
// результат по callId» — путь реконнекта. Контроллер зовём напрямую: схема авторизации и
// маршрут живут в проводке.
public class DeviceCallsControllerTests
{
    private const string Owner = "owner-1";
    private const string Device = "device-1";
    private const string Conn = "conn-1";

    private sealed class CapturingSender : IDeviceCommandSender
    {
        public readonly TaskCompletionSource<DesktopCallCommand> Sent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendCallAsync(string connectionId, DesktopCallCommand command, CancellationToken ct = default)
        {
            Sent.TrySetResult(command);
            return Task.CompletedTask;
        }

        public Task SendGoAsync(string connectionId, DesktopGoCommand go, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCancelAsync(string connectionId, DesktopCancelCommand cancel, CancellationToken ct = default) => Task.CompletedTask;
    }

    // Часы стоят: дедлайны фаз проверяет DesktopCallRouterTests, здесь важен только приём тела.
    private sealed class FrozenTime : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new NeverTimer();

        private sealed class NeverTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static DeviceCallsController NewController(DesktopCallRouter router, string? ownerId = Owner, string? deviceId = Device)
    {
        var claims = new List<Claim>();
        if (ownerId is not null) claims.Add(new Claim(DesktopProtocol.OwnerIdClaim, ownerId));
        if (deviceId is not null) claims.Add(new Claim(DesktopProtocol.DeviceIdClaim, deviceId));

        return new DeviceCallsController(router)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, DesktopProtocol.DeviceTokenScheme))
                }
            }
        };
    }

    // Поднять вызов «в полёте» и вернуть его callId: команда уже ушла в соединение,
    // результат ещё не приезжал.
    private static async Task<(DesktopCallRouter Router, string CallId, Task<DesktopCallResult> Invoke)> InFlightAsync()
    {
        var sender = new CapturingSender();
        var router = new DesktopCallRouter(sender, [], NullLogger<DesktopCallRouter>.Instance, new FrozenTime());
        router.RegisterConnection(Conn, Owner, Device);
        await router.HelloAsync(Conn, new DeviceHello(DesktopProtocol.Version, ["click"], "1.0.0"));

        var invoke = router.InvokeAsync(new DesktopCallRequest(
            Owner, Device, "chat-1", DesktopCallKinds.Act, RequiresConfirmation: false, DeviceName: "home"));
        var command = await sender.Sent.Task;
        router.Ack(command.CallId, Conn);
        return (router, command.CallId, invoke);
    }

    private static DeviceCallResultRequest Result(string outcome = DesktopOutcomes.Ok, int? step = 1, bool partial = false) =>
        new(outcome, step, "готово", partial);

    [Fact]
    public async Task Результат_ПринимаетсяОдинРазАПовтор409()
    {
        var (router, callId, invoke) = await InFlightAsync();
        var controller = NewController(router);

        controller.PostResult(callId, Result()).Should().BeOfType<OkObjectResult>();
        controller.PostResult(callId, Result()).Should().BeOfType<ConflictObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);

        (await invoke).LastAppliedStep.Should().Be(1);
    }

    [Fact]
    public async Task ЧастичныйРезультатБезИндексаШага_Принимается()
    {
        var (router, callId, invoke) = await InFlightAsync();
        var controller = NewController(router);

        controller.PostResult(callId, Result(DesktopOutcomes.Unknown, step: null, partial: true))
            .Should().BeOfType<OkObjectResult>();

        var result = await invoke;
        result.Partial.Should().BeTrue();
        result.LastAppliedStep.Should().Be(-1, "устройство индекс не прислало — честное «неизвестно»");
    }

    [Fact]
    public async Task ЧужаяПараВладелецУстройство_403()
    {
        var (router, callId, _) = await InFlightAsync();

        NewController(router, ownerId: "owner-2").PostResult(callId, Result())
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        NewController(router, deviceId: "device-2").PostResult(callId, Result())
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task ТокенБезПарыВладелецУстройство_403()
    {
        var (router, callId, _) = await InFlightAsync();

        NewController(router, deviceId: null).PostResult(callId, Result())
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        NewController(router, deviceId: null).GetResult(callId)
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task НеизвестныйCallId_404()
    {
        var (router, _, _) = await InFlightAsync();
        var controller = NewController(router);

        controller.PostResult("нет-такого", Result()).Should().BeOfType<NotFoundObjectResult>();
        controller.GetResult("нет-такого").Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task ПустойИсход_400()
    {
        var (router, callId, _) = await InFlightAsync();

        NewController(router).PostResult(callId, new DeviceCallResultRequest("", 0))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ЗабратьРезультатПоCallId_ДоПриёмаПустоПослеПриёмаОтдаётся()
    {
        var (router, callId, invoke) = await InFlightAsync();
        var controller = NewController(router);

        controller.GetResult(callId).Should().BeOfType<NoContentResult>("результата ещё нет");

        var payload = JsonDocument.Parse("""{"frames":1}""").RootElement;
        controller.PostResult(callId, new DeviceCallResultRequest(DesktopOutcomes.Ok, 2, "готово", false, payload))
            .Should().BeOfType<OkObjectResult>();

        var stored = controller.GetResult(callId).Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<DesktopCallResult>().Subject;
        stored.CallId.Should().Be(callId);
        stored.LastAppliedStep.Should().Be(2);
        stored.Payload!.Value.GetProperty("frames").GetInt32().Should().Be(1);

        (await invoke).Outcome.Should().Be(DesktopOutcomes.Ok);
    }

    [Fact]
    public void ПотолокТелаРезультата_8МБНаЭндпоинте()
    {
        var attribute = typeof(DeviceCallsController)
            .GetMethod(nameof(DeviceCallsController.PostResult))!
            .GetCustomAttributes(typeof(RequestSizeLimitAttribute), false)
            .Should().ContainSingle().Subject;

        // 8 МБ — потолок транспорта: результат везёт кадр и снапшот мимо 32-КБ лимита хаба
        DesktopProtocol.MaxResultBytes.Should().Be(8 * 1024 * 1024);
        attribute.Should().NotBeNull();
    }
}
