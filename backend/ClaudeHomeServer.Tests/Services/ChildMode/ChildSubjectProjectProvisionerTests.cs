using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

// Провижн проекта-предмета ребёнка (ChildSubjectProjectProvisioner, Б8b, ADR-041 ред. 5,
// разрез §11.1). Контракт: владелец — сам ребёнок; папка {дом}/{Предметы}/{subjectId}
// (путь — UserHomeResolver.SubjectHome); связь — маркер Project.ChildSubjectId, записанный
// в стор (после рестарта идемпотентность держится на нём). В папке сразу «Материалы/» и
// «Для репетитора/». Идемпотентность: повтор → тот же проект. Гонка: ровно один проект.
// Сбой папки: проекта нет, ошибка доходит до вызывающего, повтор успешен. Проект-предмета
// не виден админу (per-owner).
public class ChildSubjectProjectProvisionerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _homesRoot;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly ChildSubjectProjectProvisioner _sut;
    private readonly string _childId;
    private readonly string _adultId;

    // Логин с нижним регистром и slug-имена предметов: FolderKey нормализует путь через
    // ToLowerInvariant, и точное сравнение RootPath с ожидаемым путём верно на любой ОС.
    private const string ChildName = "kid.provision";

    public ChildSubjectProjectProvisionerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc_child_subject_proj_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _homesRoot = Path.Combine(_tempDir, "homes");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["DefaultProjectsPath"] = _homesRoot,
        }).Build();

        _users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _adultId = _users.GetFirst()!.Id; // дефолтный admin пустого стора — обычный пользователь
        _childId = _users.Add(ChildName, "kid-pw-123456", "user").Id;
        // Профиль ребёнка пишет админ (его PUT — KM-B1e); здесь — прямо в стор, провижер
        // обязан читать его с той же модели.
        _users.GetById(_childId)!.ChildMode = new ChildProfile { Enabled = true };

        var appSettings = new AppSettingsService(config);
        var homes = UserHomeResolver.WithoutOverrides(appSettings);
        _projects = new ProjectManager(config, _users, appSettings, homes: homes);
        _sut = new ChildSubjectProjectProvisioner(_users, _projects, homes,
            NullLogger<ChildSubjectProjectProvisioner>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // Предмет в профиле ребёнка (форма записи Т2a): возвращает запись — тот же объект,
    // который вызывает провижер на точке сохранения.
    private ChildSubject AddSubject(string id, string label)
    {
        var subject = new ChildSubject
        {
            Id = id, Label = label, Glyph = "Аа", Color = "info",
        };
        _users.GetById(_childId)!.ChildMode!.Subjects.Add(subject);
        return subject;
    }

    private int SubjectProjectCount(string subjectId) =>
        _projects.GetAll().Count(p => p.ChildSubjectId == subjectId);

    [Fact]
    public async Task EnsureSubjectProjectAsync_СоздаётПроектВладельцаРебёнка_МаркерВСторе_ПодпапкиЕсть_АдминНеВидит()
    {
        var subject = AddSubject("math", "Математика");

        var project = await _sut.EnsureSubjectProjectAsync(_childId, subject);

        project.Should().NotBeNull();
        project!.OwnerId.Should().Be(_childId, "владелец — сам ребёнок");
        project.ChildSubjectId.Should().Be("math", "связь с предметом — маркер проекта");
        // Папка — внутри дома ребёнка, в прослойке «Предметы», по id предмета
        // (у container-пользователя та же прослойка лежит в Sandbox:ProjectsRoot).
        project.RootPath.Should().EndWith(Path.Combine(ChildName, "Предметы", "math"));
        Directory.Exists(Path.Combine(project.RootPath, "Материалы")).Should().BeTrue();
        Directory.Exists(Path.Combine(project.RootPath, "Для репетитора")).Should().BeTrue();

        // Маркер — в записи стора, а не только в памяти процесса: иначе после рестарта
        // идемпотентность не держится и повторный провижн упирается в EnsureRootFree.
        // Мутация «записать маркер правкой объекта после Create, не параметром Create» —
        // краснеет здесь.
        File.ReadAllText(Path.Combine(_tempDir, "projects.json"))
            .Should().Contain("\"ChildSubjectId\":\"math\"");

        // per-owner: проекты ребёнка не видны админу ни в одном его списке.
        _projects.GetByOwner(_adultId).Should().NotContain(p => p.Id == project.Id);
        _projects.GetAccessible(_adultId).Should().NotContain(p => p.Id == project.Id,
            "проект-предмета ребёнка не появляется в списке админа");
        _projects.GetByOwner(_childId).Should().Contain(p => p.Id == project.Id);
    }

    [Fact]
    public async Task EnsureSubjectProjectAsync_Повтор_ТотЖеПроект_БезДубля()
    {
        var subject = AddSubject("math", "Математика");

        var first = await _sut.EnsureSubjectProjectAsync(_childId, subject);
        var second = await _sut.EnsureSubjectProjectAsync(_childId, subject);

        first.Should().NotBeNull();
        second!.Id.Should().Be(first!.Id, "повтор возвращает существующий проект по маркеру");
        SubjectProjectCount("math").Should().Be(1);
    }

    [Fact]
    public async Task EnsureSubjectProjectAsync_ГонкаДвухВызовов_РовноОдинПроект()
    {
        // Гонка на разных потоках: без per-ключевого семафора оба вызова проходят проверку
        // «проекта нет» и входят в Create — второй получает отказ EnsureRootFree (папка уже
        // занята) или создаёт дубль. Однопоточный прогон гонку не ловит в принципе:
        // до конца критической секции нет yield-точек, Task.Run обязателен.
        // Мутация «снять блокировку» (LockFor → новый семафор на каждый вызов) — красный.
        for (var iter = 0; iter < 20; iter++)
        {
            var subjectId = $"s{iter}";
            var subject = AddSubject(subjectId, $"Предмет {iter}");
            var first = Task.Run(() => _sut.EnsureSubjectProjectAsync(_childId, subject));
            var second = Task.Run(() => _sut.EnsureSubjectProjectAsync(_childId, subject));
            var results = await Task.WhenAll(first, second);
            results.Should().NotContainNulls($"итерация {iter}: провижн не завершился");
            results[1]!.Id.Should().Be(results[0]!.Id, $"итерация {iter}: гонка дала разные проекты");
            SubjectProjectCount(subjectId).Should().Be(1, $"итерация {iter}: гонка дала дубль");
        }
    }

    [Fact]
    public async Task EnsureSubjectProjectAsync_СбойСозданияПапки_ПроектНеЗаписан_ОшибкаВызывающему_ПовторУспешен()
    {
        var subject = AddSubject("math", "Математика");
        // Препятствие на месте папки предмета: файл вместо каталога — Directory.CreateDirectory
        // даёт IOException. Он доходит до вызывающего как есть (тот же контракт, что у
        // ChildTutorProvisioner: ошибки диска не глотаются) — тест ловит его целенаправленно,
        // наружу из вызова не выходит ничем неохваченным.
        var root = Path.Combine(_homesRoot, ChildName, "Предметы", "math");
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        File.WriteAllText(root, "не папка");

        await Assert.ThrowsAsync<IOException>(
            () => _sut.EnsureSubjectProjectAsync(_childId, subject));

        SubjectProjectCount("math").Should().Be(0, "сбой папки не оставляет проекта в сторе");
        _users.GetById(_childId)!.ChildMode!.Subjects.Should().ContainSingle(
            "провижн не правит профиль — предмет цел");

        File.Delete(root);
        var project = await _sut.EnsureSubjectProjectAsync(_childId, subject);
        project.Should().NotBeNull("после устранения препятствия повторный вызов успешен");
        SubjectProjectCount("math").Should().Be(1);
    }

    [Fact]
    public async Task EnsureSubjectProjectAsync_НеРебёнкуИБезЖивогоПредмета_Null()
    {
        var subject = AddSubject("math", "Математика");

        // Не ребёнок: обычный пользователь с тем же id предмета не провится вовсе.
        (await _sut.EnsureSubjectProjectAsync(_adultId, subject)).Should().BeNull();

        // Предмета нет в ЖИВОМ профиле (снимок вызывающего устарел на параллельной правке):
        // проект не заводится под удалённый предмет.
        var ghost = new ChildSubject { Id = "gone", Label = "Удалённый", Glyph = "Аа", Color = "info" };
        (await _sut.EnsureSubjectProjectAsync(_childId, ghost)).Should().BeNull();

        SubjectProjectCount("math").Should().Be(0);
        SubjectProjectCount("gone").Should().Be(0);
        _projects.GetAll().Should().BeEmpty();
    }
}
