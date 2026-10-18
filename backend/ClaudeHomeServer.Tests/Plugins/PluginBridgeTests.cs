using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using PluginBridge = ClaudeHomeServer.Services.Plugins.PluginBridge;

namespace ClaudeHomeServer.Tests.Plugins;

// Мост панели v1 (ADR-031 §3, шаг 3.1): диспетчер IPluginCapability + валидация
// сообщений моста. Тесты:
//   - диспетчер: нет обработчика → unavailable (503), нет права → denied (403),
//     есть право → вызывает обработчик;
//   - валидация: source=ccs-plugin + v=1 → валидно; чужой source → невалидно;
//     другая версия → невалидно;
//   - мутации (критерий): ЧужойSource_Прошёл, ДругаяВерсия_Прошёл.
public class PluginBridgeTests
{
    // --- Диспетчер: нет обработчика → unavailable (503) ---

    [Fact]
    public async Task Dispatch_НетОбработчика_ОтдаётUnavailable()
    {
        var bridge = new PluginBridge([]);
        var ctx = new PluginCallContext("owner-a", "local-a/pkg", null, null);
        var result = await bridge.DispatchAsync(ctx, "notes.read", null, [], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("unavailable");
        result.Error.Message.Should().Contain("notes");
    }

    // --- Диспетчер: нет права в granted → denied (403) ---

    [Fact]
    public async Task Dispatch_НетПрава_ОтдаётDenied()
    {
        var handler = new StubCapability("notes:read");
        var bridge = new PluginBridge([handler]);
        var ctx = new PluginCallContext("owner-a", "local-a/pkg", null, null);
        // granted пуст — право notes:read не выдано.
        var result = await bridge.DispatchAsync(ctx, "notes.read", null, [], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("denied");
        result.Error.Message.Should().Contain("notes:read");
    }

    // --- Диспетчер: есть право → вызывает обработчик ---

    [Fact]
    public async Task Dispatch_ЕстьПрава_ВызываетОбработчик()
    {
        var handler = new StubCapability("notes:read");
        var bridge = new PluginBridge([handler]);
        var ctx = new PluginCallContext("owner-a", "local-a/pkg", null, null);
        var args = JsonDocument.Parse("{\"id\":\"n1\"}").RootElement;
        var result = await bridge.DispatchAsync(ctx, "notes.read", args, ["notes:read"], CancellationToken.None);

        result.Ok.Should().BeTrue();
        handler.CalledWith.Should().Be("notes.read");
    }

    // --- Валидация: корректный frame message → валидно ---

    [Fact]
    public void Валидация_КорректныйFrameMessage_Проходит()
    {
        var msg = new PluginBridgeMessage { Source = "ccs-plugin", V = 1, Type = "ready" };
        PluginBridgeMessage.IsValidFrameMessage(msg).Should().BeTrue();
    }

    // --- Мутация: чужой source → невалидно ---
    // Мутация: разрешаем любой source (IsValidFrameMessage → true) → этот тест падает.

    [Fact]
    public void ЧужойSource_Прошёл()
    {
        var msg = new PluginBridgeMessage { Source = "ccs-evil", V = 1, Type = "ready" };
        PluginBridgeMessage.IsValidFrameMessage(msg).Should().BeFalse();
    }

    // --- Мутация: другая версия → невалидно ---
    // Мутация: разрешаем любую версию (IsValidFrameMessage → true) → этот тест падает.

    [Fact]
    public void ДругаяВерсия_Прошёл()
    {
        var msg = new PluginBridgeMessage { Source = "ccs-plugin", V = 2, Type = "ready" };
        PluginBridgeMessage.IsValidFrameMessage(msg).Should().BeFalse();
    }

    // --- Валидация: корректный host message → валидно ---

    [Fact]
    public void Валидация_КорректныйHostMessage_Проходит()
    {
        var msg = new PluginBridgeMessage { Source = "ccs-host", V = 1, Type = "init" };
        PluginBridgeMessage.IsValidHostMessage(msg).Should().BeTrue();
    }

    // --- Stub IPluginCapability: фиксирует, какой op получил ---

    private sealed class StubCapability : IPluginCapability
    {
        public string Permission { get; }
        public string? CalledWith;

        public StubCapability(string permission) => Permission = permission;

        public Task<PluginCapabilityResult> HandleAsync(
            PluginCallContext context, string op, JsonElement args, IReadOnlyList<string> granted, CancellationToken ct)
        {
            CalledWith = op;
            return Task.FromResult(new PluginCapabilityResult(Ok: true, Data: "ok"));
        }
    }
}
