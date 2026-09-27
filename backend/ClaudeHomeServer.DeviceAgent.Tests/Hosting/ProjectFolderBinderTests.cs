using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.DeviceAgent.Hosting;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Tests.Hosting;

/// <summary>
/// Выдача папки проекта агентом (решение владельца 2026-09-27): создать и разрешить ровно эту
/// папку; запретный путь, файл и выключенная автовыдача — отказ без изменений на машине.
/// </summary>
public sealed class ProjectFolderBinderTests : IDisposable
{
    // Рядом с тестами, а не в temp: на Windows temp лежит в запретном AppData
    private readonly string _base = Path.Combine(AppContext.BaseDirectory, "bind-" + Guid.NewGuid().ToString("N"));

    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public ProjectFolderBinderTests() => MakeDir(_base);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* временная папка */ }
    }

    private string Home => Path.Combine(_base, "home");
    private string Config => Path.Combine(_base, "config");

    private static void MakeDir(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
    }

    private (ProjectFolderBinder Binder, AgentRootsStore Roots) Create()
    {
        MakeDir(Config);
        MakeDir(Home);
        var roots = new AgentRootsStore(Path.Combine(Config, "roots.json"));
        var context = new AgentForbiddenContext([Home], [Config], []);
        return (new ProjectFolderBinder(roots, new AgentPathPolicy(roots), context), roots);
    }

    [Fact]
    public void НетПапки_СозданаИРазрешена_СПодписьюПроекта()
    {
        var (binder, roots) = Create();
        var path = Path.Combine(Home, "projects", "app");

        var result = binder.Bind(new BindFolderRequest(path, "Мой проект"));

        result.Outcome.Should().Be(BindFolderOutcomes.Bound);
        result.Created.Should().BeTrue();
        result.Added.Should().BeTrue();
        Directory.Exists(path).Should().BeTrue();
        roots.Roots.Should().Equal(path);
        roots.LabelOf(path).Should().Be("добавлен автоматически для проекта «Мой проект»");
    }

    [Fact]
    public void ПапкаВнеКорней_Разрешена_НоНеСоздаётся()
    {
        var (binder, roots) = Create();
        var path = Path.Combine(Home, "existing");
        MakeDir(path);

        var result = binder.Bind(new BindFolderRequest(path, "p"));

        result.Should().Be(new BindFolderResult(BindFolderOutcomes.Bound, Created: false, Added: true));
        roots.Roots.Should().Equal(path);
    }

    [Fact]
    public void ПапкаУжеВнутриКорня_НичегоНеДобавляется()
    {
        var (binder, roots) = Create();
        var root = Path.Combine(Home, "work");
        MakeDir(root);
        MakeDir(Path.Combine(root, "app"));
        roots.Add(root);

        var result = binder.Bind(new BindFolderRequest(Path.Combine(root, "app"), "p"));

        result.Should().Be(new BindFolderResult(BindFolderOutcomes.Bound));
        roots.Roots.Should().Equal(root);
        roots.LabelOf(root).Should().BeNull();
    }

    [Theory]
    [InlineData("")]                 // сам профиль
    [InlineData(".ssh")]
    [InlineData(".ssh/keys")]
    [InlineData("projects/../.ssh")]
    [InlineData(".config/app")]
    public void ЗапретныйПуть_Отказ_НичегоНеСозданоИНеДобавлено(string relative)
    {
        var (binder, roots) = Create();
        var path = relative.Length == 0 ? Home : Home + Path.DirectorySeparatorChar + relative.Replace('/', Path.DirectorySeparatorChar);

        var result = binder.Bind(new BindFolderRequest(path, "p"));

        result.Outcome.Should().Be(BindFolderOutcomes.Forbidden);
        result.Message.Should().NotBeNullOrEmpty();
        roots.Roots.Should().BeEmpty();
        Directory.Exists(Path.Combine(Home, ".ssh")).Should().BeFalse();
        Directory.Exists(Path.Combine(Home, ".config")).Should().BeFalse();
    }

    [Fact]
    public void КаталогАгента_Отказ()
    {
        var (binder, roots) = Create();

        binder.Bind(new BindFolderRequest(Path.Combine(Config, "sub"), "p")).Outcome.Should().Be(BindFolderOutcomes.Forbidden);
        roots.Roots.Should().BeEmpty();
    }

    [SkippableFact]
    public void СсылкаНаЗапретныйКаталог_Отказ()
    {
        var (binder, roots) = Create();
        var ssh = Path.Combine(Home, ".ssh");
        MakeDir(ssh);
        var link = Path.Combine(_base, "innocent");
        try { Directory.CreateSymbolicLink(link, ssh); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Skip.If(true, "Нет прав создавать символические ссылки: " + e.Message);
        }

        binder.Bind(new BindFolderRequest(link, "p")).Outcome.Should().Be(BindFolderOutcomes.Forbidden);
        binder.Bind(new BindFolderRequest(Path.Combine(link, "new"), "p")).Outcome.Should().Be(BindFolderOutcomes.Forbidden);
        roots.Roots.Should().BeEmpty();
        Directory.Exists(Path.Combine(ssh, "new")).Should().BeFalse();
    }

    [Fact]
    public void АвтовыдачаВыключена_ПрежнийОтказ_НичегоНеМеняется()
    {
        var (binder, roots) = Create();
        roots.SetAuto(false);
        var missing = Path.Combine(Home, "projects", "app");
        var outside = Path.Combine(Home, "existing");
        MakeDir(outside);

        var m = binder.Bind(new BindFolderRequest(missing, "p"));
        var o = binder.Bind(new BindFolderRequest(outside, "p"));

        m.Should().Be(new BindFolderResult(BindFolderOutcomes.AutoOff, Exists: false, IsDirectory: false, InsideRoots: false));
        o.Should().Be(new BindFolderResult(BindFolderOutcomes.AutoOff, Exists: true, IsDirectory: true, InsideRoots: false));
        Directory.Exists(missing).Should().BeFalse();
        roots.Roots.Should().BeEmpty();

        roots.SetAuto(true);
        binder.Bind(new BindFolderRequest(outside, "p")).Outcome.Should().Be(BindFolderOutcomes.Bound);
    }

    [Fact]
    public void ФайлПоПути_Отказ()
    {
        var (binder, roots) = Create();
        var file = Path.Combine(Home, "file.txt");
        File.WriteAllText(file, "x");

        binder.Bind(new BindFolderRequest(file, "p")).Outcome.Should().Be(BindFolderOutcomes.NotDirectory);
        roots.Roots.Should().BeEmpty();
    }

    [Fact]
    public void ОтносительныйПуть_Отказ()
    {
        var (binder, roots) = Create();

        binder.Bind(new BindFolderRequest("projects/app", "p")).Outcome.Should().Be(BindFolderOutcomes.Refused);
        roots.Roots.Should().BeEmpty();
    }

    [Fact]
    public void Remove_СнимаетИПодпись()
    {
        var (binder, roots) = Create();
        var path = Path.Combine(Home, "app");
        binder.Bind(new BindFolderRequest(path, "p"));

        roots.Remove(path).Should().BeTrue();
        roots.LabelOf(path).Should().BeNull();
    }
}
