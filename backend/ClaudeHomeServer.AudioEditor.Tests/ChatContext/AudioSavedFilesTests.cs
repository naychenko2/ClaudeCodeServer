using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor.ChatContext;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.ChatContext;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.AudioEditor.Tests.ChatContext;

// Сохранённые человеком файлы звуков чата (ADR-023 §3.3): след лежит в нити, а не в журнале событий
public sealed class AudioSavedFilesTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aud-saved-" + Guid.NewGuid().ToString("N"));
    private readonly AudioThreadStore _store;
    private readonly AudioSavedFiles _saved;

    public AudioSavedFilesTests()
    {
        _store = new AudioThreadStore(_root);
        _saved = new AudioSavedFiles(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static ContextScope Scope(string chat) => new(Owner, new Session { Id = chat, OwnerId = Owner }, null);

    private string Draft(string chat = Chat) => _store.Open(Owner, chat, null, "", null).Thread!.Id;

    [Fact]
    public void Сохранённый_из_нити_файл_попадает_в_список_с_видом_и_временем()
    {
        var thread = Draft();
        _store.MoveToFile(Owner, Chat, thread, "audio/intro.mp3");

        var files = _saved.List(Scope(Chat));

        files.Should().ContainSingle();
        files[0].Path.Should().Be("audio/intro.mp3");
        files[0].ThreadKind.Should().Be("audio");
        files[0].SavedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Повторное_сохранение_того_же_пути_не_дублирует_запись()
    {
        var thread = Draft();
        _store.MoveToFile(Owner, Chat, thread, "audio/intro.mp3");
        _store.MoveToFile(Owner, Chat, thread, "audio/intro.mp3");

        _saved.List(Scope(Chat)).Should().ContainSingle();
    }

    [Fact]
    public void Нить_без_сохранения_и_чужой_чат_в_список_не_попадают()
    {
        Draft();
        var other = Draft("chat-2");
        _store.MoveToFile(Owner, "chat-2", other, "audio/other.mp3");

        _saved.List(Scope(Chat)).Should().BeEmpty();
        _saved.List(Scope("chat-2")).Should().ContainSingle(f => f.Path == "audio/other.mp3");
    }
}
