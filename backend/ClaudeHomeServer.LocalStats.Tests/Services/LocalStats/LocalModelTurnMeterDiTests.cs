using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

// B8: счётчик хода, собранный реальным LlmSubsystem (не `new LocalModelTurnMeter`), получает
// реестр провайдеров — иначе TrackOf всегда даёт «main». Мутация «не передавать реестр в
// LlmSubsystem» → чат провайдера gemma виден на main и не виден на gemma → тест краснеет.
// Коллекция Inotify: полный хост открывает inotify-экземпляры (как LlmSubsystemDiCycleTests).
[Collection(TestCollections.Inotify)]
[Trait("Category", "Host")]
public class LocalModelTurnMeterDiTests
{
    [Fact]
    public void Счётчик_изLlmSubsystem_РазделяетЧатыПоДорожкам()
    {
        using var factory = new TestWebApplicationFactory();
        factory.ExtraConfig["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:8080";
        factory.ExtraConfig["LlmProviders:local-qwen:IsLocal"] = "true";
        factory.ExtraConfig["LlmProviders:local-qwen:Stats:Engine"] = "vllm";
        factory.ExtraConfig["LlmProviders:local-qwen:Stats:MetricsUrl"] = "http://127.0.0.1:8084/metrics";
        factory.ExtraConfig["LlmProviders:local-video-gemma:AnthropicBaseUrl"] = "http://127.0.0.1:8081";
        factory.ExtraConfig["LlmProviders:local-video-gemma:IsLocal"] = "true";
        factory.ExtraConfig["LlmProviders:local-video-gemma:StatsEngine"] = "gemma";
        factory.ExtraConfig["LlmProviders:cloud-x:AnthropicBaseUrl"] = "https://cloud.example";

        var sp = factory.Services;
        var meter = sp.GetRequiredService<LocalModelTurnMeter>();
        var tokens = sp.GetRequiredService<TurnTokenService>();
        string Issue(string chat) => tokens.Issue("owner", chat, "device").Grant.TurnId;

        meter.OnRequestStarted("rA", Issue("chat-A"), "local-qwen");
        meter.OnRequestStarted("rB", Issue("chat-B"), "local-video-gemma");
        meter.OnRequestStarted("rX", Issue("chat-X"), "cloud-x");

        meter.ActiveChats("main").Select(c => c.SessionId).Should().Equal(new[] { "chat-A" },
            "основной локальный — на main; gemma и провайдер без дорожки на main не уезжают");
        meter.ActiveChats("gemma").Select(c => c.SessionId).Should().Equal(new[] { "chat-B" });
    }
}
