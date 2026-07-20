using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Юнит-тесты NotesService: сканирование, парсинг [[wikilinks]] и тегов, резолв,
// backlinks, ghost-узлы, несвязанные упоминания, авто-обновление ссылок при ренейме.
// Работаем только с личным vault (проектов нет) — папка {dataDir}/notes/{userId}.
public class NotesServiceTests : IDisposable
{
    private const string User = "u1";
    private readonly string _dir;
    private readonly string _vault;
    private readonly ProjectManager _projects;
    private readonly NotesService _sut;

    public NotesServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "notes_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _vault = Path.Combine(_dir, "notes", User);
        Directory.CreateDirectory(_vault);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        _projects = new ProjectManager(config, users, appSettings);
        _sut = new NotesService(_projects, config, NullLogger<NotesService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void WriteNote(string name, string content) =>
        File.WriteAllText(Path.Combine(_vault, name + ".md"), content);

    private string IdOf(string title) =>
        _sut.GetSummaries(User, null, null).Single(n => n.Title == title).Id;

    // ─── Создание и список ───────────────────────────────────────────────────

    [Fact]
    public void Create_СоздаётФайлИПопадаетВСписок()
    {
        var note = _sut.Create(User, new CreateNoteRequest("Моя заметка", "тело", "personal"));

        note.Title.Should().Be("Моя заметка");
        File.Exists(Path.Combine(_vault, "Моя заметка.md")).Should().BeTrue();
        _sut.GetSummaries(User, null, null).Should().ContainSingle(n => n.Title == "Моя заметка");
    }

    // ─── Резолв [[wikilinks]] и backlinks ────────────────────────────────────

    [Fact]
    public void Wikilink_РезолвитсяИДаётBacklink()
    {
        WriteNote("Бета", "# Бета");
        WriteNote("Альфа", "Ссылка на [[Бета]].");

        var alpha = _sut.GetDetail(User, IdOf("Альфа"))!;
        alpha.Links.Should().ContainSingle();
        alpha.Links[0].TargetTitle.Should().Be("Бета");
        alpha.Links[0].Resolved.Should().BeTrue();

        var beta = _sut.GetDetail(User, IdOf("Бета"))!;
        beta.Backlinks.Should().ContainSingle(b => b.SourceTitle == "Альфа");
    }

    [Fact]
    public void Wikilink_СПодписьюИЯкорем_РезолвитсяПоИмени()
    {
        WriteNote("Цель", "# Цель");
        WriteNote("Исток", "[[Цель|подпись]] и [[Цель#раздел]].");

        var links = _sut.GetDetail(User, IdOf("Исток"))!.Links;
        links.Should().OnlyContain(l => l.Resolved && l.TargetTitle == "Цель");
    }

    // ─── Ghost (несуществующая цель) ─────────────────────────────────────────

    [Fact]
    public void Wikilink_НаНесуществующую_ДаётGhostУзелВГрафе()
    {
        WriteNote("Одна", "Ссылка на [[Пустоту]].");

        var link = _sut.GetDetail(User, IdOf("Одна"))!.Links.Single();
        link.Resolved.Should().BeFalse();

        var graph = _sut.GetGraph(User);
        graph.Nodes.Should().Contain(n => n.Ghost && n.Title == "Пустоту");
        graph.Edges.Should().ContainSingle();
    }

    // ─── Теги: frontmatter + inline ──────────────────────────────────────────

    [Fact]
    public void Tags_ИзFrontmatterИInline_Собираются()
    {
        WriteNote("Тегированная", "---\ntitle: Настоящий заголовок\ntags: [проект, идея]\n---\nтело с #inline и #ещё.");

        var summary = _sut.GetSummaries(User, null, null).Single();
        summary.Title.Should().Be("Настоящий заголовок");   // из frontmatter, не имя файла
        summary.Tags.Should().Contain(new[] { "проект", "идея", "inline", "ещё" });
    }

    // ─── Несвязанные упоминания ──────────────────────────────────────────────

    [Fact]
    public void UnlinkedMentions_НаходитУпоминаниеБезСсылки()
    {
        WriteNote("Гамма", "# Гамма");
        WriteNote("Обзор", "Тут упоминается Гамма просто текстом, без ссылки.");

        var mentions = _sut.GetDetail(User, IdOf("Обзор"))!.UnlinkedMentions;
        mentions.Should().ContainSingle(m => m.SourceTitle == "Гамма");
    }

    [Fact]
    public void UnlinkedMentions_ЕслиУжеЕстьСсылка_НеДублирует()
    {
        WriteNote("Дельта", "# Дельта");
        WriteNote("Связка", "Ссылка [[Дельта]] и ещё раз Дельта текстом.");

        _sut.GetDetail(User, IdOf("Связка"))!.UnlinkedMentions
            .Should().NotContain(m => m.SourceTitle == "Дельта");
    }

    // ─── Переименование обновляет входящие ссылки ────────────────────────────

    [Fact]
    public void Rename_ОбновляетВходящиеСсылки()
    {
        var target = _sut.Create(User, new CreateNoteRequest("Старое", "целевая", "personal"));
        _sut.Create(User, new CreateNoteRequest("Источник", "смотри [[Старое]] тут", "personal"));

        _sut.Update(User, target.Id, new UpdateNoteRequest(Title: "Новое"));

        var src = _sut.GetDetail(User, IdOf("Источник"))!;
        src.Content.Should().Contain("[[Новое]]").And.NotContain("[[Старое]]");
        src.Links.Should().OnlyContain(l => l.Resolved && l.TargetTitle == "Новое");
    }

    // ─── Поиск по содержимому ────────────────────────────────────────────────

    [Fact]
    public void Search_ИщетПоТелуНеТолькоПоЗаголовку()
    {
        WriteNote("Заголовок1", "уникальноеслово внутри тела");
        WriteNote("Заголовок2", "другое содержимое");

        var found = _sut.GetSummaries(User, null, "уникальноеслово");
        found.Should().ContainSingle(n => n.Title == "Заголовок1");
    }

    [Fact]
    public void Search_ОператорTag_ФильтруетПоТегу()
    {
        WriteNote("СТегом", "текст про кэш #идея");
        WriteNote("БезТега", "текст про кэш");

        var found = _sut.GetSummaries(User, null, "tag:идея кэш");
        found.Should().ContainSingle(n => n.Title == "СТегом");
    }

    [Fact]
    public void ParseQuery_РазбираетОператорыИТекст()
    {
        var (tags, sources, statuses, text) = NotesService.ParseQuery("tag:#идея source:Личный status:open про кэш");
        tags.Should().Equal("идея");
        sources.Should().Equal("Личный");
        statuses.Should().Equal("open");
        text.Should().Be("про кэш");
    }

    // ─── Шаблоны ─────────────────────────────────────────────────────────────

    [Fact]
    public void Create_СШаблоном_ПодставляетПеременные()
    {
        var tplDir = Path.Combine(_vault, "templates");
        Directory.CreateDirectory(tplDir);
        File.WriteAllText(Path.Combine(tplDir, "Встреча.md"), "# {{title}}\nДата: {{date}}\n");

        _sut.GetTemplates(User).Should().ContainSingle(t => t.Id == "Встреча");

        var note = _sut.Create(User, new CreateNoteRequest("Синк по проекту", null, "personal", "Встреча"));
        note.Content.Should().StartWith("# Синк по проекту");
        note.Content.Should().Contain($"Дата: {DateTime.Now:yyyy-MM-dd}");

        // Шаблоны не попадают в список заметок
        _sut.GetSummaries(User, null, null).Should().NotContain(n => n.Title == "Встреча");
    }

    // ─── Daily note ──────────────────────────────────────────────────────────

    [Fact]
    public void Daily_СоздаётИВозвращаетТуЖе()
    {
        var first = _sut.GetOrCreateDaily(User, "2026-07-09");
        first.Path.Should().Be("Journal/2026-07-09.md");

        File.AppendAllText(Path.Combine(_vault, "Journal", "2026-07-09.md"), "дополнение");
        var second = _sut.GetOrCreateDaily(User, "2026-07-09");
        second.Id.Should().Be(first.Id);
        // Повторный вызов не перезаписывает файл (контент проверяем на диске:
        // модель кэшируется ~2с и внешнюю правку может отдать с задержкой — это ок)
        File.ReadAllText(Path.Combine(_vault, "Journal", "2026-07-09.md"))
            .Should().Contain("дополнение");
    }

    // ─── «Связать» упоминание ────────────────────────────────────────────────

    [Fact]
    public void LinkMention_ОборачиваетПервоеВхождение()
    {
        WriteNote("Гамма", "# Гамма");
        var note = _sut.Create(User, new CreateNoteRequest("Обзор", "Тут упоминается Гамма текстом.", "personal"));

        var updated = _sut.LinkMention(User, note.Id, "Гамма")!;
        updated.Content.Should().Contain("[[Гамма]]");
        updated.Links.Should().ContainSingle(l => l.Resolved && l.TargetTitle == "Гамма");
        updated.UnlinkedMentions.Should().BeEmpty();
    }

    // ─── Папки ───────────────────────────────────────────────────────────────

    [Fact]
    public void Create_ВПапке_И_Move_МеждуПапками()
    {
        var n = _sut.Create(User, new CreateNoteRequest("Черновик", "тело", "personal", Folder: "Идеи/Сырое"));
        n.Path.Should().Be("Идеи/Сырое/Черновик.md");
        File.Exists(Path.Combine(_vault, "Идеи", "Сырое", "Черновик.md")).Should().BeTrue();

        var moved = _sut.Move(User, n.Id, "Готовое")!;
        moved.Path.Should().Be("Готовое/Черновик.md");
        moved.Id.Should().NotBe(n.Id);
        File.Exists(Path.Combine(_vault, "Готовое", "Черновик.md")).Should().BeTrue();
        File.Exists(Path.Combine(_vault, "Идеи", "Сырое", "Черновик.md")).Should().BeFalse();

        // В корень
        var root = _sut.Move(User, moved.Id, null)!;
        root.Path.Should().Be("Черновик.md");
    }

    [Fact]
    public void Move_МеждуИсточниками_ПереноситФайлИМеняетИсточник()
    {
        var project = _projects.Create("Проект", Path.Combine(_dir, "proj"), User, "u1", createDirectory: true);
        var n = _sut.Create(User, new CreateNoteRequest("Мигрант", "тело", "personal", Folder: "Идеи"));

        var moved = _sut.Move(User, n.Id, "Входящие", project.Id)!;
        moved.Source.Should().Be(project.Id);
        moved.Path.Should().Be("Входящие/Мигрант.md");
        moved.Id.Should().NotBe(n.Id);
        File.Exists(Path.Combine(_dir, "proj", "notes", "Входящие", "Мигрант.md")).Should().BeTrue();
        File.Exists(Path.Combine(_vault, "Идеи", "Мигрант.md")).Should().BeFalse();

        // Обратно в личный vault (корень)
        var back = _sut.Move(User, moved.Id, null, "personal")!;
        back.Source.Should().Be("personal");
        File.Exists(Path.Combine(_vault, "Мигрант.md")).Should().BeTrue();

        // Чужой проект — нельзя
        var alien = _projects.Create("Чужой", Path.Combine(_dir, "alien"), "u2", "u2", createDirectory: true);
        var act = () => _sut.Move(User, back.Id, null, alien.Id);
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Move_СсылкиНаЗаметку_НеЛомаются()
    {
        var target = _sut.Create(User, new CreateNoteRequest("Цель", "тело", "personal"));
        _sut.Create(User, new CreateNoteRequest("Источник", "смотри [[Цель]]", "personal"));

        _sut.Move(User, target.Id, "Папка");

        var src = _sut.GetSummaries(User, null, null).Single(s => s.Title == "Источник");
        _sut.GetDetail(User, src.Id)!.Links
            .Should().ContainSingle(l => l.Resolved && l.TargetTitle == "Цель");
    }

    // ─── Физические папки (пустые) ────────────────────────────────────────────

    [Fact]
    public void CreateFolder_СоздаётФизическуюПапку_ВидноВGetFolders()
    {
        var f = _sut.CreateFolder(User, "personal", "Идеи/Черновики");
        f.Source.Should().Be("personal");
        f.Path.Should().Be("Идеи/Черновики");
        Directory.Exists(Path.Combine(_vault, "Идеи", "Черновики")).Should().BeTrue();

        var folders = _sut.GetFolders(User).Select(x => x.Path).ToList();
        folders.Should().Contain("Идеи");            // промежуточный уровень тоже
        folders.Should().Contain("Идеи/Черновики");
    }

    [Fact]
    public void CreateFolder_Дубликат_НеПадает()
    {
        _sut.CreateFolder(User, "personal", "Архив");
        var act = () => _sut.CreateFolder(User, "personal", "Архив");
        act.Should().NotThrow();
    }

    [Fact]
    public void CreateFolder_Traversal_Санитайзится()
    {
        _sut.CreateFolder(User, "personal", "../../злая");
        // Вне vault ничего не создано; папка «злая» — внутри
        Directory.Exists(Path.Combine(_vault, "злая")).Should().BeTrue();
        Directory.Exists(Path.GetFullPath(Path.Combine(_vault, "..", "..", "злая"))).Should().BeFalse();
    }

    [Fact]
    public void CreateFolder_ИмяЗанятоФайлом_Ошибка()
    {
        _sut.Create(User, new CreateNoteRequest("Заметка", "тело", "personal"));
        // Файл «Заметка.md» есть, но «Заметка» (без .md) как папка — свободно; берём занятое имя
        File.WriteAllText(Path.Combine(_vault, "конфликт"), "x");
        var act = () => _sut.CreateFolder(User, "personal", "конфликт");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void GetFolders_ПустойVault_ПустойСписок()
    {
        _sut.GetFolders("newbie").Should().BeEmpty();   // vault не существует — без исключения
    }

    [Fact]
    public void GetFolders_ИсключаетСкрытыеИTemplates()
    {
        Directory.CreateDirectory(Path.Combine(_vault, ".obsidian", "plugins"));
        Directory.CreateDirectory(Path.Combine(_vault, "templates"));
        _sut.CreateFolder(User, "personal", "Обычная");

        var folders = _sut.GetFolders(User).Select(x => x.Path).ToList();
        folders.Should().Contain("Обычная");
        folders.Should().NotContain(p => p.StartsWith(".obsidian"));
        folders.Should().NotContain(p => p == "templates" || p.StartsWith("templates/"));
    }

    [Fact]
    public void DeleteFolder_Пустая_Удаляется()
    {
        _sut.CreateFolder(User, "personal", "Пусто");
        var removed = _sut.DeleteFolder(User, "personal", "Пусто");
        removed.Should().Be(0);
        Directory.Exists(Path.Combine(_vault, "Пусто")).Should().BeFalse();
    }

    [Fact]
    public void DeleteFolder_СЗаметками_РекурсивноИИнвалидируетСписок()
    {
        _sut.Create(User, new CreateNoteRequest("A", "тело", "personal", Folder: "Раздел"));
        _sut.Create(User, new CreateNoteRequest("B", "тело", "personal", Folder: "Раздел/Внутри"));

        var removed = _sut.DeleteFolder(User, "personal", "Раздел");
        removed.Should().Be(2);
        Directory.Exists(Path.Combine(_vault, "Раздел")).Should().BeFalse();
        _sut.GetSummaries(User, null, null).Should().NotContain(s => s.Title == "A" || s.Title == "B");
    }

    [Fact]
    public void DeleteFolder_Несуществующая_KeyNotFound()
    {
        var act = () => _sut.DeleteFolder(User, "personal", "Нет");
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void Resolve_ПрефиксПапки_РазрешаетДубликаты()
    {
        _sut.Create(User, new CreateNoteRequest("Дубль", "первая", "personal", Folder: "Идеи"));
        _sut.Create(User, new CreateNoteRequest("Дубль", "вторая", "personal", Folder: "Архив"));
        var linksNote = _sut.Create(User, new CreateNoteRequest("Ссылки",
            "короткая [[Дубль]] и точная [[Архив/Дубль]]", "personal"));

        var links = _sut.GetDetail(User, linksNote.Id)!.Links;
        // Короткая неоднозначна в одном источнике → без папки не резолвится...
        // ...но точная с префиксом папки — резолвится во «вторую»
        var precise = _sut.ResolveByName(User, "Архив/Дубль", null);
        precise.Should().NotBeNull();
        precise!.Value.Note.Path.Should().Be("Архив/Дубль.md");
        links.Should().Contain(l => l.Resolved);
    }

    [Fact]
    public void MoveFolder_ПереименованиеИПеренос_СМаппингомId()
    {
        var a = _sut.Create(User, new CreateNoteRequest("А", "тело", "personal", Folder: "Старая"));
        _sut.Create(User, new CreateNoteRequest("Б", "тело с [[А]]", "personal", Folder: "Старая/Вложенная"));

        var map = _sut.MoveFolder(User, "personal", "Старая", "Архив/Новая");
        map.Should().HaveCount(2);
        map.Should().Contain(m => m.OldId == a.Id);

        var paths = _sut.GetSummaries(User, null, null).Select(s => s.Path).ToList();
        paths.Should().Contain("Архив/Новая/А.md");
        paths.Should().Contain("Архив/Новая/Вложенная/Б.md");

        // Ссылка [[А]] пережила переезд (резолв по заголовку)
        var b = _sut.GetSummaries(User, null, null).Single(s => s.Title == "Б");
        _sut.GetDetail(User, b.Id)!.Links.Should().ContainSingle(l => l.Resolved && l.TargetTitle == "А");

        // Внутрь самой себя — нельзя
        var act = () => _sut.MoveFolder(User, "personal", "Архив", "Архив/Новая/Глубже");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SanitizeFolder_ЧиститTraversalИМусор()
    {
        NotesService.SanitizeFolder("Идеи/Черновики").Should().Be("Идеи/Черновики");
        NotesService.SanitizeFolder("../../etc").Should().Be("etc");
        NotesService.SanitizeFolder(@"a\b").Should().Be("a/b");
        NotesService.SanitizeFolder("  ").Should().Be("");
        NotesService.SanitizeFolder(null).Should().Be("");
    }

    // ─── Резолв по имени и фрагменты ─────────────────────────────────────────

    [Fact]
    public void ResolveByName_НаходитЗаметкуИФрагментПоЗаголовку()
    {
        WriteNote("Дока", "# Дока\n\n## Установка\nшаг один\nшаг два\n\n## Использование\nтекст");

        var r = _sut.ResolveByName(User, "Дока", "Установка");
        r.Should().NotBeNull();
        r!.Value.Note.Title.Should().Be("Дока");
        r.Value.Fragment.Should().Contain("шаг один").And.Contain("шаг два")
            .And.NotContain("Использование");
    }

    [Fact]
    public void ExtractFragment_БлочнаяМетка()
    {
        const string content = "первый абзац\n\nвторой абзац с меткой ^block1\n\nтретий";
        NotesService.ExtractFragment(content, "^block1")
            .Should().Be("второй абзац с меткой ^block1");
        NotesService.ExtractFragment(content, "нет такого заголовка").Should().BeNull();
    }

    [Fact]
    public void ExtractFragment_СекцияДоКонцаФайла()
    {
        const string content = "# Т\n\n## Последняя\nхвост файла";
        NotesService.ExtractFragment(content, "Последняя")
            .Should().Contain("хвост файла");
    }

    [Fact]
    public void LinkMention_РегистрОтличается_СохраняетАлиас()
    {
        WriteNote("Гамма", "# Гамма");
        var note = _sut.Create(User, new CreateNoteRequest("Обзор2", "тут гамма строчными.", "personal"));

        var updated = _sut.LinkMention(User, note.Id, "Гамма")!;
        updated.Content.Should().Contain("[[Гамма|гамма]]");
    }
}
