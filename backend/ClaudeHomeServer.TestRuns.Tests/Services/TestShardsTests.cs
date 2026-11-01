using ClaudeHomeServer.Services.TestRuns;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.TestRuns;

// Раскладка монолита по классам: каждый тест — ровно в одном шарде (объединение == список)
public class TestShardsTests
{
    private static readonly string[] Listed =
    [
        "App.Tests.Services.AlphaTests.Один",
        "App.Tests.Services.AlphaTests.Два(x: 1, s: \"a.b(c)\")",
        "App.Tests.Services.AlphaTests.Два(x: 2)",
        "App.Tests.Services.Alpha.BetaTests.Три",
        "App.Tests.Controllers.GammaTests.Четыре",
        "App.Tests.Controllers.GammaTests+Nested.Пять",
        "App.Tests.Controllers.DeltaTests.Шесть",
    ];

    private static List<string> Fqns => [.. Listed.Select(TestShards.TestFqn)];

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    public void Раскладка_КаждыйТестРовноВОдномШарде(int n) =>
        TestShards.Verify(Fqns, TestShards.Layout(Fqns, n)).Should().BeEmpty();

    [Fact]
    public void Раскладка_ВыпавшийКласс_Ловится()
    {
        var shards = TestShards.Layout(Fqns, 3).Select(s => s.ToList()).ToList();
        shards.First(s => s.Count > 0).RemoveAt(0);
        TestShards.Verify(Fqns, shards).Should().NotBeEmpty();
    }

    [Fact]
    public void Раскладка_КлассВДвухШардах_Ловится()
    {
        var shards = TestShards.Layout(Fqns, 2).Select(s => s.ToList()).ToList();
        shards[1].Add(shards[0][0]);
        TestShards.Verify(Fqns, shards).Should().NotBeEmpty();
    }

    [Fact]
    public void Раскладка_БольшийКлассРаньше_НагрузкаПоЧислуТестов()
    {
        var shards = TestShards.Layout(Fqns, 2);
        shards.Select(s => s.Sum(c => Fqns.Count(t => TestShards.ClassOf(t) == c))).Should().BeEquivalentTo([4, 3]);
    }

    [Fact]
    public void Фильтр_ЭкранируетСпецсимволыИСтавитТочку() =>
        TestShards.Filter(["A.B(x)", "C"]).Should().Be("FullyQualifiedName~A.B\\(x\\).|FullyQualifiedName~C.");

    [Fact]
    public void Префикс_НеЦепляетСоседа() =>
        TestShards.Matches("App.Tests.Services.Alpha", "App.Tests.Services.AlphaTests.Один").Should().BeFalse();

    [Fact]
    public void План_ОдинКласс_ШардовНет() =>
        TestShards.Plan(["A.B.One", "A.B.Two"], 3).Should().BeNull();

    [Theory]
    [InlineData("backend/ClaudeHomeServer.Tests", true)]
    [InlineData("backend/ClaudeHomeServer.Tests/", true)]
    [InlineData("backend\\ClaudeHomeServer.Tests\\ClaudeHomeServer.Tests.csproj", true)]
    [InlineData("backend/ClaudeHomeServer.Tests.Other", false)]
    [InlineData("backend/ClaudeHomeServer.slnx", false)]
    [InlineData(null, false)]
    public void Монолит_ПоКаталогуИлиCsproj(string? target, bool expected) =>
        TestShards.IsMonolith(target, TestRunsOptions.DefaultMonolithTarget).Should().Be(expected);
}
