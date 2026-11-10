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
}
