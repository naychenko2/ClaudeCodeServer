using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.AudioEditor.Controllers;
using ClaudeHomeServer.Services.ImageEditor.Controllers;
using ClaudeHomeServer.Services.VideoEditor.Controllers;
using FluentAssertions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// S0-5: шлюзы областей редакторов отдают наружу арендатора (владельца), а не id вызывающего —
/// нити и префы ключуются {owner}. Чужой получает тот же 404, что и несуществующий.
/// </summary>
public class EditorScopeGateTenantTests
{
    private const string Owner = "owner-1";
    private const string Stranger = "stranger-9";

    private static (IProjectManager Projects, ISessionDirectory Dir) World()
    {
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById("p1")).Returns(new Project { Id = "p1", OwnerId = Owner, RootPath = "/p1" });
        var dir = new Mock<ISessionDirectory>();
        dir.Setup(d => d.GetById("chat")).Returns(new Session { Id = "chat", OwnerId = Owner });
        return (projects.Object, dir.Object);
    }

    [Fact]
    public void Image_проект_и_личный_чат_отдают_арендатора_чужой_отказ()
    {
        var (p, d) = World();
        var gate = new ImageEditScopeGate(p, d);
        gate.TryProject(Owner, "p1", out var project, out _).Should().BeTrue();
        gate.TryPersonalChat(Owner, "chat", out var chat, out _).Should().BeTrue();
        ImageEditScopeOwner(gate, project).Should().Be(Owner);
        chat!.TenantOwnerId.Should().Be(Owner);
        gate.TryProject(Stranger, "p1", out _, out var denied).Should().BeFalse();
        denied.Should().NotBeNull();
        gate.TryPersonalChat(Stranger, "chat", out _, out _).Should().BeFalse();
    }

    // Проект отдаётся Project-ом: арендатор — его OwnerId, область — через ту же запись
    private static string? ImageEditScopeOwner(ImageEditScopeGate _, Project? project) =>
        ClaudeHomeServer.Services.ImageEditor.ImageEditScope.Of(project!).TenantOwnerId;

    [Fact]
    public void Audio_область_отдаёт_арендатора()
    {
        var (p, d) = World();
        var gate = new AudioEditScopeGate(p, d);
        gate.TryProject(Owner, "p1", out var project, out _).Should().BeTrue();
        project!.TenantOwnerId.Should().Be(Owner);
        gate.TryPersonalChat(Owner, "chat", out var chat, out _).Should().BeTrue();
        chat!.TenantOwnerId.Should().Be(Owner);
        gate.TryProject(Stranger, "p1", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Video_область_отдаёт_арендатора()
    {
        var (p, d) = World();
        var gate = new VideoEditScopeGate(p, d);
        gate.TryProject(Owner, "p1", out var project, out _).Should().BeTrue();
        project!.TenantOwnerId.Should().Be(Owner);
        gate.TryPersonalChat(Owner, "chat", out var chat, out _).Should().BeTrue();
        chat!.TenantOwnerId.Should().Be(Owner);
        gate.TryProject(Stranger, "p1", out _, out _).Should().BeFalse();
    }
}
