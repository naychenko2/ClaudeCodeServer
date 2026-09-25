using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using ClaudeHomeServer.Services.Desktop;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services.Gateway;

/// <summary>
/// Токен хода шлюза (ADR-016, сторож G4): живёт ровно столько, сколько ход, — без TTL,
/// только в памяти, отзывается по turn/completed и явными отзывами.
/// </summary>
public sealed class TurnTokenServiceTests : IDisposable
{
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private readonly TurnEventBus _bus = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero));

    private readonly GatewayTestDevice _device = new();
    private ServiceProvider? _services;

    private TurnTokenService Create() => new(_bus, _time);

    public void Dispose()
    {
        _services?.Dispose();
        _device.Dispose();
    }

    private Task CompleteTurnAsync(string sessionId, string outcome = "success") =>
        _bus.PublishAsync(new TurnCompleted(new TurnContext(sessionId, "owner-1", 1, 0), outcome));

    [Fact]
    public void Выдача_ПривязкаКВладельцуЧатуХодуИУстройству()
    {
        var sut = Create();

        var issued = sut.Issue("owner-1", "chat-1", "device-1");

        issued.Grant.OwnerId.Should().Be("owner-1");
        issued.Grant.SessionId.Should().Be("chat-1");
        issued.Grant.DeviceId.Should().Be("device-1");
        issued.Grant.TurnId.Should().NotBeNullOrEmpty();
        sut.Validate(issued.Grant.TurnId, issued.Token, "device-1").Should().Be(issued.Grant);
    }

    [Fact]
    public void Проверка_ЧужойСекретЧужойХодЧужоеУстройство_Отказ()
    {
        var sut = Create();
        var a = sut.Issue("owner-1", "chat-1", "device-1");
        var b = sut.Issue("owner-1", "chat-2");

        sut.Validate(a.Grant.TurnId, "выдуманный").Should().BeNull();
        sut.Validate(a.Grant.TurnId, b.Token, "device-1").Should().BeNull("секрет другого хода");
        sut.Validate(b.Grant.TurnId, a.Token).Should().BeNull();
        sut.Validate(a.Grant.TurnId, a.Token, "device-2").Should().BeNull("ход привязан к другому устройству");
        sut.Validate(a.Grant.TurnId, a.Token).Should().BeNull("устройство не предъявлено");
        sut.Validate(null, a.Token).Should().BeNull();
        sut.Validate(a.Grant.TurnId, null).Should().BeNull();
        sut.Validate(b.Grant.TurnId, b.Token).Should().NotBeNull("ход без устройства проверку устройства не делает");
    }

    [Fact]
    public async Task КонецХода_ОтзываетТокенЧата_АСоседнийНеТрогает()
    {
        // G4: отзыв по turn/completed — подписка на шину, а не явный вызов
        var sut = Create();
        var mine = sut.Issue("owner-1", "chat-1");
        var other = sut.Issue("owner-1", "chat-2");

        await CompleteTurnAsync("chat-1");

        sut.Validate(mine.Grant.TurnId, mine.Token).Should().BeNull();
        sut.Validate(other.Grant.TurnId, other.Token).Should().NotBeNull("ход другого чата не кончился");
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failed")]
    [InlineData("interrupted")]
    [InlineData("cancelled")]
    [InlineData("crashed")]
    [InlineData("egress_down")]
    public async Task КонецХода_ЛюбойИсход_Отзывает(string outcome)
    {
        var sut = Create();
        var t = sut.Issue("owner-1", "chat-1");

        await CompleteTurnAsync("chat-1", outcome);

        sut.Validate(t.Grant.TurnId, t.Token).Should().BeNull();
    }

    // ADR-016 §2: токен хода на устройстве живёт, пока жив процесс CLI, — а процесс
    // обслуживает много ходов
    [Fact]
    public async Task ТокенПроцесса_КонецХодаНеОтзывает_ОтзывПоКонцуПроцессаИУдалениюЧата()
    {
        var sut = Create();
        var proc = sut.Issue("owner-1", "chat-1", "device-1", lifetime: TurnTokenLifetime.Process);
        var turn = sut.Issue("owner-1", "chat-1");

        await CompleteTurnAsync("chat-1");
        await CompleteTurnAsync("chat-1", "interrupted");

        sut.Validate(proc.Grant.TurnId, proc.Token, "device-1").Should().NotBeNull("второй ход того же процесса");
        sut.Validate(turn.Grant.TurnId, turn.Token).Should().BeNull("токен хода по-прежнему гаснет с концом хода");

        sut.RevokeTurn(proc.Grant.TurnId).Should().BeTrue("выход, kill или сбой запуска процесса");
        sut.Validate(proc.Grant.TurnId, proc.Token, "device-1").Should().BeNull();

        var deleted = sut.Issue("owner-1", "chat-2", "device-1", lifetime: TurnTokenLifetime.Process);
        sut.RevokeSession("chat-2").Should().Be(1);
        sut.Validate(deleted.Grant.TurnId, deleted.Token, "device-1").Should().BeNull("чат удалён");
    }

    [Fact]
    public void ТокенПроцесса_ПотолокЖизниСнимает()
    {
        var sut = Create();
        var t = sut.Issue("owner-1", "chat-1", "device-1", lifetime: TurnTokenLifetime.Process);

        _time.Advance(TurnTokenService.DefaultMaxLifetime);

        sut.Validate(t.Grant.TurnId, t.Token, "device-1").Should().BeNull("страховочный потолок действует и на токен процесса");
    }

    [Fact]
    public void ХодДольшеШестиЧасов_ТокенЖив_ПотолокСнимает()
    {
        var sut = Create();
        var t = sut.Issue("owner-1", "chat-1");

        _time.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        sut.Validate(t.Grant.TurnId, t.Token).Should().NotBeNull("TTL нет: живой ход токен не теряет");

        _time.Advance(TimeSpan.FromHours(17));
        sut.Validate(t.Grant.TurnId, t.Token).Should().NotBeNull();

        _time.Advance(TimeSpan.FromHours(1));
        sut.Validate(t.Grant.TurnId, t.Token).Should().BeNull("страховочный потолок 24 ч на потерянное событие");
        sut.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void ЯвныеОтзывы_ХодЧатВсё()
    {
        var sut = Create();
        var launchFailed = sut.Issue("owner-1", "chat-1");
        var deleted1 = sut.Issue("owner-1", "chat-2");
        var deleted2 = sut.Issue("owner-1", "chat-2");
        var rest = sut.Issue("owner-2", "chat-3");

        sut.RevokeTurn(launchFailed.Grant.TurnId).Should().BeTrue();
        sut.Validate(launchFailed.Grant.TurnId, launchFailed.Token).Should().BeNull("ошибка запуска");

        sut.RevokeSession("chat-2").Should().Be(2);
        sut.Validate(deleted1.Grant.TurnId, deleted1.Token).Should().BeNull("чат удалён");
        sut.Validate(deleted2.Grant.TurnId, deleted2.Token).Should().BeNull();
        sut.Validate(rest.Grant.TurnId, rest.Token).Should().NotBeNull();

        sut.RevokeAll().Should().Be(1);
        sut.Validate(rest.Grant.TurnId, rest.Token).Should().BeNull();
    }

    [Fact]
    public void Рестарт_НовыйЭкземпляр_СтарыйТокенНеПринимает()
    {
        var before = Create();
        var t = before.Issue("owner-1", "chat-1");

        var after = new TurnTokenService(new TurnEventBus(), _time);

        after.Validate(t.Grant.TurnId, t.Token).Should().BeNull("токены живут только в памяти процесса");
    }

    [Fact]
    public async Task ФильтрШлюза_ДоКонцаХодаПропускает_ПослеОтзыва401()
    {
        var sut = Create();
        var t = sut.Issue("owner-1", "chat-1", _device.Id);
        var filter = new TurnTokenEndpointFilter(sut);

        var (passed, result, http) = await InvokeFilterAsync(filter, t.Grant.TurnId, t.Token);
        passed.Should().BeTrue();
        http.Items[typeof(TurnTokenGrant)].Should().Be(t.Grant);

        await CompleteTurnAsync("chat-1");

        (passed, result, _) = await InvokeFilterAsync(filter, t.Grant.TurnId, t.Token);
        passed.Should().BeFalse();
        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);

        (passed, result, _) = await InvokeFilterAsync(filter, t.Grant.TurnId, null);
        passed.Should().BeFalse();
        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task ФильтрШлюза_ЧужоеИНепредъявленноеУстройство_ТокенБезУстройства_401()
    {
        var sut = Create();
        var filter = new TurnTokenEndpointFilter(sut);
        var mine = sut.Issue("owner-1", "chat-1", _device.Id);
        var (_, otherToken) = _device.Register("owner-1", "чужое", new string('a', 64));
        var unbound = sut.Issue("owner-1", "chat-1");

        (await InvokeFilterAsync(filter, mine.Grant.TurnId, mine.Token)).Passed.Should().BeTrue();
        (await InvokeFilterAsync(filter, mine.Grant.TurnId, mine.Token, device: (otherToken, new string('a', 64))))
            .Passed.Should().BeFalse("токен привязан к другому устройству");
        (await InvokeFilterAsync(filter, mine.Grant.TurnId, mine.Token, device: (null, null)))
            .Passed.Should().BeFalse("учётка устройства не предъявлена");
        (await InvokeFilterAsync(filter, unbound.Grant.TurnId, unbound.Token))
            .Passed.Should().BeFalse("токен без привязки к устройству");
    }

    // device: null — учётка тестового устройства; (null, null) — без учётки вовсе
    private async Task<(bool Passed, object? Result, HttpContext Http)> InvokeFilterAsync(
        TurnTokenEndpointFilter filter, string turnId, string? token, (string? Token, string? Fingerprint)? device = null)
    {
        if (_services is null)
        {
            var services = new ServiceCollection().AddLogging();
            _device.AddTo(services);
            _services = services.BuildServiceProvider();
        }
        // Скоуп на запрос, как в ASP.NET: обработчик схемы живёт в скоупе и помнит свой HttpContext
        await using var scope = _services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.RouteValues[TurnTokenEndpointFilter.RouteKey] = turnId;
        if (token is not null) http.Request.Headers[TurnTokenEndpointFilter.HeaderName] = token;
        var (deviceToken, fingerprint) = device ?? (_device.Token, GatewayTestDevice.Fingerprint);
        if (deviceToken is not null) http.Request.Headers.Authorization = DesktopDeviceAuthHandler.TokenPrefix + deviceToken;
        if (fingerprint is not null) http.Request.Headers[TurnTokenEndpointFilter.DeviceFingerprintHeader] = fingerprint;
        var passed = false;
        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http),
            _ => { passed = true; return ValueTask.FromResult<object?>(Results.Ok()); });
        return (passed, result, http);
    }
}
