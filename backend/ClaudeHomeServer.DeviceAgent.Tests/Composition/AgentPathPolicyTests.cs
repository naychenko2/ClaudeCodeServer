using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;

namespace ClaudeHomeServer.DeviceAgent.Tests.Composition;

/// <summary>
/// Сторож G7 (ADR-016 §5): агент не выходит за корни. Симлинк наружу, «..», абсолютный путь,
/// корень проекта вне разрешённых корней и файл больше потолка — отказ на агенте, причём
/// через шов IProjectFiles, то есть ровно там, где его зовёт localhost-API.
/// </summary>
public sealed class AgentPathPolicyTests : IDisposable
{
    private readonly AgentSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private Project ProjectAt(string root) => new() { Id = "p1", RootPath = root };

    private AgentProjectFiles Files(AgentLimits? limits = null) => new(new FileService(), _box.Policy(limits));

    [Fact]
    public async Task ФайлВнутриПроекта_Читается()
    {
        (await Files().ReadFileAsync(ProjectAt(_box.Project), "a.txt")).Should().Be("привет");
    }

    [SkippableFact]
    public async Task СимлинкНаФайлСнаружи_Отказ()
    {
        _box.Link(Path.Combine(_box.Project, "leak.txt"), Path.Combine(_box.Outside, "secret.txt"), directory: false);

        var act = () => Files().ReadFileAsync(ProjectAt(_box.Project), "leak.txt");

        await act.Should().ThrowAsync<AgentPathRefusedException>();
    }

    [SkippableFact]
    public async Task СимлинкНаКаталогСнаружи_ОтказИНаЧтениеИНаПоток()
    {
        _box.Link(Path.Combine(_box.Project, "ext"), Path.Combine(_box.Outside, "dir"), directory: true);
        var files = Files();

        await files.Invoking(f => f.ReadFileAsync(ProjectAt(_box.Project), "ext/inner.txt"))
            .Should().ThrowAsync<AgentPathRefusedException>();
        await files.Invoking(f => f.OpenReadAsync(ProjectAt(_box.Project), "ext/inner.txt"))
            .Should().ThrowAsync<AgentPathRefusedException>();
        await files.Invoking(f => f.WriteFileAsync(ProjectAt(_box.Project), "ext/new.txt", "x"))
            .Should().ThrowAsync<AgentPathRefusedException>();
        File.Exists(Path.Combine(_box.Outside, "dir", "new.txt")).Should().BeFalse();
    }

    [SkippableFact]
    public async Task СимлинкНаружу_ВыпадаетИзДереваИПоиска()
    {
        _box.Link(Path.Combine(_box.Project, "ext"), Path.Combine(_box.Outside, "dir"), directory: true);
        _box.Link(Path.Combine(_box.Project, "leak.txt"), Path.Combine(_box.Outside, "secret.txt"), directory: false);
        var files = Files();

        var tree = await files.TreeAsync(ProjectAt(_box.Project));
        var found = await files.SearchAsync(ProjectAt(_box.Project), "inner");
        var list = await files.ListAsync(ProjectAt(_box.Project));

        tree.Select(e => e.Path).Should().Contain("a.txt").And.NotContain(["ext", "ext/inner.txt", "leak.txt"]);
        found.Should().BeEmpty();
        list.Select(e => e.Path).Should().NotContain(["ext", "leak.txt"]);
    }

    [SkippableFact]
    public async Task СимлинкВнутриПроекта_Работает()
    {
        Directory.CreateDirectory(Path.Combine(_box.Project, "real"));
        File.WriteAllText(Path.Combine(_box.Project, "real", "b.txt"), "свой");
        _box.Link(Path.Combine(_box.Project, "alias"), Path.Combine(_box.Project, "real"), directory: true);

        (await Files().ReadFileAsync(ProjectAt(_box.Project), "alias/b.txt")).Should().Be("свой");
    }

    [Theory]
    [InlineData("../outside/secret.txt")]
    [InlineData("../../outside/secret.txt")]
    [InlineData("sub/../../x.txt")]
    public async Task ДвеТочки_Отказ(string path)
    {
        await Files().Invoking(f => f.ReadFileAsync(ProjectAt(_box.Project), path))
            .Should().ThrowAsync<AgentPathRefusedException>();
    }

    [Fact]
    public async Task АбсолютныйПуть_Отказ_АНеПриклейкаККорню()
    {
        var absolute = Path.Combine(_box.Outside, "secret.txt");
        // Лексическая SafePath.Join на Linux приклеила бы «/…/secret.txt» к корню проекта
        Directory.CreateDirectory(Path.Combine(_box.Project, absolute.TrimStart('/', '\\').Split(Path.DirectorySeparatorChar)[0]));

        await Files().Invoking(f => f.ReadFileAsync(ProjectAt(_box.Project), absolute))
            .Should().ThrowAsync<AgentPathRefusedException>();
        await Files().Invoking(f => f.ReadFileAsync(ProjectAt(_box.Project), "/etc/passwd"))
            .Should().ThrowAsync<AgentPathRefusedException>();
        await Files().Invoking(f => f.ReadFileAsync(ProjectAt(_box.Project), "C:\\Windows\\win.ini"))
            .Should().ThrowAsync<AgentPathRefusedException>();
    }

    [Fact]
    public async Task КореньПроектаВнеРазрешённыхКорней_Отказ()
    {
        await Files().Invoking(f => f.ListAsync(ProjectAt(_box.Outside)))
            .Should().ThrowAsync<AgentPathRefusedException>();
    }

    [SkippableFact]
    public async Task КореньПроектаСсылкойНаружу_Отказ()
    {
        var alias = Path.Combine(_box.AllowedRoot, "alias");
        _box.Link(alias, _box.Outside, directory: true);

        await Files().Invoking(f => f.ListAsync(ProjectAt(alias)))
            .Should().ThrowAsync<AgentPathRefusedException>();
    }

    [Fact]
    public async Task СоседнийКаталогСОбщимПрефиксом_НеСчитаетсяВнутри()
    {
        var sibling = _box.AllowedRoot + "2";
        Directory.CreateDirectory(sibling);

        await Files().Invoking(f => f.ListAsync(ProjectAt(sibling)))
            .Should().ThrowAsync<AgentPathRefusedException>();
    }

    [Fact]
    public async Task ФайлБольшеПотолка_ОтказНаЧтениеИЗапись_ПотокПоСвоемуПотолку()
    {
        File.WriteAllBytes(Path.Combine(_box.Project, "big.bin"), new byte[2048]);
        var files = Files(new AgentLimits { MaxReadBytes = 1024, MaxWriteBytes = 1024, MaxStreamBytes = 4096 });

        await files.Invoking(f => f.ReadFileBytesAsync(ProjectAt(_box.Project), "big.bin"))
            .Should().ThrowAsync<AgentFileTooLargeException>();
        await files.Invoking(f => f.GetContentAsync(ProjectAt(_box.Project), "big.bin"))
            .Should().ThrowAsync<AgentFileTooLargeException>();
        await files.Invoking(f => f.WriteFileAsync(ProjectAt(_box.Project), "c.txt", new string('x', 2000)))
            .Should().ThrowAsync<AgentFileTooLargeException>();

        await using var stream = await files.OpenReadAsync(ProjectAt(_box.Project), "big.bin");
        stream.Length.Should().Be(2048);

        var tight = Files(new AgentLimits { MaxStreamBytes = 1024 });
        await tight.Invoking(f => f.OpenReadAsync(ProjectAt(_box.Project), "big.bin"))
            .Should().ThrowAsync<AgentFileTooLargeException>();
    }

    [Fact]
    public async Task ПереименованиеНаружу_Отказ()
    {
        await Files().Invoking(f => f.RenameAsync(ProjectAt(_box.Project), "a.txt", "../../outside/stolen.txt"))
            .Should().ThrowAsync<AgentPathRefusedException>();
        File.Exists(Path.Combine(_box.Project, "a.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task ШовРаботаетКакFileService_ИOnMutatedДоходит()
    {
        IProjectFiles files = Files();
        var events = new List<(string Rel, FileMutationKind Kind)>();
        files.OnMutated += (_, rel, kind, _) => events.Add((rel, kind));

        await files.CreateFileAsync(ProjectAt(_box.Project), "sub/n.txt", "новый");
        var content = await files.GetContentAsync(ProjectAt(_box.Project), "sub/n.txt");

        content.Content.Should().Be("новый");
        content.IsBinary.Should().BeFalse();
        events.Should().Contain(("sub/n.txt", FileMutationKind.Create));
    }
}
