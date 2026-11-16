using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Недоверенный срез агента: мусор в датчике, каталог без name, statvfs с free &gt; total —
/// разбор не падает, значение помечается отсутствующим (null), а не мусором в снимке.
/// Гарантия «ошибка чтения не валит разбор» (хвост ревью Р-Ш1б): держится try/catch и
/// валидацией в ReadFsUsage — здесь она закреплена тестом.
/// </summary>
[Trait("Category", "Hardware")]
public class HwmonUntrustedSliceTests
{
    private const string Root = "/sys/class/hwmon";
    private const string KvMount = "/home/an/llm/kvdisk";
    private const string KvMountinfoLine =
        "364 43 259:1 / /home/an/llm/kvdisk rw shared:197 - ext4 /dev/nvme1n1p1 rw\n";
    private const long GiB = 1024L * 1024 * 1024;

    private static HwmonReader Reader(Dictionary<string, string> files,
        Dictionary<string, string>? links = null,
        Dictionary<string, (long total, long free)>? statvfs = null) =>
        new(new SliceFileSource(files, links, statvfs));

    [Fact]
    public void Slice_GarbageTempInput_ValueIsAbsent_NotThrow()
    {
        var f = new Dictionary<string, string>
        {
            [$"{Root}/hwmon0/name"] = "k10temp\n",
            [$"{Root}/hwmon0/temp1_input"] = "не число",
            [$"{Root}/hwmon0/temp1_label"] = "Tctl\n",
            [$"{Root}/hwmon0/temp2_input"] = "  ",
            [$"{Root}/hwmon0/temp2_label"] = "Tccd1\n",
        };
        var snap = Reader(f).Read();
        snap.Should().NotBeNull("корень виден в путях среза — снимок настоящий");
        var cpu = snap!.Cpus!.Single();
        cpu.Tctl.Should().BeNull("мусорный temp1_input — датчика нет, не исключение");
        cpu.Ccd.Should().Equal(new int?[] { null }, "кристалл с мусорным значением — null, не 0");
    }

    [Fact]
    public void Slice_DirWithoutName_Skipped_SnapshotEmpty()
    {
        // Каталог с датчиками, но без name: имя чипа не определить — каталог пропускаем.
        var f = new Dictionary<string, string>
        {
            [$"{Root}/hwmon5/temp1_input"] = "55000",
            [$"{Root}/hwmon5/temp1_label"] = "Composite",
        };
        var snap = Reader(f).Read();
        snap.Should().NotBeNull("корень есть в путях среза — это не «чужая машина»");
        snap!.Cpus.Should().BeNull("чип без имени не разбирается ни как какой");
        snap.Board.Should().BeNull();
        snap.Disks.Should().BeNull();
        snap.CaseFans.Should().BeNull();
    }

    [Theory]
    [InlineData(492L, 600L)] // free > total
    [InlineData(492L, -5L)]  // free < 0
    public void Slice_BadStatvfs_UsedTotalNull(long totalGb, long freeGb)
    {
        var f = new Dictionary<string, string>
        {
            [$"{Root}/hwmon1/name"] = "nvme\n",
            ["/proc/self/mountinfo"] = KvMountinfoLine,
        };
        var links = new Dictionary<string, string> { [$"{Root}/hwmon1/device"] = "../../nvme1" };
        var statvfs = new Dictionary<string, (long, long)> { [KvMount] = (totalGb * GiB, freeGb * GiB) };

        var snap = Reader(f, links, statvfs).Read()!;
        var kv = snap.Disks!.Single(d => d.Role == "kv");
        kv.UsedGb.Should().BeNull("противоречивый statvfs — датчика нет, не мусорный UsedGb");
        kv.TotalGb.Should().BeNull();
        // Остальные поля диска живые: бракованная ФС не валит температуру и роль.
        kv.Controller.Should().Be("nvme1");
    }

    [Theory]
    [InlineData(492L, 0L)]    // граница: free = 0 — включена
    [InlineData(492L, 492L)]  // граница: free = total — включена
    public void Slice_StatvfsOnBounds_UsedTotalPresent(long totalGb, long freeGb)
    {
        var f = new Dictionary<string, string>
        {
            [$"{Root}/hwmon1/name"] = "nvme\n",
            ["/proc/self/mountinfo"] = KvMountinfoLine,
        };
        var links = new Dictionary<string, string> { [$"{Root}/hwmon1/device"] = "../../nvme1" };
        var statvfs = new Dictionary<string, (long, long)> { [KvMount] = (totalGb * GiB, freeGb * GiB) };

        var kv = Reader(f, links, statvfs).Read()!.Disks!.Single(d => d.Role == "kv");
        kv.UsedGb.Should().Be(totalGb - freeGb, "граница [0, total] — значение валидно");
        kv.TotalGb.Should().Be(totalGb);
    }
}
