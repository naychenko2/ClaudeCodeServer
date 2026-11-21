using ClaudeHomeServer.Services.Execution;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Заморозка slice по поддельному cgroupfs во временном каталоге: сервер в app.slice,
// slice агентов — в ccs.slice под тем же user@<uid>.service (раскладка прод-хоста).
public class CgroupSlicesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cgslices-" + Guid.NewGuid().ToString("N"));
    private readonly string _cgroupFile;
    private const string User = "user.slice/user-1000.slice/user@1000.service";

    public CgroupSlicesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, User, "app.slice", "ccs.service"));
        _cgroupFile = Path.Combine(_root, "self.cgroup");
        File.WriteAllText(_cgroupFile, $"0::/{User}/app.slice/ccs.service\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* временный каталог */ }
    }

    private string Slice(string rel, string freeze)
    {
        var dir = Path.Combine(_root, User, rel);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cgroup.freeze"), freeze + "\n");
        return dir;
    }

    private bool Frozen(string slice) => CgroupSlices.IsFrozen(slice, _cgroupFile, _root);

    [Fact]
    public void ЗамороженЛокальный_ОбщийНет()
    {
        Slice("ccs.slice", "0");
        Slice("ccs.slice/ccs-agents.slice", "0");
        Slice("ccs.slice/ccs-agents.slice/ccs-agents-local.slice", "1");

        Frozen("ccs-agents-local.slice").Should().BeTrue();
        Frozen("ccs-agents.slice").Should().BeFalse("заморозка дочернего не морозит родителя");
    }

    [Fact]
    public void ЗамороженРодитель_ДочернийСчитаетсяЗамороженным()
    {
        Slice("ccs.slice", "0");
        Slice("ccs.slice/ccs-agents.slice", "1");
        Slice("ccs.slice/ccs-agents.slice/ccs-agents-local.slice", "0");

        Frozen("ccs-agents-local.slice").Should().BeTrue("freeze родителя действует на детей");
    }

    [Fact]
    public void КаталогаSliceЕщёНет_СмотримПредков()
    {
        Slice("ccs.slice", "0");
        Slice("ccs.slice/ccs-agents.slice", "1");

        Frozen("ccs-agents-local.slice").Should().BeTrue();
    }

    [Fact]
    public void НичегоНеЗаморожено_False()
    {
        Slice("ccs.slice", "0");
        Slice("ccs.slice/ccs-agents.slice", "0");

        Frozen("ccs-agents-local.slice").Should().BeFalse();
        Frozen("ccs-agents.slice").Should().BeFalse();
    }

    [Theory]
    [InlineData("not-a-slice")]
    [InlineData("../x.slice")]
    public void НеИмяSlice_False(string slice) => Frozen(slice).Should().BeFalse();

    [Fact]
    public void НетФайлаCgroup_FailOpen() =>
        CgroupSlices.IsFrozen("ccs-agents.slice", Path.Combine(_root, "нет"), _root).Should().BeFalse();
}
