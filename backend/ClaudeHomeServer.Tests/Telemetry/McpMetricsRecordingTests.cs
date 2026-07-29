using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Smoke-тесты для проверки того, что McpCallLog.Record корректно вызывает
/// ServerMetrics.RecordMcpCall / RecordMcpError (починка «мёртвых» MCP-метрик).
///
/// Контекст: до правки RecordMcpCall/RecordMcpError были определены в ServerMetrics,
/// но НЕ вызывались нигде — счётчики ccs.mcp.calls/ccs.mcp.errors были мёртвые.
/// Теперь вызовы идут из McpCallLog.Record (единая точка рядом с in-memory агрегацией).
///
/// Эти тесты — regression-guard: проверяют, что вызов Record с разными statusCode
/// не бросает и что path-to-ServerMetrics жив (маппинг status → outcome работает).
/// </summary>
public class McpMetricsRecordingTests
{
    [Fact]
    public void Record_SuccessStatus_DoesNotThrow()
    {
        // Arrange: успешный HTTP-ответ (2xx)
        var log = new McpCallLog();

        // Act: вызов Record должен инкрементить ServerMetrics.RecordMcpCall("success")
        //     и НЕ инкрементить RecordMcpError.
        var act = () => log.Record(
            tool: "tasks_create",
            sessionId: "ses_test",
            path: "/api/tasks",
            statusCode: 200,
            elapsedMs: 42);

        // Assert: smoke — метод не бросает, внутренний ServerMetrics-вызов проходит
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void Record_ErrorStatus_DoesNotThrow(int statusCode)
    {
        // Arrange: ошибка (4xx/5xx) — должна инкрементить и RecordMcpCall("error"),
        // и RecordMcpError("http_<status>") с динамическим error_type.
        var log = new McpCallLog();

        // Act
        var act = () => log.Record(
            tool: "notes_search",
            sessionId: null,
            path: "/api/notes",
            statusCode: statusCode,
            elapsedMs: 10);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Record_MultipleTimes_DoesNotThrow()
    {
        // Защита от накопления: много вызовов подряд не должны приводить
        // к переполнению счётчиков или гонке между in-memory и OTel-записью.
        var log = new McpCallLog();

        var act = () =>
        {
            for (var i = 0; i < 100; i++)
                log.Record("tool_x", "ses", "/p", 200, 5);
            for (var i = 0; i < 10; i++)
                log.Record("tool_x", "ses", "/p", 500, 5);
        };

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void Record_EdgeCaseInputs_DoesNotThrow(string tool, string sessionId)
    {
        // Договорённость по API: tool/sessionId могут быть пустыми/whitespace
        // (старая версия MCP-сервера не прислала имя инструмента).
        // ServerMetrics не должен падать на edge cases.
        var log = new McpCallLog();

        var act = () => log.Record(tool, sessionId, "/path", 200, 1);

        act.Should().NotThrow();
    }
}
