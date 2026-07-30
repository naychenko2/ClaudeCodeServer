using System.Text.Json;
using ClaudeHomeServer.Telemetry;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Откуда берётся тег <c>model</c>: из события CLI, а не из настроек чата.
///
/// Контекст: <c>Session.Model</c> и слоты тиров — это НАМЕРЕНИЕ. Когда модель у чата не
/// задана и слот пуст, резолвер отдаёт null, и в телеметрию уходил литерал <c>unknown</c>.
/// Проверено на боевом ходе 29.07.2026: спан chat.turn приехал с <c>model: unknown</c> при
/// живом ответе на 8.9 секунды — то есть панель «какими моделями считаем» отвечала «не знаю».
///
/// Формы событий взяты из настоящего потока stream-json (system/init и assistant).
/// </summary>
public class TurnModelSourceTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ModelFromEvent_SystemInit_TakesTopLevelModel()
    {
        // system/init: CLI объявляет модель прогона
        var e = Parse("""
            {"type":"system","subtype":"init","session_id":"9fa3b829","model":"claude-opus-4-8",
             "cwd":"C:\\GIT\\ClaudeCodeServer","tools":[]}
            """);

        TurnTelemetry.ModelFromEvent(e).Should().Be("claude-opus-4-8");
    }

    [Fact]
    public void ModelFromEvent_Assistant_TakesMessageModel()
    {
        // assistant: модель, которая выдала ИМЕННО этот ответ
        var e = Parse("""
            {"type":"assistant","message":{"model":"claude-sonnet-4-5-20250929","id":"msg_01",
             "role":"assistant","content":[{"type":"text","text":"привет"}]}}
            """);

        TurnTelemetry.ModelFromEvent(e).Should().Be("claude-sonnet-4-5-20250929");
    }

    [Fact]
    public void ModelFromEvent_PrefersMessageOverTopLevel()
    {
        // Если в одном событии есть оба — факт (message.model) важнее объявленного на старте
        var e = Parse("""
            {"type":"assistant","model":"объявленная-на-старте",
             "message":{"model":"glm-5.2","role":"assistant"}}
            """);

        TurnTelemetry.ModelFromEvent(e).Should().Be("glm-5.2");
    }

    [Theory]
    // Событий без модели большинство — они не должны затирать уже известную
    [InlineData("""{"type":"result","subtype":"success","duration_ms":8865}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta"}}""")]
    // Пустая строка — не модель
    [InlineData("""{"type":"system","subtype":"init","model":""}""")]
    [InlineData("""{"type":"assistant","message":{"model":""}}""")]
    // Не тот тип поля
    [InlineData("""{"type":"assistant","message":{"model":42}}""")]
    [InlineData("""{"type":"assistant","message":"строка вместо объекта"}""")]
    public void ModelFromEvent_NoUsableModel_ReturnsNull(string json)
    {
        TurnTelemetry.ModelFromEvent(Parse(json)).Should().BeNull();
    }

    [Fact]
    public void ModelFromEvent_RealClaudeModelIds_SurviveCardinalityGuard()
    {
        // Значение уходит в тег метрики через MetricTagGuard.Model — если форма не пройдёт,
        // вместо модели в метрике окажется "other", и разрез снова станет бесполезным
        string[] ids = ["claude-opus-4-8", "claude-sonnet-4-5-20250929", "glm-5.2", "qwen2.5:7b"];

        foreach (var id in ids)
        {
            var e = Parse("{\"type\":\"assistant\",\"message\":{\"model\":\"" + id + "\"}}");
            var model = TurnTelemetry.ModelFromEvent(e);

            model.Should().Be(id);
            MetricTagGuard.Model(model).Should().Be(id, "модель {0} не должна схлопываться в other", id);
        }
    }
}
