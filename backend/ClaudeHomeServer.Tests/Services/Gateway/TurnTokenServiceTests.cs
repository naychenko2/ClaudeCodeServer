using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace ClaudeHomeServer.Tests.Services.Gateway;

/// <summary>
/// Токен хода шлюза (ADR-016, сторож G4): живёт ровно столько, сколько ход, — без TTL,
/// только в памяти, отзывается по turn/completed и явными отзывами.
/// </summary>
public class TurnTokenServiceTests
{
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private readonly TurnEventBus _bus = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero));

    private TurnTokenService Create() => new(_bus, _time);

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
        var t = sut.Issue("owner-1", "chat-1");
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

    private static async Task<(bool Passed, object? Result, HttpContext Http)> InvokeFilterAsync(
        TurnTokenEndpointFilter filter, string turnId, string? token)
    {
        var http = new DefaultHttpContext();
        http.Request.RouteValues[TurnTokenEndpointFilter.RouteKey] = turnId;
        if (token is not null) http.Request.Headers[TurnTokenEndpointFilter.HeaderName] = token;
        var passed = false;
        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http),
            _ => { passed = true; return ValueTask.FromResult<object?>(Results.Ok()); });
        return (passed, result, http);
    }
}
