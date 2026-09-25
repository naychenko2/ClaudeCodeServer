using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Локальные проекты в менеджере проектов (ADR-016 §1, задача 3.1): ключ «одна папка — один
/// проект» — пара «устройство + путь», путь на устройстве сервер не проверяет на своём диске,
/// соседи по серверной папке локальных проектов не включают.
/// </summary>
public class ProjectManagerLocalProjectTests : IDisposable
{
    private const string UserId = "user-1";
    private readonly string _tempDir;
    private readonly ProjectManager _sut;

    public ProjectManagerLocalProjectTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pm_local_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _sut = CreateManager();
    }

    private ProjectManager CreateManager()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
            }).Build();
        return new ProjectManager(config,
            new UserStore(config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance),
            new AppSettingsService(config));
    }

    private string MkDir(string name)
    {
        var dir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public void CreateLocal_ПапкиНаСервереНет_ПроектСоздаётсяСУстройством()
    {
        var p = _sut.CreateLocal("L", "C:\\Work\\Proj\\", UserId, "dev-1");

        p.DeviceId.Should().Be("dev-1");
        p.RootPath.Should().Be("C:\\Work\\Proj");
        ProjectCapabilities.IsDeviceBound(p).Should().BeTrue();
    }

    [Fact]
    public void ОдинПутьНаСервереИУстройстве_ДваРазныхПроекта()
    {
        var dir = MkDir("same");
        var server = _sut.Create("S", dir, UserId, "u");

        var local = _sut.CreateLocal("L", dir, UserId, "dev-1");

        local.Id.Should().NotBe(server.Id);
        _sut.GetByOwner(UserId).Should().HaveCount(2);
    }

    [Fact]
    public void ОдинПутьНаОдномУстройстве_Отказ_НаДругомМожно()
    {
        _sut.CreateLocal("A", "/home/u/p", UserId, "dev-1");

        FluentActions.Invoking(() => _sut.CreateLocal("B", "/home/u/p/", UserId, "dev-1"))
            .Should().Throw<ArgumentException>().WithMessage("*уже подключена*");
        _sut.CreateLocal("C", "/home/u/p", UserId, "dev-2").DeviceId.Should().Be("dev-2");
    }

    [Fact]
    public void GetByRootPath_ЛокальныйПроектНеСосед()
    {
        var dir = MkDir("neighbors");
        var server = _sut.Create("S", dir, UserId, "u");
        _sut.CreateLocal("L", dir, UserId, "dev-1");

        _sut.GetByRootPath(dir).Select(p => p.Id).Should().Equal(server.Id);
    }

    [Fact]
    public void CreateLocal_ОтносительныйПуть_Отказ() =>
        FluentActions.Invoking(() => _sut.CreateLocal("L", "rel/path", UserId, "dev-1"))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void Update_ПутьЛокального_НеПроверяетсяНаДискеСервера()
    {
        var p = _sut.CreateLocal("L", "/home/u/p", UserId, "dev-1");

        var updated = _sut.Update(p.Id, null, "/home/u/other/");

        updated.RootPath.Should().Be("/home/u/other");
    }

    [Fact]
    public void SetDevice_СервернуюПапкуНаУстройство_ИОбратно()
    {
        var dir = MkDir("rebind");
        var p = _sut.Create("S", dir, UserId, "u");

        var local = _sut.SetDevice(p.Id, "dev-1", "/home/u/p");
        local.DeviceId.Should().Be("dev-1");
        local.RootPath.Should().Be("/home/u/p");

        var server = _sut.SetDevice(p.Id, null, dir);
        server.DeviceId.Should().BeNull();
        server.RootPath.Should().Be(Path.GetFullPath(dir));
    }

    [Fact]
    public void SetDevice_НаСервер_ПапкиНет_Отказ()
    {
        var p = _sut.CreateLocal("L", "/nonexistent_" + Guid.NewGuid().ToString("N"), UserId, "dev-1");

        FluentActions.Invoking(() => _sut.SetDevice(p.Id, null))
            .Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void DeviceId_ПереживаетПерезагрузкуСтора()
    {
        var p = _sut.CreateLocal("L", "/home/u/p", UserId, "dev-1");

        CreateManager().GetById(p.Id)!.DeviceId.Should().Be("dev-1");
    }
}
