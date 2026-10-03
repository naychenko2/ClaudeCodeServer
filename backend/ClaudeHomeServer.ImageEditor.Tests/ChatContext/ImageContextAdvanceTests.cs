using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.ImageEditor.Tests.ChatContext;

// Приёмка Д2: результат запуска становится основным объектом. Закреплённая на версии-основе нить
// после запуска переезжает на новую версию, и следующая правка идёт от неё (ADR-023, макет §1)
public sealed class ImageContextAdvanceTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Project = "p-1";
    private const string Chat = "chat-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "image-adv-" + Guid.NewGuid().ToString("N"));
    private readonly ImageThreadStore _threads;
    private readonly ChatContextStore _context;
    private readonly ImageThreadService _service;

    public ImageContextAdvanceTests()
    {
        _threads = new ImageThreadStore(Path.Combine(_root, "image"));
        _context = new ChatContextStore(Path.Combine(_root, "ctx"), new ContextKindRegistry([new ImageContextKind(_threads)]));
        var mirror = new ChatContextFocusMirror(_context, NullLogger<ChatContextFocusMirror>.Instance);
        _service = new ImageThreadService(_threads, NullLogger<ImageThreadService>.Instance, mirror: mirror);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Pinned(string versionId)
    {
        var id = _threads.Create(Owner, Chat, null, "", focus: true).Thread.Id;
        var item = ChatContextFocusMirror.NewItem(ImageContextKind.Kind, id, ContextActor.Agent, versionId);
        _context.SetPrimary(Owner, Chat, item, null);
        return id;
    }

    private async Task<ImageThreadVersion> RunAsync(string threadId, string jobId, string? baseVersion)
    {
        _threads.AddLaunch(Owner, Chat, threadId, new ImageThreadLaunch(jobId, baseVersion, null, _threads.Now(),
            ImageThreadLaunchStatus.Running, "human", "синий"));
        var written = _threads.FinishLaunch(Owner, Chat, threadId, jobId, ImageThreadLaunchStatus.Done, [(1, "s-" + jobId)]);
        await _service.PublishVersionsAsync(Owner, Project, Chat, threadId, written, "human");
        return written.NewVersions.Single();
    }

    private string? PinnedVersion() => _context.Get(Owner, Chat).Primary!.Ref["versionId"]?.GetValue<string>();

    [Fact]
    public async Task Правка_от_закреплённой_версии_переносит_основной_на_новую_и_цепочка_идёт_дальше()
    {
        var id = Pinned(ImageThreadVersion.OriginId);

        var v1 = await RunAsync(id, "job-1", ImageThreadVersion.OriginId);
        PinnedVersion().Should().Be(v1.Id);

        var v2 = await RunAsync(id, "job-2", PinnedVersion());
        PinnedVersion().Should().Be(v2.Id, "правка за правкой не застревает на первой версии");
        _context.Get(Owner, Chat).Primary!.By.Should().Be(ContextActor.Agent, "метка того, кто выбрал объект, сохраняется");
    }

    [Fact]
    public async Task Версию_закреплённую_человеком_иначе_чем_основа_запуска_не_трогаем()
    {
        var id = Pinned(ImageThreadVersion.OriginId);
        var v1 = await RunAsync(id, "job-1", ImageThreadVersion.OriginId);
        _context.SetPrimary(Owner, Chat, ChatContextFocusMirror.NewItem(ImageContextKind.Kind, id, ContextActor.Human, v1.Id), null);

        _threads.AddLaunch(Owner, Chat, id, new ImageThreadLaunch("job-2", ImageThreadVersion.OriginId, null, _threads.Now(),
            ImageThreadLaunchStatus.Running, "human", "x"));
        var written = _threads.FinishLaunch(Owner, Chat, id, "job-2", ImageThreadLaunchStatus.Done, [(1, "s2")]);
        await _service.PublishVersionsAsync(Owner, Project, Chat, id, written, "human");

        PinnedVersion().Should().Be(v1.Id);
    }
}
