using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Guards;

/// <summary>
/// Сторож И1 для MCP (KM-B3a, ADR-041 п.9–10): каждый инструмент каждого тулсета реестра
/// проверяется детским allow-list; вызов вне списка от детского владельца — отказ, а для
/// обычного владельца гейт прозрачен. Расширение allow-list — осознанная правка этого теста
/// (замороженный список ниже).
/// </summary>
[Trait("Category", "Host")]
public class ChildModeMcpGuardTests : IClassFixture<ChildModeMcpGuardTests.Hosts>
{
    // Хост на КЛАСС (храповик «хост на класс»): тесты хост не мутируют, детская политика и гейт строятся
    // локально поверх его DI, поэтому порядок тестов не важен
    public sealed class Hosts : TestWebApplicationFactory;

    private readonly Hosts _host;

    public ChildModeMcpGuardTests(Hosts host) => _host = host;

    // Заморожено: в волне 1 allow-list ПУСТ. Студия волны 2 добавит сюда пары осознанной правкой.
    private static readonly (string Server, string Tool)[] FrozenAllowList = [];

    private sealed class FakePolicy(ChildPolicySnapshot? snapshot) : IChildModePolicy
    {
        public ChildPolicySnapshot? For(string ownerId) => snapshot;
    }

    private static ChildModeGate Gate(bool child, bool denyAll = false) =>
        new(new FakePolicy(child
            ? new ChildPolicySnapshot("kid", denyAll ? null : new ChildProfile { Enabled = true }, denyAll)
            : null));

    [Fact]
    public void AllowList_ЗаморожениеСовпадаетСКодом()
    {
        ChildModeAllowList.Pairs.OrderBy(p => p).Should().Equal(FrozenAllowList.OrderBy(p => p));
    }

    [Fact]
    public void КаждыйИнструментКаждогоТулсетаРеестра_РебёнкуОтказ_ЕслиВнеAllowList()
    {
        _ = _host.CreateClient();
        var live = ClaudeHomeServer.Tests.Services.ActorRightToolCatalogTests.LiveTools(_host.Services);
        live.Select(x => x.Server).Distinct().Count().Should().BeGreaterThan(15, "сверка не должна быть вакуумной");
        _host.Services.GetRequiredService<IChildModeGate>().Should().BeOfType<ChildModeGate>("гейт обязан быть в DI");

        var kid = Gate(child: true);
        var adult = Gate(child: false);
        foreach (var (server, tool) in live)
        {
            var allowed = FrozenAllowList.Contains((server, tool));
            (kid.Denied(server, tool, "kid") is null).Should().Be(allowed, $"{server}.{tool}: ребёнку только allow-list");
            adult.Denied(server, tool, "adult").Should().BeNull($"{server}.{tool}: обычному владельцу гейт прозрачен");
        }
    }

    [Fact]
    public void ВебПоискВнеAllowList_ОтказРебёнку()
    {
        var kid = Gate(child: true);
        kid.Denied("websearch", "web_search", "kid").Should().NotBeNull();
        kid.Denied("websearch", "web_read", "kid").Should().NotBeNull();
        kid.AllowsServer("kid", "websearch").Should().BeFalse();
    }

    [Fact]
    public void ПрофильDenyAll_ОтказываетВсё_ДажеПарыИзAllowList()
    {
        var broken = Gate(child: true, denyAll: true);
        broken.Denied("wsp", "files_read", "kid").Should().NotBeNull();
        broken.AllowsServer("kid", "any").Should().BeFalse();
        broken.IsChild("kid").Should().BeTrue("нечитаемый профиль остаётся ребёнком");
    }

    [Fact]
    public void ОбычныйВладелец_СерверыОбъявляютсяКакПрежде()
    {
        var adult = Gate(child: false);
        adult.AllowsServer("adult", "websearch").Should().BeTrue();
        adult.IsChild("adult").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ПустойВладелец_ОтказываетСамГейт_ДажеЕслиПолитикаСчитаетНеРебёнком(string? owner)
    {
        var gate = Gate(child: false);
        gate.Denied("wsp", "files_read", owner).Should().NotBeNull();
        gate.AllowsServer(owner, "wsp").Should().BeFalse();
    }

    // R-KM-B3b: чат без владельца (пустой или null ownerId) — не детский, IsChild и PolicyFor
    // отвечают одинаково. Проверка на рабочей детской политике: FakePolicy отдаёт снимок на любой
    // id, поэтому утверждение ровно о том, что правило держит гейт, а не политика.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ПустойВладелец_НеРебёнок_IsChildИPolicyForОтвечаютОдинаково(string? owner)
    {
        var gate = Gate(child: true);
        gate.IsChild(owner).Should().BeFalse("чат без владельца — не детский");
        gate.PolicyFor(owner!).Should().BeNull("тот же ответ, что у IsChild: снимка нет");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ПустойВладелец_СверкаПриЗапускеНеБросает_ЧатБезВладельцаНеДетский(string? owner)
    {
        Gate(child: false).LaunchMismatch(owner, new Session()).Should().BeNull();
    }

    private sealed class ChildHost : TestWebApplicationFactory
    {
        public ChildHost() => ExtraServices = services =>
            services.AddSingleton<IChildModePolicy>(new FakePolicy(
                new ChildPolicySnapshot("kid", new ChildProfile { Enabled = true }, DenyAll: false)));
    }

    // Третий гейт транспорта (п.9): контроллер вызывается напрямую, минуя ChildModeGateMiddleware
    // (оно само закрывает /mcp ребёнку — здесь проверяется глубокая оборона). Вызов инструмента от
    // детского владельца отказывается content-ошибкой ДО выполнения тулсета.
    private async Task<System.Text.Json.JsonElement> CallAsync(bool child)
    {
        var sp = _host.Services;
        var controller = new ClaudeHomeServer.Controllers.McpTransportController(
            sp.GetRequiredService<McpToolsetRegistry>(), sp.GetRequiredService<McpToolWhitelist>(),
            sp.GetRequiredService<ClaudeHomeServer.Services.Access.IActorRightGate>(),
            Gate(child), sp.GetRequiredService<ISessionDirectory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeHomeServer.Controllers.McpTransportController>.Instance);
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("sub", "kid")], "test"));
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"widget_show","arguments":{"html":"<b>x</b>"}}}""";
        ctx.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = ctx };
        var result = await controller.Handle("widgets", null, CancellationToken.None);
        var content = result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ContentResult>().Subject;
        return System.Text.Json.JsonDocument.Parse(content.Content!).RootElement.Clone();
    }

    [Fact]
    public async Task ТранспортMcp_ВызовОтДетскогоВладельца_ОтказКонтентОшибкой()
    {
        _ = _host.CreateClient();
        var result = (await CallAsync(child: true)).GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString().Should().Contain("детском режиме");
    }

    [Fact]
    public async Task ТранспортMcp_ВызовОтОбычногоВладельца_ГейтПрозрачен()
    {
        _ = _host.CreateClient();
        var result = (await CallAsync(child: false)).GetProperty("result");
        (result.TryGetProperty("isError", out var e) && e.GetBoolean()
            && result.GetProperty("content")[0].GetProperty("text").GetString()!.Contains("детском режиме"))
            .Should().BeFalse();
    }
}
