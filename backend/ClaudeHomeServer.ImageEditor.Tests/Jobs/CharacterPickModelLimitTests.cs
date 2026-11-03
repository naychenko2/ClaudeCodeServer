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

// Запись подбора в запуске отражает, что драйвер реально получил (ADR-027 §8): модель с
// MaxCharacterPhotos=1 берёт одно фото, и запись не показывает фото, которые модель не видела.
// Флаг выключен — обрезки нет, запись как есть
public class CharacterPickModelLimitTests : IDisposable
{
    private const string Owner = "user-p";
    private const string Project = "p1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-pick-limit-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly string[] _files;
    private readonly List<ImageEditRequest> _requests = [];
    private readonly List<ImageThreadCharacter> _picks = [];
    private readonly ImageEditJobService _service;
    private readonly ImageEditScope _scope;
    private readonly IImageEditor _editor;

    public CharacterPickModelLimitTests()
    {
        _root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(_root);
        var created = CharacterStore.Create(_root,
            new CharacterDraft("Аня", null, Characters.CharacterStoreTests.Photos(3)), DateTime.UtcNow);
        created.Error.Should().BeNull();
        var manifest = created.Value!.Manifest;
        _files = [.. manifest.Photos.Select(p => p.File)];
        // Третье фото — в профиль, второе — улыбка
        CharacterStore.Update(_root, Owner, manifest.Slug, new CharacterPatch(null, null, [], [], null, null,
            new Dictionary<string, CharacterPhotoLabels>
            {
                [_files[2]] = new("profile", "level", "neutral"),
                [_files[1]] = new("front", "level", "smile"),
            }))!.Error.Should().BeNull();
        _scope = ImageEditScope.Of(new Project { Id = Project, OwnerId = Owner, RootPath = _root });

        // Локальная модель: одно фото персонажа на человека
        var model = new ImageEditModelInfo("fake-model", "Фейк",
            new ImageEditCaps([ImageEditOp.Generate], MaskSupport.None, 5, 4, true, MaxCharacterPhotos: 1),
            new ImageEditPriceHint(0.01, ImageEditPriceUnits.Usd, "image"));
        var editor = new Mock<IImageEditor>();
        editor.SetupGet(e => e.Key).Returns("fake");
        editor.SetupGet(e => e.Label).Returns("Фейк");
        editor.SetupGet(e => e.PriceUnit).Returns(ImageEditPriceUnits.Usd);
        editor.SetupGet(e => e.Enabled).Returns(true);
        editor.SetupGet(e => e.Registered).Returns(true);
        editor.SetupGet(e => e.Models).Returns([model]);
        editor.Setup(e => e.PickModel(It.IsAny<ImageEditOp>(), It.IsAny<EditMode>(), It.IsAny<EditTraits>())).Returns(model);
        editor.Setup(e => e.RunAsync(It.IsAny<ImageEditRequest>(), It.IsAny<IProgress<EditProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((ImageEditRequest r, IProgress<EditProgress> _, CancellationToken _) =>
            {
                lock (_requests) _requests.Add(r);
                return Task.FromResult(new ImageEditResult(EditOutcome.Ok,
                    [new EditedImage(TestImages.Png(8, 6), "image/png")], new EditCost(0.01, ImageEditPriceUnits.Usd), true, null, null));
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
            new ImageEditQuoteRequest("fake", "fake-model", EditMode.Auto, ImageEditOp.Generate, 1, false,
                References: 3, HasCharacter: true), default);
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

    // Сюжет «в профиль»: подбор даёт три фото, но модель берёт одно — запись обрезается до одного
    [Fact]
    [Trait("Category", "Slow")]
    public async Task Запись_обрезается_до_числа_фото_которые_модель_взяла()
    {
        var request = await Run(true, "портрет в профиль у окна");

        // Драйвер получил одно фото персонажа (лимит модели)
        request.References.Count(r => r.Role == ReferenceRole.Character).Should().Be(1);
        lock (_picks)
        {
            var pick = _picks.Should().ContainSingle().Subject;
            pick.Pick.Should().Be("auto");
            // Точное совпадение ракурса (profile) → профильное фото первым, запись — ровно оно
            pick.Photos.Should().Equal(_files[2]);
        }
    }
}
