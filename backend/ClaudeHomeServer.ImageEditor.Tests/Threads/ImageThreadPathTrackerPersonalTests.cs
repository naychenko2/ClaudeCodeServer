using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// ADR-040 Ф2-9 («Мои файлы»): корень «Личного» в проектах не числится, поэтому его нити — личные
// чаты (ProjectId == null) того же владельца: их путь переписывается на папку этого владельца.
// Здесь IProjectFiles — мок и событие поднимается напрямую (_files.Raise): проверяется поведение
// трекера, а не доставка события. Настоящая цепочка FileService.OnMutated → ProjectFiles.OnMutated
// → трекер — в ImageThreadPathTrackerFilesChainTests (ClaudeHomeServer.Tests). Что здесь не
// проверялось: чужая папка того же пути не касается, без резолвера personal-нить не трогается
// вовсе, мутация не-Rename — не повод писать стор.
public sealed class ImageThreadPathTrackerPersonalTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Other = "owner-2";
    private const string OwnChat = "own-chat";
    private const string OtherChat = "other-chat";
    private const string OldRel = "images/a.png";
    private const string NewRel = "images/b.png";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "itpt-personal-" + Guid.NewGuid().ToString("N"));
    private readonly string _ownRoot;
    private readonly string _otherRoot;
    private readonly ImageThreadStore _store;
    private readonly Mock<IProjectManager> _projects = new();
    private readonly Mock<ISessionDirectory> _directory = new();
    private readonly Mock<IProjectFiles> _files = new();

    public ImageThreadPathTrackerPersonalTests()
    {
        _ownRoot = Path.Combine(_dir, "Personal", Owner);
        _otherRoot = Path.Combine(_dir, "Personal", Other);
        Directory.CreateDirectory(Path.Combine(_ownRoot, "images"));
        Directory.CreateDirectory(Path.Combine(_otherRoot, "images"));

        _store = new ImageThreadStore(Path.Combine(_dir, "threads"));
        _store.Create(Owner, OwnChat, OldRel, null, focus: true);
        _store.Create(Other, OtherChat, OldRel, null, focus: true);

        _directory.Setup(d => d.GetAll()).Returns(new List<Session>
        {
            new() { Id = OwnChat, OwnerId = Owner },
            new() { Id = OtherChat, OwnerId = Other },
        });
        // «Личное» — не проект: по его корню никто не находится, и это нормальный ход personal-нити
        _projects.Setup(p => p.GetByRootPath(It.IsAny<string>())).Returns(new List<Project>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // Резолвер «Личного» двух владельцев: Find требует папки на диске — обе созданы в .ctor
    private PersonalFolderResolver Resolver()
    {
        var personal = new Mock<IPersonalProjects>();
        personal.Setup(p => p.GetPersonalOf(Owner)).Returns(new Project
            { Id = "personal-" + Owner, OwnerId = Owner, Kind = ProjectKind.Personal, RootPath = _ownRoot });
        personal.Setup(p => p.GetPersonalOf(Other)).Returns(new Project
            { Id = "personal-" + Other, OwnerId = Other, Kind = ProjectKind.Personal, RootPath = _otherRoot });
        return new PersonalFolderResolver(personal.Object);
    }

    // Тот же подписчик, что ставит на сервере регистрация трекера; саму цепочку — другой файл
    private void Subscribe(PersonalFolderResolver? personal)
    {
        var tracker = new ImageThreadPathTracker(
            _projects.Object, _directory.Object, _store,
            NullLogger<ImageThreadPathTracker>.Instance, _files.Object, personal);
        tracker.StartAsync(default).GetAwaiter().GetResult();
    }

    // newRel! — только для подписи Moq (его args объявлен не-nullable): null доходит до
    // обработчика как есть, и для мутации не-Rename трекер обязан отказать по нему
    private void Raise(string root, string oldRel, FileMutationKind kind, string? newRel) =>
        _files.Raise(f => f.OnMutated += null, root, oldRel, kind, newRel!);

    private string FileOf(string owner, string chat) => _store.Get(owner, chat).Threads.Single().File!;

    [Fact]
    public void Переименование_файла_в_папке_Личного_переписывает_путь_нити_личного_чата()
    {
        Subscribe(Resolver());

        Raise(_ownRoot, OldRel, FileMutationKind.Rename, NewRel);

        FileOf(Owner, OwnChat).Should().Be(NewRel);
        // Чужая папка — тот же путь: её нить не тронута
        FileOf(Other, OtherChat).Should().Be(OldRel);
    }

    [Fact]
    public void Перенос_папки_Личного_переписывает_пути_внутри_неё()
    {
        Subscribe(Resolver());

        Raise(_ownRoot, "images", FileMutationKind.Rename, "imgs");

        FileOf(Owner, OwnChat).Should().Be("imgs/a.png");
        FileOf(Other, OtherChat).Should().Be(OldRel);
    }

    [Fact]
    public void Без_резолвера_Личного_путь_нити_личного_чата_остаётся_и_стор_не_переписывается()
    {
        var before = _store.Get(Owner, OwnChat).Revision;

        Subscribe(personal: null);
        Raise(_ownRoot, OldRel, FileMutationKind.Rename, NewRel);

        FileOf(Owner, OwnChat).Should().Be(OldRel);
        _store.Get(Owner, OwnChat).Revision.Should().Be(before);
    }

    [Fact]
    public void Мутация_не_rename_и_мутация_по_чужому_корню_нитей_не_касаются()
    {
        Subscribe(Resolver());

        Raise(_ownRoot, OldRel, FileMutationKind.Write, null);
        Raise(Path.Combine(_dir, "чужой-корень"), OldRel, FileMutationKind.Rename, NewRel);

        FileOf(Owner, OwnChat).Should().Be(OldRel);
        FileOf(Other, OtherChat).Should().Be(OldRel);
    }
}
