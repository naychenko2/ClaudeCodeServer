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
        var users = new UserStore(config, NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projects = new ProjectManager(config, users, appSettings);
        _sut = new NotesService(projects, config, NullLogger<NotesService>.Instance);
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
}
