using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.ImageEditor.ChatContext;

// Сохранённые человеком файлы картинок чата (ADR-023 §3.3): след лежит в нити, а не в журнале событий
public sealed class ImageSavedFilesTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "img-saved-" + Guid.NewGuid().ToString("N"));
    private readonly ImageThreadStore _store;
    private readonly ImageSavedFiles _saved;

    public ImageSavedFilesTests()
    {
        _store = new ImageThreadStore(_root);
        _saved = new ImageSavedFiles(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static ContextScope Scope(string chat) => new(Owner, new Session { Id = chat, OwnerId = Owner }, null);

    private string Draft(string chat = Chat) =>
        _store.Open(Owner, chat, null, "", _store.Get(Owner, chat).Revision).Thread!.Id;

    [Fact]
    public void Сохранённый_из_нити_файл_попадает_в_список_с_видом_и_временем()
    {
        var thread = Draft();
        _store.MoveToFile(Owner, Chat, thread, "images/hero.png");

        var files = _saved.List(Scope(Chat));

        files.Should().ContainSingle();
        files[0].Path.Should().Be("images/hero.png");
        files[0].ThreadKind.Should().Be("image");
        files[0].SavedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Повторное_сохранение_того_же_пути_не_дублирует_запись()
    {
        var thread = Draft();
        _store.MoveToFile(Owner, Chat, thread, "images/hero.png");
        _store.MoveToFile(Owner, Chat, thread, "images/hero.png");

        _saved.List(Scope(Chat)).Should().ContainSingle();
    }

    [Fact]
    public void Новая_нить_без_сохранения_и_чужой_чат_в_список_не_попадают()
    {
        Draft();
        var other = Draft("chat-2");
        _store.MoveToFile(Owner, "chat-2", other, "images/other.png");

        _saved.List(Scope(Chat)).Should().BeEmpty();
        _saved.List(Scope("chat-2")).Should().ContainSingle(f => f.Path == "images/other.png");
    }

    [Fact]
    public void Файл_переименовали_через_файловый_API_след_идёт_за_новым_путём()
    {
        var thread = Draft();
        _store.MoveToFile(Owner, Chat, thread, "images/hero.png");

        _store.RewritePaths(Owner, Chat, p => p.Replace("hero", "main")).Should().BeTrue();

        _saved.List(Scope(Chat)).Single().Path.Should().Be("images/main.png");
    }
}
