using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Services.Wiki;

namespace ClaudeHomeServer.Tests.Services;

// Видимость разделов вики (ADR-036, решение 2): таблица §2 — закон, всё неопределённое —
// fail-closed (набор сужается), сироты не входят никогда. Чистая логика — юнит без окружения.
public class WikiVisibilityTests
{
    private const string Owner = "u1";

    // Справочник с изменяемым состоянием (форма из PersonaZoneTests).
    private sealed class FakeDir : ISphereDirectory
    {
        public bool IsEnabled = true;
        public readonly Dictionary<string, List<string>> Spheres = new();

        public bool Enabled(string ownerId) => IsEnabled;
        public string? SphereOf(string ownerId, string projectId) =>
            IsEnabled ? Spheres.Keys.FirstOrDefault(s => Spheres[s].Contains(projectId)) : null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) =>
            IsEnabled && Spheres.TryGetValue(sphereId, out var list) ? list.ToList() : [];
        public string? SphereName(string ownerId, string sphereId) => Spheres.ContainsKey(sphereId) ? "Работа" : null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    private sealed class FakeProjects : IProjectManager
    {
        public readonly Dictionary<string, Project> Items = new();

        public Project? GetById(string id) => Items.GetValueOrDefault(id);
        public IReadOnlyCollection<Project> GetByOwner(string userId) =>
            Items.Values.Where(p => p.OwnerId == userId).ToList();
        public IReadOnlyCollection<Project> GetAll() => Items.Values.ToList();
        public IReadOnlyCollection<Project> GetByRootPath(string rootPath) => [];
    }

    // Рабочая конфигурация: p1 и p2 в сфере s1, p3 вне сфер.
    private static (FakeDir Dir, FakeProjects Projects) Working()
    {
        var dir = new FakeDir();
        dir.Spheres["s1"] = ["p1", "p2"];
        var projects = new FakeProjects();
        projects.Items["p1"] = new Project { Id = "p1", OwnerId = Owner, GroupId = "s1" };
        projects.Items["p2"] = new Project { Id = "p2", OwnerId = Owner, GroupId = "s1" };
        projects.Items["p3"] = new Project { Id = "p3", OwnerId = Owner };
        return (dir, projects);
    }

    private static WikiVisibility.CallContext InProject(string? projectId, Persona? persona = null) =>
        new(projectId, persona);

    private static Persona ProjectPersona(string projectId) =>
        new() { OwnerId = Owner, Name = "Пётр", Scope = PersonaScope.Project, ProjectId = projectId };

    private static Persona SpherePersona(string sphereId) =>
        new() { OwnerId = Owner, Name = "Лена", Scope = PersonaScope.Sphere, SphereId = sphereId };

    private static Persona GlobalPersona() =>
        new() { OwnerId = Owner, Name = "Глоба", Scope = PersonaScope.Global };

    // —— Таблица ADR-036 §2 ——

    [Theory]
    [MemberData(nameof(TableRowCases))]
    public void SectionsFor_таблица_проектныйЧат(string projectId, string[] expected)
    {
        var (dir, projects) = Working();
        WikiVisibility.SectionsFor(Owner, InProject(projectId), dir, projects).Should().Equal(expected);
    }

    public static TheoryData<string, string[]> TableRowCases => new()
    {
        // Строка 1: чат в проекте P, P в сфере S — свой, соседние по сфере, сфера, common.
        { "p1", new[] { "projects/p1", "projects/p2", "spheres/s1", "common" } },
        // Строка 2: чат в проекте P вне сфер — только свой + common.
        { "p3", new[] { "projects/p3", "common" } },
    };

    [Fact]
    public void SectionsFor_таблица_флагСферВыключен_СвойПроектИCommon()
    {
        var (dir, projects) = Working();
        dir.IsEnabled = false;
        WikiVisibility.SectionsFor(Owner, InProject("p1"), dir, projects)
            .Should().Equal("projects/p1", "common");
    }

    [Fact]
    public void SectionsFor_таблица_чатВнеПроекта_personalИCommon()
    {
        var (dir, projects) = Working();
        WikiVisibility.SectionsFor(Owner, InProject(null), dir, projects)
            .Should().Equal("personal", "common");
    }

    [Fact]
    public void SectionsFor_нетКонтекста_толькоCommon()
    {
        var (dir, projects) = Working();
        WikiVisibility.SectionsFor(Owner, null, dir, projects).Should().Equal("common");
    }

    // —— Пересечение с зоной персоны (ADR-024): выпадают только разделы проектов вне зоны ——

    [Fact]
    public void SectionsFor_проектнаяПерсона_неВидитСоседнийПроектТойЖеСферы()
    {
        var (dir, projects) = Working();
        WikiVisibility.SectionsFor(Owner, InProject("p1", ProjectPersona("p1")), dir, projects)
            .Should().Equal("projects/p1", "spheres/s1", "common");
    }

    [Fact]
    public void SectionsFor_персонаСферы_видитПроектыСвоейСферы()
    {
        var (dir, projects) = Working();
        WikiVisibility.SectionsFor(Owner, InProject("p1", SpherePersona("s1")), dir, projects)
            .Should().Equal("projects/p1", "projects/p2", "spheres/s1", "common");
    }

    [Fact]
    public void SectionsFor_глобальнаяПерсона_неСужается()
    {
        var (dir, projects) = Working();
        WikiVisibility.SectionsFor(Owner, InProject("p1", GlobalPersona()), dir, projects)
            .Should().Equal("projects/p1", "projects/p2", "spheres/s1", "common");
    }

    [Fact]
    public void SectionsFor_персонаЧужогоПроекта_выпадаетРазделПроектаЧата()
    {
        var (dir, projects) = Working();
        // Зона персоны — [p2]: выпадает раздел проекта чата p1 (вне зоны); p2 остаётся —
        // он в зоне и сосед по сфере. Выпадают только разделы вне зоны, не «чужие чату».
        WikiVisibility.SectionsFor(Owner, InProject("p1", ProjectPersona("p2")), dir, projects)
            .Should().Equal("projects/p2", "spheres/s1", "common");
    }

    // —— Fail-closed и сироты ——

    [Fact]
    public void SectionsFor_сфераУдалена_НаборСужаетсяДоСвоегоИCommon()
    {
        var (dir, projects) = Working();
        dir.Spheres.Remove("s1"); // p1 остался с GroupId=s1 — справочник даёт null
        WikiVisibility.SectionsFor(Owner, InProject("p1"), dir, projects)
            .Should().Equal("projects/p1", "common");
    }

    [Fact]
    public void SectionsFor_проектНеизвестенСфере_НаборНеРасширяется()
    {
        // p4 существует, но ни в одной сфере: соседних и раздела сферы нет.
        var (dir, projects) = Working();
        projects.Items["p4"] = new Project { Id = "p4", OwnerId = Owner };
        WikiVisibility.SectionsFor(Owner, InProject("p4"), dir, projects)
            .Should().Equal("projects/p4", "common");
    }

    [Fact]
    public void SectionsFor_разделУдалённогоПроекта_неВходитНиВОдинНабор()
    {
        var (dir, projects) = Working();

        // Чат в удалённом проекте p2: его раздела нет, расширяется не на что.
        projects.Items.Remove("p2");
        dir.Spheres["s1"] = ["p1", "p2"]; // справочник отстал от удаления — код всё равно не пустит
        WikiVisibility.SectionsFor(Owner, InProject("p2"), dir, projects).Should().Equal("common");

        // Чат в живом проекте p1: отставший справочник зовёт удалённого соседа p2 — выпадает.
        WikiVisibility.SectionsFor(Owner, InProject("p1"), dir, projects)
            .Should().Equal("projects/p1", "spheres/s1", "common");

        // Чат вне проекта: разделов проектов нет по правилу таблицы.
        WikiVisibility.SectionsFor(Owner, InProject(null), dir, projects).Should().Equal("personal", "common");
    }

    [Fact]
    public void SectionsFor_чужойПроект_неДаётРазделаЧужогоВладельца()
    {
        var (dir, projects) = Working();
        projects.Items["p5"] = new Project { Id = "p5", OwnerId = "u2", GroupId = "s1" };
        dir.Spheres["s1"] = ["p1", "p2", "p5"];
        WikiVisibility.SectionsFor(Owner, InProject("p1"), dir, projects)
            .Should().Equal("projects/p1", "projects/p2", "spheres/s1", "common");
    }

    // —— Пост-фильтр хитов (решение 6: гарантия кода поверх Dify) ——

    private sealed record Hit(string Path);

    [Fact]
    public void Filter_оставляетХитыРазделовИзНабора_остальныеВыпадают()
    {
        var hits = new[]
        {
            new Hit("Проекты/p1/А.md"),
            new Hit("Проекты/p2/Б.md"),
            new Hit("Общее/В.md"),
            new Hit("Личное/Г.md"),      // личного в наборе нет
            new Hit("Служебное/Д.md"),   // неразобранный путь — не видим нигде
        };
        var visible = new[] { "projects/p1", "projects/p2", "common" };

        var kept = WikiVisibility.Filter(hits, visible, h => WikiSections.Parse(h.Path));

        kept.Select(h => h.Path).Should().Equal("Проекты/p1/А.md", "Проекты/p2/Б.md", "Общее/В.md");
    }

    [Fact]
    public void Filter_пустыеВходИНабор_пустойРезультат()
    {
        var empty = WikiVisibility.Filter(Array.Empty<Hit>(), Array.Empty<string>(), h => WikiSections.Parse(h.Path));
        empty.Should().BeEmpty();
        var noneVisible = WikiVisibility.Filter(new[] { new Hit("Проекты/p1/А.md") }, Array.Empty<string>(), h => WikiSections.Parse(h.Path));
        noneVisible.Should().BeEmpty();
    }

    // —— [скрыто]: ссылка за пределы набора (решение 2) ——

    [Theory]
    [MemberData(nameof(RedactCases))]
    public void RedactLinks_ссылкиЗаПределамиНабора_скрыто(string text, string[] visible, string expected) =>
        WikiVisibility.RedactLinks(text, visible).Should().Be(expected);

    public static TheoryData<string, string[], string> RedactCases => new()
    {
        // Ссылка на чужой проект — [скрыто]; своя остаётся как есть.
        {
            "См. [[Проекты/p2/Страница]] и [[Проекты/p1/Своя]]",
            new[] { "projects/p1", "common" },
            "См. [скрыто] и [[Проекты/p1/Своя]]"
        },
        // Алиас и якорь не спасают: вся ссылка целиком.
        {
            "См. [[Проекты/p2/Страница|подробнее]]",
            new[] { "projects/p1", "common" },
            "См. [скрыто]"
        },
        // Якорь раздела внутри допустимой цели — остаётся.
        {
            "См. [[Общее/В#раздел]]",
            new[] { "common" },
            "См. [[Общее/В#раздел]]"
        },
        // Раздел сферы в наборе — остаётся; вне набора — [скрыто].
        {
            "[[Сферы/s1/Карточка]] и [[Сферы/s2/Карточка]]",
            new[] { "projects/p1", "spheres/s1", "common" },
            "[[Сферы/s1/Карточка]] и [скрыто]"
        },
        // Неразбираемая цель — не путь раздела: fail-closed, тоже [скрыто].
        {
            "Обычная [[СсылкаБезПути]]",
            new[] { "common" },
            "Обычная [скрыто]"
        },
        // Текст без ссылок не меняется.
        {
            "Обычный текст без ссылок.",
            new[] { "common" },
            "Обычный текст без ссылок."
        },
    };
}
