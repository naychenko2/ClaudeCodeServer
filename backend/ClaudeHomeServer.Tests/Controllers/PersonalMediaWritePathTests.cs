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
/// ADR-040, Ф2-3: пишущие пути создают «Личное» лениво (WithFolderAsync), GET-путь — никогда; подпапки вида
/// держат запись внутри Personal/&lt;вид&gt;/ и не выпускают её за корень и по символической ссылке.
/// </summary>
public class PersonalMediaWritePathTests : IDisposable
{
    private const string Owner = "owner-1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pmw_" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IPersonalProjects> _store = new();
    private readonly Session _chat = new() { Id = "chat", OwnerId = Owner };

    public PersonalMediaWritePathTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* не критично */ }
    }

    private Project Personal() => new() { Id = "personal-id", OwnerId = Owner, RootPath = _dir, Kind = ProjectKind.Personal };

    private PersonalFolderResolver EnsureCreates()
    {
        _store.Setup(s => s.GetPersonalOf(Owner)).Returns((Project?)null);
        _store.Setup(s => s.EnsurePersonalAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(Personal());
        return new PersonalFolderResolver(_store.Object);
    }

    private (IProjectManager, ISessionDirectory, IProjectAccess) World()
    {
        var dir = new Mock<ISessionDirectory>();
        dir.Setup(d => d.GetById("chat")).Returns(_chat);
        return (Mock.Of<IProjectManager>(), dir.Object, Mock.Of<IProjectAccess>());
    }

    // ── Ленивое создание: только пишущий путь ──────────────────────────────────────

    [Fact]
    public async Task GET_гейт_папку_не_создаёт_а_WithFolderAsync_создаёт_и_ключ_остаётся_personal()
    {
        var (p, d, a) = World();
        var gate = new ImageEditScopeGate(p, d, a, EnsureCreates());

        gate.TryPersonalChat(Owner, "chat", out var scope, out _).Should().BeTrue();
        scope!.HasFolder.Should().BeFalse();
        _store.Verify(s => s.EnsurePersonalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        var withFolder = await gate.WithFolderAsync(scope);

        _store.Verify(s => s.EnsurePersonalAsync(Owner, It.IsAny<CancellationToken>()), Times.Once);
        withFolder.HasFolder.Should().BeTrue();
        withFolder.Key.Should().Be(ImageEditScope.Personal, "регрессия: ключ области не меняется от «Личного»");
        withFolder.IsPersonal.Should().BeTrue();
        withFolder.TenantOwnerId.Should().Be(Owner);
        ImageEditScope.ProjectIdOf(withFolder.Key).Should().BeNull();
        _chat.ProjectId.Should().BeNull();
    }

    [Fact]
    public async Task Audio_и_Video_WithFolderAsync_создают_Личное_и_держат_ключ()
    {
        var (p, d, a) = World();
        var audio = new AudioEditScopeGate(p, d, a, EnsureCreates());
        var video = new VideoEditScopeGate(p, d, a, EnsureCreates());
        audio.TryPersonalChat(Owner, "chat", out var audioScope, out _);
        video.TryPersonalChat(Owner, "chat", out var videoScope, out _);

        var audioWith = await audio.WithFolderAsync(audioScope!);
        var videoWith = await video.WithFolderAsync(videoScope!);

        audioWith.HasFolder.Should().BeTrue();
        videoWith.HasFolder.Should().BeTrue();
        audioWith.Key.Should().Be(AudioEditScope.Personal);
        videoWith.Key.Should().Be(VideoEditScope.Personal);
    }

    [Fact]
    public async Task WithFolderAsync_область_проекта_и_готовая_папка_стор_не_трогают()
    {
        var (p, d, a) = World();
        var gate = new ImageEditScopeGate(p, d, a, EnsureCreates());
        var project = new Project { Id = "p1", OwnerId = Owner, RootPath = _dir };
        var ready = new ImageEditScope(ImageEditScope.Personal, Personal()) { TenantOwnerId = Owner };

        (await gate.WithFolderAsync(ImageEditScope.Of(project))).Project.Should().BeSameAs(project);
        (await gate.WithFolderAsync(ready)).Should().BeSameAs(ready);

        _store.Verify(s => s.EnsurePersonalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WithFolderAsync_сбой_папки_оставляет_область_без_папки()
    {
        _store.Setup(s => s.GetPersonalOf(Owner)).Returns((Project?)null);
        _store.Setup(s => s.EnsurePersonalAsync(Owner, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("нет домашней папки"));
        var (p, d, a) = World();
        var gate = new ImageEditScopeGate(p, d, a, new PersonalFolderResolver(_store.Object));
        gate.TryPersonalChat(Owner, "chat", out var scope, out _);

        var result = await gate.WithFolderAsync(scope!);

        result.HasFolder.Should().BeFalse("вызывающий ответит «папка недоступна», а не упадёт");
    }

    // ── Подпапки вида ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "images")]
    [InlineData(null, "images")]
    [InlineData(".", "images")]
    [InlineData("images", "images")]
    [InlineData("images/cats", "images/cats")]
    [InlineData("\\images\\cats\\", "images/cats")]
    public void Resolve_допускает_подпапку_вида(string? requested, string expected)
    {
        PersonalMediaFolders.Resolve(PersonalMediaFolders.Images, requested, out var folder).Should().BeNull();
        folder.Should().Be(expected);
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("imagesx")]
    [InlineData("images/../audio")]
    [InlineData("../images")]
    [InlineData("images/./a")]
    [InlineData("/etc")]
    [InlineData("images/a:b")]
    public void Resolve_отвергает_чужую_папку_и_выход_за_корень(string requested)
    {
        PersonalMediaFolders.Resolve(PersonalMediaFolders.Images, requested, out _).Should().NotBeNull();
        PersonalMediaFolders.Allows(PersonalMediaFolders.Images, requested).Should().BeFalse();
    }

    [Fact]
    public void EnsureDirectory_создаёт_вложенную_папку_внутри_корня()
    {
        var full = PersonalMediaFolders.EnsureDirectory(_dir, "images/cats");

        full.Should().Be(Path.Combine(_dir, "images", "cats"));
        Directory.Exists(full).Should().BeTrue();
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/tmp/x")]
    public void EnsureDirectory_выход_из_корня_отказ_и_ничего_не_создаёт(string folder)
    {
        PersonalMediaFolders.EnsureDirectory(_dir, folder).Should().BeNull();
        Directory.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "outside")).Should().BeFalse();
    }

    [Fact]
    public void EnsureDirectory_символическая_ссылка_наружу_отказ()
    {
        var outside = Path.Combine(Path.GetTempPath(), "pmw_out_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            try { Directory.CreateSymbolicLink(Path.Combine(_dir, "video"), outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

            PersonalMediaFolders.EnsureDirectory(_dir, "video").Should().BeNull();
            PersonalMediaFolders.EnsureDirectory(_dir, "video/sub").Should().BeNull();
            Directory.GetFileSystemEntries(outside).Should().BeEmpty("через ссылку ничего не создано");
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* не критично */ }
        }
    }
}
