using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Шов файлов проекта (ADR-016, задача 4.1): ключ — проект, guard файловой группы внутри
/// вертикали Files. Локальному проекту отказ случается до диска — корень указывает в
/// каталог-ловушку, и ни одна операция не имеет права его создать. Серверный проект
/// работает как FileService, событие OnMutated доходит до подписчиков шва.
/// </summary>
public sealed class ProjectFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "project-files-" + Guid.NewGuid().ToString("N"));
    private readonly string _trap = Path.Combine(Path.GetTempPath(), "project-files-trap-" + Guid.NewGuid().ToString("N"));

    public ProjectFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ }
        try { Directory.Delete(_trap, recursive: true); } catch { /* ловушки быть не должно */ }
    }

    private static Project Local(string root) => new() { Id = "p-local", RootPath = root, DeviceId = "dev-1" };
    private Project Server() => new() { Id = "p-server", RootPath = _root };

    public static IEnumerable<object[]> Operations() => new[]
    {
        new object[] { "List", (Func<IProjectFiles, Project, Task>)((f, p) => f.ListAsync(p)) },
        new object[] { "Tree", (Func<IProjectFiles, Project, Task>)((f, p) => f.TreeAsync(p)) },
        new object[] { "Search", (Func<IProjectFiles, Project, Task>)((f, p) => f.SearchAsync(p, "a")) },
        new object[] { "ReadFile", (Func<IProjectFiles, Project, Task>)((f, p) => f.ReadFileAsync(p, "a.txt")) },
        new object[] { "ReadFileBytes", (Func<IProjectFiles, Project, Task>)((f, p) => f.ReadFileBytesAsync(p, "a.txt")) },
        new object[] { "WriteFile", (Func<IProjectFiles, Project, Task>)((f, p) => f.WriteFileAsync(p, "a.txt", "x")) },
        new object[] { "WriteFileBytes", (Func<IProjectFiles, Project, Task>)((f, p) => f.WriteFileBytesAsync(p, "sub/a.bin", [1])) },
        new object[] { "CreateFile", (Func<IProjectFiles, Project, Task>)((f, p) => f.CreateFileAsync(p, "sub/b.txt", "x")) },
        new object[] { "CreateDirectory", (Func<IProjectFiles, Project, Task>)((f, p) => f.CreateDirectoryAsync(p, "sub")) },
        new object[] { "Delete", (Func<IProjectFiles, Project, Task>)((f, p) => f.DeleteAsync(p, "a.txt")) },
        new object[] { "Rename", (Func<IProjectFiles, Project, Task>)((f, p) => f.RenameAsync(p, "a.txt", "b.txt")) },
        new object[] { "GetDiff", (Func<IProjectFiles, Project, Task>)((f, p) => f.GetDiffAsync(p, "a.txt")) },
        new object[] { "RevertFile", (Func<IProjectFiles, Project, Task>)((f, p) => f.RevertFileAsync(p, "a.txt")) },
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task ЛокальныйПроект_ОтказДоДиска(string name, Func<IProjectFiles, Project, Task> op)
    {
        var sut = new ProjectFiles(new FileService());

        var act = () => op(sut, Local(_trap));

        var ex = await act.Should().ThrowAsync<LocalProjectException>(name);
        ex.Which.Code.Should().Be(ProjectCapabilityGuard.Code);
        Directory.Exists(_trap).Should().BeFalse($"{name}: сервер не должен трогать путь локального проекта");
    }

    [Fact]
    public async Task СерверныйПроект_РаботаетКакFileService_ИOnMutatedДоходитДоШва()
    {
        var sut = new ProjectFiles(new FileService());
        var events = new List<(string Root, string Rel, FileMutationKind Kind, string? NewRel)>();
        sut.OnMutated += (root, rel, kind, newRel) => events.Add((root, rel, kind, newRel));

        await sut.CreateFileAsync(Server(), "a.txt", "привет");
        await sut.RenameAsync(Server(), "a.txt", "b.txt");

        (await sut.ReadFileAsync(Server(), "b.txt")).Should().Be("привет");
        (await sut.ListAsync(Server())).Select(e => e.Name).Should().Contain("b.txt");
        events.Should().Equal(
            (_root, "a.txt", FileMutationKind.Create, (string?)null),
            (_root, "a.txt", FileMutationKind.Rename, "b.txt"));
    }
}
