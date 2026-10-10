using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Tests.ImageEditor.Providers;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// Явный список фото персонажа в запуске (ADR-027 §8): гейт флага и код character_photos_invalid
public class CharacterPhotosLaunchTests : IDisposable
{
    private const string Owner = "user-p";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs_launch_chars_" + Guid.NewGuid().ToString("N"));
    private readonly string[] _files;
    private readonly ImageEditScope _scope;

    public CharacterPhotosLaunchTests()
    {
        Directory.CreateDirectory(_root);
        var created = CharacterStore.Create(_root,
            new CharacterDraft("Аня", null, Characters.CharacterStoreTests.Photos(3)), DateTime.UtcNow);
        created.Error.Should().BeNull();
        _files = [.. created.Value!.Manifest.Photos.Select(p => p.File)];
        _scope = ImageEditScope.Of(new Project { Id = "p1", OwnerId = Owner, RootPath = _root });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private ImageEditLaunchAssembler Assembler(bool flagOn)
    {
        var jobs = new Mock<IImageEditJobs>();
        jobs.Setup(j => j.QuoteInfo(Owner, "p1", "q1")).Returns(new ImageEditQuoteInfo(ImageEditOp.Edit, null));
        var flags = new Mock<IFeatureFlagGate>();
        flags.Setup(f => f.IsEnabled(Owner, FeatureFlagKeys.CharacterSmartPhotos)).Returns(flagOn);
        return new ImageEditLaunchAssembler([HiggsfieldImageEditorTests.Create().Editor], jobs.Object,
            new SkiaImageRaster(), flags: flags.Object);
    }

    private static ImageEditLaunchRequest Request(string? slug, IReadOnlyList<string>? photos) =>
        new("q1", "", null, null, null, null, null, [], [], slug, CharacterPhotos: photos);

    [Fact]
    public async Task Флаг_включён_образцы_идут_в_заданном_порядке()
    {
        var result = await Assembler(true).AssembleAsync(Owner, _scope, Request("anya", [_files[2], _files[0]]), default);

        result.Value.Should().NotBeNull(result.Error);
        result.Value!.References.Select(r => Characters.CharacterStoreTests.Hex(r.Bytes)).Should().Equal(
            Characters.CharacterStoreTests.Hex(Characters.CharacterStoreTests.Jpeg(3)),
            Characters.CharacterStoreTests.Hex(Characters.CharacterStoreTests.Jpeg(1)));
    }

    [Fact]
    public async Task Флаг_выключен_явный_список_отклоняется()
    {
        var result = await Assembler(false).AssembleAsync(Owner, _scope, Request("anya", [_files[0]]), default);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.CharacterPhotosInvalid);
    }

    [Fact]
    public async Task Чужое_имя_файла_даёт_character_photos_invalid()
    {
        var result = await Assembler(true).AssembleAsync(Owner, _scope, Request("anya", [_files[0], "../x.png"]), default);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.CharacterPhotosInvalid);
    }

    [Fact]
    public async Task Файл_в_манифесте_но_не_картинка_на_диске_отклоняет_список()
    {
        File.WriteAllText(Path.Combine(_root, CharacterStore.Folder, "anya", _files[1]), "это не картинка");

        var result = await Assembler(true).AssembleAsync(Owner, _scope, Request("anya", [_files[0], _files[1]]), default);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.CharacterPhotosInvalid);
    }

    [Fact]
    public async Task Список_без_персонажа_отклоняется()
    {
        var result = await Assembler(true).AssembleAsync(Owner, _scope, Request(null, [_files[0]]), default);

        result.ErrorCode.Should().Be(ImageEditErrorCodes.CharacterPhotosInvalid);
    }

    [Fact]
    public async Task Без_списка_при_выключенном_флаге_запуск_как_раньше()
    {
        var result = await Assembler(false).AssembleAsync(Owner, _scope, Request("anya", null), default);

        result.Value.Should().NotBeNull(result.Error);
        result.Value!.References.Should().HaveCount(3);
    }
}
