using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;

namespace ClaudeHomeServer.Tests.Services;

// Разделы единой вики (ADR-036, решение 1): разбор пути в ключ, обратная папка, цепочка «вверх»,
// сироты, отказ записи в раздел сферы. Чистая логика — юнит без окружения.
public class WikiSectionsTests
{
    private const string Owner = "u1";

    // Справочник с изменяемым состоянием (форма из PersonaZoneTests): тест двигает членство
    // и флаг между вызовами.
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

    // —— Parse: единственный разбор пути вики ——

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void Parse_путьПодPages_даётКлючРаздела(string path, string? expectedKey) =>
        WikiSections.Parse(path).Should().Be(expectedKey);

    public static TheoryData<string, string?> ParseCases => new()
    {
        { "Проекты/p1/Заметка.md", "projects/p1" },
        { "Проекты/p1", "projects/p1" },
        { "Проекты/p1/_health.md", "projects/p1" },
        { "Сферы/s1/Страница.md", "spheres/s1" },
        { "Общее/Веб-страница.md", "common" },
        { "Личное/Чат.md", "personal" },
    };

    [Theory]
    [MemberData(nameof(UnparsedPaths))]
    public void Parse_неразобранныйПуть_null(string path) =>
        WikiSections.Parse(path).Should().BeNull();

    // Ревью S2d (major 1): «..» и пустые сегменты в путях верхних разделов не проверялись —
    // «Общее/../Личное/…» давал ключ «common», а «common» есть в каждом наборе видимости:
    // личный раздел и чужой проект уходили в выдачу модели. Контракт пути строгий:
    // только чистый относительный путь с «/», всё остальное — null.
    public static TheoryData<string> UnparsedPaths => new()
    {
        "",
        "   ",
        "Заметка.md", // страница в корне pages/ — ни одному разделу не принадлежит
        "Проекты/", // id нет
        "Проекты//Заметка.md", // пустой сегмент
        "проекты/p1/Заметка.md", // другой регистр — не папка раздела (папки создаёт Folder)
        "Проекты/../Заметка.md", // «..» — не путь раздела
        "Проекты/p 1/Заметка.md", // пробел — не id
        "Служебное/Файл.md", // неизвестная папка
        "Сферы/",
        // —— ревью S2d: «..» в путях верхних разделов ——
        "Общее/../Личное/Секрет.md", // личный раздел за «..» не должен стать «common»
        "Общее/../Проекты/p2/Секрет.md", // чужой проект за «..» не должен стать «common»
        "Личное/../Общее/X.md", // «..» в обе стороны
        "Общее/..", // «..» и на хвосте пути
        // —— ревью S2d: синтаксис, который не относительный путь под pages/ ——
        "/Общее/X.md", // абсолютный префикс
        "C:/Общее/X.md", // диск
        @"Общее\..\Личное\X.md", // обратные слеши
        @"Проекты\p1\Заметка.md", // обратные слеши — не путь раздела
        "Общее//X.md", // пустой сегмент
        "./Общее/X.md", // «.» — не нормализуем
        "./Проекты/p1/Заметка.md", // «.» — не нормализуем
        "Общее/\tX.md", // управляющий символ
    };

    // —— Folder: обратное к ключу; round-trip с Parse ——

    [Theory]
    [MemberData(nameof(FolderCases))]
    public void Folder_ключ_даётПапкуРаздела(string key, string expectedFolder) =>
        WikiSections.Folder(key).Should().Be(expectedFolder);

    [Theory]
    [MemberData(nameof(FolderCases))]
    public void Folder_обратна_Parse(string key, string folder)
    {
        WikiSections.Parse(folder).Should().Be(key);
        WikiSections.Parse(folder + "Страница.md").Should().Be(key);
    }

    [Theory]
    [MemberData(nameof(InvalidKeys))]
    public void Folder_не_ключа_null(string key) =>
        WikiSections.Folder(key).Should().BeNull();

    public static TheoryData<string, string> FolderCases => new()
    {
        { "projects/p1", "Проекты/p1/" },
        { "spheres/s1", "Сферы/s1/" },
        { "common", "Общее/" },
        { "personal", "Личное/" },
    };

    public static TheoryData<string> InvalidKeys => new()
    {
        "projects", "projects/", "spheres/", "projects/p 1", "unknown/x", "", "Проекты/p1",
    };

    // —— UpChain: цепочка строго вверх (решение 4) ——

    [Theory]
    [MemberData(nameof(UpChainCases))]
    public void UpChain_цепочка_строго_вверх(string key, string[] expected)
    {
        var dir = new FakeDir();
        dir.Spheres["s1"] = ["p1"];
        WikiSections.UpChain(key, Owner, dir).Should().Equal(expected);
    }

    public static TheoryData<string, string[]> UpChainCases => new()
    {
        { "projects/p1", new[] { "spheres/s1", "common" } },
        { "projects/p2", new[] { "common" } }, // проект вне сфер
        { "spheres/s1", new[] { "common" } },
        { "personal", new[] { "common" } },
        { "common", Array.Empty<string>() },
        { "unknown/x", Array.Empty<string>() },
    };

    [Fact]
    public void UpChain_флагСферВыключен_цепочкаТолькоCommon()
    {
        var dir = new FakeDir { IsEnabled = false };
        dir.Spheres["s1"] = ["p1"];
        WikiSections.UpChain("projects/p1", Owner, dir).Should().Equal("common");
    }

    [Fact]
    public void UpChain_сфераУдалена_цепочкаТолькоCommon()
    {
        // p1 остался с GroupId=s1, но сферы нет — семантика справочника даёт null.
        WikiSections.UpChain("projects/p1", Owner, new FakeDir()).Should().Equal("common");
    }

    // —— Сироты: удалённый проект или удалённая сфера (решение 1) ——

    [Fact]
    public void IsOrphan_разделУдалённогоПроекта_сирота()
    {
        // p1 удалён из стора проектов.
        WikiSections.IsOrphan("projects/p1", Owner, new FakeDir(), new FakeProjects()).Should().BeTrue();
    }

    [Fact]
    public void IsOrphan_чужойПроект_сиротаДляВладельца()
    {
        var projects = new FakeProjects();
        projects.Items["p1"] = new Project { Id = "p1", OwnerId = "u2" };
        WikiSections.IsOrphan("projects/p1", Owner, new FakeDir(), projects).Should().BeTrue();
    }

    [Fact]
    public void IsOrphan_существующийСвойПроект_неСирота()
    {
        var projects = new FakeProjects();
        projects.Items["p1"] = new Project { Id = "p1", OwnerId = Owner, GroupId = "s1" };
        WikiSections.IsOrphan("projects/p1", Owner, new FakeDir(), projects).Should().BeFalse();
    }

    [Fact]
    public void IsOrphan_разделУдалённойСферы_сирота_аЖиваяПриВыключенномФлаге_нет()
    {
        // Сферы s1 нет — сирота.
        WikiSections.IsOrphan("spheres/s1", Owner, new FakeDir(), new FakeProjects()).Should().BeTrue();
        // Живая сфера при выключенном флаге — не сирота: она есть, просто не видна (видимость — не существование).
        var dir = new FakeDir { IsEnabled = false };
        dir.Spheres["s1"] = ["p1"];
        WikiSections.IsOrphan("spheres/s1", Owner, dir, new FakeProjects()).Should().BeFalse();
    }

    [Theory]
    [InlineData("common")]
    [InlineData("personal")]
    public void IsOrphan_верхниеРазделы_никогдаНеСироты(string key) =>
        WikiSections.IsOrphan(key, Owner, new FakeDir(), new FakeProjects()).Should().BeFalse();

    [Fact]
    public void IsOrphan_неКлючРаздела_сирота() =>
        WikiSections.IsOrphan("projects", Owner, new FakeDir(), new FakeProjects()).Should().BeTrue();

    // —— Запись в раздел: сферы отказаны до следующей фичи (решение 7) ——

    [Theory]
    [MemberData(nameof(WritableCases))]
    public void IsWritable_разделыФ0Ф1(string key, bool expectedWritable, string? expectedReason)
    {
        var check = WikiSections.IsWritable(key);
        check.Writable.Should().Be(expectedWritable);
        check.Reason.Should().Be(expectedReason);
    }

    public static TheoryData<string, bool, string?> WritableCases => new()
    {
        { "projects/p1", true, null },
        { "common", true, null },
        { "personal", true, null },
        { "spheres/s1", false, "раздел сферы — следующая фича" },
        { "spheres/", false, "неразобранный раздел" }, // пустой id — не ключ раздела
    };
}
