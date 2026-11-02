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
    // (-1 — снимка нет), запас cgroup ГиБ (-1 — cgroup нет), пик на шард прошлого прогона ГиБ (-1 — записи
    // нет), потолок, ожидаемое N. Мутации краснят: порог 0,5→0,6 — строку 12.0; память на шард 3,5→3 —
    // строку 13.99; отказ от min со slice — строку 28/7; множитель 1,2 — строку пика 2/10; ядер на шард
    // 4→3 — строку nproc 12
    [Theory]
    [InlineData(24, 7.2, 31, -1, -1, 6, 6)]
    [InlineData(24, 11.99, 31, -1, -1, 6, 6)]
    [InlineData(24, 12.0, 31, -1, -1, 6, 3)]
    [InlineData(24, 14.0, 31, -1, -1, 6, 3)]
    [InlineData(24, 35.99, 31, -1, -1, 6, 3)]
    [InlineData(24, 36.0, 31, -1, -1, 6, 2)]
    [InlineData(24, 50.0, 31, -1, -1, 6, 2)]
    [InlineData(24, 2.0, 14.0, -1, -1, 6, 4)]
    [InlineData(24, 2.0, 13.99, -1, -1, 6, 3)]
    [InlineData(24, 2.0, 7.0, -1, -1, 6, 2)]
    [InlineData(24, 2.0, 6.99, -1, -1, 6, 1)]
    [InlineData(24, 2.0, 0.5, -1, -1, 6, 1)]
    [InlineData(24, 2.0, 28, 7.0, -1, 6, 2)]
    [InlineData(24, 2.0, 28, 1.5, -1, 6, 1)]
    [InlineData(24, 2.0, 28, 0, -1, 6, 1)]
    [InlineData(24, 2.0, 7.0, 20, -1, 6, 2)]
    [InlineData(24, 2.0, 31, 24, -1, 6, 6)]
    [InlineData(24, 50.0, 31, 24, -1, 6, 2)]
    [InlineData(24, 2.0, 31, 10, 2, 6, 4)]
    [InlineData(24, 2.0, 31, 10, 5, 6, 1)]
    [InlineData(24, 2.0, 31, 3, 0.1, 6, 2)]
    [InlineData(24, 2.0, 31, -1, -1, 4, 4)]
    [InlineData(24, 2.0, 31, -1, -1, 1, 1)]
    [InlineData(24, 50.0, 31, -1, -1, 1, 1)]
    [InlineData(4, 0.5, 31, -1, -1, 6, 1)]
    [InlineData(8, 1.0, 31, -1, -1, 6, 2)]
    [InlineData(12, 1.0, 31, -1, -1, 6, 3)]
    [InlineData(8, 3.9, 31, -1, -1, 6, 2)]
    [InlineData(8, 4.0, 31, -1, -1, 6, 2)]
    [InlineData(64, 2.0, 31, -1, -1, 6, 6)]
    [InlineData(64, 2.0, 31, -1, -1, 4, 4)]
    [InlineData(-1, 0, 0, -1, -1, 6, 3)]
    [InlineData(-1, 0, 0, -1, -1, 2, 2)]
    public void Адаптивно_ТаблицаОбщаяСPython(int cpus, double load1, double memGiB, double cgGiB, double peakGiB,
        int max, int expected)
    {
        MachineSnapshot? snapshot = cpus < 0 ? null : new MachineSnapshot(cpus, load1, (long)(memGiB * AdaptiveShards.GiB),
            cgGiB < 0 ? null : (long)(cgGiB * AdaptiveShards.GiB));
        long? peak = peakGiB < 0 ? null : (long)(peakGiB * AdaptiveShards.GiB);
        AdaptiveShards.Choose(snapshot, max, peak).Shards.Should().Be(expected);
    }

    [Fact]
    public void Адаптивно_ПричинаДляСводки()
    {
        AdaptiveShards.Choose(new MachineSnapshot(24, 7.2, 31 * AdaptiveShards.GiB + 5), 6)
            .Should().Be((6, "load1 7,2 из 24, свободно 31 ГиБ"));
        AdaptiveShards.Choose(new MachineSnapshot(24, 7.2, 28 * AdaptiveShards.GiB, 7 * AdaptiveShards.GiB + 5), 6)
            .Should().Be((2, "load1 7,2 из 24, свободно 28 ГиБ, в cgroup 7 ГиБ"));
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
            AdaptiveShards.Read(dir, Path.Combine(dir, "нет-cgroupfs")).Should()
                .Be(new MachineSnapshot(Environment.ProcessorCount, 7.2, 32505856L * 1024), "без cgroup — только MemAvailable");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* занят */ }
        }
    }

    // --- Запас памяти cgroup (AdaptiveShards.CgroupFree) ---

    private sealed class CgroupTree : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "ccs-cg-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Dir, "cg");
        public string File => Path.Combine(Dir, "cgroup");

        // Сервер — в app.slice, шарды — в ccs.slice/ccs-agents.slice под тем же user@<uid>.service
        public const string Server = "/user.slice/user-1000.slice/user@1000.service/app.slice/ccs.service";
        public const string Slice = "user.slice/user-1000.slice/user@1000.service/ccs.slice/ccs-agents.slice";

        public CgroupTree(string own = Server)
        {
            Directory.CreateDirectory(Path.Combine(Root, own.TrimStart('/')));
            Directory.CreateDirectory(Path.Combine(Root, Slice));
            System.IO.File.WriteAllText(File, "0::" + own + "\n");
        }

        public void Limit(string rel, string max, string current)
        {
            var node = Path.Combine(Root, rel);
            Directory.CreateDirectory(node);
            System.IO.File.WriteAllText(Path.Combine(node, "memory.max"), max + "\n");
            System.IO.File.WriteAllText(Path.Combine(node, "memory.current"), current + "\n");
        }

        public long? Free(string? slice = "ccs-agents.slice") => AdaptiveShards.CgroupFree(File, Root, slice);

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* занят */ }
        }
    }

    private static string G(long gib) => (gib * AdaptiveShards.GiB).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Cgroup_SliceИщетсяУПредковСвоегоCgroup_МинимумПоЦепочке()
    {
        using var t = new CgroupTree();
        t.Limit(CgroupTree.Slice, G(24), G(17));
        t.Limit("user.slice/user-1000.slice/user@1000.service/ccs.slice", "max", G(17));
        t.Limit("user.slice/user-1000.slice/user@1000.service/app.slice/ccs.service", G(2), G(2));

        t.Free().Should().Be(7 * AdaptiveShards.GiB, "лимит самого сервера (ccs.service) к шардам в slice не относится");
        t.Free(slice: null).Should().Be(0, "без изоляции шарды живут в cgroup сервера");
        t.Limit("user.slice", G(10), G(5));
        t.Free().Should().Be(5 * AdaptiveShards.GiB, "предок slice с меньшим запасом — он и ограничивает");
    }

    [Fact]
    public void Cgroup_НетФайлаИлиMaxИлиМусор_NullБезИсключения()
    {
        using var t = new CgroupTree();
        AdaptiveShards.CgroupFree(Path.Combine(t.Dir, "нет"), t.Root, "ccs-agents.slice").Should().BeNull("нет /proc/self/cgroup");
        t.Free().Should().BeNull("memory.max нигде нет");
        t.Limit(CgroupTree.Slice, "max", G(17));
        t.Free().Should().BeNull("везде max");
        t.Free("другой.slice").Should().BeNull("slice не найден у предков");
        t.Free("../etc.slice").Should().BeNull("не имя slice");
        t.Limit(CgroupTree.Slice, "мусор", G(1));
        t.Free().Should().BeNull();
        t.Limit(CgroupTree.Slice, G(24), "");
        t.Free().Should().BeNull();
        t.Limit(CgroupTree.Slice, G(24), G(25));
        t.Free().Should().Be(0, "перебор над max — ноль, а не минус");
        System.IO.File.WriteAllText(t.File, "12:memory:/x\n");
        t.Free().Should().BeNull("cgroup v1 — без v2-строки");
    }

    [Theory]
    [InlineData("ccs-agents.slice", "ccs.slice/ccs-agents.slice")]
    [InlineData("ccs.slice", "ccs.slice")]
    [InlineData("a-b-c.slice", "a.slice/a-b.slice/a-b-c.slice")]
    [InlineData("ccs-agents", null)]
    [InlineData("-x.slice", null)]
    [InlineData("a--b.slice", null)]
    [InlineData("a/b.slice", null)]
    public void Cgroup_ПутьSliceПоПравилуSystemd(string slice, string? expected) =>
        AdaptiveShards.SlicePath(slice).Should().Be(expected);

    // --- Пик на шард прошлого прогона (ShardPeakMemory) и сумма RSS дерева (ProcessTreeRss) ---

    [Fact]
    public void Пик_ЗаписьЧтение_МусорИОдинПроцесс_Null()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs-peak-" + Guid.NewGuid().ToString("N"));
        try
        {
            ShardPeakMemory.Load(null).Should().BeNull();
            ShardPeakMemory.Load(dir).Should().BeNull("каталога нет");
            ShardPeakMemory.Save(dir, 6 * AdaptiveShards.GiB, 1);
            ShardPeakMemory.Save(dir, 0, 3);
            ShardPeakMemory.Load(dir).Should().BeNull("один процесс и пустой пик не пишутся");
            ShardPeakMemory.Save(dir, 12 * AdaptiveShards.GiB, 4);
            ShardPeakMemory.Load(dir).Should().Be(3 * AdaptiveShards.GiB);
            foreach (var junk in new[] { "мусор", "[]", "{\"perShardBytes\":0}", "{\"perShardBytes\":-5}",
                         "{\"perShardBytes\":300000000000000}" })
            {
                File.WriteAllText(Path.Combine(dir, ShardPeakMemory.FileName), junk);
                ShardPeakMemory.Load(dir).Should().BeNull(junk);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* занят */ }
        }
    }

    [Fact]
    public void Пик_ПамятьНаШард_ЗапасноеМножительИПол()
    {
        AdaptiveShards.ShardMem(null).Should().Be(AdaptiveShards.ShardMemBytes);
        AdaptiveShards.ShardMem(2 * AdaptiveShards.GiB).Should().Be((long)(2 * AdaptiveShards.GiB * 1.2));
        AdaptiveShards.ShardMem(1).Should().Be((long)(AdaptiveShards.GiB * 1.2), "не ниже 1 ГиБ");
    }

    [Fact]
    public void RssДерева_СуммаКорнейИПотомков_БезЧужих()
    {
        var proc = Path.Combine(Path.GetTempPath(), "ccs-procfs-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Proc(int pid, int ppid, long kb)
            {
                Directory.CreateDirectory(Path.Combine(proc, pid.ToString()));
                File.WriteAllText(Path.Combine(proc, pid.ToString(), "status"), $"Name:\tx\nPPid:\t{ppid}\nVmRSS:\t   {kb} kB\n");
            }
            Proc(10, 1, 100);
            Proc(11, 10, 200);
            Proc(12, 11, 300);
            Proc(20, 1, 1000);
            Proc(30, 1, 5000); // чужой
            Directory.CreateDirectory(Path.Combine(proc, "self"));
            Directory.CreateDirectory(Path.Combine(proc, "40")); // процесс вышел: status нет

            ProcessTreeRss.SumBytes([10, 20, 40], proc).Should().Be((100 + 200 + 300 + 1000) * 1024L);
            ProcessTreeRss.SumBytes([], proc).Should().Be(0);
            ProcessTreeRss.SumBytes([10], Path.Combine(proc, "нет")).Should().Be(0, "нет /proc — как на Windows");
        }
        finally
        {
            try { Directory.Delete(proc, recursive: true); } catch { /* занят */ }
        }
    }
}
