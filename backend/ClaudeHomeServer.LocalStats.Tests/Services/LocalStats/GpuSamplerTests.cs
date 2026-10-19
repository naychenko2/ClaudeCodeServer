using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Тесты сэмплера видеокарт: ReadAsync возвращает ВСЕ карты хоста (IReadOnlyList&lt;GpuSample&gt;),
/// разбор CSV nvidia-smi (csv,noheader,nounits) с [N/A] и новыми полями вкладки «Железо»
/// (fan.speed, clocks.*, pstate, pcie.*, utilization.memory, clocks_event_reasons.*, pci.bus_id),
/// кэш на GpuCacheTtl (2 с) на фейковых часах, нет бинаря/ошибка/пусто/мусор → пустой список
/// (без исключений наружу). Запуск процесса подменяется фейковым runner (internal-конструктор).
/// </summary>
public class GpuSamplerTests
{
    // Фейковые часы: время двигает тест, а не Task.Delay.
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    // Строит полную 21-полевую строку nvidia-smi (2 + 1 name + 18 полей хвоста) в фиксированном
    // порядке QueryArgs. Необязательные поля — по умолчанию; тест задаёт только то, что проверяет.
    private static string Line(
        int index, string uuid, string name,
        string util = "99", string utilMem = "51", string memUsed = "24000", string memTotal = "24576",
        string temp = "71", string fan = "0", string clocksSm = "1545", string clocksMaxSm = "2100",
        string clocksMem = "9501", string pstate = "P2", string pcieGen = "4", string pcieWidth = "8",
        string powerDraw = "338", string powerLimit = "350",
        string swPowerCap = "Active", string hwThermal = "Not Active", string swThermal = "Not Active",
        string busId = "00000000:0B:00.0")
    {
        return string.Join(',',
            index, uuid, name,
            util, utilMem, memUsed, memTotal, temp, fan, clocksSm, clocksMaxSm, clocksMem,
            pstate, pcieGen, pcieWidth, powerDraw, powerLimit, swPowerCap, hwThermal, swThermal, busId);
    }

    // Две карты хоста (TP=2): ReadAsync возвращает ОБЕ, не только первую.
    // Мутация «вернуть samples[0]» (только первую карту) → count 1, тест краснеет.
    [Fact]
    public async Task ReadAsync_TwoCards_ReturnsBoth()
    {
        var time = new ManualTime();
        var text =
            Line(0, "GPU-uuid-1", "NVIDIA GeForce RTX 3090", util: "99", busId: "00000000:0B:00.0") + "\n" +
            Line(1, "GPU-uuid-2", "NVIDIA GeForce RTX 3090", util: "100", busId: "00000000:0C:00.0");
        var sampler = new GpuSampler(time, TimeSpan.FromSeconds(2), () => Task.FromResult<string?>(text));

        var samples = await sampler.ReadAsync();
        samples.Should().HaveCount(2, "обе карты хоста, не только первая");
        samples[0].Index.Should().Be(0);
        samples[0].Uuid.Should().Be("GPU-uuid-1");
        samples[0].Utilization.Should().Be(99);
        samples[1].Index.Should().Be(1);
        samples[1].Uuid.Should().Be("GPU-uuid-2");
        samples[1].Utilization.Should().Be(100);
    }

    // Одна карта: ReadAsync возвращает одну.
    [Fact]
    public async Task ReadAsync_OneCard_ReturnsOne()
    {
        var time = new ManualTime();
        var sampler = new GpuSampler(time, TimeSpan.FromSeconds(2),
            () => Task.FromResult<string?>(Line(0, "GPU-1", "NVIDIA GeForce RTX 4090", util: "50", memUsed: "100", memTotal: "200", temp: "40", powerDraw: "1.0", powerLimit: "2.0")));

        var samples = await sampler.ReadAsync();
        samples.Should().HaveCount(1);
        samples[0].Index.Should().Be(0);
        samples[0].Name.Should().Be("NVIDIA GeForce RTX 4090");
    }

    // Пустой вывод (карт нет) → пустой список, без исключений наружу.
    [Fact]
    public async Task ReadAsync_EmptyOutput_ReturnsEmpty()
    {
        var time = new ManualTime();
        var sampler = new GpuSampler(time, TimeSpan.FromSeconds(2), () => Task.FromResult<string?>(""));
        (await sampler.ReadAsync()).Should().BeEmpty();
    }

    // Мусорный вывод (не CSV: меньше 21 поля) → пустой список, без исключений наружу.
    [Fact]
    public async Task ReadAsync_GarbageOutput_ReturnsEmpty()
    {
        var time = new ManualTime();
        var sampler = new GpuSampler(time, TimeSpan.FromSeconds(2),
            () => Task.FromResult<string?>("NVIDIA GeForce RTX 3090\nfoo,bar"));
        (await sampler.ReadAsync()).Should().BeEmpty();
    }

    // Нет бинаря/ошибка (runner бросает) → пустой список, без исключений наружу.
    [Fact]
    public async Task ReadAsync_NoBinary_ReturnsEmpty()
    {
        var time = new ManualTime();
        var sampler = new GpuSampler(time, TimeSpan.FromSeconds(2),
            () => throw new System.ComponentModel.Win32Exception(2, "not found"));
        (await sampler.ReadAsync()).Should().BeEmpty("нет бинаря/ошибка → пустой список, без исключений наружу");
    }

    // Кэш 2 с на фейковых часах: в пределах TTL runner не дёргается повторно,
    // после TTL — дёргается снова.
    [Fact]
    public async Task Cache_WithinTtl_DoesNotRerun_AfterTtl_Reruns()
    {
        var time = new ManualTime();
        var runs = 0;
        var sampler = new GpuSampler(time, TimeSpan.FromSeconds(2),
            () => { runs++; return Task.FromResult<string?>(Line(0, "GPU-1", "Card", util: "50", memUsed: "100", memTotal: "200", temp: "40", powerDraw: "1.0", powerLimit: "2.0")); });

        var first = await sampler.ReadAsync();
        first.Should().HaveCount(1);
        runs.Should().Be(1);

        time.Advance(TimeSpan.FromSeconds(1)); // < 2 с
        var second = await sampler.ReadAsync();
        second.Should().HaveCount(1);
        runs.Should().Be(1, "в пределах TTL runner не дёргается повторно");

        time.Advance(TimeSpan.FromSeconds(2)); // > 2 с
        await sampler.ReadAsync();
        runs.Should().Be(2, "после TTL runner дёргается снова");
    }

    // ─── Разбор CSV (статический ParseCsv) ─────────────────────────────────────────

    // (1) Разбор CSV: 2 карты, [N/A] в числовых полях → null, имя с пробелами.
    // Новые поля вкладки «Железо» на месте: fan, clocks, pstate, pcie, utilization.memory,
    // clocks_event_reasons (Active/Not Active → true/false), busId.
    [Fact]
    public void ParseCsv_TwoCards_WithNa()
    {
        var text =
            Line(0, "GPU-uuid-1", "NVIDIA GeForce RTX 4090", util: "85", memUsed: "12000", memTotal: "24576",
                temp: "60", powerDraw: "300.5", powerLimit: "450.0",
                swPowerCap: "Not Active", hwThermal: "Active", swThermal: "Not Active") + "\n" +
            Line(1, "GPU-uuid-2", "NVIDIA A100", util: "[N/A]", memUsed: "8000", memTotal: "81920",
                temp: "[N/A]", fan: "[N/A]", clocksSm: "[N/A]", pstate: "[N/A]",
                powerDraw: "[N/A]", swPowerCap: "[N/A]", busId: "00000000:0D:00.0");
        var samples = GpuSampler.ParseCsv(text);
        samples.Should().HaveCount(2);

        samples[0].Index.Should().Be(0);
        samples[0].Uuid.Should().Be("GPU-uuid-1");
        samples[0].Name.Should().Be("NVIDIA GeForce RTX 4090");
        samples[0].Utilization.Should().Be(85);
        samples[0].MemoryUsed.Should().Be(12000);
        samples[0].MemoryTotal.Should().Be(24576);
        samples[0].Temperature.Should().Be(60);
        samples[0].PowerDraw.Should().Be(300.5);
        samples[0].PowerLimit.Should().Be(450.0);
        // Новые поля: троттлинг (Active → true, Not Active → false), pstate, pcie, busId.
        samples[0].SwPowerCap.Should().BeFalse("Not Active → false");
        samples[0].HwThermalSlowdown.Should().BeTrue("Active → true");
        samples[0].SwThermalSlowdown.Should().BeFalse();
        samples[0].Pstate.Should().Be("P2");
        samples[0].PcieGen.Should().Be(4);
        samples[0].PcieWidth.Should().Be(8);
        samples[0].BusId.Should().Be("00000000:0B:00.0");

        samples[1].Index.Should().Be(1);
        samples[1].Uuid.Should().Be("GPU-uuid-2");
        samples[1].Name.Should().Be("NVIDIA A100");
        samples[1].Utilization.Should().BeNull("[N/A] → null");
        samples[1].MemoryUsed.Should().Be(8000);
        samples[1].MemoryTotal.Should().Be(81920);
        samples[1].Temperature.Should().BeNull("[N/A] → null");
        samples[1].FanSpeed.Should().BeNull("[N/A] → null");
        samples[1].ClocksSm.Should().BeNull("[N/A] → null");
        samples[1].Pstate.Should().BeNull("[N/A] → null");
        samples[1].PowerDraw.Should().BeNull("[N/A] → null");
        samples[1].SwPowerCap.Should().BeNull("[N/A] → null (не поддержано)");
        samples[1].BusId.Should().Be("00000000:0D:00.0");
    }

    // Пустой ввод → пустой список.
    [Fact]
    public void ParseCsv_Empty_ReturnsEmpty()
    {
        GpuSampler.ParseCsv("").Should().BeEmpty();
        GpuSampler.ParseCsv(null).Should().BeEmpty();
    }

    // (1.1) Неверный порядок колонок: парсер позиционный (index, uuid, name, …, фиксированный
    // хвост из 18 полей). Если порядок числовых колонок нарушен, значения попадают не в те поля.
    // Фиксируем поведение: парсер не «угадывает» порядок, а читает по позиции.
    [Fact]
    public void ParseCsv_WrongColumnOrder_PositionBased()
    {
        // Нормальный порядок: util=85, mem.used=12000, … Переставляем util и mem.used:
        // теперь 18-е с конца (util) = 12000, 17-е (utilMem) = 85.
        var line = Line(0, "GPU-uuid-1", "NVIDIA GeForce RTX 4090",
            util: "12000", utilMem: "85", memUsed: "24576", memTotal: "60",
            powerDraw: "300.5", powerLimit: "450.0");
        var samples = GpuSampler.ParseCsv(line);
        samples.Should().HaveCount(1);

        // Позиционный разбор: 18-е с конца → Utilization, 17-е → UtilizationMemory (поменялись).
        samples[0].Utilization.Should().Be(12000, "18-е с конца поле = Utilization, сюда попал mem.used");
        samples[0].UtilizationMemory.Should().Be(85, "17-е с конца поле = UtilizationMemory, сюда попал util");
    }

    // ─── CanonicalBusId: связка nvidia-smi ↔ файл помощника ccs-gddr6 ───────────────

    // nvidia-smi (8-значный домен, верхний) и помощник (4-значный домен, нижний)
    // сводятся к одной канонической форме (8-значный домен, нижний регистр).
    [Theory]
    [InlineData("00000000:0B:00.0", "0000:0b:00.0", "00000000:0b:00.0")]
    [InlineData("00000000:0C:00.0", "0000:0c:00.0", "00000000:0c:00.0")]
    public void CanonicalBusId_NvidiaSmi_And_Helper_Match(string nvidiaSmi, string helper, string expected)
    {
        GpuSampler.CanonicalBusId(nvidiaSmi).Should().Be(expected, "nvidia-smi → 8-значный домен, нижний");
        GpuSampler.CanonicalBusId(helper).Should().Be(expected, "помощник → та же форма");
    }

    // Пустой/пробельный busId → пустая строка (не исключение).
    [Fact]
    public void CanonicalBusId_Empty()
    {
        GpuSampler.CanonicalBusId(null).Should().BeEmpty();
        GpuSampler.CanonicalBusId("  ").Should().BeEmpty();
    }
}
