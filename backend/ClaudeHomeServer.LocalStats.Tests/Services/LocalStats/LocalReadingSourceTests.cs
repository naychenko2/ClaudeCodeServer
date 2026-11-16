using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Тесты LocalReadingSource (Ш2b): локальный узел — реальный диск (фикстура hwmon из
/// HardwareTests: Path.GetTempPath, без реальных /sys), карты — подменённый IGpuSampler,
/// gddr6 — файл помощника во временном каталоге. AgeMs = 0 по построению; отсутствие
/// gddr6-файла штатно (null, статус ok); сбой карт — down без исключения наружу.
/// </summary>
public class LocalReadingSourceTests : IDisposable
{
    private sealed class FakeGpu(params GpuSample[] samples) : IGpuSampler
    {
        public Task<IReadOnlyList<GpuSample>> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GpuSample>>(samples);
    }

    private sealed class ThrowingGpu : IGpuSampler
    {
        public Task<IReadOnlyList<GpuSample>> ReadAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("нет nvidia-smi");
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lrs_" + Guid.NewGuid().ToString("N"));

    public LocalReadingSourceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Put(string rel, string text)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    // Фикстура hwmon — та же форма, что BuildFakeHwmon в HardwareTests: каталоги hwmonN с name/temp_input.
    private string BuildFakeHwmon()
    {
        var cpu = Path.Combine(_root, "hwmon", "hwmon0");
        Directory.CreateDirectory(cpu);
        File.WriteAllText(Path.Combine(cpu, "name"), "k10temp\n");
        File.WriteAllText(Path.Combine(cpu, "temp1_input"), "76250");
        return cpu;
    }

    // Диск читается как есть: ReadText фикстуры совпадает с содержимым файла, AgeMs = 0,
    // карты доходят до сэмплера, gddr6-файл отдаётся дословно.
    [Fact]
    public async Task ReadAsync_HwmonFixture_OkAgeZeroFilesMatchDisk()
    {
        var cpu = BuildFakeHwmon();
        var namePath = Path.Combine(cpu, "name");
        var gddr6Path = Put("gddr6.json", """{"v":1,"at":1,"gpus":[{"busId":"0000:0b:00.0","memTempC":68,"hotspotTempC":75}]}""");
        var gpu = new GpuSample { Index = 0, Uuid = "GPU-abcdef", Name = "RTX 3090", Temperature = 60 };

        var reading = await new LocalReadingSource(new FakeGpu(gpu), gddr6Path).ReadAsync();

        reading.Status.Should().Be(NodeReadingStatus.Ok);
        reading.AgeMs.Should().Be(0, "локальный снимок свежий по построению");
        reading.Files.ReadText(namePath).Should().Be(File.ReadAllText(namePath), "фикстура читается как диск");
        reading.Files.ReadText(Path.Combine(cpu, "temp1_input")).Should().Be("76250");
        reading.Files.Exists(cpu).Should().BeTrue();
        reading.Gpus.Should().ContainSingle().Which.Uuid.Should().Be("GPU-abcdef");
        reading.Gddr6.Should().Contain("memTempC");
    }

    // Gddr6-файла нет — null, статус ok: файл помощника необязателен (дальше решит Gddr6HelperReader).
    [Fact]
    public async Task ReadAsync_NoGddr6File_Gddr6NullStillOk()
    {
        var cpu = BuildFakeHwmon();

        var reading = await new LocalReadingSource(new FakeGpu(), Path.Combine(_root, "нет.json")).ReadAsync();

        reading.Status.Should().Be(NodeReadingStatus.Ok);
        reading.Gddr6.Should().BeNull();
        reading.Gpus.Should().BeEmpty();
    }

    // Неожиданный сбой карт (гипотеза: IGpuSampler по контракту не бросает) — down без исключения:
    // источник не роняет вызывающий цикл.
    [Fact]
    public async Task ReadAsync_GpuThrows_DownWithoutThrow()
    {
        var reading = await new LocalReadingSource(new ThrowingGpu(), Path.Combine(_root, "нет.json")).ReadAsync();

        reading.Status.Should().Be(NodeReadingStatus.Down);
    }
}
