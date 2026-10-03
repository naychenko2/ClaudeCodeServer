using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChatContext;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.ChatContext;

// Вид project-file: путь проверяется ProjectLinkGuard, в личном чате — отказ до RootPath
public sealed class ProjectFileContextKindTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ctx-pf-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "ctx-pf-out-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectFileContextKind _kind = new();

    public ProjectFileContextKindTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_root, "docs", "a.md"), "x");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "s");
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private ContextScope Scope(bool personal = false) =>
        new("owner-1", new Session(), personal ? null : new Project { RootPath = _root });

    private static JsonObject Ref(string path) => new() { ["path"] = path };

    private static ContextItem Item(string path) =>
        new("i1", ProjectFileContextKind.Kind, Ref(path), null, ContextActor.Human, DateTime.UtcNow);

    [Fact]
    public void Путь_внутри_проекта_годится() =>
        _kind.Validate(Scope(), "project-file", Ref("docs/a.md")).Should().BeNull();

    [Fact]
    public void Выход_через_точки_отклоняется() =>
        _kind.Validate(Scope(), "project-file", Ref("../" + Path.GetFileName(_outside) + "/secret.txt")).Should().Be("Путь вне проекта");

    [Fact]
    public void Абсолютный_путь_отклоняется() =>
        _kind.Validate(Scope(), "project-file", Ref("/etc/passwd")).Should().Be("Путь вне проекта");

    [Fact]
    public void Символическая_ссылка_наружу_отклоняется()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "link"), _outside);

        _kind.Validate(Scope(), "project-file", Ref("link/secret.txt")).Should().Be("Путь идёт через символическую ссылку");
    }

    [Fact]
    public void Личный_чат_отклоняется_до_проверки_пути() =>
        _kind.Validate(Scope(personal: true), "project-file", Ref("docs/a.md")).Should().NotBeNull();

    [Fact]
    public void Пустой_ref_отклоняется() =>
        _kind.Validate(Scope(), "project-file", new JsonObject()).Should().NotBeNull();

    [Fact]
    public void Describe_даёт_имя_файла_и_не_missing()
    {
        var s = _kind.Describe(Scope(), Item("docs/a.md"));

        s.Label.Should().Be("a.md");
        s.Missing.Should().BeFalse();
    }

    [Fact]
    public void Describe_пропавшего_файла_помечает_missing()
    {
        var s = _kind.Describe(Scope(), Item("docs/gone.md"));

        s.Label.Should().Be("gone.md");
        s.Missing.Should().BeTrue();
    }

    [Fact]
    public void Describe_в_личном_чате_не_падает_и_missing() =>
        _kind.Describe(Scope(personal: true), Item("docs/a.md")).Missing.Should().BeTrue();

    [Fact]
    public void Вид_не_бывает_основным()
    {
        _kind.AcceptedRefs(Scope(), Item("docs/a.md"), null).Should().BeEmpty();
        _kind.DescribeExecutor(Scope(), Item("docs/a.md")).Should().BeNull();
    }
}
