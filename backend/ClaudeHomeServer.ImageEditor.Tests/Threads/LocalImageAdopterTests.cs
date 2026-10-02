using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// Агент позвал local_generate_image напрямую, мимо image_generate: картинка результата усыновляется —
// нить по файлу и якорь image_thread в ленте, то есть та же карточка, что у запуска через редактор
public sealed class LocalImageAdopterTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Project = "p1";
    private const string Chat = "chat-1";
    private const string Pic = ".cc-attachments/local-media/2026-10-02/lm_a-1.png";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccs_image_adopt_" + Guid.NewGuid().ToString("N"));
    private readonly ImageThreadStore _store;
    private readonly List<StoredModuleRecord> _records = [];
    private readonly Mock<IFeatureFlagGate> _flags = new();
    private readonly Mock<ISessionDirectory> _directory = new();
    private readonly Mock<IProjectManager> _projects = new();

    public LocalImageAdopterTests()
    {
        _store = new ImageThreadStore(Path.Combine(_dir, ImageThreadStore.DirName));
        _flags.Setup(f => f.IsEnabled(Owner, FeatureFlagKeys.ImageEditor)).Returns(true);
        var session = new Session { Id = Chat, OwnerId = Owner, ProjectId = Project };
        _directory.Setup(d => d.GetById(Chat)).Returns(session);
        _directory.Setup(d => d.ResolveOwnerId(session)).Returns(Owner);
        _projects.Setup(p => p.GetById(Project)).Returns(new Project { Id = Project, OwnerId = Owner, RootPath = _dir });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private LocalImageAdopter Adopter()
    {
        var feed = new Mock<IChatFeed>();
        feed.Setup(f => f.AppendRecordAsync(It.IsAny<string>(), It.IsAny<StoredModuleRecord>(), It.IsAny<CancellationToken>()))
            .Callback<string, StoredModuleRecord, CancellationToken>((_, r, _) => _records.Add(r)).ReturnsAsync(true);
        var threads = new ImageThreadService(_store, NullLogger<ImageThreadService>.Instance, _directory.Object, feed.Object,
            new Mock<ISessionBroadcaster>().Object);
        return new LocalImageAdopter(threads, _directory.Object, _projects.Object, _flags.Object);
    }

    private static LocalMediaAdoption Result(params (string Path, string Type)[] files) =>
        new(Owner, Project, Chat, "generate_image", [.. files.Select(f => new LocalMediaAdoptedFile(f.Path, f.Type))]);

    [Fact]
    public async Task Картинка_результата_получает_нить_и_якорь_в_ленте()
    {
        await Adopter().AdoptAsync(Result((Pic, "image/png"), ("a/clip.mp4", "video/mp4")), default);

        var thread = _store.Get(Owner, Chat).Threads.Should().ContainSingle("видео картинкой не считается").Subject;
        thread.File.Should().Be(Pic);
        var record = _records.Should().ContainSingle().Subject;
        record.Module.Should().Be("imageeditor");
        record.RecordType.Should().Be(ImageThreadService.RecordTypes.Thread);
        record.Data!.Value.GetProperty("threadId").GetString().Should().Be(thread.Id);
    }

    [Fact]
    public async Task Повторное_усыновление_и_выбор_человека()
    {
        var mine = _store.Open(Owner, Chat, "images/mine.png", null, _store.Get(Owner, Chat).Revision).Thread!.Id;
        var adopter = Adopter();

        await adopter.AdoptAsync(Result((Pic, "image/png")), default);
        await adopter.AdoptAsync(Result((Pic, "image/png")), default);

        var state = _store.Get(Owner, Chat);
        state.Threads.Should().HaveCount(2);
        _records.Should().ContainSingle("второго якоря на тот же файл нет");
        state.Focus.Should().Be(mine, "выбор картинки человеком агент не трогает");
    }

    [Fact]
    public async Task Выключенный_флаг_молчит()
    {
        _flags.Setup(f => f.IsEnabled(Owner, FeatureFlagKeys.ImageEditor)).Returns(false);

        await Adopter().AdoptAsync(Result((Pic, "image/png")), default);

        _store.Get(Owner, Chat).Threads.Should().BeEmpty();
        _records.Should().BeEmpty();
    }
}
