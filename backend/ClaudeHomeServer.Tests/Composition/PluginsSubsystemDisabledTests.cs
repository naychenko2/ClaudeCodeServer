using System.Security.Claims;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Plugins.Controllers;
using ClaudeHomeServer.Services.Plugins.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeHomeServer.Tests.Composition;

// Дефект 40f17e1b: при `Subsystems:plugins:Enabled=false`
//   1) REST плагинов отдавал 500 (активация контроллера падала: PluginStore/PluginInstaller/…
//      — необязательные параметры конструктора без `= null`, DI их не резолвил) вместо честного 503;
//   2) MCP-сервер plugins исчезал из набора (тулсет регистрировался только в Register подсистемы,
//      а та не вызывается при выключении) → состав tools/list менялся, POST /mcp/plugins/{s} → 404.
// Фикс: `= null` на параметрах контроллеров (активация проходит, гейт 503 срабатает) и
// неусловная регистрация PluginsToolset в Main (сервер в наборе, вызов — честный отказ).
public class PluginsSubsystemDisabledTests
{
    private const string Owner = "owner-a";

    // Контейнер как в бою: реальный AddSubsystems (подсистема выключена → Register не вызван,
    // PluginStore/PluginDraftService/… в контейнер не попали) + IConfiguration/IFeatureFlagGate
    // (спина, всегда) + контроллеры (DI их активирует, как MVC). In-memory-конфиг не требует
    // disposal, поэтому хелпер возвращает только провайдер.
    private static ServiceProvider BuildContainer(bool subsystemEnabled)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Subsystems:plugins:Enabled"] = subsystemEnabled ? "true" : "false",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IFeatureFlagGate>(new StubFlagGate());
        services.AddSubsystems(config, new PluginsSubsystem());
        services.AddTransient<PluginsController>();
        services.AddTransient<PluginDraftsController>();
        services.AddTransient<PluginBridgeController>();
        return services.BuildServiceProvider();
    }

    private static void SetUser(ControllerBase ctl, string userId) =>
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)], "test")),
            },
        };

    // ── REST: выключенная подсистема → 503 (не 500 от активации) ─────────────────

    [Fact]
    public void ПодсистемаВыключена_Rest_503_Не500()
    {
        using var sp = BuildContainer(subsystemEnabled: false);

        // Ключевой: активация НЕ бросает (до фикса — 500 «Unable to resolve service for type PluginStore»).
        var ctl = sp.GetRequiredService<PluginsController>();
        SetUser(ctl, Owner);
        var list = (ObjectResult)ctl.List()!;
        list.StatusCode.Should().Be(503, "подсистема выключена → честный 503 с причиной, не 500");
        list.Value!.ToString()!.Should().Contain("выключена");

        var draftsCtl = sp.GetRequiredService<PluginDraftsController>();
        SetUser(draftsCtl, Owner);
        var draftsList = (ObjectResult)draftsCtl.List()!;
        draftsList.StatusCode.Should().Be(503, "GET /api/plugins/drafts — тот же честный 503");

        // Мост: обязательный параметр от отключаемой вертикали (PluginStore/PluginBridge) уронил бы
        // активацию (500). Регрессию ловит именно активация контроллера из контейнера.
        var bridgeCtl = sp.GetRequiredService<PluginBridgeController>();
        SetUser(bridgeCtl, Owner);
        var bridgeAssets = (ObjectResult)bridgeCtl.Assets("local-a~pkg", "1.0.0", "panel/index.html");
        bridgeAssets.StatusCode.Should().Be(503, "мост плагинов при выключенной подсистеме — честный 503");
    }

    // Контроль: включённая подсистема — тот же контроллер активируется и 503 НЕ отдаёт
    // (PluginStore на месте, гейт проходит).
    [Fact]
    public void ПодсистемаВключена_Rest_Не503()
    {
        using var sp = BuildContainer(subsystemEnabled: true);
        var ctl = sp.GetRequiredService<PluginsController>();
        SetUser(ctl, Owner);
        // Флаг plugins-mvp включён (StubFlagGate) → гейт проходит, Ok (не 503).
        ctl.List().Should().BeOfType<OkObjectResult>("подсистема включена, флаг on → Ok, не 503");
    }

    // ── MCP: сервер plugins остаётся в наборе (состав tools/list не меняется) ─────
    //
    // Тулсет регистрируется в Main НЕусловно (фикс): при выключенной подсистеме
    // PluginDraftService в контейнер не попадает, и тулсет строится с drafts = null.
    // Состав (5 инструментов) и вызов (честный отказ) обязаны работать и в этом состоянии.

    private static PluginsToolset BuildToolsetWithNullDrafts()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Subsystems:plugins:Enabled"] = "false",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IFeatureFlagGate>(new StubFlagGate());
        services.AddSubsystems(config, new PluginsSubsystem());
        // Неусловная регистрация (Main): тулсет в контейнере, даже когда Register не вызван.
        services.AddSingleton<IMcpToolset, PluginsToolset>();
        // Зависимости PluginsToolset (спина) — стабы. Сессия-вызыватель — валидная: TryResolve
        // обязан пройти (иначе «Чат не найден» сработает ДО гейта подсистемы и тест не проверит его).
        var sessions = new Mock<IMcpSessionAccessor>();
        sessions.Setup(a => a.GetOwned(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new Session { Id = "chat-1", OwnerId = Owner });
        services.AddSingleton<IMcpSessionAccessor>(sessions.Object);
        services.AddSingleton<IProjectManager>(new Mock<IProjectManager>().Object);

        using var sp = services.BuildServiceProvider();
        return sp.GetServices<IMcpToolset>().Single(t => t is PluginsToolset) as PluginsToolset;
    }

    [Fact]
    public void ПодсистемаВыключена_ТулсетВНаборе_ПятьИнструментов()
    {
        var toolset = BuildToolsetWithNullDrafts();
        toolset.Should().NotBeNull("MCP-тулсет plugins обязан остаться в наборе при выключенной подсистеме");

        // Состав (5 инструментов) не зависит от состояния подсистемы — инвариант McpToolsetStabilityTests.
        var names = toolset!.ToolsFor(new McpToolCallContext(Owner, "chat-1", "chat-1"))
            .Select(t => t.Name).ToList();
        names.Should().BeEquivalentTo([
            PluginsToolset.ToolCreate, PluginsToolset.ToolWriteFile, PluginsToolset.ToolValidate,
            PluginsToolset.ToolPreview, PluginsToolset.ToolDeleteFile]);
    }

    // Вызов инструмента при выключенной подсистеме — честный отказ с причиной (не пропавший сервер).
    [Fact]
    public async Task ПодсистемаВыключена_ВызовИнструмента_ОтказСПричиной()
    {
        var toolset = BuildToolsetWithNullDrafts();

        var result = await toolset!.CallAsync(PluginsToolset.ToolCreate,
            new JsonObject { ["slug"] = "my-plugin" },
            new McpToolCallContext(Owner, "chat-1", "chat-1"), default);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Подсистема плагинов выключена", "честный отказ, не 404 «нет сервера»");
    }

    // IFeatureFlagGate: оба флага вкл (тест не про гейты, а про состояние подсистемы).
    private sealed class StubFlagGate : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => true;
    }
}
