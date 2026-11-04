using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Jobs;

// F2-P2b: кадр-файл проекта проходит общий предикат private (ProjectLinkGuard.ResolveInsideAs →
// PrivateAttachments.IsForeignResolved). Чужое .cc-attachments/private/{X} — тот же отказ и тот же код,
// что у «вне проекта»: наличие чужого файла не раскрываем. Свой private и обычные файлы читаются как раньше
public sealed class VideoFrameReaderPrivateTests : IDisposable
{
    private const string Owner = "u1";
    private const string Other = "other-user";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vframe-priv-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly VideoEditScope _scope;

    public VideoFrameReaderPrivateTests()
    {
        _project = Path.Combine(_root, "project");
        var foreign = Path.Combine(_project, ".cc-attachments", "private", Other, "g1");
        var own = Path.Combine(_project, ".cc-attachments", "private", Owner, "g1");
        Directory.CreateDirectory(foreign);
        Directory.CreateDirectory(own);
        File.WriteAllBytes(Path.Combine(foreign, "secret.png"), [7, 7]);
        File.WriteAllBytes(Path.Combine(own, "mine.png"), [8, 8]);
        // closed-поддерево не под .cc-attachments корня — та же раскладка в глубине дерева
        var nested = Path.Combine(_project, "nested", ".cc-attachments", "private", Other);
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "secret.png"), [9, 9]);
        // ссылка на корень чужого проекта: под ней своё closed-поддерево (без права создавать ссылку
        // путь просто не существует — отказ остаётся тем же)
        var otherRoot = Path.Combine(_root, "other-project");
        var otherPrivate = Path.Combine(otherRoot, ".cc-attachments", "private", Other, "g1");
        Directory.CreateDirectory(otherPrivate);
        File.WriteAllBytes(Path.Combine(otherPrivate, "secret.png"), [6, 6]);
        try { Directory.CreateSymbolicLink(Path.Combine(_project, "other"), otherRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        File.WriteAllBytes(Path.Combine(_project, "hero.png"), [1, 1]);

        _scope = new VideoEditScope("proj1", new Project { Id = "proj1", RootPath = _project });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(".cc-attachments/private/other-user/g1/secret.png")]
    [InlineData(".cc-attachments/PRIVATE/other-user/g1/../g1/secret.png")]
    [InlineData("nested/.cc-attachments/private/other-user/secret.png")]
    [InlineData("other/.cc-attachments/private/other-user/g1/secret.png")]
    public async Task Кадр_из_чужого_private_отказ_без_байтов(string path)
    {
        var frame = await new VideoFrameReader().ReadAsync(Owner, _scope, FrameRef.File(path), default);

        frame.Bytes.Should().BeNull(path);
        frame.ErrorCode.Should().Be(VideoEditorErrors.FrameUnavailable, path);
        frame.Error.Should().Contain("не найден в проекте", "чужое private не раскрывает своё наличие: " + path);
    }

    [Theory]
    [InlineData(".cc-attachments/private/u1/g1/mine.png")]
    [InlineData("hero.png")]
    public async Task Свой_private_и_обычный_файл_проекта_читаются_как_раньше(string path)
    {
        var frame = await new VideoFrameReader().ReadAsync(Owner, _scope, FrameRef.File(path), default);

        frame.ErrorCode.Should().BeNull(path + " " + frame.Error);
        frame.Bytes!.Bytes.Should().NotBeEmpty(path);
    }
}
