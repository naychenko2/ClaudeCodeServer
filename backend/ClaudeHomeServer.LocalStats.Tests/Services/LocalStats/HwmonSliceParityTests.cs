using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Паритет разбора hwmon: фикстуры из HardwareTests читаются двумя путями — с диска
/// (DiskFileSource) и через срез (SliceFileSource), собранный обходом той же папки.
/// Снимки обязаны совпасть по record (с учётом массивов).
/// </summary>
public class HwmonSliceParityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hwp_" + Guid.NewGuid().ToString("N"));

    public HwmonSliceParityTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Dir(params string[] parts)
    {
        var p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    private static void Put(string dir, string file, string text) => File.WriteAllText(Path.Combine(dir, file), text);

    private static readonly Dictionary<string, (long, long)> NoFs = new();

    private static Func<string, (long total, long free)> FakeFsSize(Dictionary<string, (long totalGb, long freeGb)> map) =>
        mp => map.TryGetValue(mp, out var v)
            ? (v.totalGb * 1024L * 1024 * 1024, v.freeGb * 1024L * 1024 * 1024)
            : (0, 0);

    // Срез тем же обходом, что сделал бы агент: файлы (текст), ссылки (цель), statvfs точек монтирования.
    private SliceFileSource BuildSlice(string tree, string? mountinfo, Func<string, (long total, long free)>? fsSize,
        IEnumerable<string> mountPoints)
    {
        var files = new Dictionary<string, string>();
        var links = new Dictionary<string, string>();
        if (Directory.Exists(tree))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(tree, "*", SearchOption.AllDirectories))
            {
                var link = new FileInfo(path).LinkTarget;
                if (link is not null) links[path] = link;
                else if (File.Exists(path)) files[path] = File.ReadAllText(path);
            }
        }
        if (mountinfo is not null && File.Exists(mountinfo))
            files[mountinfo] = File.ReadAllText(mountinfo);
        var statvfs = new Dictionary<string, (long total, long free)>();
        if (fsSize is not null)
            foreach (var mp in mountPoints)
                statvfs[mp] = fsSize(mp);
        return new SliceFileSource(files, links, statvfs);
    }

    private static readonly string[] Mounts = ["/", "/home/an/llm/kvdisk", "/mnt/big", "/boot/efi"];

    private void AssertParity(string hw, string mountinfo, Func<string, (long, long)>? fsSize = null)
    {
        var disk = new HwmonReader(hw, Dir("block-" + Guid.NewGuid().ToString("N")), mountinfo, fsSize).Read();
        var slice = new HwmonReader(BuildSlice(hw, mountinfo, fsSize, Mounts), hw, mountinfo).Read();

        slice.Should().NotBeNull(because: "фикстура непустая — иначе паритет вакуумный");
        disk.Should().NotBeNull();
        slice!.Cpus.Should().BeEquivalentTo(disk!.Cpus, o => o.WithStrictOrdering());
        slice.Board.Should().Be(disk.Board);
        slice.Disks.Should().Equal(disk.Disks);
        slice.CaseFans.Should().Equal(disk.CaseFans);
    }

    [Fact]
    public void Parity_FullFixture_RolesFansBoardCpu()
    {
        var hw = Dir("hwmon");
        var nvmeKv = Dir("hwmon", "hwmon9");
        Put(nvmeKv, "name", "nvme\n");
        Put(nvmeKv, "temp1_input", "58850"); Put(nvmeKv, "temp1_label", "Composite");
        Put(nvmeKv, "temp3_input", "82850"); Put(nvmeKv, "temp3_label", "Sensor 2");
        Put(nvmeKv, "temp1_max", "83850"); Put(nvmeKv, "temp1_crit", "88850");
        File.CreateSymbolicLink(Path.Combine(nvmeKv, "device"), "../../nvme1");

        var nvmeSys = Dir("hwmon", "hwmon3");
        Put(nvmeSys, "name", "nvme\n");
        Put(nvmeSys, "temp1_input", "59850"); Put(nvmeSys, "temp1_label", "Composite");
        File.CreateSymbolicLink(Path.Combine(nvmeSys, "device"), "../../nvme0");

        var cpu = Dir("hwmon", "hwmon0");
        Put(cpu, "name", "k10temp\n");
        Put(cpu, "temp1_input", "76250"); Put(cpu, "temp1_label", "Tctl");
        Put(cpu, "temp3_input", "78000"); Put(cpu, "temp3_label", "Tccd1");
        Put(cpu, "temp4_input", "49750"); Put(cpu, "temp4_label", "Tccd2");

        var board = Dir("hwmon", "hwmon6");
        Put(board, "name", "asusec\n");
        Put(board, "temp1_input", "78000"); Put(board, "temp1_label", "Chipset");
        Put(board, "temp5_input", "53000"); Put(board, "temp5_label", "VRM");
        Put(board, "fan2_input", "1159"); Put(board, "fan2_label", "Chipset");
        Put(board, "temp4_input", "45000"); Put(board, "temp4_label", "T_Sensor");
        Put(board, "temp6_input", "-40000"); Put(board, "temp6_label", "Water_In");

        var nct = Dir("hwmon", "hwmon1");
        Put(nct, "name", "nct6798\n");
        Put(nct, "temp4_input", "127000"); Put(nct, "temp4_label", "AUXTIN1");
        Put(nct, "fan1_input", "0"); Put(nct, "fan2_input", "1159"); Put(nct, "fan7_input", "4821");

        var mountinfo = Path.Combine(_root, "mountinfo");
        File.WriteAllText(mountinfo,
            "36 1 8:1 / / rw - ext4 /dev/nvme0n1p2 rw\n" +
            "364 43 259:1 / /home/an/llm/kvdisk rw shared:197 - ext4 /dev/nvme1n1p1 rw\n");
        var fs = FakeFsSize(new() { ["/"] = (500, 100), ["/home/an/llm/kvdisk"] = (492, 122) });

        AssertParity(hw, mountinfo, fs);
        // Контроль, что фикстура вообще что-то прочитала (иначе оба пути пустые).
        new HwmonReader(BuildSlice(hw, mountinfo, fs, Mounts), hw, mountinfo).Read()!
            .Disks!.Single(d => d.Role == "kv").UsedGb.Should().Be(370);
    }

    [Fact]
    public void Parity_WaterProbeDisconnected()
    {
        var hw = Dir("hwW");
        var board = Dir("hwW", "hwmon4");
        Put(board, "name", "asusec\n");
        Put(board, "temp1_input", "73000"); Put(board, "temp1_label", "Chipset");
        Put(board, "temp4_input", "-40000"); Put(board, "temp4_label", "T_Sensor");
        AssertParity(hw, Path.Combine(_root, "miW"));
    }

    [Fact]
    public void Parity_NoRoot_BothNull()
    {
        var hw = Path.Combine(_root, "нет");
        var mi = Path.Combine(_root, "нет3");
        new HwmonReader(hw, Path.Combine(_root, "нет2"), mi).Read().Should().BeNull();
        new HwmonReader(BuildSlice(hw, mi, null, Mounts), hw, mi).Read().Should().BeNull();
    }

    private HwmonSnapshot?[] ReadBoth(string mountinfoContent, Dictionary<string, (long, long)> map)
    {
        var hw = Dir("hwmon-disk");
        var nvme1 = Dir("hwmon-disk", "hwmon1");
        Put(nvme1, "name", "nvme\n");
        File.CreateSymbolicLink(Path.Combine(nvme1, "device"), "../../nvme1");
        var nvme0 = Dir("hwmon-disk", "hwmon0");
        Put(nvme0, "name", "nvme\n");
        File.CreateSymbolicLink(Path.Combine(nvme0, "device"), "../../nvme0");
        var mi = Path.Combine(_root, "mountinfo-disk");
        File.WriteAllText(mi, mountinfoContent);
        var fs = FakeFsSize(map);
        return
        [
            new HwmonReader(hw, Dir("block-d"), mi, fs).Read(),
            new HwmonReader(BuildSlice(hw, mi, fs, Mounts), hw, mi).Read(),
        ];
    }

    [Fact]
    public void Parity_KvDisk_UsesKvdiskFs_NotLargestOnController()
    {
        var both = ReadBoth(
            "36 1 8:1 / / rw - ext4 /dev/nvme0n1p2 rw\n" +
            "364 43 259:1 / /home/an/llm/kvdisk rw shared:197 - ext4 /dev/nvme1n1p1 rw\n" +
            "365 44 259:2 / /mnt/big rw - ext4 /dev/nvme1n1p2 rw\n",
            new() { ["/"] = (500, 100), ["/home/an/llm/kvdisk"] = (492, 122), ["/mnt/big"] = (1000, 500) });

        both[1]!.Disks.Should().Equal(both[0]!.Disks);
        both[1]!.Disks!.Single(d => d.Role == "kv").UsedGb.Should().Be(370);
        both[1]!.Disks!.Single(d => d.Role == "system").UsedGb.Should().Be(400);
    }

    [Fact]
    public void Parity_NoMountedFs_UsedTotalNull()
    {
        var both = ReadBoth(
            "364 43 259:1 / /home/an/llm/kvdisk rw shared:197 - ext4 /dev/nvme1n1p1 rw\n",
            new() { ["/home/an/llm/kvdisk"] = (492, 122) });

        both[1]!.Disks.Should().Equal(both[0]!.Disks);
        both[1]!.Disks!.Single(d => d.Role == "system").UsedGb.Should().BeNull();
    }
}
