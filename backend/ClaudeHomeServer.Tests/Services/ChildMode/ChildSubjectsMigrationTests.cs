using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

// Засев трёх предметов волны 1 существующим профилям детей (Т1b, срез §1). Контракт:
// пустой Subjects → math/russian/english в порядке плиток, маршрут — из профиля; непустой
// не трогается; второй прогон — без изменений (идемпотентность состоянием, маркера нет);
// обычный пользователь не трогается; исключение стора — лог, старт идёт; параллельный PUT
// админа засевом не затирается.
public class ChildSubjectsMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly UserStore _users;
    private readonly ChildSubjectsMigration _sut;
    private readonly string _childId;
    private readonly string _adultId;

    public ChildSubjectsMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc_child_subjects_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build();

        _users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
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

        _sut = new ChildSubjectsMigration(_users, NullLogger<ChildSubjectsMigration>.Instance);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

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

    // ======== Помощники ========

    private static ChildSubject CustomSubject() =>
        new() { Id = "biology", Label = "Окружающий мир", Glyph = "Ом", Color = "accent" };

    // Содержание засева в тесте гонки произвольно: значимо, что это тройка, различимая с предметом админа
    private static List<ChildSubject> SeedTriple() => [
        new() { Id = "seed-a", Label = "Предмет А", Glyph = "A", Color = "info" },
        new() { Id = "seed-b", Label = "Предмет Б", Glyph = "Б", Color = "rose" },
        new() { Id = "seed-c", Label = "Предмет В", Glyph = "В", Color = "success" },
    ];
}
