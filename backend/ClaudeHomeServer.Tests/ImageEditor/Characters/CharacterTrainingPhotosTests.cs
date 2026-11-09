using ClaudeHomeServer.Services.ImageEditor;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.ImageEditor.Characters;

public sealed class CharacterTrainingPhotosTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ctp-" + Guid.NewGuid().ToString("N"));
    private readonly string _owner = Guid.NewGuid().ToString();
    private readonly string _project = Guid.NewGuid().ToString();
    private readonly CharacterTrainingPhotos _sut;

    public CharacterTrainingPhotosTests()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_root, "projects.json") })
            .Build();
        _sut = new CharacterTrainingPhotos(config);
    }

    public void Dispose() => Directory.Delete(_root, true);

    // PNG с заголовком IHDR нужного размера; seed делает содержимое (и хеш) уникальным
    private static byte[] Png(int w, int h, int seed = 0, int extra = 0)
    {
        var b = new byte[24 + 4 + extra];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        "IHDR"u8.ToArray().CopyTo(b, 12);
        for (var i = 0; i < 4; i++)
        {
            b[16 + i] = (byte)(w >> (24 - 8 * i));
            b[20 + i] = (byte)(h >> (24 - 8 * i));
        }
        BitConverter.GetBytes(seed).CopyTo(b, 24);
        return b;
    }

    private static PhotoUpload Up(int seed, string name = "a.png") => new(name, Png(600, 600, seed));

    private TrainingPhotosAddResult Add(params PhotoUpload[] ups) => _sut.Add(_owner, _project, "anya", ups);

    [Fact]
    public void Purge_ЧиститТолькоСвоегоПерсонажа_СоседаИЧужогоВладельцаНеТрогает()
    {
        var other = Guid.NewGuid().ToString();
        Add(Up(1));
        _sut.Add(_owner, _project, "boris", [Up(2)]);
        _sut.Add(other, _project, "anya", [Up(3)]);

        _sut.Purge(_owner, _project, "anya");

        _sut.List(_owner, _project, "anya").Should().BeEmpty();
        _sut.List(_owner, _project, "boris").Should().HaveCount(1);
        _sut.List(other, _project, "anya").Should().HaveCount(1);
        _sut.Invoking(s => s.Purge(_owner, _project, "anya")).Should().NotThrow();
    }

    [Fact]
    public void PurgeProject_ЧиститПроектВладельца_ЧужойПроектНеТрогает()
    {
        var otherProject = Guid.NewGuid().ToString();
        Add(Up(1));
        _sut.Add(_owner, otherProject, "anya", [Up(2)]);

        _sut.PurgeProject(_owner, _project);

        _sut.List(_owner, _project, "anya").Should().BeEmpty();
        _sut.List(_owner, otherProject, "anya").Should().HaveCount(1);
        _sut.Invoking(s => s.PurgeProject(_owner, _project)).Should().NotThrow();
    }

    [Fact]
    public void ШовУборкиПроекта_ЭтоТотЖеСервис_ЧиститТолькоСвоёНеТрогаяЧужогоВладельца()
    {
        var other = Guid.NewGuid().ToString();
        Add(Up(1));
        _sut.Add(other, _project, "anya", [Up(2)]);

        IProjectTrainingPhotoCleaner cleaner = _sut;
        cleaner.PurgeProject(_owner, _project);

        _sut.List(_owner, _project, "anya").Should().BeEmpty();
        _sut.List(other, _project, "anya").Should().HaveCount(1);
    }

    [Fact]
    public void НеGuidВладелец_FailClosed_БезИсключений()
    {
        _sut.List("not-a-guid", _project, "anya").Should().BeEmpty();
        _sut.Remove("not-a-guid", _project, "anya", "0123456789abcdef.png").Should().BeFalse();
        _sut.PathOf("not-a-guid", _project, "anya", "0123456789abcdef.png").Should().BeNull();
        _sut.Invoking(s => s.Purge("not-a-guid", _project, "anya")).Should().NotThrow();
        _sut.Add("not-a-guid", _project, "anya", [Up(1)]).Rejected.Should().ContainSingle(r => r.Reason == "character");
    }

    [Fact]
    public void List_РазмерыИзЗаголовка_БайтыИзFileInfo()
    {
        var added = Add(new PhotoUpload("big.png", Png(640, 800, 5, extra: 100_000))).Added.Single();
        var listed = _sut.List(_owner, _project, "anya").Single();
        listed.Should().Be(added);
        listed.Width.Should().Be(640);
        listed.Bytes.Should().Be(24 + 4 + 100_000);
    }

    [Fact]
    public void Add_Limit_49_50_51()
    {
        Add([.. Enumerable.Range(0, 49).Select(i => Up(i))]).Added.Should().HaveCount(49);
        Add(Up(100)).Added.Should().HaveCount(1);
        var over = Add(Up(101));
        over.Added.Should().BeEmpty();
        over.Rejected.Single().Reason.Should().Be("limit");
        _sut.List(_owner, _project, "anya").Should().HaveCount(50);
    }

    [Fact]
    public void Add_Duplicate_ByContent()
    {
        Add(Up(1, "x.png")).Added.Should().HaveCount(1);
        var r = Add(Up(1, "other.png"));
        r.Added.Should().BeEmpty();
        r.Rejected.Single().Reason.Should().Be("duplicate");
    }

    [Fact]
    public void Add_FormatBySignature_NotExtension()
    {
        var r = Add(new PhotoUpload("fake.png", "not an image at all, just text"u8.ToArray()));
        r.Rejected.Single().Reason.Should().Be("format");
        // настоящий PNG с поддельным расширением принимается и получает имя по сигнатуре
        var ok = Add(new PhotoUpload("real.txt", Png(600, 600, 7)));
        ok.Added.Single().Name.Should().EndWith(".png");
    }

    [Fact]
    public void Add_TooSmall_And_TooBig()
    {
        Add(new PhotoUpload("s.png", Png(511, 2000, 1))).Rejected.Single().Reason.Should().Be("too_small");
        Add(new PhotoUpload("b.png", Png(600, 600, 2, 8 * 1024 * 1024))).Rejected.Single().Reason.Should().Be("too_big");
        Add(new PhotoUpload("e.png", Png(512, 512, 3))).Added.Should().HaveCount(1);
    }

    [Fact]
    public void PhotosHash_OrderIndependent_ChangesOnReplace()
    {
        var a = CharacterTrainingPhotos.PhotosHash(["bbbbbbbbbbbbbbbb.png", "aaaaaaaaaaaaaaaa.jpg"]);
        var b = CharacterTrainingPhotos.PhotosHash(["aaaaaaaaaaaaaaaa.jpg", "bbbbbbbbbbbbbbbb.png"]);
        var c = CharacterTrainingPhotos.PhotosHash(["aaaaaaaaaaaaaaaa.jpg", "cccccccccccccccc.png"]);
        a.Should().Be(b).And.MatchRegex("^sha256:[0-9a-f]{64}$");
        c.Should().NotBe(a);
    }

    [Fact]
    public void Remove_DeletesFile_AndUnknownReturnsFalse()
    {
        var name = Add(Up(1)).Added.Single().Name;
        _sut.PathOf(_owner, _project, "anya", name).Should().NotBeNull();
        _sut.Remove(_owner, _project, "anya", name).Should().BeTrue();
        _sut.List(_owner, _project, "anya").Should().BeEmpty();
        _sut.Remove(_owner, _project, "anya", name).Should().BeFalse();
    }

    [Theory]
    [InlineData("../../projects.json")]
    [InlineData("..\\x.png")]
    [InlineData("0123456789abcdef.png\n")]
    [InlineData("0123456789ABCDEF.png")]
    [InlineData("0123456789abcdef.gif")]
    public void TraversalOrBadName_Refused(string name)
    {
        File.WriteAllText(Path.Combine(_root, "projects.json"), "x");
        CharacterTrainingPhotos.IsValidName(name).Should().BeFalse();
        _sut.PathOf(_owner, _project, "anya", name).Should().BeNull();
        _sut.Remove(_owner, _project, "anya", name).Should().BeFalse();
        File.Exists(Path.Combine(_root, "projects.json")).Should().BeTrue();
    }

    [Fact]
    public void BadSlug_NoDiskAccess()
    {
        var r = _sut.Add(_owner, _project, "../evil", [Up(1)]);
        r.Added.Should().BeEmpty();
        Directory.Exists(Path.Combine(_root, "lora-training")).Should().BeFalse();
    }

    [Fact]
    public void Storage_IsOutsideProject_PerOwnerProjectSlug()
    {
        var name = Add(Up(1)).Added.Single().Name;
        var expected = Path.Combine(_root, "lora-training", Guid.Parse(_owner).ToString("N"), Guid.Parse(_project).ToString("N"), "anya", name);
        File.Exists(expected).Should().BeTrue();
        _sut.List(Guid.NewGuid().ToString(), _project, "anya").Should().BeEmpty();
    }
}
