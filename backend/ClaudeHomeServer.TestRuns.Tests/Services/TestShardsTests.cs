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

    // Те же строки — в scripts/test_test_shards.py (ADAPTIVE_ROWS): nproc, load1, MemAvailable ГиБ
    // (-1 — снимка нет), потолок, ожидаемое N. Мутация порога 0,5→0,6 краснит строку 12.0
    [Theory]
    [InlineData(24, 7.2, 31, 6, 6)]
    [InlineData(24, 11.99, 31, 6, 6)]
    [InlineData(24, 12.0, 31, 6, 3)]
    [InlineData(24, 14.0, 31, 6, 3)]
    [InlineData(24, 35.99, 31, 6, 3)]
    [InlineData(24, 36.0, 31, 6, 2)]
    [InlineData(24, 50.0, 31, 6, 2)]
    [InlineData(24, 2.0, 15.9, 6, 3)]
    [InlineData(24, 2.0, 16.0, 6, 6)]
    [InlineData(24, 2.0, 7.9, 6, 2)]
    [InlineData(24, 2.0, 8.0, 6, 3)]
    [InlineData(24, 2.0, 31, 4, 4)]
    [InlineData(24, 2.0, 31, 1, 1)]
    [InlineData(24, 50.0, 31, 1, 1)]
    [InlineData(8, 3.9, 31, 6, 6)]
    [InlineData(8, 4.0, 31, 6, 3)]
    [InlineData(-1, 0, 0, 6, 3)]
    [InlineData(-1, 0, 0, 2, 2)]
    public void Адаптивно_ТаблицаОбщаяСPython(int cpus, double load1, double memGiB, int max, int expected)
    {
        MachineSnapshot? snapshot = cpus < 0 ? null : new MachineSnapshot(cpus, load1, (long)(memGiB * AdaptiveShards.GiB));
        AdaptiveShards.Choose(snapshot, max).Shards.Should().Be(expected);
    }

    [Fact]
    public void Адаптивно_ПричинаДляСводки()
    {
        AdaptiveShards.Choose(new MachineSnapshot(24, 7.2, 31 * AdaptiveShards.GiB + 5), 6)
            .Should().Be((6, "load1 7,2 из 24, свободно 31 ГиБ"));
        AdaptiveShards.Choose(null, 6).Should().Be((3, "нет /proc — фиксированное значение"));
    }

    [Fact]
    public void Адаптивно_ЧтениеProc_НетИлиМусор_NullБезИсключения()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs-proc-" + Guid.NewGuid().ToString("N"));
        try
        {
            AdaptiveShards.Read(dir).Should().BeNull("каталога нет — как на Windows и macOS");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "loadavg"), "мусор");
            File.WriteAllText(Path.Combine(dir, "meminfo"), "MemTotal: 1 kB\n");
            AdaptiveShards.Read(dir).Should().BeNull();
            File.WriteAllText(Path.Combine(dir, "loadavg"), "7.20 5.00 4.00 2/900 123\n");
            AdaptiveShards.Read(dir).Should().BeNull("MemAvailable нет");
            File.WriteAllText(Path.Combine(dir, "meminfo"), "MemTotal: 65000000 kB\nMemAvailable:   32505856 kB\n");
            AdaptiveShards.Read(dir).Should().Be(new MachineSnapshot(Environment.ProcessorCount, 7.2, 32505856L * 1024));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* занят */ }
        }
    }
}
