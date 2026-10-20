using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Маска «вне кода» (дефект 7928ce94, ADR-035 «Зависимость 0»): ссылки, теги и
// [[…]]-переписывание внутри `…`/```-блоков не считаются (поведение Obsidian);
// чисто числовой #807 — не тег. Обычные ссылки/теги работают как раньше.
public class NotesCodeMaskingTests : IDisposable
{
    private const string User = "u1";
    private readonly string _dir;
    private readonly string _vault;
    private readonly ProjectManager _projects;
    private readonly NotesService _sut;

    public NotesCodeMaskingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "notes_codemask_" + Guid.NewGuid().ToString("N"));
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

    // ─── Ссылки в коде ──────────────────────────────────────────────────────

    [Fact]
    public void Wikilink_ВInlineCode_НеСоздаётСсылкуИПризрака()
    {
        // «Страница» — не существующая заметка: без маски `[[Проект/Страница]]` в `…`
        // дала бы ghost «Страница» (реальный кейс заметки «Целевое видение — LLM-wiki»)
        WriteNote("Обзор", "Ссылки работают через `[[Проект/Страница]]` и всё.");

        _sut.GetDetail(User, IdOf("Обзор"))!.Links.Should().BeEmpty();

        var graph = _sut.GetGraph(User);
        graph.Nodes.Should().NotContain(n => n.Ghost && n.Title == "Страница");
    }

    [Fact]
    public void Wikilink_ВFencedBlock_НеСоздаётСсылкуИПризрака()
    {
        WriteNote("Обзор", "Пример:\n```\n[[Проект/Страница]]\n```\nконец");

        _sut.GetDetail(User, IdOf("Обзор"))!.Links.Should().BeEmpty();

        var graph = _sut.GetGraph(User);
        graph.Nodes.Should().NotContain(n => n.Ghost && n.Title == "Страница");
    }

    [Fact]
    public void Wikilink_Обычный_РаботаетКакРаньше()
    {
        WriteNote("Цель", "# Цель");
        // Живая ссылка + та же в inline-коде: считается только живая, дедупликация по цели
        WriteNote("Исток", "Смотри [[Цель]] и `[[Цель]]` в коде.");

        var links = _sut.GetDetail(User, IdOf("Исток"))!.Links;
        links.Should().ContainSingle(l => l.Resolved && l.TargetTitle == "Цель");
    }

    // ─── Теги в коде ────────────────────────────────────────────────────────

    [Fact]
    public void Tag_ВInlineCode_НеСчитается()
    {
        WriteNote("Тег", "пример `#кодовый` и живой #реальный");

        var tags = _sut.GetSummaries(User, null, null).Single().Tags;
        tags.Should().Contain("реальный");
        tags.Should().NotContain("кодовый");
    }

    [Fact]
    public void Tag_ВFencedBlock_НеСчитается()
    {
        WriteNote("Тег", "блок:\n```\n#внутри\n```\nи #снаружи");

        var tags = _sut.GetSummaries(User, null, null).Single().Tags;
        tags.Should().Contain("снаружи");
        tags.Should().NotContain("внутри");
    }

    [Fact]
    public void Tag_ЧистоЧисловой_НеСчитается()
    {
        WriteNote("Тег", "см. [Semantic-Anchors #807](https://example.com) и #текстовый");

        var tags = _sut.GetSummaries(User, null, null).Single().Tags;
        tags.Should().Contain("текстовый");
        tags.Should().NotContain("807");
    }

    // ─── RewriteLinks: переименование не переписывает код ───────────────────

    [Fact]
    public void Rename_НеПереписываетWikilinkВКод()
    {
        var target = _sut.Create(User, new CreateNoteRequest("Старое", "целевая", "personal"));
        // [[Старое]] живая ссылка + `[[Старое]]` в inline-коде
        _sut.Create(User, new CreateNoteRequest("Источник", "пример `[[Старое]]` и [[Старое]]", "personal"));

        _sut.Update(User, target.Id, new UpdateNoteRequest(Title: "Новое"));

        var src = _sut.GetDetail(User, IdOf("Источник"))!;
        src.Content.Should().Contain("[[Новое]]");                 // живая ссылка переписана
        src.Content.Should().Contain("`[[Старое]]`");            // код не тронут
        src.Links.Should().ContainSingle(l => l.Resolved && l.TargetTitle == "Новое");
    }

    // ─── Unit-тесты маски (без vault) ───────────────────────────────────────

    [Fact]
    public void CodeMask_ПозицииВInlineCodeИFence_Помечены()
    {
        var text = "тут `[[x]]` и\n```\n[[y]]\n```\nконец";
        var mask = NotesService.CodeMask(text);

        // `[[x]]` — вся inline-код-скобка в маске
        var inlineStart = text.IndexOf("`[[x]]`");
        for (var i = 0; i < "`[[x]]`".Length; i++)
            mask[inlineStart + i].Should().BeTrue($"позиция {inlineStart + i} в inline-коде");

        // [[y]] внутри fence — в маске
        var fenced = text.IndexOf("[[y]]");
        for (var i = 0; i < "[[y]]".Length; i++)
            mask[fenced + i].Should().BeTrue($"позиция {fenced + i} в fence");

        // «конец» — вне кода
        var tail = text.IndexOf("конец");
        for (var i = 0; i < "конец".Length; i++)
            mask[tail + i].Should().BeFalse($"позиция {tail + i} вне кода");
    }

    [Fact]
    public void CodeMask_ОбычныйТекстБезКода_ВсяВнеМаски()
    {
        var text = "просто текст без кода и #тега";
        var mask = NotesService.CodeMask(text);
        mask.Should().NotContain(true);
    }
}
