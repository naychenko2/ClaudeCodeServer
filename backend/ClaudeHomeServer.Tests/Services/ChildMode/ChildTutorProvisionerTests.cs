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

// Провижн персоны-тьютора ребёнка (ChildTutorProvisioner, KM-B1d) и репетитора предмета (Т2b,
// разрез §2). Контракт: тьютор — только ребёнку, владелец — сам ребёнок, Tools без web, долгая
// память выключена, аватар — инициалы; идемпотентность (повтор → тот же id), гонка
// (параллельные вызовы → одна персона), ключ закреплён в ChildProfile.TutorPersonaId /
// ChildSubject.TutorPersonaId, висящий ключ провится заново.
public class ChildTutorProvisionerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly UserStore _users;
    private readonly PersonaManager _personas;
    private readonly TestSessionBroadcaster _broadcaster;
    private readonly ChildTutorProvisioner _sut;
    private readonly string _childId;
    private readonly string _adultId;

    public ChildTutorProvisionerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc_child_tutor_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build();

        _users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _adultId = _users.GetFirst()!.Id; // дефолтный admin пустого стора — обычный пользователь
        _childId = _users.Add("kid.test", "kid-pw-123456", "user").Id;
        // Профиль ребёнка пишет админ (его PUT — KM-B1e); здесь — прямо в стор, провижер
        // обязан читать его с той же модели.
        _users.GetById(_childId)!.ChildMode = new ChildProfile { Enabled = true };
        // Предмет профиля (форма записи Т2a, ключ пустой — как после живого слияния стора):
        // провижер репетитора читает его с той же модели, что и SetChildSubjectTutor.
        _users.GetById(_childId)!.ChildMode!.Subjects.Add(new ChildSubject
        {
            Id = "math", Label = "Математика", Glyph = "×÷", Color = "info",
            ProviderKey = "kids", Model = "sonnet",
        });

        _personas = new PersonaManager(config);
        _broadcaster = new TestSessionBroadcaster();
        _sut = new ChildTutorProvisioner(_users, _personas, _broadcaster,
            NullLogger<ChildTutorProvisioner>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

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
        _broadcaster.Owner.Select(t => t.Message)
            .OfType<PersonasChangedMessage>().Count(m => m.Action == "created")
            .Should().Be(1, "вещание «created» — ровно один раз на двух конкурентов");
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

    // ======== репетитор предмета (Т2b, разрез §2) ========

    [Fact]
    public async Task EnsureSubjectTutor_СоздаётРепетитора_ВладелецРебёнок_БезWeb_ПамятьВыключена()
    {
        var id = await _sut.EnsureSubjectTutorAsync(_childId, "math");

        id.Should().NotBeNull();
        var persona = _personas.Get(id!, _childId);
        persona.Should().NotBeNull();
        persona!.OwnerId.Should().Be(_childId);
        persona.Scope.Should().Be(PersonaScope.Global, "область репетитора предмета зафиксирована: Global");
        persona.ProjectId.Should().BeNull("создаётся без projectId: репетитор не привязан к проекту");
        persona.Name.Should().Be("Репетитор: Математика", "имя несёт предмет: промпт-строитель при пустом role даёт «Ты — {Имя}»");
        persona.Role.Should().BeNull("роли в спецификации Т2b нет: предмет несёт имя");
        persona.Specialty.Should().Be(PersonaSpecialty.None);
        persona.TemplateKey.Should().BeNull();
        persona.MemoryEnabled.Should().BeFalse("долгая память репетитора предмета выключена");
        persona.Tools.Should().NotBeNull();
        persona.Tools!.Should().NotContain("web", "интернет ребёнку не открывается вовсе");
        persona.Bindings.Should().BeNull("привязок у репетитора предмета нет");
        persona.AutomationRules.Should().BeNull("правил автоматизации у репетитора предмета нет");
        persona.Avatar.Kind.Should().Be(PersonaAvatarKind.Initials, "аватар — инициалы, не генерация: не списывает бюджет ребёнка (И5 №12)");

        // Ключ закреплён в записи предмета — признак «это репетитор этого предмета»
        _users.GetById(_childId)!.ChildMode!.Subjects[0].TutorPersonaId.Should().Be(id);

        // Вещание только «created»: репетитора предмета не бывает дефолт-персоной
        var actions = _broadcaster.Owner.Select(t => t.Message)
            .OfType<PersonasChangedMessage>().Select(m => m.Action).ToList();
        actions.Should().Contain("created");
        actions.Should().NotContain("default");
    }

    [Fact]
    public async Task EnsureSubjectTutor_Повтор_ВозвращаетТотЖеId_БезДубля()
    {
        var first = await _sut.EnsureSubjectTutorAsync(_childId, "math");
        var second = await _sut.EnsureSubjectTutorAsync(_childId, "math");

        first.Should().NotBeNull();
        second.Should().Be(first, "повторный вызов не плодит второго репетитора предмета");
        _personas.GetByOwner(_childId).Should().HaveCount(1);
    }

    [Fact]
    public async Task EnsureSubjectTutor_Parallel_CreatesOnePersona()
    {
        // Гонка на разных потоках: без per-ребёночного семафора оба вызова входят в критическую
        // секцию одновременно (check → Create) и плодят двух репетитора (тот же разбор, что у
        // EnsureAsync_Parallel: однопоточный прогон гонку не ловит в принципе).
        var first = Task.Run(() => _sut.EnsureSubjectTutorAsync(_childId, "math"));
        var second = Task.Run(() => _sut.EnsureSubjectTutorAsync(_childId, "math"));
        var results = await Task.WhenAll(first, second);

        results.Should().NotContainNulls();
        results.Select(id => id!).Distinct().Should().ContainSingle(
            "два одновременных вызова на один предмет дают ровно одну персону");
        _personas.GetByOwner(_childId).Should().HaveCount(1);
        _broadcaster.Owner.Select(t => t.Message)
            .OfType<PersonasChangedMessage>().Count(m => m.Action == "created")
            .Should().Be(1, "вещание «created» — ровно один раз на двух конкурентов");
    }

    [Fact]
    public async Task EnsureSubjectTutor_НетПредмета_НичегоНеСоздаёт()
    {
        var id = await _sut.EnsureSubjectTutorAsync(_childId, "no-such-subject");

        id.Should().BeNull("предмета в профиле нет — нечем закрепить персону");
        _personas.GetByOwner(_childId).Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureSubjectTutor_ВисящийКлюч_ПровитсяЗаново_ИПерезакрепляется()
    {
        // Ключ предмета указывает на удалённую руками персону: он не возвращается —
        // репетитор провится заново, ключ перезаписывается живым.
        _users.SetChildSubjectTutor(_childId, "math", Guid.NewGuid().ToString());

        var id = await _sut.EnsureSubjectTutorAsync(_childId, "math");

        id.Should().NotBeNull();
        _users.GetById(_childId)!.ChildMode!.Subjects[0].TutorPersonaId.Should().Be(id);
        _personas.GetByOwner(_childId).Should().HaveCount(1);
    }

    [Fact]
    public async Task EnsureSubjectTutor_ИсключениеЗаписиКлюча_УдаляетПерсону_ИБросит()
    {
        // Отказ стора при закреплении ключа предмета: users.json недостижим (каталог на пути
        // файла, тот же приём, что у EnsureAsync-теста выше). Персона, созданная до отказа,
        // не осиротевает: провижер удаляет её и пробрасывает исключение (контроллер — 500
        // tutor_provision_failed с откатом профиля).
        var usersPath = Path.Combine(_tempDir, "users.json"); // путь ResolveUsersFilePath (UserStore.cs:55-60)
        File.Delete(usersPath);
        Directory.CreateDirectory(usersPath);

        await ((Func<Task>)(() => _sut.EnsureSubjectTutorAsync(_childId, "math")))
            .Should().ThrowAsync<Exception>("исключение записи ключа доходит до вызывающего");

        _personas.GetByOwner(_childId).Should().BeEmpty(
            "полуперсоны не бывает: отказ закрепления ключа не оставляет сироту");
    }

    // ======== репетитор-сирота удалённого предмета (Т2d, разрез §1) ========

    // Предмет в профиль пишет тот же формат записи, что контроллер Т2a (через стор):
    // ключ — как его оставил бы слияние (пустой или живой).
    private void SeedSubject(string id, string label, string tutorPersonaId = "") =>
        _users.GetById(_childId)!.ChildMode!.Subjects.Add(new ChildSubject
        {
            Id = id, Label = label, Glyph = "×", Color = "info",
            ProviderKey = "kids", Model = "sonnet", TutorPersonaId = tutorPersonaId,
        });

    [Fact]
    public async Task RemoveOrphan_СиротаУдалена_ЧатыПредметаНаМесте()
    {
        var tutorId = await _sut.EnsureSubjectTutorAsync(_childId, "math");
        tutorId.Should().NotBeNull();
        // Предмет удалён из профиля (эффект PUT с телом без него): ключ доживает только
        // в снимке previous — контроллер принёс его кандидатом.
        _users.GetById(_childId)!.ChildMode!.Subjects.Clear();

        // Чат удалённого предмета (Т1c/Т1d — «только чтение», не трогать): отдельный файл
        // состояния, провижер к нему не обращается вовсе.
        var chatFile = Path.Combine(_tempDir, "chats", "child-math-chat.json");
        Directory.CreateDirectory(Path.GetDirectoryName(chatFile)!);
        File.WriteAllText(chatFile, "{\"id\":\"child-math-chat\"}");

        await _sut.RemoveOrphanTutorsAsync(_childId, [tutorId!]);

        _personas.Get(tutorId!, _childId).Should().BeNull("сирота без ссылок удаляется");
        _personas.GetByOwner(_childId).Should().BeEmpty();
        File.Exists(chatFile).Should().BeTrue("чаты предмета — вне контракта удаления");

        // Вещание «deleted» — как у стора персон; «created» провижера ему не помеха
        _broadcaster.Owner.Select(t => t.Message)
            .OfType<PersonasChangedMessage>().Count(m => m.Action == "deleted" && m.PersonaId == tutorId)
            .Should().Be(1);
    }

    [Fact]
    public async Task RemoveOrphan_ПерсонаОбщаДвумПредметам_Остаётся()
    {
        // Одна персона ведёт два предмета (засев/миграция дают предмету тьютора профиля —
        // та же допустимость): удаление одного предмета не сиротит её — второй живой.
        var tutorId = await _sut.EnsureSubjectTutorAsync(_childId, "math");
        tutorId.Should().NotBeNull();
        SeedSubject("physics", "Физика", tutorId!);
        _users.GetById(_childId)!.ChildMode!.Subjects
            .RemoveAll(s => s.Id == "math"); // эффект PUT: math убран, physics остался

        await _sut.RemoveOrphanTutorsAsync(_childId, [tutorId!]);

        _personas.Get(tutorId!, _childId).Should().NotBeNull(
            "на кандидата ссылается оставшийся предмет — не сирота");
        _personas.GetByOwner(_childId).Should().HaveCount(1);
    }

    [Fact]
    public async Task RemoveOrphan_ПерсонаРавнаTutorPersonaIdПрофиля_Остаётся()
    {
        // Тьютор профиля (засев даёт предмету его): удалённый предмет ссылался той же
        // персоной — ссылка профиля сильнее удаления предмета.
        var profileTutor = await _sut.EnsureAsync(_childId);
        profileTutor.Should().NotBeNull();
        _users.SetChildSubjectTutor(_childId, "math", profileTutor!);
        _users.GetById(_childId)!.ChildMode!.Subjects.Clear();

        await _sut.RemoveOrphanTutorsAsync(_childId, [profileTutor!]);

        _personas.Get(profileTutor!, _childId).Should().NotBeNull(
            "TutorPersonaId профиля ссылается на персону — она не сирота");
        _personas.GetByOwner(_childId).Should().HaveCount(1);
    }

    [Fact]
    public async Task RemoveOrphan_НечегоУдалять_НичегоНеДелает()
    {
        var tutorId = await _sut.EnsureSubjectTutorAsync(_childId, "math");
        tutorId.Should().NotBeNull();

        // Пустой список и пустые ключи — не повод ни удалять, ни вещать; живая персона цела.
        await _sut.RemoveOrphanTutorsAsync(_childId, []);
        await _sut.RemoveOrphanTutorsAsync(_childId, ["", "  "]);

        _personas.Get(tutorId!, _childId).Should().NotBeNull();
        _broadcaster.Owner.Select(t => t.Message)
            .OfType<PersonasChangedMessage>().Count(m => m.Action == "deleted")
            .Should().Be(0, "нечего удалять — нечего и вещать");
    }

    [Fact]
    public async Task RemoveOrphan_УдалениеОдногоНеГлушитОстальные_ИОтказаНаружу()
    {
        var tutorId = await _sut.EnsureSubjectTutorAsync(_childId, "math");
        tutorId.Should().NotBeNull();
        _users.GetById(_childId)!.ChildMode!.Subjects.Clear(); // эффект PUT: предмета нет
        // Второй кандидат — фантом: персоны с таким id нет (висящий ключ удалённого
        // предмета). Отказ удаления одного — лог внутри провижера: наружу не выходит,
        // настоящий сирота удалён.
        var ghost = Guid.NewGuid().ToString();

        await _sut.Invoking(s => s.RemoveOrphanTutorsAsync(_childId, [ghost, tutorId!]))
            .Should().NotThrowAsync("сбой — лог, ответ вызывающего (200) не меняется");

        _personas.Get(tutorId!, _childId).Should().BeNull("сбой одного кандидата не глушит остальных");
    }

    [Fact]
    public async Task RemoveOrphanTutors_DeleteThrows_LogsAndContinues()
    {
        // Настоящий сбой удаления первого кандидата (минор R-T2d 66780808): не «персоны нет»,
        // как в тесте с фантомом, а исключение внутри Delete: OnPersonaDeleted поднимается
        // синхронно внутри Delete после Save (PersonaManager.cs:1481-1482) и бросает
        // IOException для первого кандидата — тип точен, на обеих платформах. Контракт:
        // исключение кандидата не выходит к вызывающему (его PUT уже сохранил профиль,
        // ответ 200), цикл продолжается с остальными.
        SeedSubject("physics", "Физика");
        var firstTutor = await _sut.EnsureSubjectTutorAsync(_childId, "math");
        var secondTutor = await _sut.EnsureSubjectTutorAsync(_childId, "physics");
        firstTutor.Should().NotBeNull();
        secondTutor.Should().NotBeNull();
        _personas.OnPersonaDeleted += p =>
        {
            if (p.Id == firstTutor) throw new IOException("имитация сбоя удаления персоны");
        };
        _users.GetById(_childId)!.ChildMode!.Subjects.Clear(); // эффект PUT: оба предмета убраны

        await _sut.Invoking(s => s.RemoveOrphanTutorsAsync(_childId, [firstTutor!, secondTutor!]))
            .Should().NotThrowAsync("сбой кандидата — лог внутри провижера, ответ вызывающего (200) не меняется");

        // Сбой первого не глушит цикл: второй удалён и о нём вещается.
        _personas.Get(secondTutor!, _childId).Should().BeNull("сбой одного кандидата не глушит остальных");
        _broadcaster.Owner.Select(t => t.Message)
            .OfType<PersonasChangedMessage>().Count(m => m.Action == "deleted" && m.PersonaId == secondTutor)
            .Should().Be(1, "цикл дошёл до второго кандидата");
        // Исключение первого — до его вещания: неудачная операция не вещается.
        _broadcaster.Owner.Select(t => t.Message)
            .OfType<PersonasChangedMessage>().Count(m => m.Action == "deleted" && m.PersonaId == firstTutor)
            .Should().Be(0, "исключение удаления раньше вещания");
    }

    [Fact]
    public async Task RemoveOrphanTutors_BroadcastThrows_PersonaDeleted_Continues()
    {
        // Реальный сбой вещания удаления (правка R-T2d, вердикт a18a26cf): ToOwner бросает
        // InvalidOperationException ПОСЛЕ того, как персона уже удалена. Прежний тест бросал
        // из OnPersonaDeleted внутри Delete — этот путь старый код ловил и без правки; здесь
        // бросает сам вещатель, и тест краснеет ровно когда вещание возвращается за пределы
        // per-кандидатного try: исключение выходит из метода к вызывающему.
        SeedSubject("physics", "Физика");
        var firstTutor = await _sut.EnsureSubjectTutorAsync(_childId, "math");
        var secondTutor = await _sut.EnsureSubjectTutorAsync(_childId, "physics");
        firstTutor.Should().NotBeNull();
        secondTutor.Should().NotBeNull();
        _users.GetById(_childId)!.ChildMode!.Subjects.Clear(); // эффект PUT: оба предмета убраны

        // Отдельный экземпляр провижера с бросающим стабом: провижн двух репетиторов выше
        // сделан через штатный _broadcaster (без отказа), бросает только вещание удаления.
        var sut = new ChildTutorProvisioner(_users, _personas,
            new ThrowingOnDeleted(_broadcaster, firstTutor!),
            NullLogger<ChildTutorProvisioner>.Instance);

        await sut.Invoking(s => s.RemoveOrphanTutorsAsync(_childId, [firstTutor!, secondTutor!]))
            .Should().NotThrowAsync("сбой вещания — лог внутри провижера, ответ вызывающего (200) не меняется");

        // Удаление предшествовало вещанию: отказ вещания его не отменяет и не откатывает.
        _personas.Get(firstTutor!, _childId).Should().BeNull(
            "персона удалена до вещания: сбой вещания не возвращает её");
        // Упавшее вещание не записано: клиенту про первого кандидата не сообщено ничего.
        _broadcaster.Owner.Select(t => t.Message)
            .OfType<PersonasChangedMessage>().Count(m => m.Action == "deleted" && m.PersonaId == firstTutor)
            .Should().Be(0, "исключение раньше записи стаба — неудачное вещание не доставлено");
        // Цикл дошёл до второго кандидата: он удалён и о нём вещается.
        _personas.Get(secondTutor!, _childId).Should().BeNull(
            "сбой вещания первого кандидата не глушит остальных");
        _broadcaster.Owner.Select(t => t.Message)
            .OfType<PersonasChangedMessage>().Count(m => m.Action == "deleted" && m.PersonaId == secondTutor)
            .Should().Be(1, "цикл дошёл до второго кандидата");
    }

    [Fact]
    public async Task RemoveOrphan_ParallelWithReassign_PersonaKept()
    {
        // Конкурентность Т2d: пока удаление идёт, параллельный PUT перестраивает профиль и
        // закрепляет кандидата за оставшимся предметом. Ставится в середину критической
        // секции детерминированно — через OnPersonaDeleted (поднимается синхронно внутри
        // Delete первого кандидата, PersonaManager.cs:1482): два Task.Run лишь перемешивали
        // бы потоки, и мутация ловилась бы случаем (первый вариант теста прошёл зелёным
        // именно так).
        // Важно: параллельный PUT — это замена объекта профиля (SetChildProfile пишет новый,
        // UserStore.cs:721), а не правка текущего: GetById отдаёт живую ссылку, и простая
        // мутация «прочитать до семафора» на мутируемом объекте не краснеет никогда.
        // Краснеет только настоящая устаревшая запись — снимок, отсоединённый заменой.
        // Первый кандидат — настоящий сирота: предмет «old» удалён, его репетитор больше
        // никем не referenced. При его удалении PUT оставляет только «physics» и закрепляет
        // за ним P. Удаление обязано увидеть новую ссылку: P остаётся, сирота удалена.
        SeedSubject("old", "Химия"); // предмет, который будет удалён (ключ провится ниже)
        var oldTutor = await _sut.EnsureSubjectTutorAsync(_childId, "old");
        var tutorId = await _sut.EnsureSubjectTutorAsync(_childId, "math");
        oldTutor.Should().NotBeNull();
        tutorId.Should().NotBeNull();
        SeedSubject("physics", "Физика"); // ключ пустой: на старте удаления ссылок нет

        _users.GetById(_childId)!.ChildMode!.Subjects
            .RemoveAll(s => s.Id == "old" || s.Id == "math"); // эффект PUT: оба предмета убраны

        // Тело того самого параллельного PUT: один «physics», ключ пустой — его write-форма.
        var putBody = new ChildProfile
        {
            Enabled = true, ProviderKey = "kids", Model = "sonnet",
            Subjects = [new ChildSubject
            {
                Id = "physics", Label = "Физика", Glyph = "×", Color = "info",
                ProviderKey = "kids", Model = "sonnet",
            }],
        };
        _personas.OnPersonaDeleted += p =>
        {
            if (p.Id != oldTutor) return;
            _users.SetChildProfile(_childId, putBody, resetRules: false); // новый объект профиля
            _users.SetChildSubjectTutor(_childId, "physics", tutorId!);   // закрепление в нём
        };

        await _sut.RemoveOrphanTutorsAsync(_childId, [oldTutor!, tutorId!]);

        _personas.Get(tutorId!, _childId).Should().NotBeNull(
            "закрепление пришло в середину удаления новым объектом профиля — живая ссылка: персона остаётся");
        _personas.Get(oldTutor!, _childId).Should().BeNull("настоящий сирота удалён");
        _users.GetById(_childId)!.ChildMode!.Subjects
            .First(s => s.Id == "physics").TutorPersonaId.Should().Be(tutorId);
    }

    // Бросающая обёртка над рекордером: всё, кроме «deleted» указанной персоны, проходит
    // в него как обычно; на «deleted» этой персоны — InvalidOperationException, настоящий
    // отказ доставки (не отмена): ровно то, что обязан покрывать per-кандидатный catch
    // RemoveOrphanTutorsAsync.
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
