using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Faces;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Tests.ImageEditor.Providers;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Jobs;

// Подстановка персонажа на лицо в личной области (ADR-040, Ф2-4): ключ области personal, персонаж лежит
// в папке «Личного», сборщик берёт образцы из scope.Project. Без папки — прежний отказ.
public class PersonalFaceTargetTests : IDisposable
{
    private const string Owner = "user-pft";
    private const string Chat = "chat-pft";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs_personal_face_" + Guid.NewGuid().ToString("N"));
    private readonly string _character;
    private readonly ImageThreadStore _store;
    private readonly ImageThreadService _threads;
    private readonly string _threadId;
    private readonly string _facesKey;

    public PersonalFaceTargetTests()
    {
        Directory.CreateDirectory(_root);
        var created = CharacterStore.Create(_root,
            new CharacterDraft("Боря", null, Characters.CharacterStoreTests.Photos(3)), DateTime.UtcNow);
        created.Error.Should().BeNull();
        _character = created.Value!.Manifest.Slug;

        File.WriteAllBytes(Path.Combine(_root, "test.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        _store = new ImageThreadStore(Path.Combine(_root, "threads"));
        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(Chat)).Returns(new Session { Id = Chat, OwnerId = Owner, ProjectId = null });
        _threads = new ImageThreadService(_store, new Mock<ILogger<ImageThreadService>>().Object, directory: directory.Object);
        _threads.OpenAsync(Owner, ImageEditScope.Personal, Chat, "test.png", null, 0, default).GetAwaiter().GetResult();
        _threadId = _store.Get(Owner, Chat).Threads.First().Id;
        _facesKey = FaceDetectionCache.FacesKey(File.ReadAllBytes(Path.Combine(_root, "test.png")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // Область личного чата: ключ personal, «Личное» кладёт гейт, когда папка есть
    private ImageEditScope Scope(bool withFolder) =>
        ImageEditScope.Of(new Session { Id = Chat, OwnerId = Owner, ProjectId = null }) with
        {
            TenantOwnerId = Owner,
            Project = withFolder
                ? new Project { Id = "personal-id", OwnerId = Owner, RootPath = _root, Kind = ProjectKind.Personal }
                : null,
        };

    private ImageEditLaunchAssembler Assembler()
    {
        var media = new Mock<ILocalImageMedia>();
        media.Setup(m => m.DetectFacesAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalFaceDetectResult(200, 200, [new LocalDetectedFace(1, 10, 10, 100, 100, 0.9, false)]));
        var jobs = new Mock<IImageEditJobs>();
        // Котировки личной области ключуются personal, а не id «Личного»
        jobs.Setup(j => j.QuoteInfo(Owner, ImageEditScope.Personal, "q1"))
            .Returns(new ImageEditQuoteInfo(ImageEditOp.Edit, null, "local", FacePasses: 1));
        return new ImageEditLaunchAssembler(
            [HiggsfieldImageEditorTests.Create().Editor],
            jobs.Object,
            new SkiaImageRaster(),
            threads: _threads,
            faces: new FaceDetectionCache(),
            localMedia: media.Object);
    }

    private ImageEditLaunchRequest Request() => new(
        "q1", null, null, null, null, null, null, [], [], null,
        ThreadSessionId: Chat, ThreadId: _threadId,
        FaceTarget: new FaceTarget("origin", _facesKey, [new FaceAssignment(1, _character)]));

    [Fact]
    public async Task Личная_область_с_папкой_образцы_только_персонаж_из_Личного()
    {
        var result = await Assembler().AssembleAsync(Owner, Owner, Scope(withFolder: true), Request(), default);

        result.Value.Should().NotBeNull(result.Error);
        result.Value!.References.Should().NotBeEmpty().And.OnlyContain(r => r.Role == ReferenceRole.Character);
        result.Value.Character!.Slug.Should().Be(_character);
        result.Value.FaceTarget!.CharacterSlug.Should().Be(_character);
    }

    [Fact]
    public async Task Личная_область_без_папки_прежний_отказ_до_диска()
    {
        var result = await Assembler().AssembleAsync(Owner, Owner, Scope(withFolder: false), Request(), default);

        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
    }
}
