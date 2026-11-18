using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

// Засев трёх предметов волны 1 существующим профилям детей (Т1b, срез §1) и досев предмета
// старым детским чатам (Т1b2).
// Контракт профилей: пустой Subjects → math/russian/english в порядке плиток, маршрут — из
// профиля; непустой не трогается; второй прогон — без изменений (идемпотентность состоянием,
// маркера нет); обычный пользователь не трогается; исключение стора — лог, старт идёт;
// параллельный PUT админа засевом не затирается.
// Контракт чатов: ChildSpace без предмета → math; чат с заданным предметом и не-детский чат —
// без изменений; UpdatedAt не двигается; чат, создающийся параллельно с досевом, своим
// предметом не затирается.
// Фикстура — общий хост ядра сессий: миграции нужны оба стора (users.json и sessions.json).
public class ChildSubjectsMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SessionCoreTestHost _host;
    private readonly UserStore _users;
    private readonly SessionManager _sessions;
    private readonly ChildSubjectsMigration _sut;
    private readonly string _childId;
    private readonly string _adultId;

    public ChildSubjectsMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc_child_subjects_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _host = SessionCoreTestHost.Build(_tempDir);
        _users = _host.Services.GetRequiredService<UserStore>();
        _sessions = _host.Manager;
        _adultId = _users.GetFirst()!.Id; // дефолтный admin пустого стора — обычный пользователь
        _childId = _users.Add("kid.subjects", "kid-pw-123456", "user").Id;
        // Профиль ребёнка с маршрутом пишет админ (его PUT — KM-B1e); здесь — прямо в стор.
        _users.GetById(_childId)!.ChildMode = new ChildProfile
        {
            Enabled = true,
            TutorPersonaId = "tutor-1",
            ProviderKey = "claude",
            Model = "sonnet",
        };

        _sut = new ChildSubjectsMigration(_users, _sessions, NullLogger<ChildSubjectsMigration>.Instance);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        // Контейнер гасится ДО удаления каталога: синглтоны стора дописывают несохранённое
        // в _tempDir. Запись history.json идёт из fire-and-forget обработчиков ядра — отсюда
        // ретрай в общем хелпере.
        _host.Dispose();
        TestFs.DeleteDirectoryResilient(_tempDir);
    }

    // ======== Первый проход: засев предметов профиля (Т1b) ========

    [Fact]
    public async Task StartAsync_ПустойПрофиль_ЗасеваетТриПредмета_ВПорядкеПлиток_СМаршрутомПрофиля()
    {
        await _sut.StartAsync(CancellationToken.None);

        // Порядок списка — порядок плиток на главной ребёнка
        var subjects = _users.GetById(_childId)!.ChildMode!.Subjects;
        subjects.Select(s => s.Id).Should().Equal("math", "russian", "english");
        subjects[0].Label.Should().Be("Математика");
        subjects[0].Glyph.Should().Be("×÷");
        subjects[0].Color.Should().Be("info");
        subjects[1].Label.Should().Be("Русский язык");
        subjects[1].Glyph.Should().Be("Аа");
        subjects[1].Color.Should().Be("rose");
        subjects[2].Label.Should().Be("Английский");
        subjects[2].Glyph.Should().Be("Ab");
        subjects[2].Color.Should().Be("success");
        // Маршрут — из профиля: засев своего маршрута не выдумывает
        subjects.Should().AllSatisfy(s =>
        {
            s.TutorPersonaId.Should().Be("tutor-1");
            s.ProviderKey.Should().Be("claude");
            s.Model.Should().Be("sonnet");
            s.Enabled.Should().BeTrue();
        });

        // Обычный пользователь не трогается: засев — только детям
        _users.GetById(_adultId)!.ChildMode.Should().BeNull();

        // Состояние доехало до стора (ключ записи — id ребёнка). Метки не-ASCII дефолтным
        // энкодером экранируются, проверяем id.
        File.ReadAllText(Path.Combine(_tempDir, "users.json"))
            .Should().Contain("\"math\"").And.Contain("\"russian\"").And.Contain("\"english\"");
    }

    [Fact]
    public async Task StartAsync_НепустойSubjects_НеТрогает()
    {
        // Админ добавил свой предмет до старта — засев не должен тронуть список
        _users.SeedChildSubjectsIfEmpty(_childId, [CustomSubject()]).Should().BeTrue();

        await _sut.StartAsync(CancellationToken.None);

        _users.GetById(_childId)!.ChildMode!.Subjects.Should().ContainSingle()
            .Which.Id.Should().Be("biology", "непустой Subjects засевом не перезаписывается");
    }

    [Fact]
    public async Task StartAsync_ВторойПрогон_БезИзменений()
    {
        await _sut.StartAsync(CancellationToken.None);
        var first = _users.GetById(_childId)!.ChildMode!.Subjects.Select(s => s.Id).ToList();
        first.Should().Equal("math", "russian", "english");

        // Идемпотентность состоянием: второй проход видит непустые списки и ничего не пишет
        await _sut.StartAsync(CancellationToken.None);

        _users.GetById(_childId)!.ChildMode!.Subjects.Select(s => s.Id).Should().Equal(first);
    }

    [Fact]
    public async Task StartAsync_ИсключениеСтора_СтартПродолжается()
    {
        // Каталог на месте users.json: запись стора бросает (паттерн ChildTutorProvisionerTests)
        var usersPath = Path.Combine(_tempDir, "users.json");
        File.Delete(usersPath);
        Directory.CreateDirectory(usersPath);

        var act = () => _sut.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync(
            "несостоявшийся засев — только лог: старт идёт, ребёнок до следующего старта живёт как до Т1b");
    }

    [Fact]
    public async Task Seed_ParallelWithAdminSet_AdminListWins()
    {
        // Гонка с конструктором окна: третий вызов стора (Add — хеш пароля под локом, ~80 мс)
        // держит лок, пока админ и засев встают в очередь на него. Админ queued раньше,
        // но при мутации «проверка пустоты вне лока» засев успевает прочесть пустой Subjects
        // ДО очереди — и после админа пишет свой тройный список поверх его предмета (красный).
        // При правильном коде (проверка и запись под одним локом) любой порядок отдачи лока
        // даёт список админа: засев проиграл очередь — видит непустой Subjects и пропускает;
        // выиграл — параллельный PUT затем заменяет профиль целиком.
        // Отдача Monitor-лока не FIFO, поэтому гонка повторяется: 20 повторов хватает,
        // чтобы мутация попала в запись хотя бы раз.
        for (var iter = 0; iter < 20; iter++)
        {
            // Возвращаем ребёнка в состояние «пустой Subjects»
            _users.GetById(_childId)!.ChildMode!.Subjects = [];

            var admin = new ChildProfile
            {
                Enabled = true,
                TutorPersonaId = "tutor-1",
                ProviderKey = "claude",
                Model = "sonnet",
                Subjects = [CustomSubject()],
            };

            var blocker = Task.Run(() => _users.Add("blocker_" + iter, "blocker-pw-123456", "user"));
            await Task.Delay(5); // хеш идёт: лок занят
            var adminTask = Task.Run(() => _users.SetChildProfile(_childId, admin, resetRules: false));
            var seedTask = Task.Run(() => _users.SeedChildSubjectsIfEmpty(_childId, SeedTriple()));
            var all = Task.WhenAll(blocker, adminTask, seedTask);
            (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30)))).Should().Be(all, "гонка не завершилась");

            var subjects = _users.GetById(_childId)!.ChildMode!.Subjects;
            subjects.Should().HaveCount(1, "список админа засевом не затирается");
            subjects[0].Id.Should().Be("biology");
        }
    }

    // ======== Второй проход: досев предмета старым детским чатам (Т1b2) ========

    [Fact]
    public async Task StartAsync_ДетскийЧатБезПредмета_ПолучаетMath_ЧатСПредметомИВзрослый_БезИзменений()
    {
        var noSubject = await MkChildChatAsync("no-subj", subject: null);
        var pinned = await MkChildChatAsync("pinned", subject: "english");
        var adult = await MkPlainChatAsync("adult");
        var noSubjectBefore = noSubject.UpdatedAt;
        var pinnedBefore = pinned.UpdatedAt;
        var adultBefore = adult.UpdatedAt;

        await _sut.StartAsync(CancellationToken.None);

        // Детский чат без предмета получает первый предмет тройки — тот же id, что засев пишет
        // его профилю (тот же ключ в обоих проходах)
        _sessions.GetById(noSubject.Id)!.ChildSubject.Should().Be(ChildSubjectsMigration.DefaultSubjectId);
        _sessions.GetById(pinned.Id)!.ChildSubject.Should().Be("english",
            "записанный предмет досев не перезаписывает");
        _sessions.GetById(adult.Id)!.ChildSubject.Should().BeNull("обычный чат досев не трогает");

        // UpdatedAt не двигается: по нему сортировка и непрочитанность (инвариант роли)
        _sessions.GetById(noSubject.Id)!.UpdatedAt.Should().Be(noSubjectBefore);
        _sessions.GetById(pinned.Id)!.UpdatedAt.Should().Be(pinnedBefore);
        _sessions.GetById(adult.Id)!.UpdatedAt.Should().Be(adultBefore);

        // Досев доехал до стора, а не только до памяти
        StoredChildSubject(noSubject.Id).Should().Be(ChildSubjectsMigration.DefaultSubjectId);
        StoredChildSubject(pinned.Id).Should().Be("english");
        StoredChildSubject(adult.Id).Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_ВторойПрогон_ЧатыНеМеняются()
    {
        var chat = await MkChildChatAsync("twice", subject: null);

        await _sut.StartAsync(CancellationToken.None);
        var after = _sessions.GetById(chat.Id)!;
        after.ChildSubject.Should().Be(ChildSubjectsMigration.DefaultSubjectId);
        var updatedBefore = after.UpdatedAt;

        // Идемпотентность состоянием: второй проход видит записанный предмет и ничего не пишет
        await _sut.StartAsync(CancellationToken.None);

        _sessions.GetById(chat.Id)!.ChildSubject.Should().Be(ChildSubjectsMigration.DefaultSubjectId);
        _sessions.GetById(chat.Id)!.UpdatedAt.Should().Be(updatedBefore,
            "повторный проход не должен двигать метку чата");
    }

    [Fact]
    public async Task Backfill_ParallelWithPinnedChat_KeepsPinnedSubject()
    {
        // Момент, ради которого тест: запись УЖЕ в реестре стора (StartNewSessionAsync вставляет
        // её ДО старта прогона и SaveSessions — TurnProcessHost.cs:117), а создание чата ещё не
        // закончено — здесь досев встречает записанный предмет. Без проверки == null ВНУТРИ
        // замка он затёр бы «english» своим «math» (мутация краснеет); с ней — пропускает.
        // Владелец — не ребёнок: И1-бис к такому чату не применяется, поэтому он создаётся без
        // полного детского профиля (тот же приём, что ChildChatReadTests.cs:56-60).
        var before = _sessions.GetAll().Select(s => s.Id).ToHashSet();

        var creating = Task.Run(() => _sessions.CreateChatAsync(_adultId, ClaudeMode.Auto,
            name: "новый урок", childSpace: ChildSpaces.Lessons, childSubject: "english"));
        var newId = await WaitForNewChatIdAsync(before);

        var backfilled = _sessions.BackfillChildSubject(ChildSubjectsMigration.DefaultSubjectId);
        var created = await creating;

        backfilled.Should().Be(0, "прикреплённый человеком предмет — не пропущенный");
        _sessions.GetById(newId)!.ChildSubject.Should().Be("english",
            "созданный параллельно чат досевом не перезаписывается");
        created.ChildSubject.Should().Be("english");
    }

    // ======== Помощники ========

    private static ChildSubject CustomSubject() =>
        new() { Id = "biology", Label = "Окружающий мир", Glyph = "Ом", Color = "accent" };

    // Содержание засева в тесте гонки произвольно: значимо, что это тройка, различимая с предметом админа
    private static List<ChildSubject> SeedTriple() => [
        new() { Id = "seed-a", Label = "Предмет А", Glyph = "A", Color = "info" },
        new() { Id = "seed-b", Label = "Предмет Б", Glyph = "Б", Color = "rose" },
        new() { Id = "seed-c", Label = "Предмет В", Glyph = "В", Color = "success" },
    ];

    // Чат вне проекта обычного пользователя: И1-бис к нему не применяется (политики нет) —
    // им же создаётся и «прикреплённый» чат теста гонки.
    private Task<Session> MkPlainChatAsync(string suffix) =>
        _sessions.CreateChatAsync(_adultId, ClaudeMode.Auto, name: "чат " + suffix);

    // Чат «Уроков» без предмета — ровно запись до Т1b: ChildSpace есть, ChildSubject null.
    // Проставляется напрямую (прецедент ChildChatReadTests.cs:71): создать такой чат фабрикой
    // больше нельзя — И1-бис отказывает ему «Этот предмет закрыт», и это ровно тот дефект,
    // который досев правит.
    private async Task<Session> MkChildChatAsync(string suffix, string? subject)
    {
        var chat = await MkPlainChatAsync(suffix);
        chat.ChildSpace = ChildSpaces.Lessons;
        chat.ChildSubject = subject;
        _sessions.SaveSessions();
        return chat;
    }

    private string? StoredChildSubject(string sessionId)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "sessions.json")));
        var entry = doc.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("Id").GetString() == sessionId);
        return entry.TryGetProperty("ChildSubject", out var v) && v.ValueKind != JsonValueKind.Null
            ? v.GetString() : null;
    }

    // Ждать, пока новый чат встанет в реестр стора (не пока его создание закончится): именно в
    // этом окне досев и должен его встретить.
    private async Task<string> WaitForNewChatIdAsync(HashSet<string> before)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var fresh = _sessions.GetAll().Select(s => s.Id).FirstOrDefault(id => !before.Contains(id));
            if (fresh is not null) return fresh;
            await Task.Delay(5);
        }
        throw new TimeoutException("новый чат не появился в реестре стора");
    }
}
