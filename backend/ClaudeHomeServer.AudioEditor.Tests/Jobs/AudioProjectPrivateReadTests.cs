using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Controllers;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Jobs;

// F2-P2c: исходник нити и входы-пути проверяются общим предикатом private
// (ProjectLinkGuard.ResolveInsideAs → PrivateAttachments.IsForeignResolved), без своей проверки в вертикали
public sealed class AudioProjectPrivateReadTests : IDisposable
{
    private const string Owner = "owner-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "apr-" + Guid.NewGuid().ToString("N"));
    private readonly AudioEditScope _scope;
    private readonly AudioEditWorkspace _workspace;

    public AudioProjectPrivateReadTests()
    {
        var projectRoot = Path.Combine(_root, "project");
        var foreign = Path.Combine(projectRoot, ".cc-attachments", "private", "other-user", "g1");
        var own = Path.Combine(projectRoot, ".cc-attachments", "private", Owner, "g1");
        Directory.CreateDirectory(foreign);
        Directory.CreateDirectory(own);
        Directory.CreateDirectory(Path.Combine(projectRoot, "sfx"));
        File.WriteAllBytes(Path.Combine(foreign, "secret.wav"), "FOREIGN"u8.ToArray());
        File.WriteAllBytes(Path.Combine(own, "mine.wav"), "MINE"u8.ToArray());
        File.WriteAllBytes(Path.Combine(projectRoot, "sfx", "jingle.wav"), "JINGLE"u8.ToArray());
        // ссылка на корень чужого проекта (без права создавать ссылку путь просто не существует — отказ тот же)
        var otherPrivate = Path.Combine(_root, "other-project", ".cc-attachments", "private", "other-user", "g1");
        Directory.CreateDirectory(otherPrivate);
        File.WriteAllBytes(Path.Combine(otherPrivate, "secret.wav"), "OTHER"u8.ToArray());
        try { Directory.CreateSymbolicLink(Path.Combine(projectRoot, "other"), Path.Combine(_root, "other-project")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _scope = AudioEditScope.Of(new Project { Id = "p-1", OwnerId = Owner, RootPath = projectRoot });
        _workspace = new AudioEditWorkspace(Path.Combine(_root, "work"));
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string? ResolveOrigin(string file, string? actor = Owner) => AudioVersionFiles.Resolve(_workspace, Owner, actor, _scope,
        AudioThreadVersion.Origin(file, DateTime.UtcNow), new AudioVersionFile(AudioFileRoles.Main, file));

    // F2-P2e: субъект — вызывающий, арендатор (Owner) на чужесть не влияет
    [Fact]
    public void Приватный_чат_участника_своё_вложение_читается_а_чужие_private_отказаны()
    {
        var own = Path.Combine(_root, "project", ".cc-attachments", "private", "user-b", "g1");
        Directory.CreateDirectory(own);
        File.WriteAllBytes(Path.Combine(own, "mine.wav"), "B"u8.ToArray());

        ResolveOrigin(".cc-attachments/private/user-b/g1/mine.wav", "user-b").Should().NotBeNull();
        ResolveOrigin($".cc-attachments/private/{Owner}/g1/mine.wav", "user-b").Should().BeNull("private владельца");
        ResolveOrigin(".cc-attachments/private/other-user/g1/secret.wav", "user-b").Should().BeNull("private другого участника");
    }

    [Fact]
    public void Общий_чат_актор_неизвестен_private_любого_участника_отказан()
    {
        ResolveOrigin($".cc-attachments/private/{Owner}/g1/mine.wav", null).Should().BeNull();
        ResolveOrigin(".cc-attachments/private/other-user/g1/secret.wav", null).Should().BeNull();
        ResolveOrigin("sfx/jingle.wav", null).Should().NotBeNull("обычный файл проекта читается");
    }

    [Theory]
    [InlineData(".cc-attachments/private/other-user/g1/secret.wav")]
    [InlineData(".cc-attachments/PRIVATE/other-user/g1/../g1/secret.wav")]
    [InlineData("other/.cc-attachments/private/other-user/g1/secret.wav")]
    public void Исходник_нити_из_чужого_private_не_резолвится(string file) =>
        ResolveOrigin(file).Should().BeNull(file);

    [Fact]
    public void Исходник_нити_свой_private_и_обычный_файл_читаются()
    {
        ResolveOrigin($".cc-attachments/private/{Owner}/g1/mine.wav").Should().NotBeNull();
        ResolveOrigin("sfx/jingle.wav").Should().NotBeNull();
    }

    [Theory]
    [InlineData(".cc-attachments/private/other-user/g1/secret.wav")]
    [InlineData("other/.cc-attachments/private/other-user/g1/secret.wav")]
    public void Вход_путь_образца_в_чужом_private_отказ(string file)
    {
        var inputs = new JsonObject { [AudioOpInputs.ReferencePath] = file };
        AudioOpInputs.Validate(inputs, _scope).Should().Be("inputs.referencePath: путь вне проекта");
    }

    [Fact]
    public void Вход_путь_образца_свой_private_и_обычный_файл_годятся()
    {
        AudioOpInputs.Validate(new JsonObject { [AudioOpInputs.ReferencePath] = $".cc-attachments/private/{Owner}/g1/mine.wav" }, _scope)
            .Should().BeNull();
        AudioOpInputs.Validate(new JsonObject { [AudioOpInputs.ReferencePath] = "sfx/jingle.wav" }, _scope).Should().BeNull();
    }
}
