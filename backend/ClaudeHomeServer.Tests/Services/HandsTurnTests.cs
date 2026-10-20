using System.Diagnostics;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Правила хода с руками (ADR-016 §7, план рук Ш4, решения 4 и 7; решение владельца 3б): маркер
// рук, отсев stdio-узлов и режим прав зависят от ОДНОГО решения ClaudeSession.HandsActiveNow.
// Shell, сабагенты и запись в .claude/.mcp.json в ходе с руками не запрещаются (3б).
public class HandsTurnTests
{
    private static readonly ExternalMcpContext External = new(
    [
        new ExternalMcpServer("ext-stdio", "stdio", "node", ["server.js"], new Dictionary<string, string>(),
            null, new Dictionary<string, string>(), AlwaysLoad: false, AuthVersion: 0),
        new ExternalMcpServer("ext-http", "http", null, [], new Dictionary<string, string>(),
            "https://mcp.example.com/mcp", new Dictionary<string, string>(), AlwaysLoad: false, AuthVersion: 0),
    ]);

    [Fact]
    public async Task РукиВключены_ShellНеЗапрещён_МаркерРук_БезStdio()
    {
        using var h = new HandsTurnHarness(true, external: () => External);
        var turn = await h.RunTurnAsync();

        turn.Disallowed.Should().NotContain(["Bash", "PowerShell", "Monitor", "BashOutput", "KillShell", "Task", "Agent",
            "Edit(.claude/**)", "Write(.claude/**)", "Edit(.mcp.json)", "Write(.mcp.json)"],
            "решение владельца 3б: shell в чате с руками разрешён, запреты Ш4 сняты");
        HandsTurnRules.PermissionRefusal(turn.Args).Should().BeNull("агент сверяет режим прав хода с руками");

        var servers = turn.McpServers!;
        var hands = servers[DeviceExecPlaceholders.HandsServerName]!.AsObject();
        ((string?)hands["type"]).Should().Be(DeviceExecPlaceholders.Hands);
        ((bool?)hands[DeviceExecPlaceholders.HandsVisionField]).Should().BeTrue("у родного Claude зрение есть");
        hands.Select(kv => kv.Key).Should().BeEquivalentTo(["type", DeviceExecPlaceholders.HandsVisionField],
            "команду и путь моста подставляет агент, сервер их не шлёт");

        servers.ContainsKey("ext-stdio").Should().BeFalse("внешний stdio-сервер — канал запуска кода на устройстве");
        servers.ContainsKey("ext-http").Should().BeTrue("http-узлы отсев не задевает");
        servers.ContainsKey("widgets").Should().BeTrue();
        servers.Where(kv => kv.Key != DeviceExecPlaceholders.HandsServerName)
            .Should().OnlyContain(kv => (string?)kv.Value!["type"] == "http" || (string?)kv.Value!["type"] == "sse");
    }

    [Fact]
    public async Task РукиВыключены_НабораНет_StdioЕдет_МаркераНет()
    {
        using var h = new HandsTurnHarness(false, external: () => External);
        var turn = await h.RunTurnAsync();

        turn.McpServers!.ContainsKey(DeviceExecPlaceholders.HandsServerName).Should().BeFalse();
        turn.McpServers!.ContainsKey("ext-stdio").Should().BeTrue();
    }

    // Решение владельца 2026-09-27: списка «кому доверяем руки» нет, руки работают у любого
    // провайдера. Раньше DeepSeek без явного доверия шёл без рук со статусом «провайдер не разрешён»
    [Fact]
    public async Task ЛюбойПровайдер_РукиЕсть_СтатусаОтказаНет()
    {
        var providers = new LlmProviderRegistry(Helpers.TestConfig.Build(new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://deepseek.example.com/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v5",
        }));
        using var h = new HandsTurnHarness(true, model: "deepseek-v5", providers: providers);
        var turn = await h.RunTurnAsync();

        turn.McpServers!.ContainsKey(DeviceExecPlaceholders.HandsServerName).Should().BeTrue(
            "руки не зависят от провайдера хода");
        HandsTurnRules.PermissionRefusal(turn.Args).Should().BeNull();
        h.Messages.OfType<HandsStatusMessage>().Should().BeEmpty("отказа по провайдеру больше нет");
    }

    // Агент отказал ходу: руки держит другой ход этой машины — бейдж узнаёт это по коду отказа
    [Fact]
    public async Task ОтказРукиЗаняты_СтатусНедоступныСПричинойBusy()
    {
        using var h = new HandsTurnHarness(true,
            refuse: new DeviceExecRefusedException(DeviceExecRefusal.HandsBusy, HandsMachineLock.BusyText));
        await h.RunTurnAsync();

        h.Messages.OfType<HandsStatusMessage>().Should().ContainSingle()
            .Which.Should().Be(new HandsStatusMessage(HandsChatStates.Unavailable, Reason: HandsEndReason.Busy));
        h.Messages.OfType<ErrorMessage>().Should().ContainSingle().Which.Text.Should().Be(HandsMachineLock.BusyText);
    }

    // Прочие отказы устройства — только ошибка хода; даже текст про занятые руки без кода busy не даёт
    [Theory]
    [InlineData(DeviceExecRefusal.AgentRefused, HandsMachineLock.BusyText)]
    [InlineData(DeviceExecRefusal.Offline, "Устройство не в сети")]
    [InlineData(DeviceExecRefusal.GatewayRefused, "Шлюз не выдал ходу маршрут.")]
    public async Task ПрочийОтказУстройства_БезПризнакаBusy(DeviceExecRefusal reason, string text)
    {
        using var h = new HandsTurnHarness(true,
            refuse: new DeviceExecRefusedException(reason, text));
        await h.RunTurnAsync();

        h.Messages.OfType<ErrorMessage>().Should().ContainSingle();
        h.Messages.OfType<HandsStatusMessage>().Should().NotContain(m => m.Reason == HandsEndReason.Busy);
    }

    [Fact]
    public async Task РукиИРежимБезОграничений_ПонижениеДоAcceptEdits_СтрокаВЛенту()
    {
        using var h = new HandsTurnHarness(true, mode: ClaudeMode.Bypass);
        var first = await h.RunTurnAsync();
        var second = await h.RunTurnAsync();

        first.Args.Should().NotContain("bypassPermissions", "ход с руками в bypassPermissions не идёт никогда");
        first.PermissionMode.Should().Be("acceptEdits");
        HandsTurnRules.PermissionRefusal(first.Args).Should().BeNull("иначе агент откажет ходу с руками");
        second.PermissionMode.Should().Be("acceptEdits");
        h.Messages.OfType<HandsNoticeMessage>().Should().ContainSingle("строка о понижении — одна, а не на каждый ход")
            .Which.Text.Should().Be(HandsTurnRules.BypassDowngradedText);
    }

    [Fact]
    public async Task БезРук_РежимБезОграниченийКакБыл()
    {
        using var h = new HandsTurnHarness(false, mode: ClaudeMode.Bypass);
        var turn = await h.RunTurnAsync();

        turn.PermissionMode.Should().Be("bypassPermissions", "контроль: понижение — только у хода с руками");
        h.Messages.OfType<HandsNoticeMessage>().Should().BeEmpty();
    }

    [Fact]
    public async Task ПровайдерБезЗрения_МаркерБезЗрения()
    {
        var providers = new LlmProviderRegistry(Helpers.TestConfig.Build(new Dictionary<string, string?>
        {
            ["LlmProviders:glm:AnthropicBaseUrl"] = "https://glm.example.com/anthropic",
            ["LlmProviders:glm:ApiKey"] = "sk-test",
            ["LlmProviders:glm:SupportsImages"] = "false",
            ["LlmProviders:glm:Models:0:Id"] = "glm-5",
        }));
        using var h = new HandsTurnHarness(true, model: "glm-5", providers: providers);
        var turn = await h.RunTurnAsync();

        var hands = turn.McpServers![DeviceExecPlaceholders.HandsServerName]!.AsObject();
        ((bool?)hands[DeviceExecPlaceholders.HandsVisionField]).Should().BeFalse(
            "без зрения агент запускает мост без screenshot_control");
    }

    [Fact]
    public void HandsPermissionMode_BypassПонижается_ОстальноеКакЕсть()
    {
        foreach (var mode in Enum.GetValues<ClaudeMode>())
            ClaudeSession.HandsPermissionMode(mode).Should().Be(mode == ClaudeMode.Bypass ? ClaudeMode.AcceptEdits : mode);
    }
}
