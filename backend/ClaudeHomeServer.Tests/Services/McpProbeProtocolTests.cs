using System.Text.Json;
using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Рукопожатие пробы: кадры, разбор ответов и трактовка кодов HTTP. Здесь легко ошибиться
// молча — кривой кадр даст «сервер не отвечает» вместо реальной причины.
public class McpProbeProtocolTests
{
    [Fact]
    public void КадрыРукопожатия_ЭтоВалидныйJsonRpc()
    {
        foreach (var frame in new[]
                 {
                     McpProbeProtocol.InitializeRequest(),
                     McpProbeProtocol.InitializedNotification(),
                     McpProbeProtocol.ToolsListRequest(),
                 })
        {
            using var doc = JsonDocument.Parse(frame);
            doc.RootElement.GetProperty("jsonrpc").GetString().Should().Be("2.0");
            doc.RootElement.TryGetProperty("method", out _).Should().BeTrue();
        }

        using var init = JsonDocument.Parse(McpProbeProtocol.InitializeRequest());
        init.RootElement.GetProperty("id").GetInt32().Should().Be(McpProbeProtocol.InitializeId);
        init.RootElement.GetProperty("params").GetProperty("protocolVersion").GetString()
            .Should().Be(McpProbeProtocol.ProtocolVersion);

        // Уведомление обязано быть БЕЗ id — иначе сервер ждёт ответа на него
        using var notification = JsonDocument.Parse(McpProbeProtocol.InitializedNotification());
        notification.RootElement.TryGetProperty("id", out _).Should().BeFalse();
    }

    [Theory]
    // Логи сервера в stdout — обычное дело, кадром их считать нельзя
    [InlineData("Server listening on stdio")]
    [InlineData("")]
    [InlineData("{ не json ")]
    // Чужой id: ответ на другой запрос ждём дальше
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":99,\"result\":{}}")]
    // Уведомление сервера без id
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\"}")]
    public void ЧужиеСтроки_КадромНеСчитаются(string line)
    {
        McpProbeProtocol.TryParseFrame(line, McpProbeProtocol.InitializeId, out _).Should().BeFalse();
    }

    [Fact]
    public void ОшибкаСервера_ДоезжаетТекстом()
    {
        var line = "{\"jsonrpc\":\"2.0\",\"id\":2,\"error\":{\"code\":-32601,\"message\":\"Method not found\"}}";

        McpProbeProtocol.TryParseFrame(line, McpProbeProtocol.ToolsListId, out var frame).Should().BeTrue();

        frame.Error.Should().Be("Method not found");
        frame.Result.Should().BeNull();
    }

    [Fact]
    public void ОтветInitialize_ДаётИмяСервера()
    {
        var line = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2025-06-18\","
                   + "\"serverInfo\":{\"name\":\"weather\",\"version\":\"0.1\"}}}";

        McpProbeProtocol.TryParseFrame(line, McpProbeProtocol.InitializeId, out var frame).Should().BeTrue();

        McpProbeProtocol.ServerNameFrom(frame.Result).Should().Be("weather");
    }

    [Fact]
    public void ОтветToolsList_ДаётИменаИнструментов()
    {
        var line = "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":["
                   + "{\"name\":\"get_forecast\"},{\"name\":\"get_alerts\"},{\"description\":\"без имени\"}]}}";

        McpProbeProtocol.TryParseFrame(line, McpProbeProtocol.ToolsListId, out var frame).Should().BeTrue();

        McpProbeProtocol.ToolNamesFrom(frame.Result).Should().Equal("get_forecast", "get_alerts");
    }

    [Fact]
    public void ПустойРезультат_НеРоняетРазбор()
    {
        McpProbeProtocol.ServerNameFrom(null).Should().BeNull();
        McpProbeProtocol.ToolNamesFrom(null).Should().BeEmpty();

        var line = "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{}}";
        McpProbeProtocol.TryParseFrame(line, McpProbeProtocol.ToolsListId, out var frame).Should().BeTrue();
        McpProbeProtocol.ToolNamesFrom(frame.Result).Should().BeEmpty();
    }

    [Fact]
    public void ТелоОтвета_РазбираетсяИКакJson_ИКакSse()
    {
        var json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}";
        McpProbeProtocol.Frames(json).Should().ContainSingle().Which.Should().Be(json);

        // Streamable HTTP вправе ответить потоком событий — кадр лежит в data:
        var sse = "event: message\r\ndata: " + json + "\r\n\r\n";
        McpProbeProtocol.Frames(sse).Should().ContainSingle().Which.Should().Be(json);

        McpProbeProtocol.Frames(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData(200, McpServerStatuses.Connected)]
    // 401/403 — «нужен вход», а не поломка: сервер жив, человеку надо авторизоваться
    [InlineData(401, McpServerStatuses.NeedsAuth)]
    [InlineData(403, McpServerStatuses.NeedsAuth)]
    [InlineData(404, McpServerStatuses.Failed)]
    [InlineData(500, McpServerStatuses.Failed)]
    public void КодОтвета_ТрактуетсяПравильно(int code, string expected)
    {
        McpProbeProtocol.StatusFromHttp(code).Should().Be(expected);
    }
}
