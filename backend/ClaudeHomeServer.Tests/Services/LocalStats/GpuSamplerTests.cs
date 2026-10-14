using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Тесты сэмплера видеокарт: разбор CSV nvidia-smi (csv,noheader,nounits) с [N/A]
/// и 2 картами, кэш на GpuCacheTtl (2 с) на фейковых часах, нет бинаря/ошибка → null.
/// Запуск процесса подменяется фейковым runner (internal-конструктор), без реального nvidia-smi.
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

    // (1) Разбор CSV: 2 карты, [N/A] в числовых полях → null, имя с пробелами.
    [Fact]
    public void ParseCsv_TwoCards_WithNa()
    {
        const string text = """
            0, GPU-uuid-1, NVIDIA GeForce RTX 4090, 85, 12000, 24576, 60, 300.5, 450.0
            1, GPU-uuid-2, NVIDIA A100, [N/A], 8000, 81920, [N/A], [N/A], 700.0
            """;
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

        samples[1].Index.Should().Be(1);
        samples[1].Uuid.Should().Be("GPU-uuid-2");
        samples[1].Name.Should().Be("NVIDIA A100");
        samples[1].Utilization.Should().BeNull("[N/A] → null");
        samples[1].MemoryUsed.Should().Be(8000);
        samples[1].MemoryTotal.Should().Be(81920);
        samples[1].Temperature.Should().BeNull("[N/A] → null");
        samples[1].PowerDraw.Should().BeNull("[N/A] → null");
        samples[1].PowerLimit.Should().Be(700.0);
    }

    // Пустой ввод → пустой список.
    [Fact]
    public void ParseCsv_Empty_ReturnsEmpty()
    {
        GpuSampler.ParseCsv("").Should().BeEmpty();
        GpuSampler.ParseCsv(null).Should().BeEmpty();
    }

    // (1.1) Неверный порядок колонок: парсер позиционный (index,uuid,name, …, последние 6 — числа
    // в фиксированном порядке). Если порядок числовых колонок нарушен, значения попадают не в те поля.
    // Фиксируем поведение: парсер не «угадывает» порядок, а читает по позиции.
    [Fact]
    public void ParseCsv_WrongColumnOrder_PositionBased()
    {
        // Нормальный порядок: util=85, mem.used=12000, mem.total=24576, temp=60, power.draw=300.5, power.limit=450.0.
        // Переставляем util и mem.used: теперь parts[len-6]=12000, parts[len-5]=85.
        const string text = "0, GPU-uuid-1, NVIDIA GeForce RTX 4090, 12000, 85, 24576, 60, 300.5, 450.0";
        var samples = GpuSampler.ParseCsv(text);
        samples.Should().HaveCount(1);

        // Позиционный разбор: 6-е с конца → Utilization, 5-е → MemoryUsed (значения поменялись местами).
        samples[0].Utilization.Should().Be(12000, "6-е с конца поле = Utilization, сюда попал mem.used");
        samples[0].MemoryUsed.Should().Be(85, "5-е с конца поле = MemoryUsed, сюда попал util");
        // Остальные колонки на месте: mem.total, temp, power.draw, power.limit.
        samples[0].MemoryTotal.Should().Be(24576);
        samples[0].Temperature.Should().Be(60);
        samples[0].PowerDraw.Should().Be(300.5);
        samples[0].PowerLimit.Should().Be(450.0);
    }

    // (2) Кэш 2 с на фейковых часах: в пределах TTL runner не дёргается повторно,
    // после TTL — дёргается снова.
    [Fact]
    public async Task Cache_WithinTtl_DoesNotRerun_AfterTtl_Reruns()
    {
        var time = new ManualTime();
        var runs = 0;
        var sampler = new GpuSampler(time, TimeSpan.FromSeconds(2),
            () => { runs++; return Task.FromResult("0, GPU-1, Card, 50, 100, 200, 40, 1.0, 2.0"); });

        var first = await sampler.ReadAsync();
        first.Should().NotBeNull();
        runs.Should().Be(1);

        time.Advance(TimeSpan.FromSeconds(1)); // < 2 с
        var second = await sampler.ReadAsync();
        second.Should().NotBeNull();
        runs.Should().Be(1, "в пределах TTL runner не дёргается повторно");

        time.Advance(TimeSpan.FromSeconds(2)); // > 2 с
        await sampler.ReadAsync();
        runs.Should().Be(2, "после TTL runner дёргается снова");
    }

    // (3) Нет бинаря/ошибка → null, без исключений наружу.
    [Fact]
    public async Task NoBinary_ReturnsNull()
    {
        var time = new ManualTime();
        // Runner бросает (симуляция «бинарь не найден» / таймаут).
        var sampler = new GpuSampler(time, TimeSpan.FromSeconds(2),
            () => throw new System.ComponentModel.Win32Exception(2, "not found"));

        var result = await sampler.ReadAsync();
        result.Should().BeNull("нет бинаря/ошибка → null, без исключений наружу");
    }

    // (4) Пустой вывод nvidia-smi (карт нет) → null.
    [Fact]
    public async Task EmptyOutput_ReturnsNull()
    {
        var time = new ManualTime();
        var sampler = new GpuSampler(time, TimeSpan.FromSeconds(2),
            () => Task.FromResult(""));

        (await sampler.ReadAsync()).Should().BeNull();
    }
}
