using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Docs;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Применение пресета каркаса (знакомство v2, п.2-3): только добавление поверх живой папки,
// честный отчёт, PresetKey — последней записью. Пути строятся от Path.GetTempPath() +
// Path.Combine (кириллица обязана проходить и в Linux-CI).
public class ProjectPresetServiceTests : IDisposable
{
    private const string TestUserId = "test-user-id";
    private const string TestUsername = "test-user";

    private readonly string _tempDir;
    private readonly FileService _files = new();
    private readonly DocsIndexService _docs;
    private readonly ProjectManager _projects;

    public ProjectPresetServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "preset_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _docs = new DocsIndexService(_files);
        _projects = CreateManager();
    }

    private ProjectManager CreateManager() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json")
            }).Build(),
        new UserStore(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json")
                }).Build(),
            new FakeHostEnvironment(),
            NullLogger<UserStore>.Instance),
        new AppSettingsService(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json")
                }).Build()));

    private Project NewProject(string name = "Каркасный")
    {
        var root = Path.Combine(_tempDir, name + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return _projects.Create(name, root, TestUserId, TestUsername);
    }

    private ProjectPresetService NewSut(Action<Project>? onCommit = null) => new(_files, _docs, _projects)
    {
        BeforePresetKeyCommit = onCommit,
    };

    private string InRoot(Project p, string relative) =>
        Path.Combine(p.RootPath, relative.Replace('/', Path.DirectorySeparatorChar));

    // Снимок всех файлов папки: путь (от корня, с прямыми слэшами) → байты
    private static Dictionary<string, byte[]> SnapshotFiles(string root)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            result[rel] = File.ReadAllBytes(file);
        }
        return result;
    }

    [Fact]
    public void Apply_НаЧистойПапке_СоздаётКаркасИОбластьДокументации()
    {
        var project = NewProject();
        var preset = PresetCatalog.Find("docs")!;
        var report = NewSut().Apply(project, preset);

        // Папки (кириллица — в Linux-CI путь от GetTempPath, без Windows-литералов)
        foreach (var folder in preset.Folders)
            Directory.Exists(InRoot(project, folder)).Should().BeTrue($"папка {folder} не создана");
        // Файлы — с содержимым каталога, где токен названия заменён именем проекта
        foreach (var file in preset.Files)
        {
            File.Exists(InRoot(project, file.Path)).Should().BeTrue();
            File.ReadAllText(InRoot(project, file.Path))
                .Should().Be(PresetCatalog.Materialize(file.Content, project.Name));
        }
        report.Created.Should().Contain(new[] { "Исходники", "CLAUDE.md", "Статус.md", ".docs", "Доска задач" });
        report.Skipped.Should().BeEmpty();

        // .docs прочитан обратно: область, «Начало», схема типов
        var (scope, docTypes) = _docs.ResolveScopeAndTypes(project);
        scope.Folders.Should().BeEquivalentTo(preset.DocsScope.Folders);
        scope.Types.Should().BeEquivalentTo(preset.DocsScope.Types);
        scope.Home.Should().Be("Статус.md");
        docTypes.Select(t => t.Id).Should().BeEquivalentTo(["working-doc", "meeting", "incoming", "archived"]);
        _docs.ResolveHome(project.RootPath, scope).Should().Be("Статус.md");

        // Доска и дискриминатор
        project.BoardColumns!.Select(c => c.Name)
            .Should().Equal("Разобрать", "В работе", "На согласовании", "Готово");
        project.PresetKey.Should().Be("docs");
        _projects.GetById(project.Id)!.PresetKey.Should().Be("docs");
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("personal")]
    public void Apply_ВсеПресеты_СоздаютКаркасНаЧистойПапке(string key)
    {
        var project = NewProject("пресет-" + key);
        var preset = PresetCatalog.Find(key)!;
        var report = NewSut().Apply(project, preset);

        report.Skipped.Should().BeEmpty();
        foreach (var folder in preset.Folders)
            Directory.Exists(InRoot(project, folder)).Should().BeTrue();
        foreach (var file in preset.Files)
            File.ReadAllText(InRoot(project, file.Path))
                .Should().Be(PresetCatalog.Materialize(file.Content, project.Name));

        var (scope, docTypes) = _docs.ResolveScopeAndTypes(project);
        scope.Folders.Should().BeEquivalentTo(preset.DocsScope.Folders);
        _docs.ResolveHome(project.RootPath, scope).Should().Be("Статус.md");
        docTypes.Select(t => t.Id).Should().BeEquivalentTo(preset.DocTypes.Select(t => t.Id));

        // Разработка не трогает CLAUDE.md: у репозитория с кодом он почти всегда свой
        if (key == "dev")
            File.Exists(InRoot(project, "CLAUDE.md")).Should().BeFalse();

        project.BoardColumns!.Select(c => c.Name)
            .Should().BeEquivalentTo(preset.BoardColumns.Select(c => c.Name));
        project.PresetKey.Should().Be(key);
    }

    [Fact]
    public void Apply_ПовторноПослеСмертиПроцесса_НичегоНеМеняетИВсёПропускает()
    {
        var project = NewProject();
        var preset = PresetCatalog.Find("docs")!;
        var sut = NewSut();
        sut.Apply(project, preset);

        // Моделируем смерть процесса до коммита: PresetKey вернулся в pending, всё
        // записанное осталось на диске. Повтор добирает ничего и не портит записанное.
        _projects.SetPresetKey(project.Id, ProjectPreset.Pending);
        var before = SnapshotFiles(project.RootPath);

        var report = sut.Apply(project, preset);

        report.Created.Should().BeEmpty("повторное применение ничего не создаёт");
        report.Skipped.Should().HaveCount(preset.Folders.Count + preset.Files.Count + 2,
            "все папки, все файлы, .docs и доска — каждый со своей причиной");
        report.Skipped.Should().OnlyContain(s =>
            s.Path != "Доска задач" || s.Reason.Contains("колонки уже настроены"));
        report.Skipped.Should().Contain(s => s.Path == "CLAUDE.md" && s.Reason.Contains("не перезаписан"));
        report.Skipped.Should().Contain(s => s.Path == ".docs" && s.Reason.Contains("уже настроена"));

        var after = SnapshotFiles(project.RootPath);
        after.Should().BeEquivalentTo(before,
            "ни один файл на диске не изменён (сравнение по байтам до и после)");
        project.PresetKey.Should().Be("docs");
    }

    [Fact]
    public void Apply_НаЖивойПапке_НичегоСуществующегоНеПерезаписывает()
    {
        var project = NewProject();
        var ownClaude = "# Живой CLAUDE.md проекта\n";
        var ownStatus = "# Свой статус\n";
        File.WriteAllText(InRoot(project, "CLAUDE.md"), ownClaude);
        File.WriteAllText(InRoot(project, "Статус.md"), ownStatus);
        _projects.UpdateBoardColumns(project.Id,
        [
            new BoardColumn { Name = "Своя колонка", Category = TaskItemStatus.Todo },
        ]);
        File.WriteAllText(InRoot(project, ".docs"),
            """
            {
              "folders": ["Моя папка"],
              "rootFiles": ["README.md"],
              "types": ["markdown"]
            }
            """);

        var report = NewSut().Apply(project, PresetCatalog.Find("docs")!);

        File.ReadAllText(InRoot(project, "CLAUDE.md")).Should().Be(ownClaude);
        File.ReadAllText(InRoot(project, "Статус.md")).Should().Be(ownStatus);
        File.ReadAllText(InRoot(project, ".docs")).Should().Contain("Моя папка");
        project.BoardColumns!.Select(c => c.Name).Should().Equal("Своя колонка");

        report.Created.Should().NotContain("CLAUDE.md").And.NotContain("Статус.md")
            .And.NotContain(".docs").And.NotContain("Доска задач");
        report.Skipped.Should().Contain(s => s.Path == "CLAUDE.md" && s.Reason.Contains("не перезаписан"));
        report.Skipped.Should().Contain(s => s.Path == "Статус.md" && s.Reason.Contains("не перезаписан"));
        report.Skipped.Should().Contain(s => s.Path == ".docs" && s.Reason.Contains("уже настроена"));
        report.Skipped.Should().Contain(s => s.Path == "Доска задач" && s.Reason.Contains("не перезаписаны"));
        // Остальной каркас создался, PresetKey зафиксирован (частичный успех — тоже успех)
        report.Created.Should().Contain("Исходники").And.Contain("Входящие");
        Directory.Exists(InRoot(project, "Исходники")).Should().BeTrue();
        project.PresetKey.Should().Be("docs");
    }

    [Fact]
    public void Apply_БитыйDocs_ФайлНеТронутПричинаВОтчёте()
    {
        var project = NewProject();
        var broken = "{ не json вообще";
        File.WriteAllText(InRoot(project, ".docs"), broken);

        var report = NewSut().Apply(project, PresetCatalog.Find("personal")!);

        File.ReadAllText(InRoot(project, ".docs")).Should().Be(broken, "файл не тронут");
        var skip = report.Skipped.Should().ContainSingle(s => s.Path == ".docs").Which;
        skip.Reason.Should().Contain("не разобран");
        // Требование п.3: при пропуске отчёт обязан сказать про Статус.md и «Начало»
        skip.Reason.Should().Contain("Статус.md").And.Contain("«Начало»");
        report.Skipped.Should().NotContain(s => s.Path == "Статус.md" && s.Reason.Contains("не удалось"),
            "сам файл Статус.md при этом создаётся — в skipped его нет");
        File.Exists(InRoot(project, "Статус.md")).Should().BeTrue();
        project.PresetKey.Should().Be("personal", "частичный успех фиксирует пресет");
    }

    [Fact]
    public void Apply_ПроставляетPresetKey_ПоследнимПослеВсехЗаписей()
    {
        var project = NewProject();
        var preset = PresetCatalog.Find("docs")!;
        string? keyAtCommit = null;
        bool foldersReady = false, docsReady = false, boardReady = false;

        var sut = NewSut(p =>
        {
            keyAtCommit = p.PresetKey;
            foldersReady = Directory.Exists(InRoot(p, "Исходники"));
            docsReady = File.Exists(InRoot(p, ".docs"));
            boardReady = p.BoardColumns is not null;
        });
        sut.Apply(project, preset);

        // В момент коммита: всё применено, ключ ещё pending (смерть процесса здесь
        // оставила бы честный «pending» с полностью записанным каркасом)
        keyAtCommit.Should().Be(ProjectPreset.Pending);
        foldersReady.Should().BeTrue("папки записываются до коммита ключа");
        docsReady.Should().BeTrue(".docs записывается до коммита ключа");
        boardReady.Should().BeTrue("колонки записываются до коммита ключа");
        project.PresetKey.Should().Be("docs");
    }

    [Fact]
    public void Apply_ИмяСПробеламиИТире_ПодставляетсяВЗаголовкиЗаготовок()
    {
        // Имя с пробелами и тире — как в живых проектах; оно обязано попасть в шапку
        // CLAUDE.md (docs) и Статус.md (personal), не оставив токен-плейсхолдер
        var project = NewProject("Документооборот - в EDMS");
        NewSut().Apply(project, PresetCatalog.Find("docs")!);

        var claude = File.ReadAllText(InRoot(project, "CLAUDE.md"));
        claude.Should().Contain("# Документооборот - в EDMS");
        claude.Should().NotContain(PresetCatalog.ProjectNameToken,
            "плейсхолдер названия не должен доживать до файла на диске");

        var personal = NewProject("Личное дело - переезд 2026");
        NewSut().Apply(personal, PresetCatalog.Find("personal")!);
        var status = File.ReadAllText(InRoot(personal, "Статус.md"));
        status.Should().Contain("# Личное дело - переезд 2026");
        status.Should().NotContain(PresetCatalog.ProjectNameToken);
        // Курсивные подсказки — подсказками и остаются: сервер их не заполняет
        status.Should().Contain("_Что это за дело");
    }

    [Fact]
    public void Apply_ПустоеИмяПроекта_ОставляетТокенКакПодсказку()
    {
        // Прямой вызов API может завести проект с пустым именем: пустой заголовок хуже
        // токена-подсказки, который человек заполнит руками или модель при первом ходе
        var project = NewProject("");
        NewSut().Apply(project, PresetCatalog.Find("docs")!);

        File.ReadAllText(InRoot(project, "CLAUDE.md"))
            .Should().Contain($"# {PresetCatalog.ProjectNameToken}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
