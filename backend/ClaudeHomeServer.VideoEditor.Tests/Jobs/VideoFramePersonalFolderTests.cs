using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.VideoEditor;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Jobs;

// ADR-040, Ф2-3: кадр A/B из файла у личной области — файл «Личного» (если папка есть); загруженный «С компьютера»
// кадр рабочей папки по-прежнему читается из неё и проходит проверку настроек без папки
public sealed class VideoFramePersonalFolderTests : IDisposable
{
    private const string Owner = "u1";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vfpf-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly VideoEditWorkspace _workspace;

    public VideoFramePersonalFolderTests()
    {
        _root = Path.Combine(_dir, "Personal");
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        File.WriteAllBytes(Path.Combine(_root, "images", "a.png"), Png);
        _workspace = new VideoEditWorkspace(Path.Combine(_dir, "work"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private VideoEditScope WithFolder() => new(VideoEditScope.Personal,
        new Project { Id = "personal-id", OwnerId = Owner, RootPath = _root, Kind = ProjectKind.Personal }) { TenantOwnerId = Owner };

    private static VideoEditScope NoFolder() => new(VideoEditScope.Personal, null) { TenantOwnerId = Owner };

    private static VideoSceneSettingsDto Settings(FrameRef frame) =>
        new(frame, null, "Камера", "fal", "veo", 5, "16:9", false, 1);

    // ── Чтение кадра ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Кадр_файл_Личного_читается_из_папки_Личного()
    {
        var read = await new VideoFrameReader(null, _workspace).ReadAsync(Owner, Owner, WithFolder(), FrameRef.File("images/a.png"), default);

        read.Error.Should().BeNull();
        read.Bytes!.Bytes.Should().Equal(Png);
        read.Bytes.ContentType.Should().Be("image/png");
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("images/нет-такого.png")]
    public async Task Кадр_вне_Личного_или_несуществующий_отказ(string path)
    {
        File.WriteAllBytes(Path.Combine(_dir, "secret.png"), Png);

        var read = await new VideoFrameReader(null, _workspace).ReadAsync(Owner, Owner, WithFolder(), FrameRef.File(path), default);

        read.Bytes.Should().BeNull();
        read.ErrorCode.Should().Be(VideoEditorErrors.FrameUnavailable);
    }

    [Fact]
    public async Task Кадр_через_символическую_ссылку_наружу_отказ()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "s.png"), Png);
        Directory.CreateSymbolicLink(Path.Combine(_root, "images", "out"), outside);

        var read = await new VideoFrameReader(null, _workspace).ReadAsync(Owner, Owner, WithFolder(), FrameRef.File("images/out/s.png"), default);

        read.Bytes.Should().BeNull();
    }

    [Fact]
    public async Task Загруженный_кадр_рабочей_папки_читается_из_неё_даже_когда_папка_Личного_есть()
    {
        var uploaded = _workspace.SaveFrame(Owner, Png, ".png", "my.png");

        var read = await new VideoFrameReader(null, _workspace).ReadAsync(Owner, Owner, WithFolder(), FrameRef.File(uploaded), default);

        read.Error.Should().BeNull();
        read.Bytes!.Bytes.Should().Equal(Png);
    }

    [Fact]
    public async Task Без_папки_кадр_файл_по_прежнему_не_найден()
    {
        var read = await new VideoFrameReader(null, _workspace).ReadAsync(Owner, Owner, NoFolder(), FrameRef.File("images/a.png"), default);

        read.Bytes.Should().BeNull();
        read.ErrorCode.Should().Be(VideoEditorErrors.FrameUnavailable);
    }

    // ── Проверка настроек сцены ──────────────────────────────────────────────────

    [Fact]
    public void Настройки_кадр_файл_Личного_проходит_если_файл_есть()
    {
        VideoSceneService.SettingsProblem(WithFolder(), Settings(FrameRef.File("images/a.png"))).Should().BeNull();
    }

    [Theory]
    [InlineData("images/нет-такого.png")]
    [InlineData("../secret.png")]
    public void Настройки_кадр_файл_Личного_отказ_если_файла_нет_или_путь_вне(string path)
    {
        var problem = VideoSceneService.SettingsProblem(WithFolder(), Settings(FrameRef.File(path)));

        problem.Should().NotBeNull();
    }

    [Fact]
    public void Настройки_загруженный_кадр_проходит_без_папки_и_с_папкой_а_обычный_файл_без_папки_отказ()
    {
        var uploaded = FrameRef.File(_workspace.SaveFrame(Owner, Png, ".png", "my.png"));

        VideoSceneService.SettingsProblem(NoFolder(), Settings(uploaded)).Should().BeNull();
        VideoSceneService.SettingsProblem(WithFolder(), Settings(uploaded)).Should().BeNull("кадр рабочей папки не ломается");
        VideoSceneService.SettingsProblem(NoFolder(), Settings(FrameRef.File("images/a.png"))).Should().NotBeNull();
    }
}
