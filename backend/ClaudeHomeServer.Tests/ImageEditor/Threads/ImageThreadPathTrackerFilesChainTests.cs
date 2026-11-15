using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

/// <summary>
/// Сквозная цепочка переноса пути нити (ADR-019 §1, ADR-040 Ф2-9): FileService.Rename →
/// ProjectFiles (ретрансляция OnMutated) → ImageThreadPathTracker. Отдельные звенья проверены
/// своими тестами (ProjectFilesTests — дошв события; ImageThreadPathTrackerPersonalTests —
/// сам трекер на мокнутом событии); здесь — что они соединены: трекер подписан на событие
/// шва и переписывает путь личной нити по настоящему переименованию файла.
/// </summary>
public sealed class ImageThreadPathTrackerFilesChainTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "own-chat";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "itpt-chain-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ImageThreadStore _store;

    public ImageThreadPathTrackerFilesChainTests()
    {
        _root = Path.Combine(_dir, "Personal", Owner);
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        File.WriteAllText(Path.Combine(_root, "images", "a.png"), "x");
        _store = new ImageThreadStore(Path.Combine(_dir, "threads"));
        _store.Create(Owner, Chat, "images/a.png", null, focus: true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private Project Personal() => new()
        { Id = "personal-" + Owner, OwnerId = Owner, Kind = ProjectKind.Personal, RootPath = _root };

    [Fact]
    public async Task Переименование_через_файловый_API_переписывает_путь_нити_личного_чата()
    {
        // Настоящие звенья цепочки: файловый сервис, шов IProjectFiles поверх него, трекер на его событии
        var files = new ProjectFiles(new FileService());
        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetAll()).Returns(new List<Session> { new() { Id = Chat, OwnerId = Owner } });
        var projects = new Mock<IProjectManager>();
        // Корень «Личного» в проектах не числится: по нему никто не находится — это нормальный ход personal-нити
        projects.Setup(p => p.GetByRootPath(It.IsAny<string>())).Returns(new List<Project>());
        var personal = new Mock<IPersonalProjects>();
        personal.Setup(p => p.GetPersonalOf(Owner)).Returns(Personal());
        var tracker = new ImageThreadPathTracker(projects.Object, directory.Object, _store,
            NullLogger<ImageThreadPathTracker>.Instance, files, new PersonalFolderResolver(personal.Object));
        await tracker.StartAsync(default);

        await files.RenameAsync(Personal(), "images/a.png", "images/b.png");

        _store.Get(Owner, Chat).Threads.Single().File.Should().Be("images/b.png",
            "нить идёт за файлом: мутация дошла до трекера тем же путём, что на сервере");
        File.Exists(Path.Combine(_root, "images", "b.png")).Should().BeTrue();
        await tracker.StopAsync(default);
        _store.Get(Owner, Chat).Threads.Single().File.Should().Be("images/b.png", "снятие подписки ничего не отменило");
    }
}
