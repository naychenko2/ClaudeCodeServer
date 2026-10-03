using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.VideoEditor.ChatContext;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.ChatContext;

// Виды «video-scene» и «video-film» контекста чата (ADR-023, КТ-5): Validate, Describe, роли кадров, засев
public sealed class VideoContextKindTests : IDisposable
{
    private const string Owner = "u1";
    private const string Chat = "c1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vctx-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly VideoThreadStore _store;
    private readonly VideoContextKind _kind;
    private readonly Session _session = new() { Id = Chat, OwnerId = Owner, CreatedAt = DateTime.UtcNow };

    public VideoContextKindTests()
    {
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(_project, "video", "утро"));
        _store = new VideoThreadStore(Path.Combine(_root, "threads"));
        _kind = new VideoContextKind(_store);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ContextScope Scope => new(Owner, _session, new Project { Id = "p1", RootPath = _project });
    private ContextScope Personal => new(Owner, _session, null);

    private static VideoSceneSettingsDto Settings() => new(null, null, "т", null, null, null, null, null, null);

    private string AddScene() => _store.AddScene(Owner, Chat, "video/утро", Settings(), null).Scene!.SceneId;

    private static JsonObject SceneRef(string id) => new() { ["sceneId"] = id };
    private static JsonObject FilmRef(string path) => new() { ["filmPath"] = path };

    private static ContextItem Item(string kind, JsonObject reference) =>
        new("i", kind, reference, null, ContextActor.Human, DateTime.UtcNow);

    [Fact]
    public void Оба_вида_основные_и_ничего_лишнего_не_объявляют()
    {
        _kind.Kinds.Should().BeEquivalentTo(["video-scene", "video-film"]);
        _kind.CanBePrimary("video-scene").Should().BeTrue();
        _kind.CanBePrimary("video-film").Should().BeTrue();
        _kind.CanBePrimary("image").Should().BeFalse();
    }

    [Fact]
    public void Validate_сцена_своего_чата_да_чужая_и_пустая_нет()
    {
        var id = AddScene();
        _store.AddScene(Owner, "другой-чат", "video/утро", Settings(), null);

        _kind.Validate(Scope, "video-scene", SceneRef(id)).Should().BeNull();
        _kind.Validate(Scope, "video-scene", SceneRef("нет-такой")).Should().NotBeNull();
        _kind.Validate(Scope, "video-scene", new JsonObject()).Should().NotBeNull();
        _kind.Validate(Scope with { OwnerId = "чужой" }, "video-scene", SceneRef(id)).Should().NotBeNull("нити хранятся по владельцу");
    }

    [Fact]
    public void Validate_фильм_только_существующий_film_внутри_video_и_не_в_личном_чате()
    {
        File.WriteAllText(Path.Combine(_project, "video", "утро", "утро.film"), "{}");
        File.WriteAllText(Path.Combine(_project, "readme.film"), "{}");

        _kind.Validate(Scope, "video-film", FilmRef("video/утро/утро.film")).Should().BeNull();
        _kind.Validate(Scope, "video-film", FilmRef("video/утро/нет.film")).Should().NotBeNull();
        _kind.Validate(Scope, "video-film", FilmRef("readme.film")).Should().NotBeNull("вне video/");
        _kind.Validate(Scope, "video-film", FilmRef("video/../readme.film")).Should().NotBeNull();
        _kind.Validate(Scope, "video-film", FilmRef("video/утро/утро.mp4")).Should().NotBeNull("не .film");
        _kind.Validate(Personal, "video-film", FilmRef("video/утро/утро.film")).Should().Contain("личном");
    }

    [Fact]
    public void Describe_сцена_называет_фильм_и_позицию_а_без_фильма_имя_сцены()
    {
        var id = AddScene();
        var item = Item("video-scene", SceneRef(id));
        _kind.Describe(Scope, item).Label.Should().Be("Сцена 1 · черновик");

        _store.SetFilmRef(Owner, Chat, id, new VideoFilmRefDto("video/утро-в-горах/утро-в-горах.film", 2));

        var summary = _kind.Describe(Scope, item);
        summary.Label.Should().Be("сцена 3 · утро-в-горах");
        summary.Missing.Should().BeFalse();
        _kind.Describe(Scope, Item("video-scene", SceneRef("нет"))).Missing.Should().BeTrue();
    }

    [Fact]
    public void Describe_фильм_по_имени_файла_и_серый_если_файла_нет()
    {
        File.WriteAllText(Path.Combine(_project, "video", "утро", "утро.film"), "{}");

        var ok = _kind.Describe(Scope, Item("video-film", FilmRef("video/утро/утро.film")));
        ok.Label.Should().Be("утро");
        ok.Missing.Should().BeFalse();
        _kind.Describe(Scope, Item("video-film", FilmRef("video/утро/нет.film"))).Missing.Should().BeTrue();
    }

    [Fact]
    public void Сцена_принимает_кадры_A_и_B_видов_image_и_project_file_фильм_ничего()
    {
        var scene = Item("video-scene", SceneRef(AddScene()));

        var roles = _kind.AcceptedRefs(Scope, scene, null);

        roles.Select(r => r.Role).Should().Equal("frame-a", "frame-b");
        roles.Should().OnlyContain(r => r.Kinds.SequenceEqual(new[] { "image", "project-file" }) && r.Ops.SequenceEqual(new[] { "shoot" }));
        _kind.AcceptedRefs(Scope, scene, "build").Should().BeEmpty("сборка фильма кадров не берёт");
        _kind.AcceptedRefs(Scope, Item("video-film", FilmRef("video/a.film")), null).Should().BeEmpty();
    }

    [Fact]
    public void Засев_берёт_сцену_в_фокусе_иначе_открытый_фильм_иначе_ничего()
    {
        _kind.SeedPrimary(Scope).Should().BeNull();
        _store.SetFocus(Owner, Chat, new VideoFocusDto(null, "video/утро/утро.film"), null);
        _kind.SeedPrimary(Scope)!.Kind.Should().Be("video-film");
        _kind.SeedPrimary(Personal).Should().BeNull("в личном чате фильмов нет");

        var id = AddScene();
        var seed = _kind.SeedPrimary(Scope)!;
        seed.Kind.Should().Be("video-scene");
        seed.Ref["sceneId"]!.GetValue<string>().Should().Be(id);
        _kind.SeedPriority.Should().BeGreaterThan(1, "после картинки и звука");
    }

    [Fact]
    public void DescribeExecutor_без_выбора_Авто_а_фильм_без_ИИ()
    {
        var scene = Item("video-scene", SceneRef(AddScene()));
        _kind.DescribeExecutor(Scope, scene).Should().Be("Авто · Авто");
        _kind.DescribeExecutor(Scope, Item("video-film", FilmRef("video/a.film"))).Should().Contain("без ИИ");
    }
}
