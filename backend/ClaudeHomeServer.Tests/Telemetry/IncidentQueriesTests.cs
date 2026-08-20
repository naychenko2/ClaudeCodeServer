using System.Text.Json;
using ClaudeHomeServer.Telemetry.Incidents;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Тела запросов к <c>/api/v5/query_range</c> и разбор ответов.
///
/// Главное здесь — валидность JSON при любом значении фильтра: тело собирается
/// программно, но значение контура приходит из конфига и меток алерта. Прежняя
/// самодельная склейка превращала апостроф в <c>\'</c> — escape, которого в JSON нет:
/// SigNoz отвечал 400, а разрез молча оказывался пустым.
/// </summary>
public class IncidentQueriesTests
{
    private static readonly DateTimeOffset From = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 8, 19, 11, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("dev")]
    [InlineData("production")]
    [InlineData("O'Brien")]                 // апостроф — тот самый случай
    [InlineData("контур \"боевой\"")]        // кавычка и кириллица
    [InlineData(@"back\slash")]
    [InlineData("")]
    public void AllBodies_AreValidJson(string environment)
    {
        var bodies = new[]
        {
            IncidentQueries.Breakdown("ccs.llm.errors", "error_type", environment, From, To),
            IncidentQueries.FailedTurns(environment, From, To),
            IncidentQueries.Logs(environment, From, To),
        };

        foreach (var body in bodies)
        {
            var parse = () => JsonDocument.Parse(body);
            parse.Should().NotThrow($"тело запроса обязано быть валидным JSON при контуре «{environment}»");
        }
    }

    [Fact]
    public void Breakdown_CarriesWindowAndAggregation()
    {
        var body = IncidentQueries.Breakdown("ccs.llm.errors", "error_type", "dev", From, To);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("start").GetInt64().Should().Be(From.ToUnixTimeMilliseconds());
        root.GetProperty("end").GetInt64().Should().Be(To.ToUnixTimeMilliseconds());
        root.GetProperty("requestType").GetString().Should().Be("time_series");

        var spec = root.GetProperty("compositeQuery").GetProperty("queries")[0].GetProperty("spec");
        spec.GetProperty("signal").GetString().Should().Be("metrics");
        spec.GetProperty("aggregations")[0].GetProperty("metricName").GetString().Should().Be("ccs.llm.errors");
        spec.GetProperty("groupBy")[0].GetProperty("name").GetString().Should().Be("error_type");
        spec.GetProperty("filter").GetProperty("expression").GetString()
            .Should().Be("deployment.environment = 'dev'");
    }

    [Fact]
    public void FailedTurns_FiltersChatTurnErrors()
    {
        var body = IncidentQueries.FailedTurns("production", From, To);

        using var doc = JsonDocument.Parse(body);
        var spec = doc.RootElement.GetProperty("compositeQuery").GetProperty("queries")[0].GetProperty("spec");
        spec.GetProperty("signal").GetString().Should().Be("traces");
        var expression = spec.GetProperty("filter").GetProperty("expression").GetString();
        expression.Should().Contain("name = 'chat.turn'").And.Contain("outcome = 'error'")
            .And.Contain("deployment.environment = 'production'");
    }

    [Fact]
    public void EmptyEnvironment_LeavesFilterUnrestricted()
    {
        // Правило без разреза по среде касается инсталляции целиком — фильтровать нечем
        var body = IncidentQueries.Breakdown("ccs.llm.errors", "error_type", null, From, To);

        using var doc = JsonDocument.Parse(body);
        var spec = doc.RootElement.GetProperty("compositeQuery").GetProperty("queries")[0].GetProperty("spec");
        spec.GetProperty("filter").GetProperty("expression").GetString().Should().BeEmpty();
    }

    [Fact]
    public void ParseBreakdown_SumsSeriesAndSortsByCount()
    {
        const string json = """
        {"status":"success","data":{"data":{"results":[{"aggregations":[{"series":[
          {"labels":{"error_type":"rate_limit"},"values":[{"value":3},{"value":2}]},
          {"labels":{"error_type":"auth"},"values":[{"value":9}]},
          {"labels":{"error_type":"network"},"values":[{"value":0}]}
        ]}]}]}}}
        """;

        var rows = IncidentQueries.ParseBreakdown(json, "error_type");

        rows.Should().HaveCount(2, "нулевые серии в разрез не попадают");
        rows[0].Label.Should().Be("auth");
        rows[0].Count.Should().Be(9);
        rows[1].Label.Should().Be("rate_limit");
        rows[1].Count.Should().Be(5);
    }

    [Fact]
    public void ParseTurns_ReadsChatIdFromRow()
    {
        const string json = """
        {"data":{"data":{"results":[{"rows":[
          {"timestamp":"2026-08-19T10:31:00Z","data":{"chat_id":"chat-1","model":"opus","provider":"claude",
           "error_type":"rate_limit","duration_nano":2500000000}}
        ]}]}}}
        """;

        var turns = IncidentQueries.ParseTurns(json);

        turns.Should().ContainSingle();
        turns[0].ChatId.Should().Be("chat-1");
        turns[0].ErrorType.Should().Be("rate_limit");
        turns[0].DurationMs.Should().Be(2500);
        turns[0].At.Should().Be(new DateTimeOffset(2026, 8, 19, 10, 31, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ParseLogs_SkipsEmptyBodies()
    {
        const string json = """
        {"data":{"data":{"results":[{"rows":[
          {"timestamp":"2026-08-19T10:31:00Z","data":{"severity_text":"Error","body":"Ход упал"}},
          {"timestamp":"2026-08-19T10:32:00Z","data":{"severity_text":"Warning"}}
        ]}]}}}
        """;

        var logs = IncidentQueries.ParseLogs(json);

        logs.Should().ContainSingle();
        logs[0].Severity.Should().Be("Error");
        logs[0].Message.Should().Be("Ход упал");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не json")]
    [InlineData("{\"data\":{}}")]
    public void Parsers_TolerateGarbage(string? json)
    {
        // Разбор чужого ответа не должен ронять карточку: SigNoz после обновления
        // вполне может ответить иначе — это повод показать пусто, а не 500
        IncidentQueries.ParseBreakdown(json, "error_type").Should().BeEmpty();
        IncidentQueries.ParseTurns(json).Should().BeEmpty();
        IncidentQueries.ParseLogs(json).Should().BeEmpty();
    }
}
