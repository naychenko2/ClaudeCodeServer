using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Tests.Helpers;

namespace ClaudeHomeServer.Tests.Services.Desktop;

/// <summary>
/// Каталог релизов агента (agent-distribution AD-3, Р3, Р11): всё, что пришло с диска,
/// проверяется до того, как станет URL или путём; нет каталога — честное «не раздаёт»;
/// снимок держится до смены mtime указателя.
/// </summary>
public class AgentReleaseCatalogTests : IDisposable
{
    private readonly AgentReleaseFixture _rel = new();
    private readonly RecordingReleaseFileSystem _fs = new();

    public void Dispose() => _rel.Dispose();

    private AgentReleaseCatalog Catalog(bool withRoot = true) => new(_rel.Config(withRoot), fileSystem: _fs);

    private static string Sha(char c) => new(c, 64);

    [Fact]
    public void ОпубликованныйРелиз_ТекущаяВерсияСАрхивамиПодОбаRid()
    {
        var latest = Catalog().Current().Latest;

        latest.Should().NotBeNull();
        latest!.Version.Should().Be(AgentReleaseFixture.Version);
        var win = latest.Archives[DeviceAgentRids.WinX64];
        win.File.Should().Be(AgentReleaseFixture.WinFile);
        win.Size.Should().Be(_rel.WinBytes.Length);
        win.Sha256.Should().Be(AgentReleaseFixture.Sha(_rel.WinBytes));
        win.RelativePath.Should().Be($"{AgentReleaseFixture.Version}/win-x64/{AgentReleaseFixture.WinFile}");
        latest.Archives[DeviceAgentRids.LinuxX64].File.Should().Be(AgentReleaseFixture.LinuxFile);
    }

    [Fact]
    public void КаталогНеЗадан_НеРаздаётСПричиной()
    {
        var snapshot = Catalog(withRoot: false).Current();

        snapshot.Served.Should().BeFalse();
        snapshot.Problem.Should().StartWith(AgentReleaseCatalog.NotServedPrefix);
        snapshot.Versions.Should().BeEmpty();
    }

    [Fact]
    public void КаталогаНетНаДиске_НеРаздаёт()
    {
        Directory.Delete(_rel.Root, recursive: true);

        var snapshot = Catalog().Current();

        snapshot.Served.Should().BeFalse();
        snapshot.Problem.Should().StartWith(AgentReleaseCatalog.NotServedPrefix);
    }

    [Fact]
    public void УказателяНет_НеРаздаёт()
    {
        File.Delete(_rel.PointerPath);

        Catalog().Current().Served.Should().BeFalse();
    }

    [Fact]
    public void НезнакомыйRidВМанифесте_Пропускается()
    {
        _rel.WriteManifest("1.201.0", AgentReleaseFixture.ManifestJson("1.201.0",
            ("win-x64", "a.zip", 10, Sha('a')), ("osx-arm64", "b.tar.gz", 10, Sha('b'))));
        _rel.WritePointer("1.201.0");

        var latest = Catalog().Current().Latest!;

        latest.Archives.Keys.Should().Equal("win-x64");
    }

    public static TheoryData<string, string, long, string> BrokenEntries => new()
    {
        { "SHA на 63 символа", "a.zip", 10, new string('a', 63) },
        { "SHA не hex", "a.zip", 10, new string('z', 64) },
        { "имя с ..", "../a.zip", 10, Sha('a') },
        { "имя с /", "x/a.zip", 10, Sha('a') },
        { "имя с \\", "x\\a.zip", 10, Sha('a') },
        { "абсолютное имя", "/etc/passwd", 10, Sha('a') },
        { "скрытое имя", ".a.zip", 10, Sha('a') },
        { "нулевой размер", "a.zip", 0, Sha('a') },
    };

    [Theory]
    [MemberData(nameof(BrokenEntries))]
    public void БитыйМанифест_ВерсияВыпадаетЦеликом(string why, string file, long size, string sha)
    {
        _rel.WriteManifest("1.201.0", AgentReleaseFixture.ManifestJson("1.201.0",
            ("linux-x64", "ok.tar.gz", 10, Sha('b')), ("win-x64", file, size, sha)));
        _rel.WritePointer("1.201.0");

        var snapshot = Catalog().Current();

        snapshot.Versions.Should().NotContainKey("1.201.0", why);
        snapshot.Served.Should().BeFalse(why);
        snapshot.Versions.Should().ContainKey(AgentReleaseFixture.Version, "исправные версии остаются в каталоге");
    }

    [Fact]
    public void ОдноИмяАрхиваУДвухRid_ВерсияВыпадает()
    {
        _rel.WriteManifest("1.201.0", AgentReleaseFixture.ManifestJson("1.201.0",
            ("win-x64", "same.zip", 10, Sha('a')), ("linux-x64", "same.zip", 10, Sha('b'))));

        Catalog().Current().Versions.Should().NotContainKey("1.201.0");
    }

    [Fact]
    public void ВерсияМанифестаНеСовпалаСКаталогом_ВерсияВыпадает()
    {
        _rel.WriteManifest("1.201.0", AgentReleaseFixture.ManifestJson("1.202.0", ("win-x64", "a.zip", 10, Sha('a'))));

        Catalog().Current().Versions.Should().NotContainKey("1.201.0");
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("01.201.0")]
    [InlineData("1.201")]
    public void НеканоническоеИмяКаталога_НеВерсия(string dir)
    {
        _rel.WriteManifest(dir, AgentReleaseFixture.ManifestJson(dir, ("win-x64", "a.zip", 10, Sha('a'))));

        Catalog().Current().Versions.Keys.Should().Equal(AgentReleaseFixture.Version);
    }

    [Fact]
    public void УказательРасходитсяСМанифестом_НеРаздаёт()
    {
        _rel.WritePointerJson(AgentReleaseFixture.ManifestJson(AgentReleaseFixture.Version,
            ("win-x64", AgentReleaseFixture.WinFile, _rel.WinBytes.Length, Sha('f')),
            ("linux-x64", AgentReleaseFixture.LinuxFile, _rel.LinuxBytes.Length, AgentReleaseFixture.Sha(_rel.LinuxBytes))));

        var snapshot = Catalog().Current();

        snapshot.Served.Should().BeFalse();
        snapshot.Problem.Should().StartWith(AgentReleaseCatalog.NotServedPrefix);
    }

    [Fact]
    public void Кэш_ДержитсяДоСменыMtimeУказателя()
    {
        var catalog = Catalog();
        catalog.Current();
        var reads = _fs.Count("read");

        catalog.Current();
        catalog.Current();
        _fs.Count("read").Should().Be(reads, "без смены указателя манифесты не перечитываются");

        _rel.WriteRelease("1.201.0", ("win-x64", "next.zip", new byte[] { 1, 2, 3 }));
        catalog.Current().Latest!.Version.Should().Be(AgentReleaseFixture.Version, "указатель ещё старый");

        _rel.WritePointer("1.201.0");
        catalog.Current().Latest!.Version.Should().Be("1.201.0");
        _fs.Count("read").Should().BeGreaterThan(reads);
    }

    [Fact]
    public void FindArchive_ТолькоТочноеСовпадениеСМанифестом()
    {
        var catalog = Catalog();

        catalog.FindArchive(AgentReleaseFixture.Version, "win-x64", AgentReleaseFixture.WinFile).Should().NotBeNull();
        catalog.FindArchive(AgentReleaseFixture.Version, "linux-x64", AgentReleaseFixture.WinFile).Should().BeNull();
        catalog.FindArchive(AgentReleaseFixture.Version, "WIN-X64", AgentReleaseFixture.WinFile).Should().BeNull();
        catalog.FindArchive(AgentReleaseFixture.Version, "win-x64", AgentReleaseFixture.WinFile.ToUpperInvariant()).Should().BeNull();
        catalog.FindArchive("1.9.0", "win-x64", AgentReleaseFixture.WinFile).Should().BeNull();
    }
}
