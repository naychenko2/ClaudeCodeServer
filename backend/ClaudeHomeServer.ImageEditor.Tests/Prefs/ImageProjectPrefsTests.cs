using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Chats;
using ClaudeHomeServer.Services.ImageEditor.Prefs;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Prefs;

// Выбор человека в полосе «Картинки» проекта на сервере: умолчания без файла, запись с событием
// владельцу, удалённый персонаж читается как null, новая нить от человека наследует выбор, блок
// хвоста хода показывает выбор и правило «не передавай provider/model/character без просьбы».
public class ImageProjectPrefsTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-1";
    private const string ProjectId = "p1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ie-prefs-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly Project _project;
    private readonly ImageProjectPrefsStore _store;
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly ImageProjectPrefsService _prefs;

    public ImageProjectPrefsTests()
    {
        _root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        File.WriteAllBytes(Path.Combine(_root, "images", "hero.png"), TestImages.Png(4, 4));
        _project = new Project { Id = ProjectId, OwnerId = Owner, RootPath = _root };
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(ProjectId)).Returns(_project);
        _store = new ImageProjectPrefsStore(Path.Combine(_dir, ImageProjectPrefsStore.DirName));
        _prefs = new ImageProjectPrefsService(_store, NullLogger<ImageProjectPrefsService>.Instance, projects.Object, _broadcaster);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private string Character(string name = "Аня") =>
        CharacterStore.Create(_root, new CharacterDraft(name, null, CharacterStoreTests.Photos(3)), DateTime.UtcNow).Value!.Manifest.Slug;

    [Fact]
    public void Без_файла_умолчания()
    {
        _prefs.Get(Owner, _project).Should().Be(new ImageProjectPrefs(null, null, 2, true, null));
    }

    [Fact]
    public async Task Запись_сохраняется_на_владельца_и_проект_и_рассылается_владельцу()
    {
        var slug = Character();

        var saved = await _prefs.SetAsync(Owner, _project, new ImageProjectPrefs("fal", " m1 ", 3, false, slug));

        saved.Should().Be(new ImageProjectPrefs("fal", "m1", 3, false, slug));
        _prefs.Get(Owner, _project).Should().Be(saved);
        _store.Get("owner-2", ProjectId).Should().Be(ImageProjectPrefs.Default, "у другого владельца свой выбор");
        var sent = _broadcaster.ToOwnerCalls.Should().ContainSingle().Subject;
        sent.OwnerId.Should().Be(Owner);
        var message = sent.Message.Should().BeOfType<ImageProjectPrefsChangedMessage>().Subject;
        message.Type.Should().Be("image_prefs_changed");
        message.ProjectId.Should().Be(ProjectId);
        message.Prefs.Should().Be(saved);
    }

    [Fact]
    public async Task Удалённый_персонаж_читается_как_null()
    {
        var slug = Character();
        await _prefs.SetAsync(Owner, _project, new ImageProjectPrefs(null, null, 2, true, slug));

        CharacterStore.Delete(_root, slug).Should().BeTrue();

        _prefs.Get(Owner, _project).CharacterSlug.Should().BeNull();
        _prefs.Get(Owner, ProjectId).CharacterSlug.Should().BeNull();
    }

    [Fact]
    public async Task Новая_нить_человека_наследует_выбор_проекта_а_существующая_нет()
    {
        var threads = new ImageThreadStore(Path.Combine(_dir, ImageThreadStore.DirName));
        var service = new ImageThreadService(threads, NullLogger<ImageThreadService>.Instance, prefs: _prefs);
        await _prefs.SetAsync(Owner, _project, new ImageProjectPrefs("local", "qwen-image-2.1", 3, false, null));

        var opened = await service.OpenAsync(Owner, ProjectId, Chat, "images/hero.png", null, 0, default);
        var draft = await service.OpenAsync(Owner, ProjectId, Chat, null, "", opened.State.Revision, default);

        var expected = new ImageThreadSettings("local", "qwen-image-2.1", 3, false);
        opened.Thread!.Settings.Should().Be(expected);
        draft.Thread!.Settings.Should().Be(expected, "черновик «Новая картинка» тоже начинает с выбора в полосе");

        await _prefs.SetAsync(Owner, _project, new ImageProjectPrefs("fal", null, 1, true, null));
        var again = await service.OpenAsync(Owner, ProjectId, Chat, "images/hero.png", null, draft.State.Revision, default);
        again.Existing.Should().BeTrue();
        again.Thread!.Settings.Should().Be(expected, "у уже взятой картинки свои настройки");
    }

    [Fact]
    public async Task Блок_хода_показывает_выбор_человека_и_правило()
    {
        var threads = new ImageThreadStore(Path.Combine(_dir, ImageThreadStore.DirName));
        var slug = Character();
        await _prefs.SetAsync(Owner, _project, new ImageProjectPrefs("fal", "m1", 3, true, slug));
        var focused = threads.Open(Owner, Chat, "images/hero.png", null, 0, new ImageThreadSettings("higgsfield", null, 1, true));
        var flags = new Mock<IFeatureFlagGate>();
        flags.Setup(f => f.IsEnabled(Owner, FeatureFlagKeys.ImageEditor)).Returns(true);
        var contributor = new ImageEditorStateContributor(flags.Object, threads: threads, prefs: _prefs);
        var context = new PromptSessionContext(new Session { Id = Chat, OwnerId = Owner, ProjectId = ProjectId }, Owner, null, _root);

        var text = (await contributor.BuildAsync(context, "дальше"))!.Sections.Single().Text;

        text.Should().Contain($"Выбор человека в полосе «Картинки»: поставщик higgsfield, модель auto, вариантов 1, персонаж {slug}",
            "у картинки в работе — её настройки, персонаж — из полосы проекта");
        text.Should().Contain("не передавай provider/model/character в image_generate, если он сам не просил сменить");

        threads.SetFocus(Owner, Chat, null, focused.State.Revision);
        CharacterStore.Delete(_root, slug);
        var unfocused = (await contributor.BuildAsync(context, "дальше"))!.Sections.Single().Text;
        unfocused.Should().Contain("Выбор человека в полосе «Картинки»: поставщик fal, модель m1, вариантов 3, персонаж не подключён",
            "без картинки в работе — выбор проекта; удалённый персонаж не подключён");
    }
}
