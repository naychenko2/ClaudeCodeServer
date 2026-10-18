using System.Text.Json;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Plugins;

// Контракт шва IPluginCapability (ADR-031 §2.4, M8): PluginCallContext несёт владельца
// (OwnerId) и id плагина (PluginId) из JWT + контекст слота (ProjectId/ObjectId);
// аргументы операции — JsonElement (сырой JSON из моста), не object?. Реализации
// Notes/Tasks — в отдельной задаче; здесь фиксируем сам контракт.
public class PluginCapabilityTests
{
    // Мутация (критерий): убираем OwnerId из PluginCallContext → этот тест падает
    // (конструкция/поле OwnerId исчезают, stub не получает владельца).
    [Fact]
    public void IPluginCapability_ПередаётOwner()
    {
        var ctx = new PluginCallContext(
            OwnerId: "owner-a",
            PluginId: "local-a/pkg",
            ProjectId: "p1",
            ObjectId: null);

        var handler = new StubCapability();
        var args = JsonDocument.Parse("{}").RootElement;
        _ = handler.HandleAsync(ctx, "notes.read", args, ["notes:read"], CancellationToken.None).GetAwaiter().GetResult();

        handler.ReceivedOwner.Should().Be("owner-a", "контекст передаёт владельца (M8)");
        handler.ReceivedPluginId.Should().Be("local-a/pkg", "контекст передаёт id плагина (M8)");
    }

    // Stub IPluginCapability: фиксирует, что получил из контекста.
    private sealed class StubCapability : IPluginCapability
    {
        public string Permission => "notes:read";
        public string? ReceivedOwner;
        public string? ReceivedPluginId;

        public Task<PluginCapabilityResult> HandleAsync(
            PluginCallContext context, string op, JsonElement args, IReadOnlyList<string> granted, CancellationToken ct)
        {
            ReceivedOwner = context.OwnerId;
            ReceivedPluginId = context.PluginId;
            return Task.FromResult(new PluginCapabilityResult(Ok: true, Data: "ok"));
        }
    }
}
