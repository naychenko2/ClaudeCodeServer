using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Тесты контракта среза v1 (Ш1a, ADR-029 «Узлы фермы»): NodeSliceParser.Parse — чистая функция
/// разбора недоверенного тела: любой вход даёт результат (Ok/Incompatible/Invalid), исключение
/// наружу не выходит никогда. Потолки — из ADR, константами в NodeSlice.cs: тело ≤ 256 КБ до разбора,
/// карт ≤ 16, чипов hwmon ≤ 64 (наименьшие N), записей files/links ≤ 4096, строка > 4 КБ отбрасывается
/// целиком (ADR: «отбрасывается целиком, а не обрезается»), исключения — mountinfo ≤ 64 КБ, gddr6 и ipmi
/// ≤ 16 КБ. ipmi — необязательное поле без смены v: нет ключа / null / не строка → null, срез принимается;
/// TryReadIpmi — разбор обёртки {at, sdr} со свежестью clockMs − at ≤ 30 000 (несвежее/кривое → null).
/// Плюс реестр HardwareNodesOptions: EffectiveNodes — пропуск неверных элементов и фолбэк worker/local.
/// </summary>
public class NodeSliceTests
{
    // ─── Happy path: валидный срез v1 ─────────────────────────────────────────────

    [Fact]
    public void Parse_ValidV1_Ok()
    {
        var r = NodeSliceParser.Parse(Encoding.UTF8.GetBytes(ValidSliceJson()));

        r.Status.Should().Be(NodeSliceParseStatus.Ok);
        var s = r.Slice!;
        s.V.Should().Be(1);
        s.AgeMs.Should().Be(840);
        s.ClockMs.Should().Be(1763283600123);
        s.Files["/sys/class/hwmon/hwmon2/name"].Should().Be("k10temp\n");
        s.Files["/proc/meminfo"].Should().StartWith("MemTotal:");
        s.Links["/sys/class/hwmon/hwmon4/device"].Should().Be("../../nvme0");
        s.GpuCsv.Should().StartWith("0, GPU-uuid, NVIDIA GeForce RTX 3090");
        s.Gddr6.Should().Contain("\"at\"");
        s.Statvfs.Should().ContainSingle().Which.Mount.Should().Be("/");
        s.Statvfs[0].Total.Should().Be(1999421571072);
        s.Statvfs[0].Free.Should().Be(812345671680);
    }

    // ─── Версия: принимаемая только 1 (ADR-029, «Версии и статусы узла») ─────────

    [Fact]
    public void Parse_Version99_Incompatible()
    {
        var r = ParseSlice(v: 99);

        r.Status.Should().Be(NodeSliceParseStatus.Incompatible);
        r.Version.Should().Be(99);
        r.Slice.Should().BeNull();
    }

    [Fact]
    public void Parse_VersionMissingOrNotNumber_Incompatible()
    {
        var noV = NodeSliceParser.Parse(Encoding.UTF8.GetBytes(
            """{"ageMs":0,"clockMs":0,"files":{},"links":{},"gpuCsv":null,"gddr6":null,"statvfs":[]}"""));
        noV.Status.Should().Be(NodeSliceParseStatus.Incompatible);
        noV.Version.Should().Be(0);

        var strV = NodeSliceParser.Parse(Encoding.UTF8.GetBytes(
            """{"v":"1","ageMs":0,"clockMs":0,"files":{},"links":{},"gpuCsv":null,"gddr6":null,"statvfs":[]}"""));
        strV.Status.Should().Be(NodeSliceParseStatus.Incompatible);
    }

    // ─── Не срез: не-JSON, корень не объект, пустое/длинное тело → Invalid ───────

    [Fact]
    public void Parse_NotJson_Invalid()
    {
        var r = NodeSliceParser.Parse(Encoding.UTF8.GetBytes("это не JSON {{{"));

        r.Status.Should().Be(NodeSliceParseStatus.Invalid);
        r.Slice.Should().BeNull();
    }

    [Fact]
    public void Parse_RootNotObject_Invalid()
    {
        NodeSliceParser.Parse(Encoding.UTF8.GetBytes("[1,2,3]")).Status.Should().Be(NodeSliceParseStatus.Invalid);
        NodeSliceParser.Parse(Encoding.UTF8.GetBytes("null")).Status.Should().Be(NodeSliceParseStatus.Invalid);
    }

    [Fact]
    public void Parse_EmptyBody_Invalid()
    {
        var r = NodeSliceParser.Parse(ReadOnlySpan<byte>.Empty);

        r.Status.Should().Be(NodeSliceParseStatus.Invalid);
    }

    [Fact]
    public void Parse_BodyOver256K_Invalid_BeforeParse()
    {
        // 256 КБ + 1 байт — отброшен по размеру ДО разбора (причина — размер, не битый JSON);
        // ровно 256 КБ — по размеру проходит (здесь падает как битый JSON).
        var over = new byte[NodeSliceLimits.MaxBodyBytes + 1];
        Array.Fill(over, (byte)'x');
        var r = NodeSliceParser.Parse(over);
        r.Status.Should().Be(NodeSliceParseStatus.Invalid);
        r.Reason.Should().Contain("262144");

        var at = new byte[NodeSliceLimits.MaxBodyBytes];
        Array.Fill(at, (byte)'x');
        NodeSliceParser.Parse(at).Reason.Should().StartWith("сбой разбора");
    }

    [Fact]
    public void Parse_RequiredFieldWrongType_Invalid()
    {
        var r = NodeSliceParser.Parse(Encoding.UTF8.GetBytes(
            """{"v":1,"ageMs":"840","clockMs":0,"files":{},"links":{},"gpuCsv":null,"gddr6":null,"statvfs":[]}"""));

        r.Status.Should().Be(NodeSliceParseStatus.Invalid);
    }

    // ─── Потолки содержимого ──────────────────────────────────────────────────────

    [Fact]
    public void Parse_GpuCsv17Rows_KeepsFirst16()
    {
        var csv = string.Join("\n", Enumerable.Range(0, 17).Select(i => $"{i}, GPU-{i}, name, 1, 1")) + "\n";

        var s = ParseSlice(gpuCsv: csv).Slice!;

        var rows = s.GpuCsv!.Split('\n');
        rows.Should().HaveCount(16, "разбираем первые 16 строк, остальное отбрасываем");
        rows[0].Should().Be("0, GPU-0, name, 1, 1");
        rows[15].Should().Be("15, GPU-15, name, 1, 1");
    }

    [Fact]
    public void Parse_Hwmon65Dirs_Keeps64SmallestN_RegardlessOfJsonOrder()
    {
        // 65 каталогов, в обратном порядке N: «первые 64 по порядку» оставили бы 64..1 и выкинули hwmon0 —
        // ADR требует наименьшие N (агент обходит по возрастанию). Не-hwmon запись не чип — остаётся.
        var files = new Dictionary<string, string>();
        for (var n = 64; n >= 0; n--)
            files[$"/sys/class/hwmon/hwmon{n}/name"] = "chip\n";
        files["/proc/uptime"] = "123456.78 456789.01\n";

        var s = ParseSlice(files: files).Slice!;

        s.Files.Should().HaveCount(65, "64 чипа + /proc/uptime");
        for (var n = 0; n < 64; n++)
            s.Files.Should().ContainKey($"/sys/class/hwmon/hwmon{n}/name");
        s.Files.Should().NotContainKey("/sys/class/hwmon/hwmon64/name");
        s.Files.Should().ContainKey("/proc/uptime");
    }

    [Fact]
    public void Parse_ValueOver4Kb_DroppedEntirely_NotTruncated()
    {
        // ADR-029 «Потолки»: значение длиннее потолка отбрасывается ЦЕЛИКОМ (как будто файла нет),
        // не обрезается. Граница: ровно 4096 байта остаётся, 4097 — нет.
        var files = new Dictionary<string, string>
        {
            ["/sys/class/hwmon/hwmon2/temp1_input"] = new string('x', 4096),
            ["/proc/uptime"] = new string('x', 4097),
        };

        var s = ParseSlice(files: files).Slice!;

        s.Files.Should().ContainKey("/sys/class/hwmon/hwmon2/temp1_input");
        s.Files.Should().NotContainKey("/proc/uptime");
    }

    [Fact]
    public void Parse_MountinfoOver4K_UpTo64K_Kept()
    {
        // Исключение из потолка строки: /proc/self/mountinfo после фильтра — до 64 КБ.
        var files = new Dictionary<string, string> { ["/proc/self/mountinfo"] = new string('x', 10_000) };

        var s = ParseSlice(files: files).Slice!;

        s.Files.Should().ContainKey("/proc/self/mountinfo");
    }

    [Fact]
    public void Parse_Gddr6Over16K_Null()
    {
        ParseSlice(gddr6: new string('x', NodeSliceLimits.MaxGddr6Bytes + 1)).Slice!.Gddr6.Should().BeNull();
        ParseSlice(gddr6: new string('x', NodeSliceLimits.MaxGddr6Bytes)).Slice!.Gddr6.Should().NotBeNull();
    }

    // ─── ipmi: необязательное поле без смены v (ADR-029, «Узел — набор устройств») ───

    [Fact]
    public void Parse_IpmiAbsentOrNull_Ok_Null()
    {
        // Нет ключа — Ok, Ipmi null: это не Invalid (отличается от обязательных полей).
        var noKey = NodeSliceParser.Parse(Encoding.UTF8.GetBytes(
            """{"v":1,"ageMs":0,"clockMs":1000,"files":{},"links":{},"gpuCsv":null,"gddr6":null,"statvfs":[]}"""));
        noKey.Status.Should().Be(NodeSliceParseStatus.Ok);
        noKey.Slice!.Ipmi.Should().BeNull();

        // Ключ с null — то же (агент так и отдаёт, когда файла нет).
        ParseSlice(ipmi: null).Slice!.Ipmi.Should().BeNull();
    }

    [Fact]
    public void Parse_IpmiNotString_Ok_Null()
    {
        // Не строка — null, срез при этом принимается.
        ParseSlice(ipmi: 123).Slice!.Ipmi.Should().BeNull();
    }

    [Fact]
    public void Parse_IpmiOver16K_Ok_Null_NotTruncated()
    {
        // Потолок 16 КБ, как у gddr6: сверх — отбрасывается целиком, не обрезается. Граница включена.
        ParseSlice(ipmi: new string('x', NodeSliceLimits.MaxIpmiBytes + 1)).Slice!.Ipmi.Should().BeNull();
        ParseSlice(ipmi: new string('x', NodeSliceLimits.MaxIpmiBytes)).Slice!.Ipmi.Should().NotBeNull();
    }

    [Fact]
    public void TryReadIpmi_Fresh30000_HasValue_Stale30001_Null()
    {
        // Граница свежести: clockMs − at = 30 000 — значение есть; 30 001 — null.
        var fresh = ParseSlice(clockMs: 1_000_000, ipmi: """{"at":970000,"sdr":"CPU1 Temp | 30.1 | ok | 3.1 | 42 degrees C"}""").Slice!;
        var v = fresh.TryReadIpmi();
        v.Should().NotBeNull();
        v!.Value.at.Should().Be(970000);
        v.Value.sdr.Should().StartWith("CPU1 Temp");

        var stale = ParseSlice(clockMs: 1_000_000, ipmi: """{"at":969999,"sdr":"CPU1 Temp | 30.1 | ok | 3.1 | 42 degrees C"}""").Slice!;
        stale.TryReadIpmi().Should().BeNull();
    }

    [Fact]
    public void TryReadIpmi_BadWrapper_Null()
    {
        // Несвежий или кривой → null; текстов нет (помощника на узле нет) → null.
        ParseSlice().Slice!.TryReadIpmi().Should().BeNull();
        ParseSlice(ipmi: "не JSON").Slice!.TryReadIpmi().Should().BeNull();
        ParseSlice(ipmi: """[1,2]""").Slice!.TryReadIpmi().Should().BeNull(); // корень не объект
        ParseSlice(ipmi: """{"at":1}""").Slice!.TryReadIpmi().Should().BeNull(); // нет sdr
        ParseSlice(ipmi: """{"sdr":"x"}""").Slice!.TryReadIpmi().Should().BeNull(); // нет at
        ParseSlice(ipmi: """{"at":"1","sdr":"x"}""").Slice!.TryReadIpmi().Should().BeNull(); // at не число
    }

    [Fact]
    public void Parse_FilesOver4096_KeepsFirst4096()
    {
        var files = new Dictionary<string, string>();
        for (var i = 0; i < 4097; i++)
            files[$"/proc/custom/{i}"] = "1\n";

        var s = ParseSlice(files: files).Slice!;

        s.Files.Should().HaveCount(4096);
    }

    [Fact]
    public void Parse_StatvfsOver64_KeepsFirst64()
    {
        var statvfs = Enumerable.Range(0, 65).Select(i => (object)new { mount = $"/m{i}", total = 1L, free = 1L }).ToList();

        var s = ParseSlice(statvfs: statvfs).Slice!;

        s.Statvfs.Should().HaveCount(64);
    }

    // ─── Недоверенный вход: ни разу не бросает (c2) ──────────────────────────────

    [Fact]
    public void Parse_UntrustedInput_NeverThrows()
    {
        var anyStatus = new[] { NodeSliceParseStatus.Ok, NodeSliceParseStatus.Incompatible, NodeSliceParseStatus.Invalid };

        // 200 случайных буферов, фиксированный seed.
        var rng = new Random(20261116);
        for (var i = 0; i < 200; i++)
        {
            var buf = new byte[rng.Next(0, 2048)];
            rng.NextBytes(buf);
            NodeSliceParser.Parse(buf).Status.Should().BeOneOf(anyStatus);
        }

        // Все усечённые префиксы валидного среза.
        var valid = Encoding.UTF8.GetBytes(ValidSliceJson());
        for (var cut = 0; cut < valid.Length; cut++)
            NodeSliceParser.Parse(valid.AsSpan(0, cut)).Status.Should().BeOneOf(anyStatus);
    }

    // ─── Реестр HardwareNodes (ADR-029, «Реестр HardwareNodes») ───────────────────

    [Fact]
    public void EffectiveNodes_EmptySection_SingleWorkerLocal()
    {
        var nodes = new HardwareNodesOptions().EffectiveNodes();

        nodes.Should().ContainSingle();
        nodes[0].Key.Should().Be("worker");
        nodes[0].Title.Should().Be("worker");
        nodes[0].Source.Should().Be("local");
    }

    [Fact]
    public void EffectiveNodes_FromExampleFile_ThreeNodes()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "backend", "ClaudeHomeServer", "appsettings.Local.example.json"))
            .Build();

        var nodes = HardwareNodesOptions.FromConfiguration(config).EffectiveNodes();

        nodes.Select(n => n.Key).Should().Equal("worker", "ai", "video");
        nodes[0].Source.Should().Be("local");
        nodes[1].Source.Should().Be("http://192.168.x.x:9101");
        nodes[2].Media.Should().BeTrue();
    }

    [Fact]
    public void EffectiveNodes_SkipsInvalidElements_KeepsValid()
    {
        var opts = new HardwareNodesOptions
        {
            Nodes =
            [
                new HardwareNodeDef { Key = "worker", Source = "local" },
                new HardwareNodeDef { Key = "Worker", Source = "local" }, // не ключ: заглавная
                new HardwareNodeDef { Key = "ai", Source = "http://10.0.0.11:9105" },
                new HardwareNodeDef { Key = "ai", Source = "http://10.0.0.12:9105" }, // дубль ключа
                new HardwareNodeDef { Key = "bad-url", Source = "http://10.0.0.13:9105/stats" }, // URL с путём
                new HardwareNodeDef { Key = "ftp", Source = "ftp://10.0.0.14:9105" }, // не http(s)
                new HardwareNodeDef { Key = "second-local", Source = "local" }, // второй local
            ],
        };

        var nodes = opts.EffectiveNodes();

        nodes.Select(n => n.Key).Should().Equal("worker", "ai");
    }

    [Fact]
    public void EffectiveNodes_AllInvalid_FallbackWorkerLocal()
    {
        var opts = new HardwareNodesOptions
        {
            Nodes = [new HardwareNodeDef { Key = "Bad Key!", Source = "local" }],
        };

        var nodes = opts.EffectiveNodes();

        nodes.Should().ContainSingle().Which.Source.Should().Be("local");
    }

    [Fact]
    public void FromConfiguration_ObjectForm_ReadsNodes()
    {
        // Допускается и объект с "Nodes" (aside от массива из ADR) — чтобы форма конфига не ломала фолбэк.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HardwareNodes:Nodes:0:Key"] = "ai",
                ["HardwareNodes:Nodes:0:Source"] = "http://10.0.0.11:9105",
            })
            .Build();

        var nodes = HardwareNodesOptions.FromConfiguration(config).EffectiveNodes();

        nodes.Should().ContainSingle().Which.Key.Should().Be("ai");
    }

    // ─── Хелперы ──────────────────────────────────────────────────────────────────

    // Срез по образцу ADR-029 («Срез v1»): все обязательные ключи на месте.
    private static string ValidSliceJson() =>
        """{"v":1,"ageMs":840,"clockMs":1763283600123,"files":{"/sys/class/hwmon/hwmon2/name":"k10temp\n","/proc/meminfo":"MemTotal:       127341452 kB\n"},"links":{"/sys/class/hwmon/hwmon4/device":"../../nvme0"},"gpuCsv":"0, GPU-uuid, NVIDIA GeForce RTX 3090, 97, 41, 24000, 24576, 61, 30, 1900, 1965, 9751, P3, 1, 16, 300.00, 300.00, Not Active, Not Active, Not Active, 00000000:0B:00.0\n","gddr6":"{\"at\": 1763283599800, \"gpus\": []}","statvfs":[{"mount":"/","total":1999421571072,"free":812345671680}]}""";

    // Срез с подменёнными полями: остальные — валидные, чтобы краснел ровно проверяемый потолок.
    private static NodeSliceParseResult ParseSlice(
        int v = 1,
        Dictionary<string, string>? files = null,
        Dictionary<string, string>? links = null,
        string? gpuCsv = null,
        string? gddr6 = null,
        object? ipmi = null,
        List<object>? statvfs = null,
        long ageMs = 0,
        long clockMs = 0)
    {
        var body = new
        {
            v,
            ageMs,
            clockMs,
            files = files ?? new Dictionary<string, string>(),
            links = links ?? new Dictionary<string, string>(),
            gpuCsv,
            gddr6,
            ipmi,
            statvfs = statvfs ?? new List<object> { new { mount = "/", total = 1L, free = 1L } },
        };
        return NodeSliceParser.Parse(JsonSerializer.SerializeToUtf8Bytes(body));
    }

    // Корень репозитория от каталога сборки (как в ClaudeCliChangelogTests): .git — папка или файл worktree.
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
               && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден");
    }
}
