using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

public class ProjectManagerTests : IDisposable
{
    private const string TestUserId = "test-user-id";
    private const string TestUsername = "test-user";

    private readonly string _tempDir;
    private readonly ProjectManager _sut;

    public ProjectManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pm_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _sut = CreateManager();
    }

    private UserStore CreateUserStore() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json")
            }).Build(),
        new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(),
        NullLogger<UserStore>.Instance);

    private AppSettingsService CreateAppSettings() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json")
            }).Build());

    private ProjectManager CreateManager() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json")
            }).Build(),
        CreateUserStore(),
        CreateAppSettings());

    private string MkDir(string name)
    {
        var dir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void GetAll_EmptyStore_ReturnsEmpty()
    {
        _sut.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void GetById_NonExistent_ReturnsNull()
    {
        _sut.GetById("unknown").Should().BeNull();
    }

    [Fact]
    public void Create_ValidDir_ReturnsProject()
    {
        var dir = MkDir("proj1");
        var p = _sut.Create("MyProject", dir, TestUserId, TestUsername);
        p.Name.Should().Be("MyProject");
        p.RootPath.Should().Be(dir);
        p.Id.Should().NotBeEmpty();
        p.OwnerId.Should().Be(TestUserId);
    }

    [Fact]
    public void Create_NonExistentDir_ThrowsDirectoryNotFound()
    {
        var act = () => _sut.Create("Bad", @"C:\nonexistent\fake_" + Guid.NewGuid(), TestUserId, TestUsername);
        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void Create_PersistsAcrossInstances()
    {
        var dir = MkDir("persist");
        _sut.Create("Persisted", dir, TestUserId, TestUsername);

        var manager2 = CreateManager();
        manager2.GetAll().Should().ContainSingle(p => p.Name == "Persisted");
    }

    [Fact]
    public void GetAll_MultipleProjects_ReturnsAll()
    {
        _sut.Create("A", MkDir("a"), TestUserId, TestUsername);
        _sut.Create("B", MkDir("b"), TestUserId, TestUsername);
        _sut.GetAll().Should().HaveCount(2);
    }

    [Fact]
    public void GetByOwner_FiltersCorrectly()
    {
        _sut.Create("A", MkDir("a1"), "user-1", "user-one");
        _sut.Create("B", MkDir("b1"), "user-2", "user-two");
        _sut.Create("C", MkDir("c1"), "user-1", "user-one");

        _sut.GetByOwner("user-1").Should().HaveCount(2);
        _sut.GetByOwner("user-2").Should().HaveCount(1);
        _sut.GetByOwner("user-3").Should().BeEmpty();
    }

    [Fact]
    public void GetById_ExistingProject_ReturnsProject()
    {
        var created = _sut.Create("X", MkDir("x"), TestUserId, TestUsername);
        _sut.GetById(created.Id).Should().NotBeNull()
            .And.Subject.As<object?>().Should().BeEquivalentTo(new { Name = "X" });
    }

    [Fact]
    public void Update_Name_UpdatesName()
    {
        var created = _sut.Create("Old", MkDir("upd"), TestUserId, TestUsername);
        var updated = _sut.Update(created.Id, "New", null);
        updated.Name.Should().Be("New");
        updated.RootPath.Should().Be(created.RootPath);
    }

    [Fact]
    public void Update_RootPath_UpdatesPath()
    {
        var created = _sut.Create("P", MkDir("r1"), TestUserId, TestUsername);
        var dir2 = MkDir("r2");
        var updated = _sut.Update(created.Id, null, dir2);
        updated.RootPath.Should().Be(dir2);
    }

    [Fact]
    public void Update_NonExistent_ThrowsKeyNotFound()
    {
        var act = () => _sut.Update("nope", "X", null);
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void Update_NonExistentNewPath_ThrowsDirectoryNotFound()
    {
        var created = _sut.Create("P", MkDir("valid"), TestUserId, TestUsername);
        var act = () => _sut.Update(created.Id, null, @"C:\fake_" + Guid.NewGuid());
        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void Delete_ExistingProject_ReturnsTrueAndRemoves()
    {
        var created = _sut.Create("D", MkDir("del"), TestUserId, TestUsername);
        _sut.Delete(created.Id).Should().BeTrue();
        _sut.GetById(created.Id).Should().BeNull();
    }

    [Fact]
    public void Delete_NonExistentProject_ReturnsFalse()
    {
        _sut.Delete("ghost").Should().BeFalse();
    }

    [Fact]
    public void Delete_PersistsRemoval()
    {
        var created = _sut.Create("Gone", MkDir("gone"), TestUserId, TestUsername);
        _sut.Delete(created.Id);

        var manager2 = CreateManager();
        manager2.GetById(created.Id).Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
