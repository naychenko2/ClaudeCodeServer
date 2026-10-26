using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// SP3 («учётка хода»): провайдер и модель записи расхода — из фактического маршрута прогона
// (ResultMessage.UpstreamProvider/UpstreamModel), а не из настроек чата и не из отчёта CLI.
public class SpendMappingTests
{
    private sealed class RecordingSpend : ISpendCollector
    {
        public List<SpendRecord> Records { get; } = [];

        public void Record(SpendRecord record) => Records.Add(record);
    }

    // local-qwen — локальный движок (IsLocal), minimax — платный облачный с моделью MiniMax-M3.
    private static LlmProviderRegistry Providers() => new(TestConfig.Build(new Dictionary<string, string?>
    {
        ["LlmProviders:local-qwen:DisplayName"] = "Local Qwen",
        ["LlmProviders:local-qwen:ApiBaseUrl"] = "http://127.0.0.1:8080/v1",
        ["LlmProviders:local-qwen:IsLocal"] = "true",
        ["LlmProviders:local-qwen:Models:0:Id"] = "qwen3.8-27b",
        ["LlmProviders:minimax:DisplayName"] = "MiniMax",
        ["LlmProviders:minimax:ApiBaseUrl"] = "https://api.minimax.io",
        ["LlmProviders:minimax:ApiKey"] = "test-key",
        ["LlmProviders:minimax:Models:0:Id"] = "MiniMax-M3",
    }));

    private static Session Chat(string provider, string? model) =>
        new() { Id = "s-1", ProjectId = "p-1", Provider = provider, Model = model };

    private static ResultMessage Result(
        string? usageModel = null, string? upstreamProvider = null, string? upstreamModel = null) =>
        new("success", 100, 1, new UsageInfo(1000, 200, 0, 0), 0.01,
            UsageModel: usageModel, UpstreamProvider: upstreamProvider, UpstreamModel: upstreamModel);

    private static SpendRecord RecordTurn(Session s, ResultMessage m)
    {
        var spend = new RecordingSpend();
        SpendMapping.RecordTurnSpend(spend, Providers(), _ => "owner-1", null, s, m);
        return Assert.Single(spend.Records);
    }

    // Чат настроен Claude, ход через шлюз ушёл в minimax: запись обязана брать маршрут —
    // модель шлюза важнее отчёта CLI (сессия ротацию шлюза не видит).
    [Fact]
    public void RecordTurnSpend_МаршрутШлюза_ВажнееНастроекЧатаИОтчётаCLI()
    {
        var r = RecordTurn(Chat("claude", "opus"),
            Result(usageModel: "opus", upstreamProvider: "minimax", upstreamModel: "MiniMax-M3"));
        r.Provider.Should().Be("minimax");
        r.Model.Should().Be("MiniMax-M3");
        r.Source.Should().Be(SpendSources.ChatTurn);
        SpendCostTiers.Of(r.Source, r.Provider, r.Model).Should().Be(SpendCostTiers.Cheap);
    }

    // Ход на локальном движке: запись «free», даже когда настройки чата — Claude.
    [Fact]
    public void RecordTurnSpend_ЛокальныйАпстрим_ИсточникFree()
    {
        var r = RecordTurn(Chat("claude", "opus"),
            Result(usageModel: "opus", upstreamProvider: "local-qwen", upstreamModel: "qwen3.8-27b"));
        r.Provider.Should().Be("local-qwen");
        r.Model.Should().Be("qwen3.8-27b");
        r.Source.Should().Be(SpendSources.Free);
        // Класс и источник не разъезжаются: та же запись по правилу классов — «бесплатно»
        SpendCostTiers.Of(r.Source, r.Provider, r.Model).Should().Be(SpendCostTiers.Free);
    }

    // Признак isLocal считается по тому же ключу, что пишется в запись: чат, настроенный на
    // локальный движок, но реально ходивший в облако, не должен записываться «бесплатным».
    [Fact]
    public void RecordTurnSpend_НастройкиЛокальныНоМаршрутОблачный_НеБесплатно()
    {
        var r = RecordTurn(Chat("local-qwen", "qwen3.8-27b"),
            Result(upstreamProvider: "minimax", upstreamModel: "MiniMax-M3"));
        r.Provider.Should().Be("minimax");
        r.Source.Should().Be(SpendSources.ChatTurn);
    }

    // result до SP1 (без upstream-полей): поведение прежнее — провайдер из настроек сессии
    // (sub-* нормализуется в claude), модель из modelUsage, фолбэк — модель сессии.
    [Theory]
    [InlineData("deepseek", "deepseek-v4-pro", null, "deepseek", "deepseek-v4-pro")]
    [InlineData("sub-work", "opus", null, "claude", "opus")]
    [InlineData("claude", "opus", "sonnet", "claude", "sonnet")]
    public void RecordTurnSpend_АпстримаНет_ПрежнееПоведение(
        string sessionProvider, string? sessionModel, string? usageModel, string wantProvider, string wantModel)
    {
        var r = RecordTurn(Chat(sessionProvider, sessionModel), Result(usageModel: usageModel));
        r.Provider.Should().Be(wantProvider);
        r.Model.Should().Be(wantModel);
        r.Source.Should().Be(SpendSources.ChatTurn);
    }

    // Провайдер не резолвится по модели из отчёта CLI: подписка claude-3 остаётся claude-3,
    // класс — «дорого» (префиксный резолв по UsageModel уводил бы реальные подписки в «дёшево»).
    [Fact]
    public void RecordTurnSpend_ПодпискаClaude_НеУводитсяПрефикснымРезолвом()
    {
        var r = RecordTurn(Chat("claude-3", "claude-opus-5-5"),
            Result(usageModel: "claude-opus-5-5", upstreamProvider: "claude-3"));
        r.Provider.Should().Be("claude-3");
        r.Model.Should().Be("claude-opus-5-5");
        r.Source.Should().Be(SpendSources.ChatTurn);
        SpendCostTiers.Of(r.Source, r.Provider, r.Model).Should().Be(SpendCostTiers.Expensive);
    }
}
