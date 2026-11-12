using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Золотые тесты правила материала (FS-B0, волна 3 «Обзор фич», docs/research/
// features-simplify-architecture-2026-11.md §7.1): FeatureMaterials.IsMaterial / IsMockup —
// чистая функция без записи. Инвариант: код, тесты и файлы вне docs/ материалом не бывают
// никогда; индексы (CHANGELOG/README/index под docs/) и макеты (группа «Макеты») — тоже нет.
// Мутации: вернуть в IsMaterial принятие путей вне docs/ → строки backend/… краснеют;
// снять отброс README/index → их строки краснеют; снять отброс «..» → docs/../x.md краснеет.
public class FeatureMaterialsTests
{
    // ── IsMaterial: таблица путей ────────────────────────────────────────────────────────────

    [Theory]
    // Материал: файл под docs/, не индекс и не макет.
    [InlineData("docs/research/x.md", true)]
    [InlineData("docs/adr/y.md", true)]
    [InlineData("docs/architecture/z.md", true)]
    [InlineData("docs/features/f.md", true)]
    [InlineData("docs/operations/o.md", true)]
    [InlineData("docs/a/b/c/deep.md", true)]
    [InlineData("Docs/Research/X.MD", true)] // сравнение регистронезависимое
    // Не материал: код, тесты, скрипты, данные — всё вне docs/.
    [InlineData("backend/A.cs", false)]
    [InlineData("frontend/b.test.ts", false)]
    [InlineData("scripts/c.sh", false)]
    [InlineData("data/tasks.json", false)]
    [InlineData("README.md", false)] // корневой README — вне docs/
    [InlineData("mcp/x.py", false)]
    // Не материал: индексы.
    [InlineData("docs/CHANGELOG.md", false)]
    [InlineData("docs/README.md", false)]
    [InlineData("docs/guide/README.md", false)] // любой README.md под docs/
    [InlineData("docs/guide/index.md", false)] // любой index.md под docs/
    [InlineData("docs/Readme.MD", false)]
    // Не материал: макеты (группа «Макеты»).
    [InlineData("docs/mockups/m.html", false)]
    [InlineData("docs/mockups/sub/m.html", false)]
    [InlineData("docs/mockups", false)]
    // Не материал: «..», пустое, некорректное.
    [InlineData("docs/../x.md", false)]
    [InlineData("docs/adr/../leak.md", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("docs", false)] // не под docs/ (нет «/»)
    [InlineData("docs/", false)] // каталог, не файл
    public void IsMaterial_ТаблицаПутей(string path, bool expected) =>
        FeatureMaterials.IsMaterial(path).Should().Be(expected);

    [Fact]
    public void IsMaterial_Null_False() =>
        FeatureMaterials.IsMaterial(null).Should().BeFalse();

    // Граница префикса макетов: каталог «docs/mockupsx/» — не территория макетов, это материал.
    [Fact]
    public void IsMaterial_КаталогПохожийНаMockups_ЭтоМатериал() =>
        FeatureMaterials.IsMaterial("docs/mockupsx/x.html").Should().BeTrue();

    // ── IsMockup: таблица путей ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("docs/mockups/m.html", true)]
    [InlineData("docs/mockups/sub/m.html", true)]
    [InlineData("docs/mockups", true)]
    [InlineData("Docs/Mockups/M.HTML", true)]
    [InlineData("docs/mockupsx/x.html", false)] // не каталог макетов
    [InlineData("docs/research/x.md", false)]
    [InlineData("backend/A.cs", false)]
    [InlineData("", false)]
    public void IsMockup_ТаблицаПутей(string path, bool expected) =>
        FeatureMaterials.IsMockup(path).Should().Be(expected);

    [Fact]
    public void IsMockup_Null_False() =>
        FeatureMaterials.IsMockup(null).Should().BeFalse();

    // ── Cap: общий потолок на фичу ───────────────────────────────────────────────────────────

    [Fact]
    public void Cap_Равен30_ОбщийНаФичу() =>
        FeatureMaterials.Cap.Should().Be(30);

    // ── Build: materials[] по задачам фичи (FS-B3a, §7.4) ────────────────────────────────────

    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static TaskItem TaskOf(string id, string[] linkedFiles, DateTime updatedAt,
        DateTime? completedAt = null, string? projectId = null) =>
        new()
        {
            Id = id,
            ProjectId = projectId,
            LinkedFiles = [.. linkedFiles],
            UpdatedAt = updatedAt,
            CompletedAt = completedAt,
        };

    // Дубль пути у двух задач — один элемент, от самой свежей задачи (at = CompletedAt ?? UpdatedAt,
    // здесь — CompletedAt свежей). Дубль с другим регистром схлопнут (тот же регистронезависимый
    // ключ, что в IsMaterial), путь остаётся от свежей задачи — как она его записала.
    // Мутации: «не сравнивать at» (первый выигрывает) → TaskId t-old (краснеет);
    // «at = UpdatedAt без CompletedAt» → At ≠ CompletedAt (краснеет).
    [Fact]
    public void Build_ДубльПуза_ОдинЭлементОтСвежейЗадачи()
    {
        var old = TaskOf("t-old", ["docs/research/a.md", "docs/adr/b.md"], Base);
        var fresh = TaskOf("t-fresh", ["Docs/Research/A.MD"], Base.AddDays(1), completedAt: Base.AddDays(2));

        var got = FeatureMaterials.Build([old, fresh], null);

        got.Should().HaveCount(2, "дубль a.md схлопнут: a.md + b.md");
        got[0].TaskId.Should().Be("t-fresh", "свежая сверху");
        var a = got.Single(m => m.TaskId == "t-fresh");
        a.Path.Should().Be("Docs/Research/A.MD", "путь остаётся от свежей задачи");
        a.At.Should().Be(Base.AddDays(2), "at = CompletedAt свежей задачи");
        got[1].TaskId.Should().Be("t-old");
        got[1].At.Should().Be(Base, "без CompletedAt — at = UpdatedAt");
    }

    // Не материал — никогда: код, макеты, индексы — не попадают в Build (тот же IsMaterial,
    // что в таблице выше). Мутация «не звать IsMaterial» → backend/A.cs в выдаче (краснеет).
    [Fact]
    public void Build_КодИМакетыНеПопадают()
    {
        var t = TaskOf("t1",
            ["backend/A.cs", "docs/mockups/m.html", "docs/README.md", "docs/CHANGELOG.md", "docs/research/ok.md"],
            Base);

        FeatureMaterials.Build([t], null).Should().ContainSingle()
            .Which.Path.Should().Be("docs/research/ok.md");
    }

    // Потолок Cap=30 на всю фичу: 35 путей → 30, остались самые свежие по at, порядок — at
    // убыванием. Граница: ровно 30 путей → все 30 (ничего не отрезано). Пустой вход — пустой
    // выход. Мутация «снять Take(Cap)» → 35 (краснеет); «порядок по at возрастанием» →
    // первый p00 (краснеет).
    [Fact]
    public void Build_Потолок30_СвежиеСверху()
    {
        var tasks = Enumerable.Range(0, 35)
            .Select(i => TaskOf($"t{i}", [$"docs/research/p{i:D2}.md"], Base.AddMinutes(i)))
            .ToList();

        var got = FeatureMaterials.Build(tasks, null);

        got.Should().HaveCount(FeatureMaterials.Cap);
        got.Select(m => m.At).Should().BeInDescendingOrder();
        got.First().Path.Should().Be("docs/research/p34.md");
        got.Last().Path.Should().Be("docs/research/p05.md", "отрезаны 5 самых старых");

        var exactly30 = Enumerable.Range(0, 30)
            .Select(i => TaskOf($"e{i}", [$"docs/research/q{i:D2}.md"], Base.AddMinutes(i)))
            .ToList();
        FeatureMaterials.Build(exactly30, null).Should().HaveCount(30, "граница потолка: ровно 30 — все на месте");

        FeatureMaterials.Build([], null).Should().BeEmpty();
    }

    // Фильтр по проекту эпика — как в AllLinkedFiles: задача чужого проекта не даёт путь, даже
    // свежее своей. Эпик без проекта (null) — только задачи без проекта (тот же эталон).
    // Мутация «снять фильтр ProjectId» → foreign.md в выдаче (краснеет).
    [Fact]
    public void Build_ЧужойПроектОтбрасывается()
    {
        var own = TaskOf("own", ["docs/research/own.md"], Base, projectId: "proj-epic");
        var foreign = TaskOf("foreign", ["docs/research/foreign.md"], Base.AddDays(1), projectId: "proj-other");
        var noProject = TaskOf("none", ["docs/research/none.md"], Base);

        FeatureMaterials.Build([own, foreign], "proj-epic").Select(m => m.Path)
            .Should().Equal("docs/research/own.md");
        // Эпик без проекта (null): задача с проектом — чужая, без проекта — своя.
        FeatureMaterials.Build([own, noProject], null).Select(m => m.Path)
            .Should().Equal("docs/research/none.md");
    }
}
