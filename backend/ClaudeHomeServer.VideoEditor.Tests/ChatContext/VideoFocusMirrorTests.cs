using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.VideoEditor.ChatContext;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.ChatContext;

// Двойная запись фокуса «Видео» (КТ-5, 3б-1): составной фокус {sceneId, filmPath} распадается —
// основным становится выбранное последним; агент выбор человека не перезаписывает; сохранённые файлы
public sealed class VideoFocusMirrorTests : IDisposable
{
    private const string Owner = "u1";
    private const string Chat = "c1";
    private const string Film = "video/утро/утро.film";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vmirror-" + Guid.NewGuid().ToString("N"));
    private readonly VideoThreadStore _threads;
    private readonly ChatContextStore _context;
    private readonly VideoJobThreads _jobs;
    private readonly VideoSceneService _scenes;
    private readonly VideoEditScope _scope = new("proj1", new Project { Id = "proj1", RootPath = "/nope" });

    public VideoFocusMirrorTests()
    {
        _threads = new VideoThreadStore(Path.Combine(_root, "threads"));
        var registry = new ContextKindRegistry([new VideoContextKind(_threads)]);
        _context = new ChatContextStore(Path.Combine(_root, "ctx"), registry);
        var mirror = new ChatContextFocusMirror(_context, NullLogger<ChatContextFocusMirror>.Instance);
        _jobs = new VideoJobThreads(_threads, NullLogger<VideoJobThreads>.Instance, mirror: mirror);
        _scenes = new VideoSceneService(_jobs, new Prefs.VideoPrefsService(new Prefs.VideoPrefsStore(Path.Combine(_root, "prefs"))));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static VideoSceneSettingsDto Settings() => new(null, null, "т", null, null, null, null, null, null);

    private async Task<string> Add(ContextActor by = ContextActor.Human)
    {
        var call = await _scenes.AddAsync(Owner, _scope, Chat, "", Settings(), null, null, default, by);
        return call.Written!.Scene!.SceneId;
    }

    [Fact]
    public async Task Новая_сцена_становится_основным_объектом_контекста_от_человека()
    {
        var id = await Add();

        var primary = _context.Get(Owner, Chat).Primary!;
        primary.Kind.Should().Be("video-scene");
        primary.Ref["sceneId"]!.GetValue<string>().Should().Be(id);
        primary.By.Should().Be(ContextActor.Human);
    }

    [Fact]
    public async Task Агент_ставит_свою_сцену_с_пометкой_но_выбор_человека_той_же_сцены_не_перезаписывает()
    {
        var human = await Add();
        await _scenes.FocusAsync(Owner, _scope, Chat, new VideoFocusDto(human, null), null, ContextActor.Agent);
        _context.Get(Owner, Chat).Primary!.By.Should().Be(ContextActor.Human, "агент не перезаписывает выбор человека");

        var byAgent = await Add(ContextActor.Agent);
        var primary = _context.Get(Owner, Chat).Primary!;
        primary.Ref["sceneId"]!.GetValue<string>().Should().Be(byAgent);
        primary.By.Should().Be(ContextActor.Agent);
    }

    [Fact]
    public async Task Фильм_в_фокусе_заменяет_сцену_основным_а_сцена_выбранная_позже_возвращается()
    {
        var first = await Add();
        var scene = await Add();
        await _scenes.FocusAsync(Owner, _scope, Chat, new VideoFocusDto(first, null), null);

        await _scenes.FocusAsync(Owner, _scope, Chat, new VideoFocusDto(scene, Film), null);
        _context.Get(Owner, Chat).Primary!.Kind.Should().Be("video-scene", "сменились оба разом — выигрывает сцена");

        await _scenes.FocusAsync(Owner, _scope, Chat, new VideoFocusDto(scene, "video/утро/другой.film"), null);
        var film = _context.Get(Owner, Chat).Primary!;
        film.Kind.Should().Be("video-film", "менялся только фильм — он выбран последним");
        film.Ref["filmPath"]!.GetValue<string>().Should().Be("video/утро/другой.film");

        await _scenes.FocusAsync(Owner, _scope, Chat, new VideoFocusDto(null, "video/утро/другой.film"), null);
        _context.Get(Owner, Chat).Primary!.Kind.Should().Be("video-film");
    }

    [Fact]
    public async Task DTO_нитей_берёт_фокус_из_основного_объекта()
    {
        var scene = await Add();
        await _scenes.FocusAsync(Owner, _scope, Chat, new VideoFocusDto(scene, Film), null);
        await _scenes.FocusAsync(Owner, _scope, Chat, new VideoFocusDto(null, Film), null);

        _jobs.View(Owner, Chat).Focus.Should().Be(new VideoFocusDto(null, Film));
        _context.SetPrimary(Owner, Chat, null, null);
        _jobs.View(Owner, Chat).Focus.Should().Be(new VideoFocusDto(null, null), "основного нет — фокус распался");
    }

    [Fact]
    public async Task Удалённая_сцена_уходит_из_основного()
    {
        var id = await Add();
        _threads.Remove(Owner, Chat, id, _threads.Get(Owner, Chat).Revision);
        _jobs.Forget(Owner, Chat, id);

        _context.Get(Owner, Chat).Primary.Should().BeNull();
    }

    [Fact]
    public void Сохранённые_файлы_сцен_отдаёт_шов_со_временем_а_старые_записи_с_временем_сцены()
    {
        var scene = _threads.AddScene(Owner, Chat, "video/утро", Settings(), null).Scene!;
        var at = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        _threads.AddSavedFile(Owner, Chat, scene.SceneId, new VideoSavedFileDto("v1", "video/утро/scene-01.mp4", at));
        _threads.AddSavedFile(Owner, Chat, scene.SceneId, new VideoSavedFileDto("v1", "video/утро/old.mp4"));
        _threads.AddScene(Owner, "другой-чат", "video/утро", Settings(), null);

        var files = new VideoSavedFiles(_threads).List(new ContextScope(Owner, new Session { Id = Chat }, null));

        files.Should().HaveCount(2).And.OnlyContain(f => f.ThreadKind == "video");
        files.Single(f => f.Path.EndsWith("scene-01.mp4")).SavedAt.Should().Be(at);
        files.Single(f => f.Path.EndsWith("old.mp4")).SavedAt.Should().Be(scene.CreatedAt);
    }
}
