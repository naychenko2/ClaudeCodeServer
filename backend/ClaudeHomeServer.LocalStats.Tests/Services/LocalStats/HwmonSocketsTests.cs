using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>Сокеты и чипы платы: порядок по PCI-адресу device, не по hwmonN (значения — worker, задача 48ab796f).</summary>
public class HwmonSocketsTests
{
    private const string Root = "/sys/class/hwmon";

    private static SliceFileSource Slice(Dictionary<string, string> files, Dictionary<string, string>? links = null) =>
        new(files, links);

    private static void K10(Dictionary<string, string> f, string dir, int tctl, params (int k, int file, int temp)[] ccd)
    {
        f[$"{Root}/{dir}/name"] = "k10temp\n";
        f[$"{Root}/{dir}/temp1_input"] = tctl + "000"; f[$"{Root}/{dir}/temp1_label"] = "Tctl\n";
        foreach (var c in ccd)
        {
            f[$"{Root}/{dir}/temp{c.file}_input"] = c.temp + "000";
            f[$"{Root}/{dir}/temp{c.file}_label"] = $"Tccd{c.k}\n";
        }
    }

    [Fact]
    public void TwoSockets_OrderByPciDevice_NotByHwmonNumber_SparseCcd()
    {
        var f = new Dictionary<string, string>();
        // hwmon2 → 0000:00:18.3 (сокет 0), hwmon3 → 0000:00:19.3 (сокет 1); ссылки нарочно в обратных номерах.
        K10(f, "hwmon3", 41, (1, 3, 31), (2, 4, 32));
        K10(f, "hwmon2", 62, (1, 3, 51), (2, 4, 52), (3, 5, 53), (5, 6, 55), (7, 7, 57), (8, 8, 58));
        var links = new Dictionary<string, string>
        {
            [$"{Root}/hwmon2/device"] = "../../../0000:00:18.3",
            [$"{Root}/hwmon3/device"] = "../../../0000:00:19.3",
        };
        var snap = new HwmonReader(Slice(f, links)).Read()!;
        snap.Cpus.Should().HaveCount(2);
        var cpus = snap.Cpus!;
        cpus[0].Tctl.Should().Be(62);
        cpus[0].Ccd.Should().Equal(51, 52, 53, 55, 57, 58); // Tccd1,2,3,5,7,8 — подряд, без дыр
        cpus[1].Tctl.Should().Be(41);
        cpus[1].Ccd.Should().Equal(31, 32);
    }

    [Fact]
    public void PciOrderBeatsNumbers_WhenNumbersReversed()
    {
        var f = new Dictionary<string, string>();
        K10(f, "hwmon2", 70); // адрес БОЛЬШЕ, номер меньше
        K10(f, "hwmon5", 40);
        var links = new Dictionary<string, string>
        {
            [$"{Root}/hwmon2/device"] = "../../../0000:00:19.3",
            [$"{Root}/hwmon5/device"] = "../../../0000:00:18.3",
        };
        new HwmonReader(Slice(f, links)).Read()!.Cpus!.Select(c => c.Tctl).Should().Equal(40, 70);
    }

    [Fact]
    public void ChipsWithoutLink_GoAfter_ByAscendingNumber()
    {
        var f = new Dictionary<string, string>();
        K10(f, "hwmon9", 10);
        K10(f, "hwmon4", 20);
        K10(f, "hwmon7", 30);
        var links = new Dictionary<string, string> { [$"{Root}/hwmon9/device"] = "../../0000:00:18.3" };
        new HwmonReader(Slice(f, links)).Read()!.Cpus!.Select(c => c.Tctl).Should().Equal(10, 20, 30);
    }

    [Fact]
    public void BoardChips_Duplicate_FirstByDevice_AndReported()
    {
        var f = new Dictionary<string, string>();
        foreach (var (dir, vrm) in new[] { ("hwmon1", 90), ("hwmon8", 55) })
        {
            f[$"{Root}/{dir}/name"] = "asusec\n";
            f[$"{Root}/{dir}/temp5_input"] = vrm + "000"; f[$"{Root}/{dir}/temp5_label"] = "VRM\n";
        }
        var links = new Dictionary<string, string>
        {
            [$"{Root}/hwmon1/device"] = "../../0000:0a:00.0",
            [$"{Root}/hwmon8/device"] = "../../0000:03:00.0",
        };
        var snap = new HwmonReader(Slice(f, links)).Read()!;
        snap.Board!.Vrm.Should().Be(55, "первый по device, а не по номеру");
        snap.AmbiguousChips.Should().Equal("asusec");
    }

    [Fact]
    public async Task Sampler_SingleSocket_BoardVrmGoesToVrms0_TwoSockets_ToBoardVrm()
    {
        var f = new Dictionary<string, string>();
        K10(f, "hwmon0", 50);
        f[$"{Root}/hwmon6/name"] = "asusec\n";
        f[$"{Root}/hwmon6/temp5_input"] = "53000"; f[$"{Root}/hwmon6/temp5_label"] = "VRM\n";
        var one = new FakeNodeSource(); foreach (var kv in f) one.Files[kv.Key] = kv.Value;
        K10(f, "hwmon1", 60);
        var two = new FakeNodeSource(); foreach (var kv in f) two.Files[kv.Key] = kv.Value;

        var time = TimeProvider.System;
        var s = new HardwareSampler(new FakeOpts(), [new("one", one), new("two", two)], new Gddr6HelperReader(time), time);
        await s.PollNodeAsync("one");
        await s.PollNodeAsync("two");
        var a = s.ForNode("one")!.Current!;
        a.Vrms.Should().Equal(53);
        a.BoardVrm.Should().BeNull();
        var b = s.ForNode("two")!.Current!;
        b.Cpus.Should().HaveCount(2);
        b.Vrms.Should().Equal(new int?[] { null, null });
        b.BoardVrm.Should().Be(53);
    }

    private sealed class FakeOpts : Microsoft.Extensions.Options.IOptionsMonitor<LocalModelStatsOptions>
    {
        public LocalModelStatsOptions CurrentValue { get; } = new() { Enabled = true };
        public LocalModelStatsOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<LocalModelStatsOptions, string?> listener) => null;
    }
}
