using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

// Провижнер предмета ребёнка (ChildTutorProvisioner, Б17a, ADR-041 ред. 7, решения 69–70, И10)
// и тьютор профиля (EnsureAsync, KM-B1d).
// Контракт провижнера предмета: проект (ChildSubjectProjectProvisioner, Б8b) → проектная персона
// (владелец — ребёнок, Scope = Project, ProjectId = проект, характер из шаблона, MemoryEnabled
// = false, Tools без web, аватар — инициалы) → руководитель проекта (DefaultPersonaId —
// единственная точка правды) → кураторы (заглушка, Б14a). Идемпотентность: повтор → тот же
// руководитель без создания. Гонка: ровно один проект и одна персона. Сбой после проекта:
// проект остаётся, персоны-сироты нет, повтор подхватывает. Поля ChildSubject.TutorPersonaId
// и контракта сирот (Т2d) сняты Б17c.
public class ChildTutorProvisionerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _homesRoot;
    private readonly UserStore _users;
    private readonly PersonaManager _personas;
    private readonly ProjectManager _projects;
    private readonly UserHomeResolver _homes;
    private readonly TestSessionBroadcaster _broadcaster;
    private readonly ChildSubjectProjectProvisioner _subjectProjects;
    private readonly ChildTutorProvisioner _sut;
    private readonly string _childId;
    private readonly string _adultId;

    // Логин с нижним регистром и slug-имена предметов: путь дома резолвится через
    // ToLowerInvariant, и точное сравнение RootPath с ожидаемым путём верно на любой ОС.
    private const string ChildName = "kid.test";

    public ChildTutorProvisionerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc_child_tutor_" + Guid.NewGuid().ToString("N"));
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
        // Предмет профиля (форма записи Т2a, ключ пустой — как после живого слияния стора):
        // провижер читает его с той же модели.
        _users.GetById(_childId)!.ChildMode!.Subjects.Add(new ChildSubject
        {
            Id = "math", Label = "Математика", Glyph = "×÷", Color = "info",
        });

        var appSettings = new AppSettingsService(config);
        _homes = UserHomeResolver.WithoutOverrides(appSettings);
        _personas = new PersonaManager(config);
        _projects = new ProjectManager(config, _users, appSettings, homes: _homes);
        _broadcaster = new TestSessionBroadcaster();
        _subjectProjects = new ChildSubjectProjectProvisioner(_users, _projects, _homes,
            NullLogger<ChildSubjectProjectProvisioner>.Instance);
        _sut = new ChildTutorProvisioner(_users, _personas, _broadcaster, _subjectProjects, _projects,
            null, NullLogger<ChildTutorProvisioner>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // Предмет в профиль — тот же формат записи, что у контроллера Т2a: ключ пустой или живой.
    private void SeedSubject(string id, string label) =>
        _users.GetById(_childId)!.ChildMode!.Subjects.Add(new ChildSubject
        {
            Id = id, Label = label, Glyph = "×", Color = "info",
        });

    // Проект-предмета по маркеру — per-owner, тот же поиск, что у провижнера.
    private Project? SubjectProject(string subjectId) =>
        _projects.GetByOwner(_childId).FirstOrDefault(p => p.ChildSubjectId == subjectId);

    private int SubjectProjectCount(string subjectId) =>
        _projects.GetAll().Count(p => p.ChildSubjectId == subjectId);

    private int CreatedBroadcastCount() => _broadcaster.Owner.Select(t => t.Message)
        .OfType<PersonasChangedMessage>().Count(m => m.Action == "created");

    // ======== тьютор профиля (EnsureAsync, KM-B1d) ========

    [Fact]
    public async Task EnsureAsync_СоздаётТьютора_ВладелецРебёнок_БезWeb_ПамятьВыключена()
    {
        var id = await _sut.EnsureAsync(_childId);

        id.Should().NotBeNull();
        var persona = _personas.Get(id!, _childId);
        persona.Should().NotBeNull();
        persona!.OwnerId.Should().Be(_childId);
        persona.Name.Should().Be("Помощник");
        persona.Role.Should().Be("Репетитор 4–5 класс: математика, русский, английский");
        persona.Tools.Should().NotBeNull();
        persona.Tools!.Should().NotContain("web", "интернет ребёнку не открывается");
        persona.MemoryEnabled.Should().BeFalse("долгая память тьютора выключена");
        persona.Avatar.Kind.Should().Be(PersonaAvatarKind.Initials, "аватар — инициалы, не генерация");
        _personas.GetByOwner(_childId).Should().HaveCount(1);

        // Ключ закреплён в профиле ребёнка — признак «это тьютор»
        _users.GetById(_childId)!.ChildMode!.TutorPersonaId.Should().Be(id);

        // Вещание только «created»: тьютор не дефолт-персона ребёнка
        var actions = _broadcaster.Owner.Select(t => t.Message)
            .OfType<PersonasChangedMessage>().Select(m => m.Action).ToList();
        actions.Should().Contain("created");
        actions.Should().NotContain("default");
    }

    [Fact]
    public async Task EnsureAsync_Повтор_ВозвращаетТотЖеId_БезДубля()
    {
        var first = await _sut.EnsureAsync(_childId);
        var second = await _sut.EnsureAsync(_childId);

        first.Should().NotBeNull();
        second.Should().Be(first, "повторный вызов не плодит второго тьютора");
        _personas.GetByOwner(_childId).Should().HaveCount(1);
    }

    [Fact]
    public async Task EnsureAsync_Parallel_CreatesOnePersona()
    {
        // Гонка на разных потоках: без per-ключевого семафора оба вызова входят в критическую
        // секцию одновременно (check → Create) и плодят двух тьюторов. Однопоточный прогон
        // гонку не ловит в принципе: в EnsureAsync нет yield-точек до конца критической секции,
        // поэтому Task.Run обязателен.
        var first = Task.Run(() => _sut.EnsureAsync(_childId));
        var second = Task.Run(() => _sut.EnsureAsync(_childId));
        var results = await Task.WhenAll(first, second);

        results.Should().NotContainNulls();
        results.Select(id => id!).Distinct().Should().ContainSingle(
            "два одновременных вызова дают ровно одну персону");
        _personas.GetByOwner(_childId).Should().HaveCount(1);
        CreatedBroadcastCount().Should().Be(1, "вещание «created» — ровно один раз на двух конкурентов");
    }

    [Fact]
    public async Task EnsureAsync_ОбычномуПользователю_НичегоНеСоздаёт()
    {
        var id = await _sut.EnsureAsync(_adultId);

        id.Should().BeNull("тьютор — только ребёнку: без профиля нечем закрепить ключ");
        _personas.GetByOwner(_adultId).Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureAsync_ВисящийКлюч_ПровитсяЗаново_ИПерезакрепляется()
    {
        // Ключ указывает на удалённую руками персону: он не возвращается —
        // тьютор провится заново, ключ перезаписывается живым.
        _users.SetChildTutorPersona(_childId, Guid.NewGuid().ToString());

        var id = await _sut.EnsureAsync(_childId);

        id.Should().NotBeNull();
        _users.GetById(_childId)!.ChildMode!.TutorPersonaId.Should().Be(id);
        _personas.GetByOwner(_childId).Should().HaveCount(1);
    }

    [Fact]
    public async Task EnsureAsync_ИсключениеЗаписиКлюча_УдаляетПерсону_ИБросит()
    {
        // Отказ стора при закреплении ключа (доработка KM-B1d, вердикт d4fc053c): users.json
        // недостижим — на его пути каталог с тем же именем, File.Move в JsonFileStore.Save
        // (JsonFileStore.cs:332) бросит на обеих платформах. Create при этом пишет другой
        // файл (personas.json) и успевает. Персона, созданная до отказа, не должна осиротеть
        // без ключа: провижер удаляет её и пробрасывает исключение вызывающему (KM-B1e — 500).
        var usersPath = Path.Combine(_tempDir, "users.json"); // путь ResolveUsersFilePath (UserStore.cs:55-60)
        File.Delete(usersPath);
        Directory.CreateDirectory(usersPath);

        await ((Func<Task>)(() => _sut.EnsureAsync(_childId)))
            .Should().ThrowAsync<Exception>("исключение записи ключа доходит до вызывающего");

        _personas.GetByOwner(_childId).Should().BeEmpty(
            "полуперсоны не бывает: отказ закрепления ключа не оставляет сироту");
    }

    [Fact]
    public async Task EnsureAsync_ИсключениеЗаписиКлюча_ИОтказОтката_НаружуИсходноеИсключение()
    {
        // Продолжение теста выше: отказ закрепления И сбой самого отката (хвост R-KM-B1d,
        // ddbc7a1c). Отказ удаления моделируем подпиской на OnPersonaDeleted (PersonaManager.cs:1482,
        // вызывается синхронно внутри Delete): её InvalidOperationException делает исключение
        // удаления различимым. Откат не должен маскировать причину: наружу — исключение Save
        // users.json (каталог на пути файла, как в тесте выше), а не исключение удаления.
        var usersPath = Path.Combine(_tempDir, "users.json"); // путь ResolveUsersFilePath (UserStore.cs:55-60)
        File.Delete(usersPath);
        Directory.CreateDirectory(usersPath);
        _personas.OnPersonaDeleted += _ => throw new InvalidOperationException("откат не проходит");

        var thrown = (await ((Func<Task>)(() => _sut.EnsureAsync(_childId)))
            .Should().ThrowAsync<Exception>("исключение записи ключа доходит до вызывающего")).Which;
        thrown.Should().NotBeOfType<InvalidOperationException>(
            "сбой отката проглатывается с логом: исходное исключение сохранения не маскируется");

        _personas.GetByOwner(_childId).Should().BeEmpty(
            "полуперсоны не бывает: откат удалён из состояния до отказа OnPersonaDeleted");
    }

    [Fact]
    public async Task EnsureAsync_ПрофильИсчезПриЗакреплении_УдаляетПерсону_ВозвращаетNull()
    {
        // Профиль ребёнка исчез между перечитыванием под локом и закреплением ключа
        // (параллельная правка админа): SetChildTutorPersona (UserStore.cs:616-621)
        // возвращает false. Это «профиля нет» из контракта EnsureAsync — провижер удаляет
        // созданную персону и возвращает null, сирота в personas.json не остаётся.
        // Исчезновение моделируем на единственной точке между проверкой и записью — событии
        // OnPersonaCreated (PersonaManager.cs:656), которое Create поднимает синхронно ПОСЛЕ
        // перечитывания провижера: без него профиль пришлось бы снять до вызова, и Ensure
        // коротко замкнулся бы на ранней проверке, не доходя до закрепления.
        _personas.OnPersonaCreated += _ => _users.GetById(_childId)!.ChildMode = null;

        var id = await _sut.EnsureAsync(_childId);

        id.Should().BeNull("ключа нет — провижн невозможен");
        _personas.GetByOwner(_childId).Should().BeEmpty(
            "полуперсоны не бывает: отказ закрепления ключа не оставляет сироту");
    }

    // ======== провижер предмета (Б17a): проект → персона → руководитель ========

    [Fact]
    public async Task EnsureSubjectTutor_СоздаётПроектПерсонуИРуководителя_ВладелецРебёнок_БезWeb_ПамятьВыключена()
    {
        var r = await _sut.EnsureSubjectTutorAsync(_childId, "math");

        r.Should().NotBeNull();
        r!.Value.Created.Should().BeTrue("персона создана этим вызовом — точный ответ знает провижер под своим замком");

        // 1. Проект-предмета (Б8b): маркер, владелец — ребёнок.
        var project = SubjectProject("math");
        project.Should().NotBeNull("провижнер предмета заводит проект через провижер Б8b");
        project!.ChildSubjectId.Should().Be("math");
        project.OwnerId.Should().Be(_childId);

        // 2. Проектная персона-репетитор (решение 69).
        var persona = _personas.Get(r.Value.Id, _childId);
        persona.Should().NotBeNull();
        persona!.OwnerId.Should().Be(_childId);
        persona.Scope.Should().Be(PersonaScope.Project, "репетитор — проектная персона (решение 69)");
        persona.ProjectId.Should().Be(project.Id, "персона привязана к проекту предмета");
        persona.Name.Should().Be("Репетитор: Математика", "имя несёт предмет: промпт-житель при пустом role даёт «Ты — {Имя}»");
        persona.Role.Should().BeNull("роли нет: предмет несёт имя");
        persona.Specialty.Should().Be(PersonaSpecialty.None);
        persona.TemplateKey.Should().BeNull();
        persona.MemoryEnabled.Should().BeFalse("долгая память репетитора выключена (решение 70)");
        persona.Tools.Should().NotBeNull();
        persona.Tools!.Should().NotContain("web", "интернет ребёнку не открывается вовсе");
        persona.Bindings.Should().BeNull("привязок у репетитора нет");
        persona.AutomationRules.Should().BeNull("правил автоматизации у репетитора нет");
        persona.Avatar.Kind.Should().Be(PersonaAvatarKind.Initials, "аватар — инициалы, не генерация: не списывает бюджет ребёнка (И5 №12)");
        persona.Contract.Should().NotBeNull("характер из шаблона (решение 70)");
        persona.Contract!.Character.Should().NotBeNullOrWhiteSpace();

        // 3. Руководитель проекта — единственная точка правды (И10).
        project.DefaultPersonaId.Should().Be(r.Value.Id);

        // Вещание только «created»: репетитор — руководитель проекта, не дефолт-персона ребёнка.
        var actions = _broadcaster.Owner.Select(t => t.Message)
            .OfType<PersonasChangedMessage>().Select(m => m.Action).ToList();
        actions.Should().Contain("created");
        actions.Should().NotContain("default");
    }

    [Fact]
    public async Task EnsureSubjectTutor_Повтор_ТотЖеРуководитель_БезДубля()
    {
        var first = await _sut.EnsureSubjectTutorAsync(_childId, "math");
        var second = await _sut.EnsureSubjectTutorAsync(_childId, "math");

        first.Should().NotBeNull();
        second!.Value.Id.Should().Be(first!.Value.Id, "повторный вызов не плодит второго репетитора");
        second.Value.Created.Should().BeFalse("живой руководитель — не создание этого вызова");
        SubjectProjectCount("math").Should().Be(1);
        _personas.GetByOwner(_childId).Should().HaveCount(1);
        CreatedBroadcastCount().Should().Be(1, "повтор не вещает");
    }

    [Fact]
    public async Task EnsureSubjectTutor_ГонкаДвухПровижнов_РовноОдинПроектИПерсона()
    {
        // Гонка на разных потоках (Б17a): без per-ключевого семафора childId:subjectId оба
        // вызова проходят проверку «живого руководителя нет» и создают персону — два руководителя
        // на один проект (проект при этом один: его держит свой замок провижера Б8b).
        // Мутация «снять блокировку» (LockFor → новый семафор на каждый вызов) — красный
        // на первой же итерации: Distinct даёт два id. Однопоточный прогон гонку не ловит
        // в принципе: до конца критической секции нет yield-точек, Task.Run обязателен.
        for (var iter = 0; iter < 20; iter++)
        {
            var subjectId = $"s{iter}";
            SeedSubject(subjectId, $"Предмет {iter}");
            var first = Task.Run(() => _sut.EnsureSubjectTutorAsync(_childId, subjectId));
            var second = Task.Run(() => _sut.EnsureSubjectTutorAsync(_childId, subjectId));
            var results = await Task.WhenAll(first, second);

            results.Should().NotContainNulls($"итерация {iter}: провижн не завершился");
            results.Select(r => r!.Value.Id).Distinct().Should().ContainSingle(
                $"итерация {iter}: гонка дала двух репетиторов");

            var project = SubjectProject(subjectId);
            project.Should().NotBeNull();
            SubjectProjectCount(subjectId).Should().Be(1, $"итерация {iter}: гонка дала дубль проекта");
            _personas.GetByOwner(_childId).Count(p => p.ProjectId == project!.Id)
                .Should().Be(1, $"итерация {iter}: персон проекта — ровно одна");
            project!.DefaultPersonaId.Should().Be(results[0]!.Value.Id,
                $"итерация {iter}: руководитель перезаписан вторым конкурентом");
            CreatedBroadcastCount().Should().Be(iter + 1, $"итерация {iter}: вещание «created» — ровно одно на пару");
        }
    }

    [Fact]
    public async Task EnsureSubjectTutor_СбойПерсоныПослеПроекта_ПроектОстаётся_СиротыНет_ПовторПодхватит()
    {
        // Решение 70 и его откат: persona Create бросает ПОСЛЕ того, как проект уже создан и
        // записан. personas.json недостижим — каталог на пути файла (тот же приём, что у
        // теста EnsureAsync выше): Create персон бросает на обеих платформах.
        // Контракт: проект остаётся (повторный провижна подхватывает его по маркеру),
        // персоны-сироты нет, наружу — исключение стора (контроллер — 500 с откатом профиля),
        // не необработанное состояние.
        var personasPath = Path.Combine(_tempDir, "personas.json"); // путь стора персон (PersonaManager.cs:68)
        File.Delete(personasPath);
        Directory.CreateDirectory(personasPath);

        await ((Func<Task>)(() => _sut.EnsureSubjectTutorAsync(_childId, "math")))
            .Should().ThrowAsync<Exception>("ошибка стора не глотается");

        SubjectProjectCount("math").Should().Be(1, "проект создан и остаётся: его повторный провижна подхватит");
        SubjectProject("math")!.DefaultPersonaId.Should().BeNull("закреплять было нечем");
        // Сироты нет в сторе: personas.json под каталогом не записан ни одной персон —
        // Create бросает на Save (in-memory запись упавшего Create — артефакт процесса
        // PersonaManager, не его стора: после рестарта её нет).
        File.Exists(personasPath).Should().BeFalse("ни одной персон не записано в стор");
        Directory.Delete(personasPath);

        var r = await _sut.EnsureSubjectTutorAsync(_childId, "math");
        r.Should().NotBeNull("после устранения препятствия повторный вызов успешен");
        r!.Value.Created.Should().BeTrue();
        SubjectProjectCount("math").Should().Be(1, "повтор подхватывает существующий проект, а не заводит новый");
        SubjectProject("math")!.DefaultPersonaId.Should().Be(r.Value.Id);
        _personas.Get(r.Value.Id, _childId)!.ProjectId.Should().Be(SubjectProject("math")!.Id);
    }

    [Fact]
    public async Task EnsureSubjectTutor_СбойЗакрепленияРуководителя_УдаляетПерсону_ПроектОстаётся_ИБросит()
    {
        // Сбой ровно на закреплении руководителя (SetDefaultPersona, ProjectManager.cs:303):
        // проект и персона уже записаны; препятствие ставится в точке между ними — хук
        // OnPersonaCreated (синхронный, после Save персон, PersonaManager.cs:656) заменяет
        // projects.json каталогом: следующий Save стора проектов бросит. Отказ закрепления
        // компенсируется удалением созданной персоны (образец KM-B1d): персоны без
        // руководителя не остаётся; проект — остаётся (решение 70: повтор подхватит).
        var projectsPath = Path.Combine(_tempDir, "projects.json"); // путь стора проектов (DataPath)
        var tripped = false;
        _personas.OnPersonaCreated += _ =>
        {
            if (tripped) return; // препятствие — на один вызов: повтор обязан пройти
            tripped = true;
            File.Delete(projectsPath);
            Directory.CreateDirectory(projectsPath);
        };

        await ((Func<Task>)(() => _sut.EnsureSubjectTutorAsync(_childId, "math")))
            .Should().ThrowAsync<Exception>("исключение записи руководителя доходит до вызывающего");

        _personas.GetByOwner(_childId).Should().BeEmpty(
            "полуперсоны без руководителя не бывает: отказ закрепления удаляет созданную");
        SubjectProjectCount("math").Should().Be(1, "проект остаётся: его повторный провижна подхватит");

        Directory.Delete(projectsPath);
        var r = await _sut.EnsureSubjectTutorAsync(_childId, "math");
        r.Should().NotBeNull();
        r!.Value.Created.Should().BeTrue();
        SubjectProjectCount("math").Should().Be(1, "повтор не заводит второй проект");
        SubjectProject("math")!.DefaultPersonaId.Should().Be(r.Value.Id);
    }

    [Fact]
    public async Task EnsureSubjectTutor_ОтказВещания_УдаляетСозданнуюПерсону_ИБросит()
    {
        // Отказ вещания (не отмена) — сбой запроса: созданная персона удаляется откатом,
        // исключение идёт контроллеру (500 с откатом профиля): отказанный вызов не оставляет
        // «Репетитор: …» в сторе (тот же контракт Т2b; там доказательством создания служил
        // записанный ключ, здесь — сам факт создания под замком).
        var sut = new ChildTutorProvisioner(_users, _personas,
            new ThrowingOnCreated(_broadcaster), _subjectProjects, _projects,
            null, NullLogger<ChildTutorProvisioner>.Instance);

        await ((Func<Task>)(() => sut.EnsureSubjectTutorAsync(_childId, "math")))
            .Should().ThrowAsync<Exception>("отказ доставки — сбой запроса, не тихий пропуск");

        _personas.GetByOwner(_childId).Should().BeEmpty("отказанный вызов не оставляет репетитора");
    }

    [Fact]
    public async Task EnsureSubjectTutor_НетПредмета_НичегоНеСоздаёт()
    {
        var r = await _sut.EnsureSubjectTutorAsync(_childId, "no-such-subject");

        r.Should().BeNull("предмета в профиле нет — проект и персона не заводятся");
        _personas.GetByOwner(_childId).Should().BeEmpty();
        _projects.GetAll().Should().BeEmpty("проект под несуществующий предмет не заводится");
    }

    [Fact]
    public async Task EnsureSubjectTutor_НеРебёнку_НичегоНеСоздаёт()
    {
        var r = await _sut.EnsureSubjectTutorAsync(_adultId, "math");

        r.Should().BeNull("предмет — только ребёнку: у обычного пользователя профиля нет");
        _projects.GetAll().Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureSubjectTutor_ВисящийРуководитель_ПровитсяЗаново_ИПерезаписывается()
    {
        // DefaultPersonaId указывает на удалённую руками персону: он не возвращается —
        // репетитор провится заново, руководитель перезаписывается живым.
        var first = await _sut.EnsureSubjectTutorAsync(_childId, "math");
        first.Should().NotBeNull();
        _personas.Delete(first!.Value.Id, _childId);

        var second = await _sut.EnsureSubjectTutorAsync(_childId, "math");

        second.Should().NotBeNull();
        second!.Value.Id.Should().NotBe(first.Value.Id);
        second.Value.Created.Should().BeTrue();
        SubjectProject("math")!.DefaultPersonaId.Should().Be(second.Value.Id);
        _personas.GetByOwner(_childId).Count(p => p.Scope == PersonaScope.Project).Should().Be(1);
    }

    // Бросающая обёртка над рекордером: всё проходит в него как обычно; на «created» —
    // InvalidOperationException, настоящий отказ доставки (не отмена): ровно то, что обязан
    // покрывать откат созданной персоны в EnsureSubjectTutorAsync.
    private sealed class ThrowingOnCreated : ISessionBroadcaster
    {
        private readonly TestSessionBroadcaster _inner;

        public ThrowingOnCreated(TestSessionBroadcaster inner) => _inner = inner;

        public Task ToOwner(string ownerId, ServerMessage message)
        {
            if (message is PersonasChangedMessage m && m.Action == "created")
                throw new InvalidOperationException("имитация сбоя вещания создания");
            return _inner.ToOwner(ownerId, message);
        }

        public Task ToSession(string sessionId, ServerMessage message) => _inner.ToSession(sessionId, message);
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message)
            => _inner.ToSessionExcept(sessionId, exceptConnectionId, message);
        public Task ToProject(string projectId, ServerMessage message) => _inner.ToProject(projectId, message);
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message)
            => _inner.ToPreviewLog(projectId, serviceId, message);
    }

    // Бросающая обёртка на «deleted» указанной персоны: всё остальное проходит как обычно
    // (тот же приём, что у ThrowingOnCreated).
    private sealed class ThrowingOnDeleted : ISessionBroadcaster
    {
        private readonly TestSessionBroadcaster _inner;
        private readonly string _personaId;

        public ThrowingOnDeleted(TestSessionBroadcaster inner, string personaId)
        {
            _inner = inner;
            _personaId = personaId;
        }

        public Task ToOwner(string ownerId, ServerMessage message)
        {
            if (message is PersonasChangedMessage m && m.Action == "deleted" && m.PersonaId == _personaId)
                throw new InvalidOperationException("имитация сбоя вещания удаления");
            return _inner.ToOwner(ownerId, message);
        }

        public Task ToSession(string sessionId, ServerMessage message) => _inner.ToSession(sessionId, message);
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message)
            => _inner.ToSessionExcept(sessionId, exceptConnectionId, message);
        public Task ToProject(string projectId, ServerMessage message) => _inner.ToProject(projectId, message);
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message)
            => _inner.ToPreviewLog(projectId, serviceId, message);
    }
}
