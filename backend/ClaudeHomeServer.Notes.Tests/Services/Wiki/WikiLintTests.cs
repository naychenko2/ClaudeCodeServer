using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// S3-2 фичи LLM-wiki: механический lint раздела по снимку графа (ADR-035, решение 2 и
// «Последствия»; ADR-036, решения 1, 4, 5). Фикстуры — готовые снимки: проверяется чистая
// функция, без диска и без модели. Одна фикстура — ровно одна находка нужного вида и ни
// одной лишней: иначе фикстура не различает виды.
//
// Решения Софьи по ревью S3-1 проверены здесь же: M3 (аннотации вне находок и метрик),
// M4 (призраки — все узлы снимка), недоверенный ввод (заголовки — только данные, не разметка
// сообщения), детерминизм порядка и совпадение разбора id с живым NotesService.
public class WikiLintTests
{
    private const string Sec = "projects/p1";
    private const string Sec2 = "projects/p2";
    private const string OldSphere = "spheres/s9";
    private const string P1 = "Проекты/p1/Порядок-работ.md";
    private const string P2 = "Проекты/p1/Состав-команды.md";
    private const string Card = "Проекты/p1/Источники/Источник-работ.md";

    // Единственное время теста: everything is relative to it.
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    // ─── c3: по одной фикстуре на каждый из 10 видов находок ───────────────────

    [Fact]
    public void Призрак_НаНегоСсылаютсяТриСтраницыРаздела_РовноОднаНаходка()
    {
        var newPage = "Проекты/p1/Порядок-встреч.md";
        // Третья страница фона: связана входящей ссылкой (иначе сама стала бы сиротой) и
        // вместе с двумя другими ссылается на одну и ту же несуществующую цель.
        var f = Background()
            .Page(newPage, "Порядок встреч")
            .Link(newPage, P1)
            .Link(P2, newPage)
            .Ghost(P1, "Нет такой цели")
            .Ghost(P2, "Нет такой цели")
            .Ghost(newPage, "Нет такой цели");

        var finding = Single(f.Run(), WikiFindingKind.DanglingTarget);
        finding.Count.Should().Be(3);
        finding.Related.Should().HaveCount(3);
        finding.Value.Should().Be("Нет такой цели");
    }

    [Fact]
    public void Призрак_ДвеСсылки_НеНаходка()
    {
        var f = Background()
            .Ghost(P1, "Порядок встреч")
            .Ghost(P2, "Порядок встреч");

        f.Run().Findings.Should().BeEmpty();
    }

    [Fact]
    public void СтраницаБезВходящихСсылок_РовноОднаНаходкаСироты()
    {
        var f = new Fixture()
            .Page(P1, "Порядок работ", sources: ["Источники/Источник-работ"])
            .Page(Card, "Источник работ", type: WikiFrontmatter.TypeSource, sources: ["Источники/Источник-работ"])
            .Link(P1, Card);

        var finding = Single(f.Run(), WikiFindingKind.Orphan);
        finding.PageRelPath.Should().Be(P1);
    }

    [Fact]
    public void ЗаголовокДругойСтраницыВТекстеБезСсылки_РовноОднаНаходка()
    {
        var f = Background()
            .Page("Проекты/p1/Порядок-встреч.md", "Порядок встреч")
            .Link(P2, "Проекты/p1/Порядок-встреч.md")
            .Text(P1, "Обсудили Порядок встреч и записали решение.");

        var finding = Single(f.Run(), WikiFindingKind.UnlinkedMention);
        finding.PageRelPath.Should().Be(P1);
        finding.Value.Should().Be("Порядок встреч");
    }

    [Fact]
    public void УпоминаниеВнутриКода_НеНаходка()
    {
        var f = Background()
            .Page("Проекты/p1/Порядок-встреч.md", "Порядок встреч")
            .Link(P2, "Проекты/p1/Порядок-встреч.md")
            .Text(P1, "Пример синтаксиса: `Порядок встреч` — не упоминание.");

        f.Run().Findings.Should().BeEmpty();
    }

    [Fact]
    public void ЗаголовкиДвухСтраницНаРасстоянииДва_РовноОднаНаходкаДубля()
    {
        var f = new Fixture()
            .Page(P1, "Порядок работ")
            .Page("Проекты/p1/Порядок-работ-2.md", "Порядок работ 2", text: "Копия той же страницы.")
            .Link(P1, "Проекты/p1/Порядок-работ-2.md")
            .Link("Проекты/p1/Порядок-работ-2.md", P1);

        var finding = Single(f.Run(), WikiFindingKind.NearDuplicate);
        finding.Count.Should().Be(2);
        // Порядок пары — по пути (Ordinal): «-2.md» идёт раньше «.md» — '-' меньше '.'.
        finding.Related.Should().Equal("Проекты/p1/Порядок-работ-2.md", P1);
    }

    [Fact]
    public void РасстояниеТри_НеНаходка()
    {
        var f = new Fixture()
            .Page(P1, "Порядок работ")
            .Page("Проекты/p1/Порядок-работ-2.md", "Порядка работ 23", text: "Копия той же страницы.")
            .Link(P1, "Проекты/p1/Порядок-работ-2.md")
            .Link("Проекты/p1/Порядок-работ-2.md", P1);

        f.Run().Findings.Should().BeEmpty();
    }

    [Fact]
    public void СтраницаЗнанийБезИсточников_РовноОднаНаходка()
    {
        var f = new Fixture()
            .Page(P1, "Порядок работ", sources: [])
            .Page(P2, "Состав команды", sources: ["Источники/Источник-работ"])
            .Link(P1, P2)
            .Link(P2, P1);

        var finding = Single(f.Run(), WikiFindingKind.MissingSources);
        finding.PageRelPath.Should().Be(P1);
    }

    [Fact]
    public void ОдинOwnsУДвухСтраниц_РовноОднаНаходка()
    {
        var f = Background()
            .Owns(P1, "порядок работ")
            .Owns(P2, "порядок работ");

        var finding = Single(f.Run(), WikiFindingKind.OwnsConflict);
        finding.Value.Should().Be("порядок работ");
        finding.Related.Should().Equal(P1, P2);
    }

    [Fact]
    public void СтраницыНетВМанифеста_РовноОднаНаходкаДрейфа()
    {
        var f = Background().NoManifest(P2);

        var finding = Single(f.Run(), WikiFindingKind.Drift);
        finding.PageRelPath.Should().Be(P2);
    }

    [Fact]
    public void МанифестНечитаем_ОднаНаходкаОРазделе()
    {
        var f = Background();

        var report = f.Run(WikiManifestState.Invalid);

        var finding = Single(report, WikiFindingKind.Drift);
        finding.PageRelPath.Should().BeNull();
        report.Findings.Should().ContainSingle();
    }

    [Fact]
    public void БезИзмененийРовноДевяностоДней_РовноОднаНаходка()
    {
        var f = Background().Updated(P1, Now.AddDays(-90));

        var finding = Single(f.Run(), WikiFindingKind.Stale);
        finding.PageRelPath.Should().Be(P1);
        finding.Count.Should().Be(90);
    }

    [Fact]
    public void БезИзмененийВосемьдесятДевятьДней_НеНаходка()
    {
        var f = Background().Updated(P1, Now.AddDays(-89));

        f.Run().Findings.Should().BeEmpty();
    }

    // ─── c1: смена сферы и предложение повышения (ADR-036, решения 4 и 5) ──────

    [Fact]
    public void ПроектСменилСферу_СсылкаНаСтраницуПрежнейСферыДаётOutOfReach()
    {
        // Цепочка вверх строится по ТЕКУЩЕЙ сфере: прежняя сфера в неё не входит (решение 2:
        // «Проект сменил сферу — видимость его раздела меняется со следующего вызова»).
        var f = Background()
            .Page("Сферы/s9/Порядок-встреч.md", "Порядок встреч")
            .Link(P1, "Сферы/s9/Порядок-встреч.md");

        var finding = Single(f.Run(), WikiFindingKind.OutOfReach);
        finding.PageRelPath.Should().Be(P1);
        finding.Value.Should().Be(OldSphere);
    }

    [Fact]
    public void СсылкаНаСтраницуСвоейСферыИНаОбщее_НеНаходка()
    {
        var f = Background()
            .Page("Сферы/s7/Порядок-встреч.md", "Порядок встреч")
            .Page("Общее/Порядок-смены.md", "Порядок смены")
            .Link(P1, "Сферы/s7/Порядок-встреч.md")
            .Link(P1, "Общее/Порядок-смены.md")
            .Reach("spheres/s7", "common");

        f.Run().Findings.Should().BeEmpty();
    }

    [Fact]
    public void ТаЖеСущностьВДвухПроектахСферы_ПредложениеПовышения()
    {
        var f = Background()
            .Page("Проекты/p2/Порядок-работ.md", "Порядок работ")
            .Link("Проекты/p2/Порядок-работ.md", P2)
            .InSphere(Sec2);

        var finding = Single(f.Run(), WikiFindingKind.PromoteSuggestion);
        finding.Related.Should().Equal(P1, "Проекты/p2/Порядок-работ.md");
        finding.Count.Should().Be(2);
    }

    [Fact]
    public void ТотЖеАлиасВДвухПроектах_ПредложениеПовышения()
    {
        var f = Background()
            .Page("Проекты/p2/Рабочий-порядок.md", "Рабочий порядок", aliases: ["Порядок работ"])
            .Link("Проекты/p2/Рабочий-порядок.md", P2)
            .InSphere(Sec2);

        Single(f.Run(), WikiFindingKind.PromoteSuggestion).Value.Should().Be("порядок работ");
    }

    [Fact]
    public void ДваПроектаЦитируютИсточникСОднимХэшем_ПредложениеПовышения()
    {
        // Карточка второго проекта названа иначе: та же сущность по заголовку дала бы
        // второе предложение повышения другим механизмом — фикстура проверяет хэш.
        var f = Background()
            .Page("Проекты/p2/Источники/Источник-работ.md", "Копия источника",
                type: WikiFrontmatter.TypeSource, hash: "a1b2c3")
            .Page("Проекты/p2/Порядок-встреч.md", "Порядок встреч", sources: ["Источники/Источник-работ"])
            .Link("Проекты/p2/Порядок-встреч.md", "Проекты/p2/Источники/Источник-работ.md")
            .Link("Проекты/p2/Порядок-встреч.md", P2)
            .Hash(Card, "a1b2c3")
            .InSphere(Sec2);

        var finding = Single(f.Run(), WikiFindingKind.PromoteSuggestion);
        finding.Value.Should().Be("a1b2c3");
        finding.Related.Should().Equal(Card, "Проекты/p2/Источники/Источник-работ.md");
    }

    [Fact]
    public void СферыНет_ПредложенияПовышенияНеСтроятся()
    {
        var f = Background()
            .Page("Проекты/p2/Порядок-работ.md", "Порядок работ")
            .Link("Проекты/p2/Порядок-работ.md", P2);

        f.Run().Findings.Should().BeEmpty();
    }

    // ─── c2: человеческие заметки — вне находок, внутри метрик ─────────────────

    [Fact]
    public void ЗаметкиЧеловека_НиОднойНаходки_МетрикиСчитаютсяПоВсейБазе()
    {
        // Сирота-заметка человека, её призрачная цель и её же сирота: ни одна из них не
        // относится к страницам вики раздела — и ни одна не должна стать находкой.
        var f = Background()
            .Note("Идеи/Заметка человека.md", "Заметка человека")
            .Note("Идеи/Вторая заметка.md", "Вторая заметка")
            .Note("Journal/2026-01-01.md", "Дневник")
            .NoteGhost("Идеи/Заметка человека.md", "Нет такой цели");

        var report = f.Run();

        report.Findings.Should().BeEmpty(
            "проблемы — только по страницам вики раздела (ADR-035:325): человеческие заметки — не её страницы");
        report.Metrics.NoteCount.Should().Be(6, "метрики — по всей базе: 3 страницы вики + 3 заметки человека");
        report.Metrics.OrphanCount.Should().Be(3, "сироты базы: обе заметки человека и дневник");
        report.Metrics.OrphanCountExcludingJournal.Should().Be(2, "дневник из сирот исключается, как в базовой линии");
        report.Metrics.DanglingTargetCount.Should().Be(1);
        report.Metrics.WikiPageCount.Should().Be(3);
    }

    // ─── M3: аннотации и узлы-комментарии — вне находок и метрик ───────────────

    [Fact]
    public void УзелКомментарийБезРёбер_НеСирота_ИВнеМетрик()
    {
        var f = Background().Page("Проекты/p1/Комментарий.md", "Комментарий к документу").AsAnnotation("Проекты/p1/Комментарий.md");

        var report = f.Run();

        report.Findings.Should().BeEmpty(
            "синтезируемых рёбер «комментарий → документ» в снимке нет: по снимку он выглядел бы сиротой, которой не является (M3)");
        report.Metrics.NoteCount.Should().Be(3, "аннотация — не заметка базовой линии");
    }

    // ─── M4: призраки — все узлы снимка, а не только со степенью > 0 ────────────

    [Fact]
    public void ПризракБезРёбер_УчитываетсяВМетрикеПризраков()
    {
        var f = Background();
        var orphan = new NotesGraphSnapshotNode("ghost:ни-кого-не-ссылается", "Никто не ссылается", "", "", true);
        f.Nodes.Add(orphan);
        f.Ghosts["ghost:ни-кого-не-ссылается"] = "Никто не ссылается";

        var metrics = f.Run().Metrics;

        metrics.DanglingLinkCount.Should().Be(0, "у этого призрака нет ни одного ребра");
        metrics.DanglingTargetCount.Should().Be(1,
            "публичный граф отсекает призраков по степени > 0 (NotesService.cs:771), метрике нужен полный счёт снимка (M4)");
    }

    // ─── недоверенный ввод: заголовки — данные, а не разметка сообщения ─────────

    [Fact]
    public void ЗаголовокСРазметкой_НеПопадаетВСообщение_ИОстаётсяДаннымНаходки()
    {
        var dirty = "Сущность | [[Другая]] #тег `код`";
        var f = new Fixture()
            .Page(P1, dirty, sources: [])
            .Page(P2, "Состав команды", sources: ["Источники/Источник-работ"])
            .Link(P1, P2)
            .Link(P2, P1);

        var finding = Single(f.Run(), WikiFindingKind.MissingSources);

        finding.Message.Should().NotContain(dirty);
        finding.PageRelPath.Should().Be(P1);
    }

    // ─── детерминизм ────────────────────────────────────────────────────────────

    [Fact]
    public void ОдинИТотЖеСнимок_ОдинаковыйОтчётПриЛюбомПорядкеВхода()
    {
        var a = Background()
            .Page("Проекты/p1/Порядок-встреч.md", "Порядок встреч")
            .Page("Проекты/p1/Порядок-работ-2.md", "Порядка работ 23", text: "Копия.")
            .Link("Проекты/p1/Порядок-встреч.md", P1)
            .Owns(P1, "порядок работ")
            .Owns(P2, "порядок работ")
            .Owns("Проекты/p1/Порядок-встреч.md", "порядок работ")
            .Ghost(P1, "Нет такой цели")
            .Ghost(P2, "Нет такой цели")
            .NoManifest("Проекты/p1/Порядок-работ-2.md");
        var b = a.Reversed();

        // Сравнение — по сигнатуре: record с полями-списками равенства по содержанию не
        // даёт, а сравнивать надо именно порядок и состав находок.
        var first = a.Run().Findings.Select(Signature).ToList();
        var second = b.Run().Findings.Select(Signature).ToList();

        second.Should().Equal(first, "порядок находок — часть контракта: вид, путь, ключ, связанные");
        first.Should().HaveCountGreaterThan(1, "иначе сравнение порядков ничего не проверяет");
    }

    // ─── id узла: та же кодировка, что у живого NotesService ────────────────────

    [Fact]
    public void РазборIdЛинтера_СовпадаетСЖивымNotesService()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wiki_lint_id_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pages = WikiPaths.Pages(dir, "u1");
            Directory.CreateDirectory(Path.Combine(pages, "Проекты", "p1"));
            File.WriteAllText(Path.Combine(pages, WikiPaths.SchemaFileName), "# Схема\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(pages, "Проекты", "p1", "Порядок-работ.md"),
                "---\ntype: topic\naliases: []\nowns: []\nsources: []\n---\n# Порядок работ\nТекст.",
                new UTF8Encoding(false));

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(dir, "projects.json") })
                .Build();
            var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
            var projects = new ProjectManager(config, users, new AppSettingsService(config));
            var notes = new NotesService(projects, config, NullLogger<NotesService>.Instance, flags: new WikiFlagGate());

            var snapshot = notes.GetGraphSnapshot("u1");
            // Заголовок узла — имя файла без .md: своего title в шапке живой страницы нет.
            var page = snapshot.Nodes.Single(n => n.Title == "Порядок-работ");
            const string rel = "Проекты/p1/Порядок-работ.md";

            var report = WikiLint.Run(
                snapshot, Sec,
                new WikiManifestReadResult(WikiManifestState.Valid, new Dictionary<string, string> { [rel] = "sha" }),
                Now, new WikiLintContext([Sec], [], [],
                    [new WikiPageFacts(rel, "Порядок-работ", WikiFrontmatter.TypeTopic, [], [], [], Now, null, "Текст.")]));

            // Линтер опознаёт страницу по своему разбору id: не совпало бы с кодировкой
            // NotesService — не нашёл бы ни одной страницы и не нашёл бы ничего вовсе.
            report.Findings.Should().Contain(f => f.Kind == WikiFindingKind.Orphan && f.PageRelPath == rel);
            report.Findings.Should().Contain(f => f.Kind == WikiFindingKind.MissingSources && f.PageRelPath == rel);
            report.Metrics.WikiPageCount.Should().Be(1);
            page.Ghost.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ПустойСнимок_НулиМетрикИНиОднойНаходки()
    {
        var report = new Fixture().Run();

        report.Findings.Should().BeEmpty();
        report.Metrics.NoteCount.Should().Be(0);
        report.Metrics.DanglingLinkShare.Should().Be(0, "пустой знаменатель — 0, не деление на ноль");
        report.Metrics.OrphanShareExcludingJournal.Should().Be(0);
    }

    // ─── фикстура ───────────────────────────────────────────────────────────────

    // Фон, который сам по себе не даёт ни одной находки: две страницы знаний с источниками,
    // связанные встречно (ни одна не сирота), карточка источника, на которую есть ссылка,
    // манифест со всеми тремя, свежие updated. Дефект добавляется одним вызовом.
    private static Fixture Background() =>
        new Fixture()
            .Page(P1, "Порядок работ", sources: ["Источники/Источник-работ"])
            .Page(P2, "Состав команды", sources: ["Источники/Источник-работ"])
            .Page(Card, "Источник работ", type: WikiFrontmatter.TypeSource, sources: ["Источники/Источник-работ"])
            .Link(P1, P2)
            .Link(P2, P1)
            .Link(P1, Card);

    private static WikiFinding Single(WikiLintReport report, WikiFindingKind kind)
    {
        report.Findings.Should().ContainSingle(f => f.Kind == kind, $"ожидалась ровно одна находка вида {kind}");
        report.Findings.Should().HaveCount(1, "фикстура не должна порождать находок другого вида");
        return report.Findings[0];
    }

    private static string Signature(WikiFinding f) =>
        $"{f.Kind}|{f.PageRelPath}|{f.Value}|{f.Count}|{string.Join("|", f.Related)}|{f.Message}";

    private sealed class Fixture
    {
        public List<NotesGraphSnapshotNode> Nodes { get; } = [];
        public List<NotesGraphSnapshotEdge> Edges { get; } = [];
        public List<WikiPageFacts> Facts { get; } = [];
        public Dictionary<string, string> Manifest { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Ghosts { get; } = new(StringComparer.Ordinal);
        public List<string> Reachable { get; } = [Sec];
        public List<string> Sphere { get; } = [];
        public List<string> Annotations { get; } = [];

        public Fixture Page(
            string relPath,
            string title,
            string type = WikiFrontmatter.TypeTopic,
            string[]? aliases = null,
            string[]? owns = null,
            string[]? sources = null,
            DateTimeOffset? updated = null,
            string? hash = null,
            string? text = null)
        {
            // Текст по умолчанию намеренно без заголовка своей страницы: иначе каждая
            // фикстура давала бы «упоминание без ссылки» там, где его быть не должно.
            Nodes.Add(new NotesGraphSnapshotNode(IdOf(relPath), title, "wiki", "Вики", false));
            Facts.Add(new WikiPageFacts(
                relPath, title, type, aliases ?? [], owns ?? [], sources ?? ["Источники/Источник-работ"],
                updated ?? Now.AddDays(-1), hash, text ?? "Содержимое страницы без упоминаний других."));
            Manifest[relPath] = "sha-" + relPath;
            return this;
        }

        public Fixture Link(string fromRelPath, string toRelPath)
        {
            Edges.Add(new NotesGraphSnapshotEdge(IdOf(fromRelPath), IdOf(toRelPath)));
            return this;
        }

        public Fixture Ghost(string fromRelPath, string name)
        {
            var id = "ghost:" + name.ToLowerInvariant();
            Edges.Add(new NotesGraphSnapshotEdge(IdOf(fromRelPath), id));
            Ghosts[id] = name;
            return this;
        }

        public Fixture Note(string relPath, string title)
        {
            Nodes.Add(new NotesGraphSnapshotNode(IdOf("personal", relPath), title, "personal", "Личный", false));
            return this;
        }

        public Fixture NoteGhost(string fromRelPath, string name)
        {
            var id = "ghost:" + name.ToLowerInvariant();
            Edges.Add(new NotesGraphSnapshotEdge(IdOf("personal", fromRelPath), id));
            Ghosts[id] = name;
            return this;
        }

        public Fixture Text(string relPath, string text)
        {
            var index = Facts.FindIndex(f => f.RelPath == relPath);
            Facts[index] = Facts[index] with { Text = text };
            return this;
        }

        public Fixture Owns(string relPath, string owns)
        {
            var index = Facts.FindIndex(f => f.RelPath == relPath);
            Facts[index] = Facts[index] with { Owns = [owns] };
            return this;
        }

        public Fixture Hash(string relPath, string hash)
        {
            var index = Facts.FindIndex(f => f.RelPath == relPath);
            Facts[index] = Facts[index] with { Hash = hash };
            return this;
        }

        public Fixture Updated(string relPath, DateTimeOffset updated)
        {
            var index = Facts.FindIndex(f => f.RelPath == relPath);
            Facts[index] = Facts[index] with { Updated = updated };
            return this;
        }

        public Fixture NoManifest(string relPath)
        {
            Manifest.Remove(relPath);
            return this;
        }

        public Fixture Reach(params string[] sections)
        {
            Reachable.AddRange(sections);
            return this;
        }

        public Fixture InSphere(params string[] sections)
        {
            Sphere.AddRange(sections);
            return this;
        }

        public Fixture AsAnnotation(string relPath)
        {
            Annotations.Add(IdOf(relPath));
            return this;
        }

        // Тот же состав в обратном порядке: обход словарей и списков не должен менять отчёт.
        public Fixture Reversed()
        {
            var copy = new Fixture();
            foreach (var fact in Facts.AsEnumerable().Reverse())
                copy.Facts.Add(fact);
            foreach (var node in Nodes.AsEnumerable().Reverse())
                copy.Nodes.Add(node);
            foreach (var edge in Edges.AsEnumerable().Reverse())
                copy.Edges.Add(edge);
            foreach (var (k, v) in Manifest)
                copy.Manifest[k] = v;
            foreach (var (k, v) in Ghosts)
                copy.Ghosts[k] = v;
            copy.Reachable.AddRange(Reachable);
            copy.Sphere.AddRange(Sphere);
            copy.Annotations.AddRange(Annotations);
            return copy;
        }

        public NotesGraphSnapshot Snapshot() =>
            new(Nodes, Edges, Ghosts, new Dictionary<string, IReadOnlyList<NoteBacklinkDto>>());

        public WikiLintContext Context() => new(Reachable, Sphere, Annotations, Facts);

        public WikiLintReport Run(WikiManifestState state = WikiManifestState.Valid) =>
            WikiLint.Run(Snapshot(), Sec, new WikiManifestReadResult(state, Manifest), Now, Context());
    }

    // Тот же base64url, что у NotesService.EncodeId: его проверка — тестом ниже и тестом
    // «РазборIdЛинтера_СовпадаетСЖивымNotesService».
    private static string IdOf(string relPath) => IdOf("wiki", relPath);

    private static string IdOf(string sourceKey, string relPath) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sourceKey}|{relPath}"))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // Источник wiki в графе гейтится флагом llm-wiki (хвост ревью S2e) — живой тест с ним.
    private sealed class WikiFlagGate : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }
}
