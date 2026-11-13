using ClaudeHomeServer.Services;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// ADR-040, Ф2-3: личная ветка ImageFrameSource — кадр нити по файлу личного чата берётся из папки «Личного»;
// без папки или без резолвера — по-прежнему null
public sealed class ImageFrameSourcePersonalTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ifs-personal-" + Guid.NewGuid().ToString("N"));
    private readonly string _personalRoot;
    private readonly ImageThreadStore _store;
    private readonly ImageThreadService _threads;
    private readonly Mock<ISessionDirectory> _directory = new();
    private readonly Mock<IProjectManager> _projects = new();
    private readonly string _threadId;

    public ImageFrameSourcePersonalTests()
    {
        _personalRoot = Path.Combine(_root, "Personal");
        Directory.CreateDirectory(Path.Combine(_personalRoot, "images"));
        File.WriteAllBytes(Path.Combine(_personalRoot, "images", "a.png"), [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        _store = new ImageThreadStore(Path.Combine(_root, "threads"));
        _directory.Setup(d => d.GetById(Chat)).Returns(new Session { Id = Chat, OwnerId = Owner });
        _threads = new ImageThreadService(_store, NullLogger<ImageThreadService>.Instance, _directory.Object);
        _threadId = _store.Open(Owner, Chat, "images/a.png", null, _store.Get(Owner, Chat).Revision).Thread!.Id;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ImageFrameSource Source(PersonalFolderResolver? personal) =>
        new(_store, _threads, _projects.Object, _directory.Object, null, personal);

    private PersonalFolderResolver Resolver(bool folderOnDisk = true)
    {
        var store = new Mock<IPersonalProjects>();
        store.Setup(s => s.GetPersonalOf(Owner)).Returns(new Project
        {
            Id = "personal-id", OwnerId = Owner, Kind = ProjectKind.Personal,
            RootPath = folderOnDisk ? _personalRoot : Path.Combine(_root, "нет-такой"),
        });
        return new PersonalFolderResolver(store.Object);
    }

    [Fact]
    public async Task Нить_по_файлу_Личного_отдаёт_картинку_из_папки_Личного()
    {
        var frame = await Source(Resolver()).GetAsync(Owner, Owner, _threadId, ImageThreadVersion.OriginId, default);

        frame.Should().NotBeNull();
        frame!.Bytes.Should().Equal(0x89, 0x50, 0x4E, 0x47, 1, 2, 3);
    }

    [Fact]
    public async Task Без_резолвера_или_без_папки_кадра_нет()
    {
        (await Source(null).GetAsync(Owner, Owner, _threadId, ImageThreadVersion.OriginId, default)).Should().BeNull();
        (await Source(Resolver(folderOnDisk: false)).GetAsync(Owner, Owner, _threadId, ImageThreadVersion.OriginId, default))
            .Should().BeNull();
    }

    [Fact]
    public async Task Чужая_нить_Личного_владельцу_не_отдаётся()
    {
        (await Source(Resolver()).GetAsync("other-owner", "other-owner", _threadId, ImageThreadVersion.OriginId, default))
            .Should().BeNull();
    }
}
