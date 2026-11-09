using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tasks.Tests;

// Тест на БОЕВОЙ LocalExecutorRoster с настоящим ModelAssignmentResolver (хвост ревью 411eeaf2).
// LocalExecutorsContributorTests бьют по подставному FakeRoster: он сам повторяет «тир weak →
// локальный», поэтому поломка боевой реализации — подмена ExecutorModel на PersonaModel, игнор
// тира задачи, опускание персоны — в них не краснеет. Здесь проверяется сама реализация шва:
// разворот модели — только ModelAssignmentResolver, проводкой как в бою: DI собирает его через
// AddSingleton<ModelAssignmentResolver> (LlmSubsystem.cs:274) и подставляет все пять аргументов,
// включая SpecialtySettingsStore (:357) и LlmProviderRegistry (:302); тест держит тот же набор
// (инвариант «резолвер теста = боевой резолвер»). Локальность — IModelResolver.IsLocal
// провайдера этой модели.
//
// Слоты инстанса (личных слотов у пользователя нет, UserModelTierResolver падает на них):
// strong/medium — облачная модель, weak — локальная. Провайдеры: живой локальный (IsLocal +
// BaseUrl → Enabled), выключенный локальный (IsLocal без BaseUrl → Enabled=false, см.
// LlmProviderConfig.Enabled) и облачный.
public class LocalExecutorRosterTests : IDisposable
{
    private const string Owner = "u";
    private const string Project = "p1";
    private const string LocalModel = "qwen-roster-test";
    private const string CloudModel = "cloud-roster-test";
    private const string DisabledLocalModel = "qwen-roster-off-test";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ler_" + Guid.NewGuid().ToString("N"));
    private readonly PersonaManager _personas;
    private readonly ModelAssignmentResolver _models;
    private readonly SpecialtySettingsStore _specialty;
    private readonly IModelResolver _resolver;
    private readonly LocalExecutorRoster _sut;

    public LocalExecutorRosterTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            ["PersonasPath"] = Path.Combine(_dir, "personas.json"),
            ["ClaudeUserProfileDir"] = Path.Combine(_dir, "claude-profile"),
            // Живой локальный провайдер: IsLocal + BaseUrl → Enabled
            ["LlmProviders:local-roster:IsLocal"] = "true",
            ["LlmProviders:local-roster:AnthropicBaseUrl"] = "http://127.0.0.1:18025/v1",
            ["LlmProviders:local-roster:Models:0:Id"] = LocalModel,
            // Выключенный локальный: IsLocal, BaseUrl нет → Enabled=false
            ["LlmProviders:off-roster:IsLocal"] = "true",
            ["LlmProviders:off-roster:Models:0:Id"] = DisabledLocalModel,
            // Облачный
            ["LlmProviders:cloud-roster:ApiKey"] = "sk-test",
            ["LlmProviders:cloud-roster:AnthropicBaseUrl"] = "https://cloud.example.com",
            ["LlmProviders:cloud-roster:Models:0:Id"] = CloudModel,
        }).Build();

        var appSettings = new AppSettingsService(config);
        appSettings.Save(new AppSettings
        {
            ModelTierStrong = CloudModel,
            ModelTierMedium = CloudModel,
            ModelTierWeak = LocalModel,
        });

        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var registry = new LlmProviderRegistry(config);
        // Те же пять аргументов, что подставляет боевой DI (см. шапку файла). Без specialty форма
        // «персона без явной модели + дефолт тира специальности» развернулась бы по-другому —
        // по дефолту места Strong: см. ПерсонаБезМодели_ДефолтТираСпециальности_...
        _specialty = new SpecialtySettingsStore(config, users,
            NullLogger<SpecialtySettingsStore>.Instance, registry);
        _models = new ModelAssignmentResolver(appSettings, new LocalActionOverridesStore(config),
            new UserModelTierResolver(users, appSettings), _specialty, registry);
        _resolver = new LlmModelResolverAdapter(registry);
        _personas = new PersonaManager(config);
        _sut = new LocalExecutorRoster(_personas, _models, _resolver, registry);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    // Персона: исполнитель (Access=Full + специальность исполнителя — PersonaConsultantToolset.
    // IsExecutor) или вне ростера (ревьюер). model — явная модель персоны, сильнее слотов
    // (null — без явной модели: решает уровень задачи/специальности/места). scope/projectId —
    // зона персоны (для роста контекста проекта).
    private Persona NewPersona(string name, PersonaSpecialty specialty, string? model = null,
        PersonaScope scope = PersonaScope.Global, string? projectId = null) =>
        _personas.Create(Owner, name, null, null, null, model, null, scope, projectId,
            null, null, true, specialty: specialty);

    private static TaskItem NewTask(string? personaId, ModelTier? tier) =>
        new() { Title = "Шаг", PersonaId = personaId, ModelTier = tier };

    // Ростер контекста: без id — вне проекта; с id — как в бою (LocalExecutorsContributor.Render
    // зовёт For(ownerId, session.ProjectId), LocalIdleNudgeService.TickProjectAsync — id группы).
    private IEnumerable<string> RosterIds(string? projectId = null) =>
        _sut.For(Owner, projectId).Select(r => r.PersonaId);

    // Кейс 1 постановки ревью: тир задачи weak без персоны → модель по слоту владельца,
    // ход локальный; имени персоны в строке нет.
    [Fact]
    public void ТирЗадачиWeakБезПерсоны_ЛокальныйХод_ИмениПерсоныНет()
    {
        var turn = _sut.LocalTurnOf(NewTask(null, ModelTier.Weak), Owner);

        turn.Should().NotBeNull("слабый слот — локальная модель: это ход на движке");
        turn!.PersonaName.Should().BeNull("у задачи нет персоны");
    }

    [Fact]
    public void ТирЗадачиStrongБезПерсоны_ЛокальногоХодаНет()
    {
        _sut.LocalTurnOf(NewTask(null, ModelTier.Strong), Owner)
            .Should().BeNull("сильный слот — облачная модель: слот движка не занят");
    }

    // Кейс 2 постановки ревью: локальная персона-исполнитель — в ростере (её модель разворачивается
    // в локального провайдера), но сильный тир задачи сильнее её модели: ход идёт не на локали.
    // Здесь же видна граница двух счётов: «персона в ростере» ≠ «ход на движке».
    [Fact]
    public void ЛокальнаяПерсонаНаСильномТиреЗадачи_ВРостере_НоХодаНаДвижкеНет()
    {
        var p = NewPersona("Кира", PersonaSpecialty.BackendExecutor, LocalModel);

        RosterIds().Should().Contain(p.Id, "модель персоны — локального провайдера, запуск на неё возможен");
        _sut.LocalTurnOf(NewTask(p.Id, ModelTier.Strong), Owner)
            .Should().BeNull("тир задачи сильнее модели персоны: разворот по сильному (облачному) слоту");
    }

    // Без тира задачи решает модель персоны: локальная персона даёт локальный ход. Если
    // реализация опустит персону и возьмёт только дефолт места «исполнитель задач» (Strong) —
    // тест красный: карточка исполнителя перестала бы считаться на своём движке.
    [Fact]
    public void БезТираЗадачи_МодельЛокальнойПерсоны_ЛокальныйХодСИменемПерсоны()
    {
        var p = NewPersona("Денис", PersonaSpecialty.BackendExecutor, LocalModel);

        var turn = _sut.LocalTurnOf(NewTask(p.Id, null), Owner);

        turn.Should().NotBeNull("без уровня задачи решает модель персоны");
        turn!.PersonaName.Should().Be(p.Name);
    }

    [Fact]
    public void ОблачнаяПерсона_НиВРостереНиЛокальногоХода()
    {
        var p = NewPersona("Тимур", PersonaSpecialty.BackendExecutor, CloudModel);

        RosterIds().Should().NotContain(p.Id, "её модель — облачный провайдер");
        _sut.LocalTurnOf(NewTask(p.Id, null), Owner).Should().BeNull();
    }

    // Кейс 3 постановки ревью: персона-ревьюер вне ростера на слабом тире — ход на движке есть,
    // в ростере исполнителей её нет (тот же случай, что РевьюНаСлабомТиреВнеРостера в
    // LocalExecutorsContributorTests, но на боевой реализации, а не на подставном ростере).
    [Fact]
    public void ПерсонаРевьюерВнеРостераНаСлабомТире_ЛокальныйХод_ВРостереНет()
    {
        var p = NewPersona("Глеб", PersonaSpecialty.Reviewer, CloudModel);

        RosterIds().Should().NotContain(p.Id, "ревьюер не исполнитель");
        var turn = _sut.LocalTurnOf(NewTask(p.Id, ModelTier.Weak), Owner);

        turn.Should().NotBeNull("тир задачи разворачивается в локальную модель, специальности не смотрят");
        turn!.PersonaName.Should().Be(p.Name);
    }

    // Минор 2 ревью: LocalTurnOf НЕ спрашивает Enabled провайдера — поведение осознанное (ход
    // уже идёт: слот занят независимо от выключателя) и РАЗОЙДЕННОЕ с IsLocalPersona (там
    // выключатель нужен: там решается «можно ли запустить»). Тест закрепляет разницу как
    // договорённость: менять её — только явным решением и правкой обоих мест, не молча.
    [Fact]
    public void ЛокальныйПровайдерВыключен_ПерсонаВРостерНет_ИдущийХодСчитаетсяЛокальным()
    {
        var p = NewPersona("Кузьма", PersonaSpecialty.BackendExecutor, DisabledLocalModel);

        RosterIds().Should().NotContain(p.Id,
            "Enabled=false — запуск на этот провайдер невозможен");
        _sut.LocalTurnOf(NewTask(p.Id, null), Owner).Should().NotBeNull(
            "ход уже идёт на этой модели: выключатель провайдера на неё не влияет");
    }

    // Хвост ревью 7eb1552e4 (минор 1): форма «персона без явной модели + заданный админом дефолт
    // тира специальности». В бою уровень такой персоны даёт SpecialtyDefaultTier (ModelAssignment
    // Resolver.PersonaModelDetailed), а не дефолт места «исполнитель задач» (Strong, LocalAction
    // Catalog.cs:194); без SpecialtySettingsStore в резолвере она падала бы на сильный (облачный)
    // слот и локального хода не было бы. Здесь дефолт тира — слабый: исход LocalTurnOf обязан
    // совпасть с боевым разворотом модели задачи.
    [Fact]
    public void ПерсонаБезМодели_ДефолтТираСпециальности_ЛокальныйХодСИменемПерсоны()
    {
        // Своих ячеек у специальности нет, только DefaultTier=Weak → разворот падает на слот weak.
        _specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Specialties = { ["backendExecutor"] = new SpecialtyTemplateSettings { DefaultTier = ModelTier.Weak } },
        });
        var p = NewPersona("Кузьма", PersonaSpecialty.BackendExecutor);

        _models.ExecutorModel(NewTask(p.Id, null), p, Owner)
            .Should().Be(LocalModel, "уровень даёт DefaultTier специальности: модель — разворот её уровня");

        var turn = _sut.LocalTurnOf(NewTask(p.Id, null), Owner);
        turn.Should().NotBeNull("тот же исход, что боевой разворот: слабый слот занят локальной моделью");
        turn!.PersonaName.Should().Be(p.Name);
    }

    // Хвост ревью 7eb1552e4 (минор 2): ростер проверяется с проектным id — как его зовёт бой
    // (LocalExecutorsContributor.Render → For(ownerId, session.ProjectId), LocalIdleNudgeService.
    // TickProjectAsync → For(ownerId, projectId) группы эпика). Инвариант: ростер = локальные
    // исполнители владельца в контексте проекта (PersonaManager.GetForContext: глобальные + своего
    // проекта + своей сферы) — персона другой зоны вне ростера этого контекста.
    [Fact]
    public void ПроектнаяПерсона_ВРостереСвоегоПроекта_ВнеРостераБезПроекта()
    {
        var p = NewPersona("Кира", PersonaSpecialty.BackendExecutor, LocalModel, PersonaScope.Project, Project);

        RosterIds(Project).Should().Contain(p.Id,
            "боевой вызов идёт с проектным id: своя проектная персона — в ростере своего проекта");
        RosterIds().Should().NotContain(p.Id,
            "Scope=Project не входит в контекст без проекта: в глобальный ростер она не попадает");
    }

    // Инвариант: LocalTurnOf даёт ровно тот же исход, что боевой разворот модели задачи
    // (ModelAssignmentResolver.ExecutorModel → IsLocal провайдера). Подмена точки — PersonaModel
    // вместо ExecutorModel, игнор тира задачи, опускание персоны — краснит этот тест на тех же
    // конфигурациях, где краснеют кейсы выше.
    [Theory]
    [InlineData(PersonaSpecialty.BackendExecutor, LocalModel, null)]
    [InlineData(PersonaSpecialty.BackendExecutor, LocalModel, ModelTier.Strong)]
    [InlineData(PersonaSpecialty.BackendExecutor, LocalModel, ModelTier.Weak)]
    [InlineData(PersonaSpecialty.BackendExecutor, CloudModel, null)]
    [InlineData(PersonaSpecialty.Reviewer, CloudModel, ModelTier.Weak)]
    [InlineData(PersonaSpecialty.Reviewer, CloudModel, null)]
    [InlineData(PersonaSpecialty.None, null, ModelTier.Weak)]
    [InlineData(PersonaSpecialty.None, null, ModelTier.Strong)]
    public void LocalTurnOf_СовпадаетСБоевымExecutorModel(
        PersonaSpecialty specialty, string? personaModel, ModelTier? taskTier)
    {
        var persona = personaModel is null ? null : NewPersona("Персона", specialty, personaModel);
        var task = NewTask(persona?.Id, taskTier);

        var model = _models.ExecutorModel(task, persona, Owner);
        var expected = model is not null
            && _resolver.ResolveProviderKeyByModel(model) is { } key
            && _resolver.IsLocal(key);

        (_sut.LocalTurnOf(task, Owner) is not null).Should().Be(expected,
            $"эффективная модель задачи — «{model ?? "<не определена>"}»: решает только её провайдер");
    }
}
