using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.VideoEditor.ChatContext;
using ClaudeHomeServer.Services.VideoEditor.Films;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Services.VideoEditor.Chats;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Chats;

// Блок «Видео в этом чате» (ADR-022 §5): состояние, правило приоритета video_* над прямыми генераторами и правило
// темпа «после сцены — спроси»; едет хвостом хода, есть только когда сервер доехал до хода
public sealed class VideoEditorStateContributorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vstate-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Flags(bool on) : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => on && key == FeatureFlagKeys.VideoEditor;
    }

    private static PromptSessionContext Ctx(bool hasMcp = true, string? projectId = "p1") =>
        new(new Session { Id = "s1", OwnerId = "u1", ProjectId = projectId }, "u1", null, null, HasVideoEditorMcp: hasMcp);

    private static IConfiguration Config(bool agentLaunch) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["VideoEditor:AgentLaunch"] = agentLaunch ? "true" : "false" }).Build();

    // Регрессия зависания старта: контрибьютор секций собирается при сборке контейнера, и всё, что он тянет в
    // конструктор, обязано обходиться без SessionManager. FilmService (через VideoJobThreads → справочник чатов) вёл
    // обратно к реестру секций — бесконечная рекурсия при разрешении сервисов и тихое зависание приложения
    [Fact]
    public void Конструктор_берёт_только_хранилища_и_не_тянет_сервисы_ведущие_к_SessionManager()
    {
        var allowed = new[]
        {
            typeof(IFeatureFlagGate), typeof(VideoThreadStore), typeof(ClaudeHomeServer.Services.VideoEditor.Prefs.VideoPrefsService),
            typeof(ClaudeHomeServer.Services.VideoEditor.Films.FilmSideStore), typeof(IConfiguration),
            typeof(Lazy<ChatContextFocusMirror>), // только ленивый: прямое зеркало замыкает цикл через SessionManager
        };

        var parameters = typeof(VideoEditorStateContributor).GetConstructors().Single().GetParameters().Select(p => p.ParameterType);

        parameters.Should().BeSubsetOf(allowed);
    }

    [Fact]
    public void Блок_есть_только_когда_сервер_доехал_и_флаг_включён()
    {
        new VideoEditorStateContributor(new Flags(true)).IsEnabled(Ctx()).Should().BeTrue();
        new VideoEditorStateContributor(new Flags(true)).IsEnabled(Ctx(hasMcp: false)).Should().BeFalse("инструментов у хода нет");
        new VideoEditorStateContributor(new Flags(false)).IsEnabled(Ctx()).Should().BeFalse();
    }

    [Fact]
    public async Task Секция_едет_хвостом_хода_между_звуком_и_правилом_локальной_модели()
    {
        var contributor = new VideoEditorStateContributor(new Flags(true), new VideoThreadStore(_dir));

        var section = (await contributor.BuildAsync(Ctx(), null))!.Sections.Single();

        section.InTurnTail.Should().BeTrue("состояние меняется от хода к ходу: в системном блоке оно обнуляло бы prefix cache");
        contributor.Order.Should().Be(707);
        contributor.Key.Should().Be("video-editor-state");
    }

    [Fact]
    public async Task Блок_несёт_правило_приоритета_и_правило_после_сцены_спроси()
    {
        var contributor = new VideoEditorStateContributor(new Flags(true), new VideoThreadStore(_dir));

        var text = (await contributor.BuildAsync(Ctx(), null))!.Sections.Single().Text;

        text.Should().Contain(VideoEditorStateContributor.PriorityRule)
            .And.Contain("video_new → video_scene_set → video_shoot")
            .And.Contain("local_image_to_video").And.Contain("generate_video Higgsfield")
            .And.Contain("мимо редактора");
        text.Should().Contain("Снимать следующую?").And.Contain("сними все");
    }

    [Fact]
    public async Task Сними_все_означает_снять_оставшиеся_сохранить_и_собрать_без_вопросов()
    {
        var contributor = new VideoEditorStateContributor(new Flags(true), new VideoThreadStore(_dir));

        var text = (await contributor.BuildAsync(Ctx(), null))!.Sections.Single().Text;

        text.Should().Contain(VideoEditorStateContributor.PaceRule);
        VideoEditorStateContributor.PaceRule.Should()
            .Contain("«Сними все» означает").And.Contain("video_save_scene").And.Contain("video_film_build")
            .And.Contain("video_wait").And.Contain("не спрашивая")
            .And.Contain("сам не переснимай").And.Contain("заверши ход и доложи");
    }

    [Fact]
    public async Task Личный_чат_получает_правило_без_локальных_моделей()
    {
        var contributor = new VideoEditorStateContributor(new Flags(true), new VideoThreadStore(_dir));

        var text = (await contributor.BuildAsync(Ctx(projectId: null), null))!.Sections.Single().Text;

        text.Should().Contain(VideoEditorStateContributor.PersonalPriorityRule).And.NotContain("provider local");
    }

    [Fact]
    public async Task Без_запуска_агентом_правил_про_video_shoot_нет()
    {
        var contributor = new VideoEditorStateContributor(new Flags(true), new VideoThreadStore(_dir), config: Config(false));

        (await contributor.BuildAsync(Ctx(), null)).Should().BeNull("правил про video_shoot нет, а остального в блоке не осталось");
    }

    [Fact]
    public void Траты_открытого_фильма_остаются_в_блоке()
    {
        var state = new VideoThreadsState(new VideoFocusDto("sc1", "video/утро/утро.film"), 3, []);

        var text = VideoEditorStateContributor.Render(state, [], new VideoSpentDto(1.5, 0, 30), agentLaunch: false, personal: false);

        text.Should().Contain("Потрачено на открытый фильм video/утро/утро.film: $1.50 + 30 GPU-с")
            .And.NotContain("В работе:");
    }

    [Fact]
    public async Task Журнал_с_прошлого_сообщения_показывается_один_раз()
    {
        var store = new VideoThreadStore(_dir);
        var scene = store.AddScene("u1", "s1", "", new VideoSceneSettingsDto(null, null, "т", null, null, null, null, null, null), null).Scene!;
        store.AddLaunch("u1", "s1", scene.SceneId,
            new VideoLaunchDto("j1", DateTime.UtcNow, VideoLaunchStatus.Running, false, VideoInitiators.Agent, "fal", "m", 1, "т", null, null),
            new VideoThreadEvent(DateTime.UtcNow, VideoThreadEventKinds.Launched, "Claude запустил: съёмка", scene.SceneId, "j1"));
        var contributor = new VideoEditorStateContributor(new Flags(true), store);

        var first = (await contributor.BuildAsync(Ctx(), null))!.Sections.Single().Text;
        var second = (await contributor.BuildAsync(Ctx(), null))!.Sections.Single().Text;

        first.Should().Contain("С прошлого сообщения:").And.Contain("Claude запустил: съёмка");
        second.Should().NotContain("Claude запустил: съёмка");
    }

    // ── Строка контекста (ADR-023 §3.1, 3б-1): блок худеет ──

    [Fact]
    public async Task нет_В_работе_Открыт_фильм_и_Выбор_человека_а_правила_и_журнал_остаются()
    {
        var store = new VideoThreadStore(_dir);
        var scene = store.AddScene("u1", "s1", "", new VideoSceneSettingsDto(null, null, "т", null, null, null, null, null, null), null).Scene!;
        store.SetFocus("u1", "s1", new VideoFocusDto(scene.SceneId, "video/утро/утро.film"), null);
        store.SetSettings("u1", "s1", scene.SceneId, scene.Settings, null,
            new VideoThreadEvent(DateTime.UtcNow, VideoThreadEventKinds.Versions, "Готово: новых версий 1", scene.SceneId));
        var contributor = new VideoEditorStateContributor(new Flags(true), store);

        var text = (await contributor.BuildAsync(Ctx(), null))!.Sections.Single().Text;

        text.Should().StartWith("## Видео в этом чате\n")
            .And.NotContain("В работе:").And.NotContain("Открыт фильм:").And.NotContain("Выбор человека");
        text.Should().Contain(VideoEditorStateContributor.PriorityRule).And.Contain(VideoEditorStateContributor.PaceRule)
            .And.Contain("С прошлого сообщения:").And.Contain("Готово: новых версий 1");
    }

    [Fact]
    public async Task Потрачено_на_фильм_считается_по_фильму_из_контекста_а_не_из_записи_нити()
    {
        using var w = new Films.FilmWorld(withContext: true);
        w.WriteFile("video/утро/a.film");
        w.WriteFile("video/утро/b.film");
        w.Threads.SetFocus("u1", "s1", new VideoFocusDto(null, "video/утро/a.film"), null);
        w.Side.Update("u1", "p1", "video/утро/a.film",
            side => side with { Spends = [new FilmSpendEntry("v1", "usd", 1.00, 0, DateTime.UtcNow)] });
        w.Side.Update("u1", "p1", "video/утро/b.film",
            side => side with { Spends = [new FilmSpendEntry("v2", "usd", 3.25, 0, DateTime.UtcNow)] });
        // Человек открывает B ручкой контекста: запись нити по-прежнему говорит «A»
        w.Context!.SetPrimary("u1", "s1",
            ChatContextFocusMirror.NewItem(VideoContextKind.FilmKind, "video/утро/b.film", ContextActor.Human, refKey: VideoContextKind.FilmKey), null);
        var contributor = new VideoEditorStateContributor(new Flags(true), w.Threads, w.Side, mirror: new Lazy<ChatContextFocusMirror>(() => w.Mirror!));

        var text = (await contributor.BuildAsync(Ctx(), null))!.Sections.Single().Text;

        text.Should().Contain("Потрачено на открытый фильм video/утро/b.film: $3.25").And.NotContain("a.film");
    }

    [Fact]
    public async Task без_запуска_агентом_и_без_журнала_блока_нет()
    {
        var contributor = new VideoEditorStateContributor(new Flags(true), new VideoThreadStore(_dir), config: Config(agentLaunch: false));

        (await contributor.BuildAsync(Ctx(), null)).Should().BeNull();
    }
}
