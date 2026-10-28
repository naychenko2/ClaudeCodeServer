using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// Модель, которая берёт несколько фото персонажа как одного человека (Klein, MaxCharacterPhotos=5),
// получает пять фото разных ракурсов: главное первым, дальше анфас, три четверти, профиль, анфас.
// И без флага подбора (порядок ForRequest), и с ним (подбор в задаче — тот же набор, запись auto)
public class KleinCharacterPhotosLaunchTests : IDisposable
{
    private const string Owner = "user-k";
    private const string Project = "p1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-klein-photos-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly string[] _files;
    private readonly List<ImageEditRequest> _requests = [];
    private readonly List<ImageThreadCharacter> _picks = [];
    private readonly ImageEditJobService _service;
    private readonly ImageEditScope _scope;
    private readonly IImageEditor _editor;

    public KleinCharacterPhotosLaunchTests()
    {
        _root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(_root);
        var created = CharacterStore.Create(_root,
            new CharacterDraft("Аня", null, Characters.CharacterStoreTests.Photos(7)), DateTime.UtcNow);
        created.Error.Should().BeNull();
        var manifest = created.Value!.Manifest;
        _files = [.. manifest.Photos.Select(p => p.File)];
        manifest.Photos[0].Primary.Should().BeTrue();
        CharacterStore.Update(_root, Owner, manifest.Slug, new CharacterPatch(null, null, [], [], null, null,
            new Dictionary<string, CharacterPhotoLabels>
            {
                [_files[0]] = new("front", "level", "neutral"),
                [_files[1]] = new("profile", "level", "neutral"),
                [_files[2]] = new("front", "up", "neutral"),
                [_files[3]] = new("three-quarter", "up", "neutral"),
                [_files[4]] = new("three-quarter", "level", "neutral"),
                [_files[5]] = new("front", "level", "neutral"),
                [_files[6]] = new("profile", "level", "neutral"),
            }))!.Error.Should().BeNull();
        _scope = ImageEditScope.Of(new Project { Id = Project, OwnerId = Owner, RootPath = _root });

        var model = new ImageEditModelInfo("fake-klein", "Фейк Klein",
            new ImageEditCaps([ImageEditOp.Generate], MaskSupport.None, 15, 4, true, MaxCharacterPhotos: 5, MaxFaceCropPhotos: 1),
            new ImageEditPriceHint(0, ImageEditPriceUnits.Free, "image"));
        var editor = new Mock<IImageEditor>();
        editor.SetupGet(e => e.Key).Returns("fake");
        editor.SetupGet(e => e.Label).Returns("Фейк");
        editor.SetupGet(e => e.PriceUnit).Returns(ImageEditPriceUnits.Free);
        editor.SetupGet(e => e.Enabled).Returns(true);
        editor.SetupGet(e => e.Registered).Returns(true);
        editor.SetupGet(e => e.Models).Returns([model]);
        editor.Setup(e => e.PickModel(It.IsAny<ImageEditOp>(), It.IsAny<EditMode>(), It.IsAny<EditTraits>())).Returns(model);
        editor.Setup(e => e.RunAsync(It.IsAny<ImageEditRequest>(), It.IsAny<IProgress<EditProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((ImageEditRequest r, IProgress<EditProgress> _, CancellationToken _) =>
            {
                lock (_requests) _requests.Add(r);
                return Task.FromResult(new ImageEditResult(EditOutcome.Ok,
                    [new EditedImage(TestImages.Png(8, 6), "image/png")], new EditCost(0, ImageEditPriceUnits.Free), false, null, null));
            });
        _service = new ImageEditJobService([editor.Object], new ImageEditWorkspace(Path.Combine(_dir, "image-editor")),
            NullLogger<ImageEditJobService>.Instance, new MemorySpendStore(), new RecordingBroadcaster());
        _service.CharacterPicked += (_, _, launch) =>
        {
            lock (_picks) _picks.Add(launch);
            return Task.CompletedTask;
        };
        _editor = editor.Object;
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class NoRunner : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private async Task<ImageEditRequest> Run(bool flagOn, string prompt)
    {
        var flags = new Mock<IFeatureFlagGate>();
        flags.Setup(f => f.IsEnabled(Owner, FeatureFlagKeys.CharacterSmartPhotos)).Returns(flagOn);
        var assembler = new ImageEditLaunchAssembler([_editor], _service, new SkiaImageRaster(),
            flags: flags.Object, sceneParser: new CharacterSceneParser(new NoRunner()));

        var quote = await _service.QuoteAsync(Owner, Project,
            new ImageEditQuoteRequest("fake", "fake-klein", EditMode.Auto, ImageEditOp.Generate, 1, false,
                References: 0, HasCharacter: true), default);
        quote.Value.Should().NotBeNull(quote.Error);
        var assembled = await assembler.AssembleAsync(Owner, _scope,
            new ImageEditLaunchRequest(quote.Value!.QuoteId, prompt, null, null, null, null, null, [], [], "anya",
                CharacterPhotos: null), default);
        assembled.Value.Should().NotBeNull(assembled.Error);
        var started = await _service.StartAsync(Owner, Project, assembled.Value!, default);
        started.Value.Should().NotBeNull(started.Error);
        for (var i = 0; i < 500 && _service.Get(Owner, Project, started.Value!.JobId)!.IsActive(); i++) await Task.Delay(10);
        lock (_requests) return _requests.Last();
    }

    // Фото персонажа в запросе — по байтам файлов папки персонажа
    private string[] SentFiles(ImageEditRequest request)
    {
        var dir = CharacterStore.CharacterDir(_root, "anya")!;
        var bytes = _files.ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(dir, f)));
        return [.. request.References.Where(r => r.Role == ReferenceRole.Character)
            .Select(r => bytes.Single(kv => kv.Value.SequenceEqual(r.Bytes)).Key)];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Slow")]
    public async Task Пять_фото_разных_ракурсов_главное_первым(bool flagOn)
    {
        var request = await Run(flagOn, "портрет в профиль у окна");

        // анфас (главное), анфас снизу, три четверти ровно, профиль, анфас — профиль сюжета набор не ломает
        SentFiles(request).Should().Equal(_files[0], _files[2], _files[4], _files[1], _files[5]);
        lock (_picks)
        {
            if (!flagOn) _picks.Should().BeEmpty();
            else
            {
                var pick = _picks.Should().ContainSingle().Subject;
                pick.Pick.Should().Be("auto");
                pick.Photos.Should().Equal(_files[0], _files[2], _files[4], _files[1], _files[5]);
            }
        }
    }
}
