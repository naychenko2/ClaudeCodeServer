using System.Text.Json;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

public class ClaudeRateLimitParserTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void TryParse_ПолноеСобытие_ИзвлекаетПоля()
    {
        var root = Root("""
        {"type":"rate_limit_event","rate_limit_info":{
            "rateLimitType":"five_hour","status":"allowed","utilization":0.42,
            "isUsingOverage":false,"resetsAt":"2026-07-17T12:00:00Z"}}
        """);

        ClaudeRateLimitParser.TryParse(root, out var m).Should().BeTrue();
        m.LimitType.Should().Be("five_hour");
        m.Status.Should().Be("allowed");
        m.Utilization.Should().Be(0.42);
        m.IsUsingOverage.Should().BeFalse();
        m.ResetsAt.Should().Be("2026-07-17T12:00:00Z");
    }

    [Fact]
    public void TryParse_ResetsAtUnixСекунды_НормализуетВIso()
    {
        // 1_800_000_000 сек — заведомо < порога мс (100_000_000_000) → трактуется как секунды
        var root = Root("""
        {"type":"rate_limit_event","rate_limit_info":{
            "rateLimitType":"five_hour","utilization":0.5,"resetsAt":1800000000}}
        """);

        ClaudeRateLimitParser.TryParse(root, out var m).Should().BeTrue();
        DateTimeOffset.Parse(m.ResetsAt!).Should().Be(DateTimeOffset.FromUnixTimeSeconds(1800000000));
    }

    [Fact]
    public void TryParse_SnakeCaseТипОкна_Поддерживается()
    {
        var root = Root("""
        {"type":"rate_limit_event","rate_limit_info":{"rate_limit_type":"seven_day","utilization":0.1}}
        """);

        ClaudeRateLimitParser.TryParse(root, out var m).Should().BeTrue();
        m.LimitType.Should().Be("seven_day");
    }

    [Fact]
    public void TryParse_OverageDisabledReason_Разбирается()
    {
        var root = Root("""
        {"type":"rate_limit_event","rate_limit_info":{
            "rateLimitType":"five_hour","status":"rejected","utilization":0.9,
            "overageDisabledReason":"org_level_disabled"}}
        """);

        ClaudeRateLimitParser.TryParse(root, out var m).Should().BeTrue();
        m.OverageDisabledReason.Should().Be("org_level_disabled");
    }

    [Fact]
    public void TryParse_OverageDisabledReason_SnakeCase_ТожеРазбирается()
    {
        var root = Root("""
        {"type":"rate_limit_event","rate_limit_info":{
            "rate_limit_type":"five_hour","utilization":0.9,
            "overage_disabled_reason":"out_of_credits"}}
        """);

        ClaudeRateLimitParser.TryParse(root, out var m).Should().BeTrue();
        m.OverageDisabledReason.Should().Be("out_of_credits");
    }

    [Fact]
    public void TryParse_БезПричиныПерерасхода_Null()
    {
        var root = Root("""
        {"type":"rate_limit_event","rate_limit_info":{"rateLimitType":"five_hour","utilization":0.5}}
        """);

        ClaudeRateLimitParser.TryParse(root, out var m).Should().BeTrue();
        m.OverageDisabledReason.Should().BeNull();
    }

    [Fact]
    public void TryParse_БезRateLimitInfo_False()
    {
        ClaudeRateLimitParser.TryParse(Root("""{"type":"rate_limit_event"}"""), out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_НиТипаНиUtilization_False()
    {
        var root = Root("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed"}}""");
        ClaudeRateLimitParser.TryParse(root, out _).Should().BeFalse();
    }

    /// <summary>
    /// Телеметрия пишется на КАЖДОЕ событие, в том числе на то, что TryParse отвергает —
    /// иначе метрика молча теряла бы часть отказов.
    /// </summary>
    [Fact]
    public void ReadStatus_СобытиеОтвергнутоеTryParse_ВсёРавноОтдаётСтатус()
    {
        var root = Root("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected"}}""");

        ClaudeRateLimitParser.TryParse(root, out _).Should().BeFalse();
        ClaudeRateLimitParser.ReadStatus(root).Should().Be("rejected");
    }

    [Fact]
    public void ReadStatus_БезПоляСтатуса_Null()
    {
        ClaudeRateLimitParser.ReadStatus(Root("""{"type":"rate_limit_event"}"""))
            .Should().BeNull();
        ClaudeRateLimitParser.ReadStatus(Root("""{"type":"rate_limit_event","rate_limit_info":{"utilization":0.5}}"""))
            .Should().BeNull();
    }
}
