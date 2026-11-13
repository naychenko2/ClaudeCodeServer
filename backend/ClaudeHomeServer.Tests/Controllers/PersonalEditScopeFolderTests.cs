using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Controllers;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Controllers;
using ClaudeHomeServer.Services.VideoEditor;
using ClaudeHomeServer.Services.VideoEditor.Controllers;
using FluentAssertions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// ADR-040, Ф2-2: область личного чата знает про папку «Личного» (HasFolder), но ключ остаётся
/// "personal", Session.ProjectId — null, а чтение области папку не создаёт.
/// </summary>
public class PersonalEditScopeFolderTests : IDisposable
{
    private const string Owner = "owner-1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pef_" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IPersonalProjects> _store = new();
    private readonly Session _chat = new() { Id = "chat", OwnerId = Owner };

    public PersonalEditScopeFolderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* не критично */ }
    }

    private Project Personal(string? root = null) =>
        new() { Id = "personal-id", OwnerId = Owner, RootPath = root ?? _dir, Kind = ProjectKind.Personal };

    private PersonalFolderResolver Resolver(bool record, bool folderOnDisk = true)
    {
        _store.Setup(s => s.GetPersonalOf(Owner))
            .Returns(record ? Personal(folderOnDisk ? _dir : Path.Combine(_dir, "нет-такой")) : null);
        return new PersonalFolderResolver(_store.Object);
    }

    private (IProjectManager, ISessionDirectory, IProjectAccess) World()
    {
        var dir = new Mock<ISessionDirectory>();
        dir.Setup(d => d.GetById("chat")).Returns(_chat);
        return (Mock.Of<IProjectManager>(), dir.Object, Mock.Of<IProjectAccess>());
    }

    // ── PersonalFolderResolver ────────────────────────────────────────────────────

    [Fact]
    public void Find_запись_и_папка_есть_отдаёт_проект()
    {
        var found = Resolver(record: true).Find(Owner);

        found.Should().NotBeNull();
        found!.Kind.Should().Be(ProjectKind.Personal);
    }

    [Fact]
    public void Find_нет_записи_или_папки_или_владельца_отдаёт_null()
    {
        Resolver(record: false).Find(Owner).Should().BeNull();
        Resolver(record: true, folderOnDisk: false).Find(Owner).Should().BeNull("запись без папки — области без диска");
        Resolver(record: true).Find(null).Should().BeNull();
        Resolver(record: true).Find("").Should().BeNull();
    }

    [Fact]
    public void Find_папку_не_создаёт_GET_путь()
    {
        var resolver = Resolver(record: false);

        resolver.Find(Owner);

        _store.Verify(s => s.EnsurePersonalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureAsync_создаёт_через_стор_а_сбой_папки_отдаёт_null()
    {
        _store.Setup(s => s.EnsurePersonalAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(Personal());
        _store.Setup(s => s.EnsurePersonalAsync("no-home", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("нет домашней папки"));
        var resolver = new PersonalFolderResolver(_store.Object);

        (await resolver.EnsureAsync(Owner)).Should().NotBeNull();
        (await resolver.EnsureAsync("no-home")).Should().BeNull();
        (await resolver.EnsureAsync(null)).Should().BeNull();
    }

    // ── Гейты: личный чат с папкой / без ───────────────────────────────────────────

    [Fact]
    public void Image_личный_чат_с_папкой_ключ_остаётся_personal()
    {
        var (p, d, a) = World();
        new ImageEditScopeGate(p, d, a, Resolver(record: true)).TryPersonalChat(Owner, "chat", out var scope, out _).Should().BeTrue();

        scope!.HasFolder.Should().BeTrue();
        scope.Project!.Kind.Should().Be(ProjectKind.Personal);
        scope.Key.Should().Be(ImageEditScope.Personal);
        scope.IsPersonal.Should().BeTrue();
        scope.TenantOwnerId.Should().Be(Owner);
        _chat.ProjectId.Should().BeNull("Session.ProjectId у личного чата остаётся null");
    }

    [Fact]
    public void Audio_личный_чат_с_папкой_ключ_остаётся_personal()
    {
        var (p, d, a) = World();
        new AudioEditScopeGate(p, d, a, Resolver(record: true)).TryPersonalChat(Owner, "chat", out var scope, out _).Should().BeTrue();

        scope!.HasFolder.Should().BeTrue();
        scope.Key.Should().Be(AudioEditScope.Personal);
        scope.IsPersonal.Should().BeTrue();
        scope.TenantOwnerId.Should().Be(Owner);
    }

    [Fact]
    public void Video_личный_чат_с_папкой_ключ_остаётся_personal()
    {
        var (p, d, a) = World();
        new VideoEditScopeGate(p, d, a, Resolver(record: true)).TryPersonalChat(Owner, "chat", out var scope, out _).Should().BeTrue();

        scope!.HasFolder.Should().BeTrue();
        scope.Key.Should().Be(VideoEditScope.Personal);
        scope.IsPersonal.Should().BeTrue();
        scope.TenantOwnerId.Should().Be(Owner);
    }

    [Theory]
    [InlineData("нет резолвера")]
    [InlineData("нет записи")]
    [InlineData("записи без папки")]
    public void Личный_чат_без_папки_HasFolder_false_и_ничего_не_создаётся(string case_)
    {
        var (p, d, a) = World();
        PersonalFolderResolver? resolver = case_ switch
        {
            "нет резолвера" => null,
            "нет записи" => Resolver(record: false),
            _ => Resolver(record: true, folderOnDisk: false),
        };

        new ImageEditScopeGate(p, d, a, resolver).TryPersonalChat(Owner, "chat", out var image, out _).Should().BeTrue();
        new AudioEditScopeGate(p, d, a, resolver).TryPersonalChat(Owner, "chat", out var audio, out _).Should().BeTrue();
        new VideoEditScopeGate(p, d, a, resolver).TryPersonalChat(Owner, "chat", out var video, out _).Should().BeTrue();

        image!.HasFolder.Should().BeFalse();
        audio!.HasFolder.Should().BeFalse();
        video!.HasFolder.Should().BeFalse();
        image.Key.Should().Be(ImageEditScope.Personal);
        _store.Verify(s => s.EnsurePersonalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "GET-путь гейта папку не создаёт");
    }

    [Fact]
    public void Чужой_чат_папку_не_получает_отказ_прежний()
    {
        var (p, d, a) = World();
        var gate = new ImageEditScopeGate(p, d, a, Resolver(record: true));

        gate.TryPersonalChat("stranger-9", "chat", out var scope, out var denied).Should().BeFalse();

        scope.Should().BeNull();
        denied.Should().NotBeNull();
    }

    [Fact]
    public void Область_проекта_HasFolder_а_личный_ключ_нет()
    {
        var project = new Project { Id = "p1", OwnerId = Owner, RootPath = _dir };

        ImageEditScope.Of(project).HasFolder.Should().BeTrue();
        AudioEditScope.Of(project).IsPersonal.Should().BeFalse();
        VideoEditScope.Of(project).HasFolder.Should().BeTrue();
        new VideoEditScope(VideoEditScope.Personal, null).HasFolder.Should().BeFalse();
    }

    // ── Контракт ProjectIdOf: трата и рассылка личной области — без проекта ────────

    [Fact]
    public void ProjectIdOf_личный_ключ_null_даже_когда_у_области_есть_Личное()
    {
        var scope = new ImageEditScope(ImageEditScope.Personal, Personal()) { TenantOwnerId = Owner };

        ImageEditScope.ProjectIdOf(scope.Key).Should().BeNull();
        AudioEditScope.ProjectIdOf(AudioEditScope.Personal).Should().BeNull();
        VideoEditScope.ProjectIdOf(VideoEditScope.Personal).Should().BeNull();
        ImageEditScope.ProjectIdOf("p1").Should().Be("p1");
        AudioEditScope.ProjectIdOf("p1").Should().Be("p1");
        VideoEditScope.ProjectIdOf("p1").Should().Be("p1");
    }
}
