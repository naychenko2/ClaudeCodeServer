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
        // Живая ссылка + та же в inline-коде. Различаем «ссылка в коде исключена» и
        // «дубль схлопнут дедупом по цели» по бэклинкам цели: backlinks.Add идёт на
        // каждую сырую ссылку (Build, :392-393), дедуплицируются только исходящие.
        // Без маски у «Цели» было бы 2 бэклика от «Истка».
        WriteNote("Исток", "Смотри [[Цель]] и `[[Цель]]` в коде.");

        var links = _sut.GetDetail(User, IdOf("Исток"))!.Links;
        links.Should().ContainSingle(l => l.Resolved && l.TargetTitle == "Цель");

        _sut.GetDetail(User, IdOf("Цель"))!.Backlinks
            .Should().ContainSingle(b => b.SourceTitle == "Исток");
    }

    // ─── Теги в коде ────────────────────────────────────────────────────────

    [Fact]
    public void Tag_ВInlineCode_НеСчитается()
    {
        // «#» в коде должен стоять после пробела: InlineTag требует границы слова
        // (?<=^|\s), :27). В старом случае «`#кодовый`» тег не находился и без маски —
        // тест ничего не проверял. Здесь тег реально находится без маски и отбивается маской.
        WriteNote("Тег", "пример `x #тег y` и живой #реальный");

        var tags = _sut.GetSummaries(User, null, null).Single().Tags;
        tags.Should().Contain("реальный");
        tags.Should().NotContain("тег");
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

    // ─── RemoveWikilinksTo: вычищание ссылки при удалении цели ───────────────

    [Fact]
    public void RemoveWikilinksTo_ВычищаетВОбычномТексте_НоНеВКоде()
    {
        const string original = "живая [[Цель]] в тексте, `[[Цель]]` в inline-коде и в заборе:\n```\n[[Цель]]\n```\n";
        WriteNote("Цель", "# Цель");
        WriteNote("Исток", original);

        _sut.RemoveWikilinksTo(User, "Цель", IdOf("Цель"));

        // Файл = исходное содержимое с ровно одной заменой — той, что вне кода
        File.ReadAllText(Path.Combine(_vault, "Исток.md"))
            .Should().Be(original.Replace("живая [[Цель]] в тексте", "живая Цель в тексте"));
    }

    // ─── Несвязанные упоминания и LinkMention ────────────────────────────────

    [Fact]
    public void UnlinkedMention_ВКоде_НеСчитается_и_LinkMention_НеСвязывает()
    {
        // Все файлы пишем до первого обращения к модели: NotesService кэширует её на 2 с,
        // внешняя правка файла кэш не инвалидирует.
        WriteNote("Важная Тема", "# Важная Тема");
        const string inCode = "про пример: `там Важная Тема в коде` всё.";
        WriteNote("ИстокВКоде", inCode);
        WriteNote("ИстокВнеКода", "упомянута Важная Тема в обычном тексте");

        // Только внутри кода: не несвязанное упоминание, и связывать нечем (FindWord, :445-459)
        // (в UnlinkedMentions SourceId/SourceTitle — это упомянутая заметка, не исток)
        var masked = _sut.GetDetail(User, IdOf("ИстокВКоде"))!;
        masked.UnlinkedMentions.Should().NotContain(m => m.SourceTitle == "Важная Тема");
        _sut.LinkMention(User, masked.Id, "Важная Тема")!.Content.Should().Be(inCode);

        // Вне кода: упоминание есть, LinkMention (:784-803) оборачивает его в [[…]]
        var plain = _sut.GetDetail(User, IdOf("ИстокВнеКода"))!;
        plain.UnlinkedMentions.Should().Contain(m => m.SourceTitle == "Важная Тема");
        _sut.LinkMention(User, plain.Id, "Важная Тема")!.Content.Should().Contain("[[Важная Тема]]");
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
