using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.LocalStats;

// Контракты Core для флага local-model-stats: SessionId без дубля в record-конструкторе
// и OwnerId вне сериализации (JsonIgnore на positional-параметре).
public class CoreContractsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ComfyJobRow_serialization_excludes_ownerId()
    {
        var row = new ComfyJobRow(
            Kind: "image", Op: "generate", Heavy: false,
            OwnerId: "owner-1", SessionId: "sess-1", ProjectId: null,
            Foreign: null, Steps: null, Percent: null, PercentExact: false,
            EtaSeconds: null, RemainingSeconds: null, StartInSeconds: null,
            StartedAt: null);

        var json = JsonSerializer.Serialize(row, Web);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("ownerId", out _).Should().BeFalse(
            "OwnerId внутренний — наружу REST его не отдаёт");
    }

    [Fact]
    public void ComfyDoneRow_serialization_excludes_ownerId()
    {
        var row = new ComfyDoneRow(
            Kind: "video", Op: "generate",
            OwnerId: "owner-2", Outcome: "completed",
            RunSeconds: 42, EtaSeconds: 30,
            FinishedAt: new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));

        var json = JsonSerializer.Serialize(row, Web);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("ownerId", out _).Should().BeFalse(
            "OwnerId внутренний — наружу REST его не отдаёт");
    }

    [Fact]
    public void LocalModelLiveMessage_has_type_discriminator_and_single_sessionId()
    {
        var msg = new LocalModelLiveMessage(
            Phase: "generating", GenTps: 75.0, WaitingTotal: 0)
        { SessionId = "sess-42" };

        var json = JsonSerializer.Serialize(msg, Web);
        var doc = JsonDocument.Parse(json);

        // type-дискриминатор из базового ServerMessage
        doc.RootElement.GetProperty("type").GetString()
            .Should().Be("local_model_live");

        // sessionId ровно один — из базового ServerMessage, не из positional-конструктора
        var sessionIdCount = CountProperty(doc.RootElement, "sessionId");
        sessionIdCount.Should().Be(1,
            "SessionId унаследован от ServerMessage; дубль в record-конструкторе убран");
    }

    [Fact]
    public void LocalModelTurnStatsMessage_has_type_discriminator_and_single_sessionId()
    {
        var msg = new LocalModelTurnStatsMessage(
            GenTps: 42.5, PrefillTps: null, TtftMs: 300,
            QueueMsApprox: 1500, CacheHitPerc: 0.8, OutTokens: 128,
            MainRequests: 1, SubagentRequests: 2, CompactionRequests: 1, AuxiliaryRequests: 0,
            AgentTypes: ["Explore"], CompactionKind: "auto")
        { SessionId = "sess-77" };

        var json = JsonSerializer.Serialize(msg, Web);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("type").GetString()
            .Should().Be("local_model_turn_stats");

        var sessionIdCount = CountProperty(doc.RootElement, "sessionId");
        sessionIdCount.Should().Be(1,
            "SessionId унаследован от ServerMessage; дубль в record-конструкторе убран");

        // prefillTps — null (движок пока не отчитывается), в wire — null, не пропуск.
        doc.RootElement.GetProperty("prefillTps").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // Считает вхождения ключа в JSON-объекте верхнего уровня.
    private static int CountProperty(JsonElement obj, string name)
    {
        var count = 0;
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name == name) count++;
        }
        return count;
    }
}
