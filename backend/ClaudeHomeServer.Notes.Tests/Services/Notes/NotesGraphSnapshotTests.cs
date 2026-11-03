using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// S3-1 фичи LLM-wiki: внутренний снимок графа NotesGraphSnapshot (для линтера S3-2
// без повторного построения). Проверяем: совпадение состава с публичным GetGraph на
// фикстуре (10 заметок + 2 страницы вики), отсутствие повторного построения при живом
// кэше, неизменяемость выданного снимка после мутации графа и изоляцию по владельцу.
public class NotesGraphSnapshotTests : IDisposable
{
    private const string User = "u1";
    private const string Other = "u2";
    private readonly string _dir;     // dataDir
    private readonly string _vault;   // {dir}/notes/{User} — личный vault
    private readonly string _pages;   // {dir}/wiki/{User}/pages — корень источника wiki
    private readonly NotesService _sut;

    public NotesGraphSnapshotTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "notes_graph_snapshot_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _vault = Path.Combine(_dir, "notes", User);
        Directory.CreateDirectory(_vault);
        _pages = WikiPaths.Pages(_dir, User);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(config, users, new AppSettingsService(config));
        // Источник wiki в графе гейтится флагом llm-wiki (хвост ревью S2e) — фикстура с включённым.
        _sut = new NotesService(projects, config, NullLogger<NotesService>.Instance, flags: new WikiFlagGate());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // Фикстура: 10 заметок личного vault + 2 страницы вики заведённой вики.
    // 12 реальных узлов + 2 призрака («Призрак альфа» — на него ссылаются две заметки,
    // «Призрак бета» — одна), 11 рёбер (9 между заметками + 2 от «Страницы раз»).
    private void SeedGraph()
    {
        InitWiki();
        WriteNote("Заметка один", "[[Заметка два]] и [[Заметка три]].");
        WriteNote("Заметка два", "[[Заметка три]].");
        WriteNote("Заметка три", "Нет цели: [[Призрак альфа]].");
        WriteNote("Заметка четыре", "Та же несозданная цель: [[Призрак альфа]].");
        WriteNote("Заметка пять", "[[Заметка один]].");
        WriteNote("Заметка шесть", "Текст без ссылок.");
        WriteNote("Заметка семь", "[[Заметка восемь]].");
        WriteNote("Заметка восемь", "Нет цели: [[Призрак бета]].");
        WriteNote("Заметка девять", "Текст без ссылок.");
        WriteNote("Заметка десять", "[[Заметка девять]].");
        WriteWikiPage("Раздел", "Страница раз", "[[Страница два]] и [[Заметка один]].");
        WriteWikiPage("Раздел", "Страница два", "Текст страницы.");
    }

    // ─── c1: снимок = публичный граф по составу ───────────────────────────────

    [Fact]
    public void Снимок_СовпадаетСПубличнымГрафом_ЧислаУзловРёберИПризраков()
    {
        SeedGraph();

        var graph = _sut.GetGraph(User);
        var snapshot = _sut.GetGraphSnapshot(User);

        // Фикстура нетривиальна: 14 узлов (12 реальных + 2 призрака) и 11 рёбер —
        // иначе сравнение «по нулям» ничего не проверяет.
        graph.Nodes.Should().HaveCount(14);
        graph.Edges.Should().HaveCount(11);

        snapshot.Nodes.Should().HaveCount(graph.Nodes.Count);
        snapshot.Edges.Should().HaveCount(graph.Edges.Count);
        snapshot.Ghosts.Should().HaveCount(graph.Nodes.Count(n => n.Ghost));

        snapshot.Nodes.Select(n => n.Id).OrderBy(x => x, StringComparer.Ordinal)
            .Should().Equal(graph.Nodes.Select(n => n.Id).OrderBy(x => x, StringComparer.Ordinal),
            "снимок — то же покрытие узлов, что публичный граф");
        snapshot.Edges.Select(e => (e.Source, e.Target))
            .Should().BeEquivalentTo(graph.Edges.Select(e => (e.Source, e.Target)));

        // узлы несут источник: реальные — свой SourceKey/SourceLabel, призраки — без него
        snapshot.Nodes.Where(n => !n.Ghost).Should().OnlyContain(n => n.SourceKey.Length > 0 && n.SourceLabel.Length > 0);
        snapshot.Nodes.Where(n => n.Ghost).Should().OnlyContain(n => n.SourceKey == "" && n.SourceLabel == "");

        // обратные ссылки снимка — те же, что у публичной детали
        var twoId = graph.Nodes.Single(n => n.Title == "Заметка два").Id;
        snapshot.Backlinks[twoId].Should().ContainSingle(b => b.SourceTitle == "Заметка один");
    }

    // ─── c2: живой кэш — второго построения нет ───────────────────────────────

    [Fact]
    public void ДваСнимкаПриЖивомКэше_ГрафСтроитсяОдинРаз()
    {
        SeedGraph();

        var first = _sut.GetGraphSnapshot(User);
        var second = _sut.GetGraphSnapshot(User);

        _sut.ModelBuildCount.Should().Be(1,
            "второй снимок обязан взять живый кэш: повторное построение графа — то, чего S3-1 не допускает");
        second.Nodes.Should().HaveCount(first.Nodes.Count);
        second.Edges.Should().HaveCount(first.Edges.Count);
    }

    // ─── неизменяемость выданного снимка ──────────────────────────────────────

    [Fact]
    public void МутацияГрафаПослеВыдачи_ВыданныйСнимокНеМеняется()
    {
        SeedGraph();
        var snapshot = _sut.GetGraphSnapshot(User);
        var nodesBefore = snapshot.Nodes.Count;
        var edgesBefore = snapshot.Edges.Count;
        var ghostsBefore = snapshot.Ghosts.Count;

        _sut.Create(User, new CreateNoteRequest("Добавленная", "ссылка [[Заметка один]].", "personal"));

        var after = _sut.GetGraphSnapshot(User);
        after.Nodes.Should().HaveCount(nodesBefore + 1, "перестроение видно в новом снимке");
        after.Nodes.Should().Contain(n => n.Title == "Добавленная");

        // выданный ранее снимок заморожен на момент выдачи: тот же состав, ни нового узла
        snapshot.Nodes.Should().HaveCount(nodesBefore);
        snapshot.Edges.Should().HaveCount(edgesBefore);
        snapshot.Ghosts.Should().HaveCount(ghostsBefore);
        snapshot.Nodes.Should().NotContain(n => n.Title == "Добавленная");
    }

    // ─── M1/M2 ревью S3-1: коллекции снимка — копии, а не заморозка ────────────

    // Снимок не обещает неизменяемых контейнеров (NotesGraphSnapshot.cs: «копии, а не
    // заморозка»): под IReadOnlyList/IReadOnlyDictionary лежат массив и словарь. Проверяем
    // то, что действительно должно держаться: правка коллекции выданного снимка не трогает
    // модель и следующий снимок. Мутация «отдать model.Backlinks / model.Ghosts без копии»
    // краснит этот тест.
    [Fact]
    public void ПравкаКоллекцийВыданногоСнимка_НеТрагаетсяСледующийСнимок()
    {
        SeedGraph();
        var first = _sut.GetGraphSnapshot(User);
        var targetId = first.Nodes.Single(n => n.Title == "Заметка два").Id;
        var ghostId = first.Ghosts.Keys.First(k => first.Ghosts[k] == "Призрак альфа");

        var backlinks = (NoteBacklinkDto[])first.Backlinks[targetId];
        backlinks[0] = backlinks[0] with { SourceTitle = "Правка в копии" };
        var ghosts = (Dictionary<string, string>)first.Ghosts;
        ghosts[ghostId] = "Правка в копии";

        var second = _sut.GetGraphSnapshot(User);

        second.Backlinks[targetId].Select(b => b.SourceTitle).Should().NotContain("Правка в копии",
            "обратные ссылки модели не могут быть поправлены читателем графа");
        second.Ghosts[ghostId].Should().Be("Призрак альфа", "имя призрака — данные модели, не общий контейнер");
        first.Backlinks[targetId].Should().Contain(b => b.SourceTitle == "Правка в копии",
            "контейнер снимка изменяем: правка остаётся в этом снимке (М2 — «копии», а не заморозка)");
    }

    // ─── изоляция per-owner ────────────────────────────────────────────────────

    [Fact]
    public void СнимокСтрогоПоВладельцу_ЧужойВладелецНеВидитУзлов()
    {
        SeedGraph();
        _sut.Create(Other, new CreateNoteRequest("Чужая заметка", "своё", "personal"));

        var mine = _sut.GetGraphSnapshot(User);
        var other = _sut.GetGraphSnapshot(Other);

        other.Nodes.Should().ContainSingle(n => n.Title == "Чужая заметка");
        mine.Nodes.Should().NotContain(n => n.Title == "Чужая заметка");
        other.Nodes.Select(n => n.Id).Should().NotIntersectWith(mine.Nodes.Select(n => n.Id),
            "граф per-owner: чужой владелец не видит ни одного узла");
    }

    // ─── хелперы фикстуры ─────────────────────────────────────────────────────

    private void WriteNote(string name, string content) =>
        File.WriteAllText(Path.Combine(_vault, name + ".md"), content, new UTF8Encoding(false));

    private void InitWiki()
    {
        Directory.CreateDirectory(_pages);
        File.WriteAllText(Path.Combine(_pages, WikiPaths.SchemaFileName),
            "# Схема раздела\n", new UTF8Encoding(false));
    }

    private void WriteWikiPage(string section, string title, string content)
    {
        var dir = Path.Combine(_pages, section);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, title + ".md"), content, new UTF8Encoding(false));
    }

    private sealed class WikiFlagGate : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }
}
