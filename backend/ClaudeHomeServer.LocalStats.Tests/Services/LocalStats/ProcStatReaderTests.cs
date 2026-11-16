using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Тесты чистых функций разбора (Ш1c): текст /proc/meminfo (MeminfoReader.Parse — тот же
/// разбор, что Read по файлу), /proc/uptime (ParseUptimeS: первое число, вниз до целого),
/// строка «cpu» /proc/stat (ParseCpu: total = сумма полей, idle = idle+iowait) и загрузка CPU
/// как дельта двух соседних срезов (CpuLoadPerc). Текст — недоверенный везде:
/// null/пусто/мусор/отрицательное/вне long → null, без исключений.
/// </summary>
public class ProcStatReaderTests
{
    // ─── MeminfoReader.Parse (тот же разбор, что Read по файлу) ─────────────────

    [Fact]
    public void Meminfo_Parse_UsedTotal()
    {
        var text = "MemTotal:       127341452 kB\nMemFree:          1024 kB\nMemAvailable:   67108864 kB\n";
        var snap = MeminfoReader.Parse(text)!;
        snap.TotalGb.Should().Be(121, "127341452 kB / 1024 / 1024");
        snap.UsedGb.Should().Be(57, "127341452 − 67108864 kB");
    }

    [Fact]
    public void Meminfo_Parse_CrlfLines_Parsed()
    {
        var snap = MeminfoReader.Parse("MemTotal: 2097152 kB\r\nMemAvailable: 1048576 kB\r\n")!;
        snap.TotalGb.Should().Be(2);
        snap.UsedGb.Should().Be(1);
    }

    [Fact]
    public void Meminfo_Parse_NoAvailable_UsedEqualsTotal()
    {
        var snap = MeminfoReader.Parse("MemTotal: 1048576 kB")!;
        snap.UsedGb.Should().Be(1, "нет MemAvailable — занято = всего (как раньше по файлу)");
        snap.TotalGb.Should().Be(1);
    }

    [Fact]
    public void Meminfo_Read_SameAsParse_ByFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "meminfo_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "MemTotal: 1048576 kB\nMemAvailable: 524288 kB\n");
        try
        {
            var byFile = new MeminfoReader(path).Read()!;
            var byText = MeminfoReader.Parse("MemTotal: 1048576 kB\nMemAvailable: 524288 kB\n")!;
            byFile.TotalGb.Should().Be(byText.TotalGb);
            byFile.UsedGb.Should().Be(byText.UsedGb);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("cpu -1 x")]
    [InlineData("MemTotal: 9223372036854775808 kB")] // вне long
    [InlineData("MemTotal: -1 kB")]                  // отрицательное
    public void Meminfo_Parse_Garbage_Null(string? text) =>
        MeminfoReader.Parse(text).Should().BeNull("нет MemTotal или значение — мусор → null");

    // ─── /proc/uptime ────────────────────────────────────────────────────────────

    [Fact]
    public void Uptime_FirstNumber_FlooredDown()
    {
        ProcStatReader.ParseUptimeS("12345.67 999.0").Should().Be(12345, "первое число, вниз до целого");
        ProcStatReader.ParseUptimeS("12345.67 999.0\n").Should().Be(12345);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("cpu -1 x")]
    [InlineData("-5.5 100.0")]                  // отрицательное
    [InlineData("9223372036854775808 1.0")]     // вне long
    [InlineData(".5")]                          // нет целой части
    public void Uptime_Garbage_Null(string? text) =>
        ProcStatReader.ParseUptimeS(text).Should().BeNull();

    // ─── /proc/stat: строка «cpu» ────────────────────────────────────────────────

    private const string StatSample =
        "cpu  237419 103 134093 8935064 4544 0 4352 0 0 0\n" +
        "cpu0 100 0 50 1000 10 0 0 0 0 0\n" +
        "intr 12345 6\nprocesses 1234\nprocs_running 1\nprocs_blocked 0\n";

    [Fact]
    public void CpuLine_TotalIsSum_IdleIsIdlePlusIowait()
    {
        var j = ProcStatReader.ParseCpu(StatSample)!;
        j.Total.Should().Be(237419 + 103 + 134093 + 8935064 + 4544 + 0 + 4352 + 0 + 0 + 0,
            "сумма всех полей строки cpu");
        j.Idle.Should().Be(8935064 + 4544, "idle = idle+iowait (поля 4 и 5)");
    }

    [Fact]
    public void CpuLine_NoIowait_Field4Only()
    {
        var j = ProcStatReader.ParseCpu("cpu 10 20 30 400")!;
        j.Total.Should().Be(460);
        j.Idle.Should().Be(400, "в старых ядрах iowait нет — только поле idle");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("cpu -1 x")]
    [InlineData("cpu0 1 2 3 4")]                    // нет строки «cpu»
    [InlineData("cpu 1 2 3")]                       // меньше 4 полей (нет idle)
    [InlineData("cpu 1 2 3 9223372036854775808")]   // вне long
    [InlineData("cpu 1 2 3 -4")]                    // отрицательное
    public void CpuLine_Garbage_Null(string? text) =>
        ProcStatReader.ParseCpu(text).Should().BeNull();

    // ─── Загрузка CPU: дельта двух соседних срезов ───────────────────────────────

    private static CpuJiffies J(long total, long idle) => new() { Total = total, Idle = idle };

    [Fact]
    public void Load_Pair_Plus1000_250_Is75()
    {
        ProcStatReader.CpuLoadPerc(J(1000, 500), J(2000, 750)).Should().Be(75,
            "Δtotal 1000, Δidle 250 → (1000−250)/1000 = 75");
    }

    [Theory]
    [InlineData(1000L, 1000L, 0)]   // всё время простаивали
    [InlineData(1000L, 0L, 100)]    // всё время работали
    public void Load_EndsOfRange(long dTotal, long dIdle, int expected) =>
        ProcStatReader.CpuLoadPerc(J(0, 0), J(dTotal, dIdle)).Should().Be(expected);

    [Fact]
    public void Load_Fractional_FloorsDown()
    {
        ProcStatReader.CpuLoadPerc(J(0, 0), J(3, 1)).Should().Be(66, "2/3 → 66 (целое вниз)");
    }

    [Fact]
    public void Load_NoPrev_Null()
    {
        ProcStatReader.CpuLoadPerc(null, J(1000, 500)).Should().BeNull();
        ProcStatReader.CpuLoadPerc(J(1000, 500), null).Should().BeNull();
    }

    [Fact]
    public void Load_CounterBack_Null()
    {
        // Мутация «убрать проверку Δtotal ≤ 0» → краснеет: без неё деление даёт 0 вместо null.
        ProcStatReader.CpuLoadPerc(J(1000, 500), J(500, 0)).Should().BeNull("счётчик ушёл назад — перезагрузка узла");
        // Мутация той же проверки → DivideByZero.
        ProcStatReader.CpuLoadPerc(J(1000, 500), J(1000, 700)).Should().BeNull("за интервал не было тиков");
    }

    [Fact]
    public void Load_IdleWentBack_ClampedTo100()
    {
        // Мутация «убрать Math.Clamp» → краснеет: без клампа (100−(−50))/100 = 150.
        ProcStatReader.CpuLoadPerc(J(1000, 500), J(1100, 450)).Should().Be(100,
            "Δtotal 100, Δidle −50 → зажатие к 100");
    }

    [Fact]
    public void Load_IdleGrewMoreThanTotal_ClampedTo0()
    {
        // Мутация «убрать Math.Clamp» → краснеет: без клампа (100−150)/100 = −50.
        ProcStatReader.CpuLoadPerc(J(1000, 500), J(1100, 650)).Should().Be(0,
            "Δidle 150 > Δtotal 100 → зажатие к 0");
    }

    // ─── Сквозной кейс живой машины: два среза /proc/stat → загрузка ─────────────

    [Fact]
    public void Load_TwoStatSnapshots_RealFormat()
    {
        var prev = ProcStatReader.ParseCpu("cpu  1000 0 500 8000 0 0 0 0 0 0")!;
        var cur = ProcStatReader.ParseCpu("cpu  1200 0 700 8100 0 0 0 0 0 0")!;
        // Δtotal = 200+200+100 = 500, Δidle = 100 → 80%.
        ProcStatReader.CpuLoadPerc(prev, cur).Should().Be(80);
    }
}
