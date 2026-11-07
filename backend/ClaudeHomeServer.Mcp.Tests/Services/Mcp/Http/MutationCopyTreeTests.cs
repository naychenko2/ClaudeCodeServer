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

    // ---- tree = worktree проекта (git worktree list) ----

    private string MakeWorktree(string name)
    {
        var dir = Path.Combine(_base, "wt", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".git"), "gitdir: x");
        return dir;
    }

    private Task<(bool Ok, string Root, string Error)> ResolveAsync(string? tree, params string[] worktrees) =>
        MutationCopyTree.ResolveAsync(tree, _chatTree, _copies,
            _ => Task.FromResult<IReadOnlyList<string>>(worktrees), default);

    [Fact]
    public async Task WorktreeИзСпискаПроекта_Принимается_КореньИменноОн()
    {
        var mine = MakeWorktree("mine");
        var other = MakeWorktree("other");

        var r = await ResolveAsync(mine, _chatTree, mine, other);

        r.Ok.Should().BeTrue(r.Error);
        r.Root.Should().Be(mine);
    }

    [Fact]
    public async Task ПутьВнеСпискаИВнеКопий_Отказ_СОбеимиФормами()
    {
        var listed = MakeWorktree("listed");
        var stray = MakeWorktree("stray");

        var r = await ResolveAsync(stray, listed);

        r.Ok.Should().BeFalse();
        r.Root.Should().Be(_chatTree);
        r.Error.Should().Contain("git worktree list").And.Contain("копии мутации");
        (await ResolveAsync("/etc", listed)).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task БезСпискаWorktree_ПринимаетсяТолькоКопия()
    {
        var wt = MakeWorktree("w");
        var copy = MakeCopy("c1", _chatTree);

        (await MutationCopyTree.ResolveAsync(wt, _chatTree, _copies, null, default)).Ok.Should().BeFalse();
        var r = await MutationCopyTree.ResolveAsync(copy, _chatTree, _copies, null, default);
        r.Ok.Should().BeTrue(r.Error);
        r.Root.Should().Be(copy);
    }

    [Fact]
    public async Task ТочкиТочки_СворачиваютсяДоWorktree_ВыходНаружу_Отказ()
    {
        var wt = MakeWorktree("w");
        Directory.CreateDirectory(Path.Combine(wt, "backend"));

        var r = await ResolveAsync(Path.Combine(wt, "backend", ".."), wt);
        r.Ok.Should().BeTrue(r.Error);
        r.Root.Should().Be(wt);

        // «..» уводит из worktree в его родителя — родителя в списке нет
        (await ResolveAsync(Path.Combine(wt, ".."), wt)).Ok.Should().BeFalse();
        // Вложенный каталог worktree — не сам worktree
        (await ResolveAsync(Path.Combine(wt, "backend"), wt)).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task СсылкаНаWorktree_ТотЖеКанонический_СсылкаНаЧужое_Отказ()
    {
        var wt = MakeWorktree("w");
        var stray = MakeWorktree("stray");
        var toWt = Path.Combine(_base, "link-to-wt");
        var toStray = Path.Combine(_base, "link-to-stray");
        Directory.CreateSymbolicLink(toWt, wt);
        Directory.CreateSymbolicLink(toStray, stray);

        var r = await ResolveAsync(toWt, wt);
        r.Ok.Should().BeTrue(r.Error);
        r.Root.Should().Be(wt, "корень — каноническая форма: от неё ключуются блокировка и отпечатки");

        (await ResolveAsync(toStray, wt)).Ok.Should().BeFalse();
        // Ссылка в самом списке: запись списка тоже сверяется канонически
        (await ResolveAsync(wt, toWt)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task КаталогБезGit_ДажеИзСписка_Отказ()
    {
        var dir = Path.Combine(_base, "plain");
        Directory.CreateDirectory(dir);

        (await ResolveAsync(dir, dir)).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task ОтносительныйПуть_Отказ_СОбеимиФормами()
    {
        var r = await ResolveAsync("wt/w", MakeWorktree("w"));

        r.Ok.Should().BeFalse();
        r.Error.Should().Contain("абсолютный").And.Contain("git worktree list");
    }

    [Fact]
    public async Task ПопыткаКопии_ПодКорнемКопий_ОтказСТочнойПричинойКопии()
    {
        var copy = MakeCopy("c1", Path.Combine(_base, "other-tree"));

        var r = await ResolveAsync(copy);

        r.Ok.Should().BeFalse();
        r.Error.Should().Contain("другого дерева");
    }

    [Fact]
    public async Task СбойЧтенияСписка_FailClosed()
    {
        var wt = MakeWorktree("w");

        var r = await MutationCopyTree.ResolveAsync(wt, _chatTree, _copies,
            _ => throw new InvalidOperationException("git упал"), default);

        r.Ok.Should().BeFalse();
    }

    [Fact]
    public void ОписаниеПараметра_НеНавязываетСкриптКопийИНазываетWorktree()
    {
        MutationCopyTree.ParamDescription.Should().Contain("git worktree list")
            .And.Contain("если он есть в проекте");
    }
}
