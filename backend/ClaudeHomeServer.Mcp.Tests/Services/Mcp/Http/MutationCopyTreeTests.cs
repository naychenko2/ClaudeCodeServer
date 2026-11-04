using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Mcp.Http;

/// <summary>
/// Параметр tree у run_tests/build: принимается только законная копия мутации — прямой потомок
/// корня копий, без ссылок в пути, с .meta, чей source — дерево этого чата.
/// </summary>
public class MutationCopyTreeTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "ccs-mut-tree-" + Guid.NewGuid().ToString("N"));
    private readonly string _copies;
    private readonly string _chatTree;

    public MutationCopyTreeTests()
    {
        // Каноническая форма: временный каталог может лежать за ссылкой (macOS /var → /private/var)
        Directory.CreateDirectory(_base);
        var real = Path.GetFullPath(_base);
        _copies = Path.Combine(real, "copies");
        _chatTree = Path.Combine(real, "chat-tree");
        Directory.CreateDirectory(_copies);
        Directory.CreateDirectory(_chatTree);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch { }
    }

    private string MakeCopy(string name, string? source, bool withGit = true)
    {
        var dir = Path.Combine(_copies, name);
        Directory.CreateDirectory(dir);
        if (withGit) File.WriteAllText(Path.Combine(dir, ".git"), "gitdir: x");
        if (source is not null)
        {
            Directory.CreateDirectory(Path.Combine(_copies, ".meta"));
            File.WriteAllText(Path.Combine(_copies, ".meta", name), $"source={source}\ncreated=1\nhead=abc\n");
        }
        return dir;
    }

    private bool Resolve(string? tree, out string root, out string error) =>
        MutationCopyTree.TryResolve(tree, _chatTree, _copies, out root, out error);

    [Fact]
    public void Пусто_КореньОстаётсяДеревомЧата()
    {
        Resolve(null, out var root, out _).Should().BeTrue();
        root.Should().Be(_chatTree);
        Resolve("  ", out root, out _).Should().BeTrue();
        root.Should().Be(_chatTree);
    }

    [Fact]
    public void ЗаконнаяКопия_ИзДереваЧата_Принимается()
    {
        var copy = MakeCopy("c1", _chatTree);

        Resolve(copy, out var root, out var error).Should().BeTrue(error);
        root.Should().Be(copy);
    }

    [Fact]
    public void Копия_ИзЧужогоДерева_Отказ()
    {
        var copy = MakeCopy("c1", Path.Combine(_base, "other-tree"));

        Resolve(copy, out _, out var error).Should().BeFalse();
        error.Should().Contain("другого дерева");
    }

    [Fact]
    public void БезМетаданных_Отказ()
    {
        var copy = MakeCopy("c1", null);

        Resolve(copy, out _, out var error).Should().BeFalse();
        error.Should().Contain(".meta");
    }

    [Fact]
    public void ПроизвольныйПуть_ВнеКорняКопий_Отказ()
    {
        Resolve(_chatTree, out _, out var error).Should().BeFalse();
        error.Should().Contain("копией мутации");
        Resolve("/etc", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void ОтносительныйПуть_Отказ()
    {
        MakeCopy("c1", _chatTree);

        Resolve("c1", out _, out var error).Should().BeFalse();
        error.Should().Contain("абсолютный");
    }

    [Fact]
    public void ВложенныйКаталогКопии_Отказ()
    {
        var copy = MakeCopy("c1", _chatTree);
        Directory.CreateDirectory(Path.Combine(copy, "backend"));

        Resolve(Path.Combine(copy, "backend"), out _, out _).Should().BeFalse("копия — только прямой потомок корня");
    }

    [Fact]
    public void ПутьЧерезТочкиТочки_ВыходитЗаКореньКопий_Отказ()
    {
        var copy = MakeCopy("c1", _chatTree);

        Resolve(Path.Combine(_copies, "c1", "..", "..", "chat-tree"), out _, out _).Should().BeFalse();
        Resolve(Path.Combine(_copies, "..", "chat-tree"), out _, out _).Should().BeFalse();
        copy.Should().NotBeNull();
    }

    [Fact]
    public void СимвольнаяСсылкаВместоКопии_Отказ()
    {
        // Ссылка внутри корня копий на чужой каталог с правдоподобными метаданными
        var target = Path.Combine(_base, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, ".git"), "gitdir: x");
        Directory.CreateDirectory(Path.Combine(_copies, ".meta"));
        File.WriteAllText(Path.Combine(_copies, ".meta", "link"), $"source={_chatTree}\n");
        var link = Path.Combine(_copies, "link");
        Directory.CreateSymbolicLink(link, target);

        Resolve(link, out _, out var error).Should().BeFalse();
        error.Should().Contain("ссылк");
    }

    [Fact]
    public void КопияБезGit_Отказ()
    {
        var copy = MakeCopy("c1", _chatTree, withGit: false);

        Resolve(copy, out _, out var error).Should().BeFalse();
        error.Should().Contain(".git");
    }

    [Fact]
    public void СлужебныйКаталогMeta_КопиейНеБывает()
    {
        MakeCopy("c1", _chatTree);

        Resolve(Path.Combine(_copies, ".meta"), out _, out _).Should().BeFalse();
    }
}
