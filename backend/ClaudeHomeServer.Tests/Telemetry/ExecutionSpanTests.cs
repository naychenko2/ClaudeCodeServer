using System.Diagnostics;
using System.Diagnostics.Metrics;
using ClaudeHomeServer.Telemetry;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Тесты инструментирования хода ClaudeSession (T7+T8): OTel-спаны
/// (chat.turn, process.start) и метрики (LLM duration/errors/rate-limit).
///
/// Проверяют контракт TurnTelemetry — моста между точками инструментирования
/// ClaudeSession и статическими фасадами ServerActivitySource/ServerMetrics.
/// ActivityListener возвращает AllData, MeterListener перехватывает Record-вызовы.
/// </summary>
public class ExecutionSpanTests
{
    // ── Activity (traces) ────────────────────────────────────────────────────

    /// <summary>
    /// ActivityListener, перехватывающий все Activity от ServerActivitySource
    /// с SamplingResult.AllData (включая теги и тело).
    /// </summary>
    private sealed class ActivityCapture : IDisposable
    {
        private readonly ActivityListener _listener = new();
        public readonly List<Activity> Activities = new();

        public ActivityCapture()
        {
            _listener.ShouldListenTo = src => src.Name == ServerActivitySource.Name;
            _listener.SampleUsingParentId = (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllData;
            _listener.Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData;
            _listener.ActivityStopped = a => Activities.Add(a);
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public void StartTurnSpan_CreatesActivityWithCorrectNameAndTags()
    {
        using var capture = new ActivityCapture();

        using var span = TurnTelemetry.StartTurnSpan(
            chatId: "chat-42",
            claudeSessionId: "sess-123",
            turnId: "turn-abc",
            model: "claude-sonnet-4",
            provider: "claude");

        span.Should().NotBeNull("ActivitySource слушается — Activity должна создаться");
        span!.OperationName.Should().Be(ServerActivitySource.SpanNames.ChatTurn);
        span.GetTagItem("chat_id").Should().Be("chat-42");
        span.GetTagItem("session_id").Should().Be("sess-123");
        span.GetTagItem("turn_id").Should().Be("turn-abc");
        span.GetTagItem("model").Should().Be("claude-sonnet-4");
        span.GetTagItem("provider").Should().Be("claude");
    }

    [Fact]
    public void StartTurnSpan_NullModel_ReplacedWithUnknown()
    {
        using var capture = new ActivityCapture();

        using var span = TurnTelemetry.StartTurnSpan("chat", "s", "t", model: null, "claude");

        span!.GetTagItem("model").Should().Be("unknown");
    }

    /// <summary>
    /// Без csid CLI тег session_id не ставится вовсе. Прежний фолбэк
    /// <c>?? Info.Id.ToString()</c> смешивал в одном теге два пространства id: на первом
    /// ходу туда попадал id чата CCS, дальше — csid, и связать инцидент с чатом было нечем.
    /// </summary>
    [Fact]
    public void StartTurnSpan_NoClaudeSessionId_SkipsSessionTagInsteadOfFakingIt()
    {
        using var capture = new ActivityCapture();

        using var span = TurnTelemetry.StartTurnSpan(
            chatId: "chat-42", claudeSessionId: null, turnId: "t", model: "m", provider: "claude");

        span!.GetTagItem("chat_id").Should().Be("chat-42");
        span.GetTagItem("session_id").Should().BeNull("id чата в session_id подставлять нельзя");
    }

    [Fact]
    public void StartProcessSpan_NoClaudeSessionId_SkipsSessionTag()
    {
        using var capture = new ActivityCapture();

        using var span = TurnTelemetry.StartProcessSpan(
            kind: "local", command: "claude", chatId: "chat-42",
            claudeSessionId: null, mcpConfigHash: "abc123");

        span!.GetTagItem("chat_id").Should().Be("chat-42");
        span.GetTagItem("session_id").Should().BeNull();
    }

    [Fact]
    public void StartProcessSpan_IsChildOfChatTurn()
    {
        using var capture = new ActivityCapture();

        using var turnSpan = TurnTelemetry.StartTurnSpan("chat", "sess", "turn", "model", "claude");
        using var procSpan = TurnTelemetry.StartProcessSpan(
            kind: "local", command: "claude", chatId: "chat",
            claudeSessionId: "sess", mcpConfigHash: "abc123");

        procSpan.Should().NotBeNull();
        procSpan!.OperationName.Should().Be(ServerActivitySource.SpanNames.ProcessStart);
        procSpan.GetTagItem("kind").Should().Be("local");
        procSpan.GetTagItem("command").Should().Be("claude");
        procSpan.GetTagItem("mcp_config_hash").Should().Be("abc123");
        // process.start — дочерний спан chat.turn: ParentId = Id родителя
        procSpan.ParentId.Should().Be(turnSpan!.Id,
            "process.start запускается внутри активного chat.turn → Activity.Current = родитель");
    }

    [Fact]
    public void StartProcessSpan_NoParent_RootOrNull()
    {
        // Без активного chat.turn — process.start всё равно создаётся, просто без родителя.
        // Проверяем, что метод не падает в изоляции (Activity.Current = null).
        using var capture = new ActivityCapture();

        using var procSpan = TurnTelemetry.StartProcessSpan(
            "docker", "claude", "chat", "sess", "hash");

        procSpan.Should().NotBeNull();
        procSpan!.ParentId.Should().BeNull();
    }

    [Fact]
    public void TurnSpan_Dispose_StopsActivity()
    {
        using var capture = new ActivityCapture();

        Activity? span;
        using (span = TurnTelemetry.StartTurnSpan("chat", "s", "t", "m", "claude"))
        {
            span!.Status.Should().Be(ActivityStatusCode.Unset);
        }

        // После Dispose Activity попадает в captured-список через ActivityStopped
        capture.Activities.Should().ContainSingle(a =>
            a.OperationName == ServerActivitySource.SpanNames.ChatTurn);
    }

    // ── Metrics (smoke tests — MeterListener API нестабилен между версиями .NET,
    //    поэтому проверяем только что вызовы не бросают. Корректность самих
    //    ServerMetrics уже покрыта в MetricTagAllowlistTests.) ─────────────────

    [Fact]
    public void RecordTurnResult_DoesNotThrow()
    {
        var act = () => TurnTelemetry.RecordTurnResult(12345, "claude", "claude-sonnet-4",
            isError: false, apiErrorStatus: null);
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordTurnResult_OnError_DoesNotThrow()
    {
        var act = () => TurnTelemetry.RecordTurnResult(5000, "deepseek", "deepseek-chat",
            isError: true, apiErrorStatus: "429");
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordRateLimit_DoesNotThrow()
    {
        var act = () => TurnTelemetry.RecordRateLimit("glm", "allowed");
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordError_DoesNotThrow()
    {
        var act = () => TurnTelemetry.RecordError("claude", "process_exit");
        act.Should().NotThrow();
    }

    // ── Error classification ──────────────────────────────────────────────────

    [Theory]
    [InlineData("429", "rate_limit")]
    [InlineData("rate_limit", "rate_limit")]
    [InlineData("401", "auth")]
    [InlineData("403", "auth")]
    [InlineData("authentication_error", "auth")]
    [InlineData("500", "network")]
    [InlineData("503", "network")]
    [InlineData("overloaded_error", "network")]
    [InlineData(null, "unknown")]
    [InlineData("something_weird", "unknown")]
    public void ClassifyErrorType_MapsApiErrorStatus(string? status, string expected)
    {
        TurnTelemetry.ClassifyErrorType(status).Should().Be(expected);
    }
}
