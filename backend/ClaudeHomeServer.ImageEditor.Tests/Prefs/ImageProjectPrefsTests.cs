using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Chats;
using ClaudeHomeServer.Services.ImageEditor.Mcp;
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
    public async Task Явное_сохранение_видно_только_после_записи_и_только_в_своём_проекте()
    {
        _prefs.HasSaved(Owner, ProjectId).Should().BeFalse("файла ещё нет");

        await _prefs.SetAsync(Owner, _project, ImageProjectPrefs.Default);

        _prefs.HasSaved(Owner, ProjectId).Should().BeTrue("даже выбор, совпавший с умолчаниями, сохранён явно");
        _store.Save("owner-2", ProjectId, ImageProjectPrefs.Default);
        _prefs.HasSaved("owner-2", ProjectId).Should().BeFalse("проект чужой, даже если файл лежит");
        _prefs.HasSaved(Owner, "missing").Should().BeFalse("проекта нет");
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

    // ── Выбор по режимам «Создать» и «Править» (панель v5) ─────────────────────

    private static readonly ImageCreatePrefs CreateLocal = new("local", "qwen-image-2.1", 1);
    private static readonly ImageEditPrefs EditHiggs = new("higgsfield", null, 4, "removeBackground", "fast", "16:9");

    [Fact]
    public async Task Запись_без_режимов_не_затирает_сохранённые_режимы()
    {
        await _prefs.SetAsync(Owner, _project, new ImageProjectPrefs("fal", null, 2, true, null, CreateLocal, EditHiggs));

        // Старый фронт из другой вкладки шлёт только плоские поля
        var saved = await _prefs.SetAsync(Owner, _project, new ImageProjectPrefs("fal", "m2", 3, false, null));

        saved.Should().Be(new ImageProjectPrefs("fal", "m2", 3, false, null, CreateLocal, EditHiggs));
        _store.Get(Owner, ProjectId).Should().Be(saved);

        var edit = EditHiggs with { Op = "outpaint", Count = 1 };
        (await _prefs.SetAsync(Owner, _project, saved with { Edit = edit, Create = null }))
            .Should().Be(saved with { Edit = edit }, "присланный режим заменяется, неприсланный остаётся");
    }

    [Fact]
    public void Старый_файл_без_режимов_читается_и_даёт_прежние_настройки()
    {
        var path = _store.PathOf(Owner, ProjectId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"provider":"fal","model":"m1","count":3,"matchSourceSize":false,"characterSlug":null}""");

        var prefs = _store.Get(Owner, ProjectId);

        prefs.Should().Be(new ImageProjectPrefs("fal", "m1", 3, false, null));
        prefs.HasModes.Should().BeFalse();
        var flat = new ImageThreadSettings("fal", "m1", 3, false);
        prefs.CreateSettings().Should().Be(flat);
        prefs.EditSettings().Should().Be(flat);
        _prefs.SettingsFor(Owner, ProjectId).Should().Be(flat);
    }

    [Fact]
    public void Режим_берёт_поставщика_и_модель_парой_а_число_с_запасом_из_плоских()
    {
        var prefs = new ImageProjectPrefs("fal", "flux", 3, false, null,
            new ImageCreatePrefs("local", null, null), new ImageEditPrefs(null, null, 2, "upscale", null, null));

        prefs.CreateSettings().Should().Be(new ImageThreadSettings("local", null, 3, false),
            "модель плоских полей выбрана у другого поставщика");
        prefs.EditSettings().Should().Be(new ImageThreadSettings("fal", "flux", 2, false));
    }

    [Fact]
    public async Task Новая_нить_получает_настройки_режима_Править()
    {
        var threads = new ImageThreadStore(Path.Combine(_dir, ImageThreadStore.DirName));
        var service = new ImageThreadService(threads, NullLogger<ImageThreadService>.Instance, prefs: _prefs);
        await _prefs.SetAsync(Owner, _project, new ImageProjectPrefs("fal", null, 2, true, null, CreateLocal, EditHiggs));

        var opened = await service.OpenAsync(Owner, ProjectId, Chat, "images/hero.png", null, 0, default);

        opened.Thread!.Settings.Should().Be(new ImageThreadSettings("higgsfield", null, 4, true));
    }

    [Theory]
    [InlineData("edit", null, null, 2, null)]
    [InlineData("removeBackground", "photoreal", "9:16", 4, null)]
    [InlineData("generate", null, null, 2, "Недопустимая операция правки: generate")]
    [InlineData("bogus", null, null, 2, "Недопустимая операция правки: bogus")]
    [InlineData("RemoveBackground", null, null, 2, "Недопустимая операция правки: RemoveBackground")]
    [InlineData(null, "slow", null, 2, "Недопустимый режим подбора: slow")]
    [InlineData(null, null, "4:3", 2, "Пропорции 4:3 не поддерживаются: только 1:1, 16:9, 9:16")]
    [InlineData(null, null, null, 5, "Число вариантов — от 1 до 4")]
    [InlineData(null, null, null, 0, "Число вариантов — от 1 до 4")]
    public void Режим_Править_проверяется_белыми_списками(string? op, string? mode, string? ratio, int count, string? error)
    {
        ImageProjectPrefsService.Validate(ImageProjectPrefs.Default with { Edit = new(null, null, count, op, mode, ratio) })
            .Should().Be(error);
    }

    [Fact]
    public void Число_вариантов_режима_Создать_проверяется_лимитом()
    {
        ImageProjectPrefsService.Validate(ImageProjectPrefs.Default with { Create = new(null, null, 5) })
            .Should().Be("Число вариантов — от 1 до 4");
        ImageProjectPrefsService.Validate(ImageProjectPrefs.Default with { Create = new(null, null, null) }).Should().BeNull();
    }

    [Fact]
    public async Task Блок_хода_с_режимами_показывает_оба_выбора()
    {
        await _prefs.SetAsync(Owner, _project, new ImageProjectPrefs("fal", null, 2, true, null, CreateLocal, EditHiggs));
        var (contributor, context, threads) = Contributor();

        var text = (await contributor.BuildAsync(context, "нарисуй кота"))!.Sections.Single().Text;

        text.Should().Contain("Выбор человека в полосе «Картинки»: "
            + "новая картинка (генерация по тексту) — поставщик local, модель qwen-image-2.1, вариантов 1; "
            + "правка — поставщик higgsfield, модель auto, вариантов 4, операция removeBackground; персонаж не подключён");

        threads.Open(Owner, Chat, "images/hero.png", null, 0, new ImageThreadSettings("fal", "m1", 2, true));
        var focused = (await contributor.BuildAsync(context, "дальше"))!.Sections.Single().Text;
        focused.Should().Contain("правка картинки в работе — поставщик fal, модель m1, вариантов 2, операция removeBackground",
            "у картинки в работе правка идёт по её настройкам");
        focused.Should().Contain("новая картинка (генерация по тексту) — поставщик local");
    }

    private (ImageEditorStateContributor Contributor, PromptSessionContext Context, ImageThreadStore Threads) Contributor(
        bool agentLaunch = true, bool personal = false, bool flag = true)
    {
        var threads = new ImageThreadStore(Path.Combine(_dir, ImageThreadStore.DirName));
        var flags = new Mock<IFeatureFlagGate>();
        flags.Setup(f => f.IsEnabled(Owner, FeatureFlagKeys.ImageEditor)).Returns(flag);
        var config = TestImages.Config((ImageEditorToolset.AgentLaunchKey, agentLaunch ? "true" : "false"));
        var contributor = new ImageEditorStateContributor(flags.Object, threads: threads, prefs: _prefs, config: config);
        var context = new PromptSessionContext(
            new Session { Id = Chat, OwnerId = Owner, ProjectId = personal ? null : ProjectId }, Owner, null, _root);
        return (contributor, context, threads);
    }

    [Fact]
    public async Task Без_нитей_и_без_сохранённого_выбора_блока_нет()
    {
        var (contributor, context, _) = Contributor();

        contributor.IsEnabled(context).Should().BeFalse();
        (await contributor.BuildAsync(context, "нарисуй кота")).Should().BeNull();
    }

    [Fact]
    public async Task Без_нитей_с_сохранённым_выбором_блок_с_выбором_и_правилом_приоритета()
    {
        await _prefs.SetAsync(Owner, _project, new ImageProjectPrefs("local", "qwen-image-2.1", 2, true, null));
        var (contributor, context, _) = Contributor();

        contributor.IsEnabled(context).Should().BeTrue("выбор в полосе сохранён явно");
        var text = (await contributor.BuildAsync(context, "нарисуй кота"))!.Sections.Single().Text;

        text.Should().Contain("В работе: ничего не выбрано");
        text.Should().Contain("Выбор человека в полосе «Картинки»: поставщик local, модель qwen-image-2.1, вариантов 2, персонаж не подключён");
        text.Should().Contain(ImageEditorStateContributor.ChoiceRule);
        text.Should().Contain(ImageEditorStateContributor.PriorityRule);
        text.Should().NotContain("С прошлого сообщения");
    }

    [Fact]
    public async Task Без_запуска_агентом_правила_приоритета_нет()
    {
        await _prefs.SetAsync(Owner, _project, ImageProjectPrefs.Default);
        var (contributor, context, _) = Contributor(agentLaunch: false);

        var text = (await contributor.BuildAsync(context, "нарисуй кота"))!.Sections.Single().Text;

        text.Should().Contain(ImageEditorStateContributor.ChoiceRule);
        text.Should().NotContain("image_new → image_generate", "инструмента image_generate у агента нет");
    }

    [Fact]
    public async Task С_нитями_прежний_вывод_плюс_правило_приоритета()
    {
        var (contributor, context, threads) = Contributor();
        var opened = threads.Open(Owner, Chat, "images/hero.png", null, 0, new ImageThreadSettings("fal", null, 1, true));

        var text = (await contributor.BuildAsync(context, "дальше"))!.Sections.Single().Text;

        text.Should().Contain($"- {opened.Thread!.Id} (в работе): файл images/hero.png");
        text.Should().Contain(ImageEditorStateContributor.PriorityRule);
    }

    // ── Личный чат вне проекта: блок всегда при флаге и AgentLaunch (решение Андрея 29.09) ──

    [Fact]
    public async Task Личный_чат_без_нитей_и_выбора_короткий_блок_без_выбора_человека()
    {
        var (contributor, context, _) = Contributor(personal: true);

        contributor.IsEnabled(context).Should().BeTrue("в личном чате блок есть всегда — иначе «нарисуй» уйдёт мимо image_generate");
        var text = (await contributor.BuildAsync(context, "нарисуй кота"))!.Sections.Single().Text;

        text.Should().Be(ImageEditorStateContributor.RenderEmpty());
        text.Should().Contain("В работе: ничего не выбрано");
        text.Should().Contain(ImageEditorStateContributor.PersonalPriorityRule);
        text.Should().NotContain("Выбор человека", "выбора человека нет");
        text.Should().NotContain(ImageEditorStateContributor.ChoiceRule);
        text.Should().NotContain("local-media", "в личном чате local-media нет");
        text.Should().NotContain("проект");
    }

    [Fact]
    public void Личный_чат_правило_приоритета_ведёт_локальную_просьбу_в_image_generate()
    {
        ImageEditorStateContributor.PersonalPriorityRule.Should().Contain("image_generate с provider local")
            .And.NotContain("local-media");
        ImageEditorStateContributor.PriorityRule.Should().Contain("local-media", "проектный текст не меняется");
    }

    [Fact]
    public async Task Личный_чат_с_сохранённым_личным_выбором_блок_с_выбором()
    {
        await _prefs.SetAsync(Owner, new ImageEditScope(ImageEditScope.Personal, null),
            new ImageProjectPrefs("local", "qwen-image-2.1", 2, true, null));
        var (contributor, context, _) = Contributor(personal: true);

        var text = (await contributor.BuildAsync(context, "нарисуй кота"))!.Sections.Single().Text;

        text.Should().Contain("Выбор человека в полосе «Картинки»: поставщик local, модель qwen-image-2.1, вариантов 2, персонаж не подключён");
        text.Should().Contain(ImageEditorStateContributor.ChoiceRule);
        text.Should().Contain(ImageEditorStateContributor.PersonalPriorityRule);
        text.Should().NotContain(ImageEditorStateContributor.PriorityRule);
    }

    [Fact]
    public async Task Личный_чат_с_нитями_черновик_скачивает_человек()
    {
        var (contributor, context, threads) = Contributor(personal: true);
        var draft = threads.Open(Owner, Chat, null, "", 0, null);

        var text = (await contributor.BuildAsync(context, "дальше"))!.Sections.Single().Text;

        text.Should().Contain($"- {draft.Thread!.Id} (в работе): новая картинка, ещё не сохранена (человек скачает её)");
        text.Should().Contain(ImageEditorStateContributor.PersonalPriorityRule);
        text.Should().NotContain("корень проекта");
    }

    // Фокус с прошлого сообщения не должен превращать просьбу о новой картинке в правку (баг 30.09)
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Картинка_в_работе_не_обязывает_продолжать_её_для_новой_просьбы(bool personal)
    {
        var (contributor, context, threads) = Contributor(personal: personal);
        var draft = threads.Open(Owner, Chat, null, "", 0, null);

        var text = (await contributor.BuildAsync(context, "нарисуй собаку"))!.Sections.Single().Text;

        text.Should().Contain($"В работе: картинка {draft.Thread!.Id} — {ImageEditorStateContributor.FocusIsNotBindingText}");
        text.Should().Contain(ImageEditorStateContributor.NewOrContinueRule);
    }

    [Fact]
    public void Оба_правила_приоритета_различают_новую_картинку_и_продолжение()
    {
        foreach (var rule in new[] { ImageEditorStateContributor.PriorityRule, ImageEditorStateContributor.PersonalPriorityRule })
            rule.Should().Contain(ImageEditorStateContributor.NewOrContinueRule);
        ImageEditorStateContributor.NewOrContinueRule.Should().Contain("всегда image_new")
            .And.Contain("даже если другая картинка в работе")
            .And.Contain("Сомневаешься — новая картинка")
            // Опорные примеры обеих сторон: без них модель хуже различает граничные просьбы
            .And.Contain("«нарисуй собаку»")
            .And.Contain("«поправь»");
    }

    [Fact]
    public async Task Личный_чат_без_запуска_агентом_как_у_проекта()
    {
        var (contributor, context, threads) = Contributor(agentLaunch: false, personal: true);

        contributor.IsEnabled(context).Should().BeFalse("без image_generate блоку без нитей и выбора нечего сказать");
        (await contributor.BuildAsync(context, "нарисуй кота")).Should().BeNull();

        threads.Open(Owner, Chat, null, "", 0, null);
        contributor.IsEnabled(context).Should().BeTrue("нити есть — блок есть");
        (await contributor.BuildAsync(context, "дальше"))!.Sections.Single().Text
            .Should().NotContain("image_new → image_generate");
    }

    [Fact]
    public void Личный_чат_без_флага_блока_нет()
    {
        var (contributor, context, _) = Contributor(personal: true, flag: false);

        contributor.IsEnabled(context).Should().BeFalse();
    }

    [Fact]
    public async Task Сохранённый_выбор_по_области_личный_отдельно_от_проекта()
    {
        _prefs.HasSaved(Owner, new ImageEditScope(ImageEditScope.Personal, null)).Should().BeFalse();
        _prefs.HasSaved(Owner, ImageEditScope.Personal).Should().BeFalse("строковая версия — только для проекта");

        await _prefs.SetAsync(Owner, new ImageEditScope(ImageEditScope.Personal, null), ImageProjectPrefs.Default);

        _prefs.HasSaved(Owner, new ImageEditScope(ImageEditScope.Personal, null)).Should().BeTrue();
        _prefs.HasSaved("owner-2", new ImageEditScope(ImageEditScope.Personal, null)).Should().BeFalse("у другого владельца свой выбор");
        _prefs.HasSaved(Owner, ImageEditScope.Of(_project)).Should().BeFalse("выбор личного чата не делает выбор проекта");
    }
}
