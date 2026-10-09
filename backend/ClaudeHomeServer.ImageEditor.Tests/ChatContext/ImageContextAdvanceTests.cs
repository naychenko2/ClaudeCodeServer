using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Chats;
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
        var written = _threads.FinishLaunch(Owner, Chat, threadId, jobId, ImageThreadLaunchStatus.Done, [(1, "s-" + jobId, null)]);
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
        var written = _threads.FinishLaunch(Owner, Chat, id, "job-2", ImageThreadLaunchStatus.Done, [(1, "s2", null)]);
        await _service.PublishVersionsAsync(Owner, Project, Chat, id, written, "human");

        PinnedVersion().Should().Be(v1.Id);
    }

    private ImageThread Thread(string id) => _threads.Get(Owner, Chat).Threads.Single(t => t.Id == id);

    private void Launch(string id, string job) =>
        _threads.AddLaunch(Owner, Chat, id, new ImageThreadLaunch(job, ImageThreadVersion.OriginId, null, _threads.Now(),
            ImageThreadLaunchStatus.Running, "human", "x"));

    private async Task FinishAsync(string id, string job, params (int Variant, double? Similarity)[] tail)
    {
        var written = _threads.FinishLaunch(Owner, Chat, id, job, ImageThreadLaunchStatus.Done,
            [.. tail.Select(t => (t.Variant, "s" + t.Variant + job, t.Similarity))]);
        await _service.PublishVersionsAsync(Owner, Project, Chat, id, written, "human");
    }

    private async Task ProgressAsync(string id, string job, int variant, double? similarity)
    {
        var written = _threads.AddLaunchVersions(Owner, Chat, id, job, [(variant, "s" + variant + job, similarity)]);
        await _service.PublishVersionsAsync(Owner, Project, Chat, id, written, "human");
    }

    [Fact]
    public async Task Без_выбора_человека_основной_объект_и_текущая_уходят_на_самую_похожую()
    {
        var id = Pinned(ImageThreadVersion.OriginId);
        Launch(id, "job-1");
        await ProgressAsync(id, "job-1", 1, 0.5);
        await ProgressAsync(id, "job-1", 2, 0.9);
        await FinishAsync(id, "job-1");

        var best = Thread(id).Versions.Single(v => v.MostSimilar);
        best.Variant.Should().Be(2);
        PinnedVersion().Should().Be(best.Id);
        Thread(id).CurrentVersionId.Should().Be(best.Id);
    }

    [Fact]
    public async Task Человек_закрепил_другую_версию_по_ходу_основной_и_текущая_не_двигаются()
    {
        var id = Pinned(ImageThreadVersion.OriginId);
        Launch(id, "job-1");
        await ProgressAsync(id, "job-1", 1, 0.5);
        await ProgressAsync(id, "job-1", 2, 0.6);
        var chosen = Thread(id).Versions.Single(v => v.Variant == 2).Id;
        _threads.SetCurrentVersion(Owner, Chat, id, chosen, null, null);
        _context.SetPrimary(Owner, Chat, ChatContextFocusMirror.NewItem(ImageContextKind.Kind, id, ContextActor.Human, chosen), null);
        await FinishAsync(id, "job-1", (3, 0.9));

        PinnedVersion().Should().Be(chosen);
        Thread(id).CurrentVersionId.Should().Be(chosen);
    }

    [Fact]
    public async Task Все_варианты_только_в_финале_основной_объект_на_самой_похожей()
    {
        var id = Pinned(ImageThreadVersion.OriginId);
        Launch(id, "job-1");
        await FinishAsync(id, "job-1", (1, 0.5), (2, 0.9), (3, 0.6));

        var best = Thread(id).Versions.Single(v => v.MostSimilar);
        best.Variant.Should().Be(2);
        PinnedVersion().Should().Be(best.Id);
    }

    [Fact]
    public async Task Самая_похожая_первая_версия_запуска_один_вызов_зеркала()
    {
        var id = Pinned(ImageThreadVersion.OriginId);
        Launch(id, "job-1");
        var written = _threads.FinishLaunch(Owner, Chat, id, "job-1", ImageThreadLaunchStatus.Done,
            [(1, "a", 0.9), (2, "b", 0.5)]);
        written.MovedToMostSimilar.Should().BeNull();
        await _service.PublishVersionsAsync(Owner, Project, Chat, id, written, "human");

        PinnedVersion().Should().Be(Thread(id).Versions.Single(v => v.MostSimilar).Id);
        Thread(id).Versions.Single(v => v.MostSimilar).Variant.Should().Be(1);
    }

    [Fact]
    public async Task Ручной_выбор_первой_версии_запуска_не_перебивается_самой_похожей()
    {
        var id = Pinned(ImageThreadVersion.OriginId);
        Launch(id, "job-1");
        await ProgressAsync(id, "job-1", 1, 0.5);
        var first = Thread(id).Versions.Single(v => v.Variant == 1).Id;
        // человек выбирает первую версию запуска сам: Ref без служебного маркера
        _context.SetPrimary(Owner, Chat, ChatContextFocusMirror.NewItem(ImageContextKind.Kind, id, ContextActor.Human, first), null);
        await ProgressAsync(id, "job-1", 2, 0.9);
        await FinishAsync(id, "job-1");

        PinnedVersion().Should().Be(first);
        Thread(id).CurrentVersionId.Should().Be(first, "сырые поля нити сходятся с выбором в контексте");
        Thread(id).Versions.Single(v => v.Variant == 2).MostSimilar.Should().BeTrue("пометка остаётся справкой");
    }

    [Fact]
    public async Task Ручной_выбор_основы_запуска_во_время_запуска_остаётся_на_основе()
    {
        var id = Pinned(ImageThreadVersion.OriginId);
        Launch(id, "job-1");
        await ProgressAsync(id, "job-1", 1, 0.5);
        _context.SetPrimary(Owner, Chat, ChatContextFocusMirror.NewItem(ImageContextKind.Kind, id, ContextActor.Human,
            ImageThreadVersion.OriginId), null);
        await ProgressAsync(id, "job-1", 2, 0.9);
        await FinishAsync(id, "job-1");

        PinnedVersion().Should().Be(ImageThreadVersion.OriginId);
    }

    [Fact]
    public async Task Запуск_из_одного_варианта_не_помечает_самую_похожую_и_текущую_не_двигает()
    {
        var id = Pinned(ImageThreadVersion.OriginId);
        Launch(id, "job-1");
        await FinishAsync(id, "job-1", (1, 0.8));

        var only = Thread(id).Versions.Single(v => v.Variant == 1);
        only.MostSimilar.Should().BeFalse();
        Thread(id).CurrentVersionId.Should().Be(only.Id);
        ImageEditorStateContributor.SimilarityText(Thread(id), only).Should().NotContain("самый похожий");
    }
}
