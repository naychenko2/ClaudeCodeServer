using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Composition;

// Стор проектов «Личное» (ADR-040, Ф2-1): создание, идемпотентность, гонка, дубль, битый файл,
// коллизия корней и регрессия «Личное не светится в перечислениях ProjectManager».
public class PersonalProjectStoreTests : IDisposable
{
    private const string Username = "alice";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "pps_tests_" + Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly IConfiguration _config;
    private readonly AppSettingsService _appSettings;
    private readonly UserStore _users;
    private readonly UserHomeResolver _homes;
    private readonly string _ownerId;

    public PersonalProjectStoreTests()
    {
        _home = Path.Combine(_tempDir, "home");
        Directory.CreateDirectory(_home);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
            ["DefaultProjectsPath"] = _tempDir,
            [$"Projects:UserHomeOverrides:{Username}"] = _home,
        }).Build();
        _appSettings = new AppSettingsService(_config);
        _users = new UserStore(_config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(),
            NullLogger<UserStore>.Instance);
        _homes = new UserHomeResolver(_config, _appSettings);
        _ownerId = _users.Add(Username, "password12345", "user").Id;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* не критично */ }
    }

    private string StorePath => Path.Combine(_tempDir, "data", "personal-projects.json");

    private PersonalProjectStore CreateStore(IServiceProvider? services = null) =>
        new(_config, _users, _homes, services: services);

    private ProjectManager CreateManager(IPersonalProjects? personal = null) =>
        new(_config, _users, _appSettings, homes: _homes, personal: personal);

    private static IServiceProvider ProviderOf(IProjectManager projects) =>
        new ServiceCollection().AddSingleton(projects).BuildServiceProvider();

    [Fact]
    public async Task Ensure_СоздаётЗаписьИПапку_ИПереживаетПерезапуск()
    {
        var sut = CreateStore();

        var project = await sut.EnsurePersonalAsync(_ownerId);

        project.Name.Should().Be("Личное");
        project.Kind.Should().Be(ProjectKind.Personal);
        project.OwnerId.Should().Be(_ownerId);
        project.RootPath.Should().Be(Path.Combine(_home, "Personal"));
        Directory.Exists(project.RootPath).Should().BeTrue();
        File.Exists(StorePath).Should().BeTrue();

        var reloaded = CreateStore().GetPersonalOf(_ownerId);
        reloaded.Should().NotBeNull();
        reloaded!.Id.Should().Be(project.Id);
        reloaded.Kind.Should().Be(ProjectKind.Personal);
    }

    [Fact]
    public async Task Ensure_СбойСохранения_НеОставляетЗаписьВПамяти()
    {
        var sut = CreateStore();
        // Путь файла занят каталогом: запись на диск гарантированно падает
        Directory.CreateDirectory(StorePath);

        var act = async () => await sut.EnsurePersonalAsync(_ownerId);

        await act.Should().ThrowAsync<Exception>();
        sut.GetPersonalOf(_ownerId).Should().BeNull("запись не попала на диск");
        sut.GetAllPersonal().Should().BeEmpty();

        Directory.Delete(StorePath);
        var project = await sut.EnsurePersonalAsync(_ownerId);
        CreateStore().GetPersonalOf(_ownerId)!.Id.Should().Be(project.Id);
    }

    [Fact]
    public async Task Ensure_Идемпотентен()
    {
        var sut = CreateStore();

        var first = await sut.EnsurePersonalAsync(_ownerId);
        var second = await sut.EnsurePersonalAsync(_ownerId);

        second.Id.Should().Be(first.Id);
        sut.GetAllPersonal().Should().ContainSingle();
    }

    [Fact]
    public async Task Ensure_ПараллельныеВызовы_ДаютОднуЗапись()
    {
        var sut = CreateStore();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(() => sut.EnsurePersonalAsync(_ownerId))));

        results.Select(p => p.Id).Distinct().Should().ContainSingle();
        sut.GetAllPersonal().Should().ContainSingle();
        CreateStore().GetAllPersonal().Should().ContainSingle();
    }

    [Fact]
    public async Task Ensure_ПапкаПропала_ПересоздаётПоТомуЖеПути()
    {
        var sut = CreateStore();
        var project = await sut.EnsurePersonalAsync(_ownerId);
        Directory.Delete(project.RootPath, recursive: true);

        var again = await sut.EnsurePersonalAsync(_ownerId);

        again.Id.Should().Be(project.Id);
        Directory.Exists(again.RootPath).Should().BeTrue();
    }

    [Fact]
    public async Task Ensure_НеизвестныйВладелец_Бросает()
    {
        var act = () => CreateStore().EnsurePersonalAsync("no-such-user");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void GetPersonalOf_НеСоздаётНиЗаписиНиПапки()
    {
        var sut = CreateStore();

        sut.GetPersonalOf(_ownerId).Should().BeNull();

        File.Exists(StorePath).Should().BeFalse();
        Directory.Exists(Path.Combine(_home, "Personal")).Should().BeFalse();
    }

    [Fact]
    public void Загрузка_ДублиНаВладельца_ОставляетСтарейшую()
    {
        var older = new Project { Name = "Личное", RootPath = Path.Combine(_home, "Personal"), OwnerId = _ownerId,
            Kind = ProjectKind.Personal };
        // CreatedAt — init-свойство: второй объект собираем инициализатором с нужной датой
        var newer = new Project { Name = "Личное", RootPath = Path.Combine(_home, "Personal-2"), OwnerId = _ownerId,
            Kind = ProjectKind.Personal, CreatedAt = older.CreatedAt.AddDays(1) };
        JsonFileStore.Save(StorePath, new List<Project> { newer, older });

        var sut = CreateStore();

        sut.GetAllPersonal().Should().ContainSingle().Which.Id.Should().Be(older.Id);
    }

    [Fact]
    public async Task Загрузка_БитыйФайл_ПустоИЗаписьПересоздаётсяПоТомуЖеПути()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, "{ это не json");

        var sut = CreateStore();

        sut.GetAllPersonal().Should().BeEmpty();
        var project = await sut.EnsurePersonalAsync(_ownerId);
        project.RootPath.Should().Be(Path.Combine(_home, "Personal"));
    }

    [Fact]
    public async Task GetByIdIncludingPersonal_НаходитЗапись_ИНеЧужойId()
    {
        var sut = CreateStore();
        var project = await sut.EnsurePersonalAsync(_ownerId);

        sut.GetByIdIncludingPersonal(project.Id).Should().BeSameAs(project);
        sut.GetByIdIncludingPersonal("other").Should().BeNull();
    }

    [Fact]
    public async Task DeleteOf_УдаляетЗапись_ПапкаОстаётся()
    {
        var sut = CreateStore();
        var project = await sut.EnsurePersonalAsync(_ownerId);
        File.WriteAllText(Path.Combine(project.RootPath, "keep.txt"), "x");

        sut.DeleteOf(_ownerId).Should().BeTrue();

        sut.GetPersonalOf(_ownerId).Should().BeNull();
        CreateStore().GetPersonalOf(_ownerId).Should().BeNull();
        File.Exists(Path.Combine(project.RootPath, "keep.txt")).Should().BeTrue();
        sut.DeleteOf(_ownerId).Should().BeFalse();
    }

    [Fact]
    public async Task Ensure_КорньЗанятОбычнымПроектомВладельца_БерётPersonal2()
    {
        var plainRoot = Path.Combine(_home, "Personal");
        Directory.CreateDirectory(plainRoot);
        var manager = CreateManager();
        manager.Create("Мой", plainRoot, _ownerId, Username);
        var sut = CreateStore(ProviderOf(new ProjectManagerAdapter(manager)));

        var project = await sut.EnsurePersonalAsync(_ownerId);

        project.RootPath.Should().Be(Path.Combine(_home, "Personal-2"));
    }

    [Fact]
    public async Task EnsureRootFree_КореньЛичного_ОтклоняетсяКакСлужебный()
    {
        var store = CreateStore();
        var personal = await store.EnsurePersonalAsync(_ownerId);
        var manager = CreateManager(store);

        var act = () => manager.Create("Близнец", personal.RootPath, _ownerId, Username);

        act.Should().Throw<ArgumentException>().WithMessage("*служебным*");
        manager.GetAll().Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureRootFree_ДругойВладелец_НеБлокируется()
    {
        // Сверка идёт только с «Личным» самого владельца, как и у обычных проектов
        var other = _users.Add("bob", "password12345", "user");
        var store = CreateStore();
        var personal = await store.EnsurePersonalAsync(_ownerId);
        var manager = CreateManager(store);

        var project = manager.Create("Чужой", personal.RootPath, other.Id, "bob");

        project.OwnerId.Should().Be(other.Id);
    }

    [Fact]
    public async Task ProjectManager_НеОтдаётЛичное_НиВОдномПеречислении()
    {
        var store = CreateStore();
        var personal = await store.EnsurePersonalAsync(_ownerId);
        var manager = CreateManager(store);
        var regularRoot = Path.Combine(_home, "regular");
        Directory.CreateDirectory(regularRoot);
        var regular = manager.Create("Обычный", regularRoot, _ownerId, Username);

        manager.GetAll().Select(p => p.Id).Should().Equal(regular.Id);
        manager.GetByOwner(_ownerId).Select(p => p.Id).Should().Equal(regular.Id);
        manager.GetAccessible(_ownerId).Select(p => p.Id).Should().Equal(regular.Id);
        manager.GetById(personal.Id).Should().BeNull();
        manager.GetByName("Личное").Should().BeNull();
        manager.GetByRootPath(personal.RootPath).Should().BeEmpty();

        // projects.json «Личного» не содержит: старый бинарник после отката его не увидит
        File.ReadAllText(Path.Combine(_tempDir, "data", "projects.json")).Should().NotContain(personal.Id);
    }

    [Fact]
    public void Project_ОбычныйВидВФайлНеПишется_ЛичноеПишется()
    {
        var regular = JsonSerializer.Serialize(new Project { Name = "a" });
        var personal = JsonSerializer.Serialize(new Project { Name = "a", Kind = ProjectKind.Personal });

        using var regularDoc = JsonDocument.Parse(regular);
        using var personalDoc = JsonDocument.Parse(personal);

        regularDoc.RootElement.TryGetProperty("Kind", out _).Should().BeFalse("Regular на верхнем уровне не пишется; Kind внутри Icon — другое поле");
        personalDoc.RootElement.GetProperty("Kind").GetInt32().Should().Be(1);
    }
}
