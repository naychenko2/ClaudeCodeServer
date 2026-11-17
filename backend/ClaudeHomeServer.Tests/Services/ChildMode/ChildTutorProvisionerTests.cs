using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

// Провижн персоны-тьютора ребёнка (ChildTutorProvisioner, KM-B1d). Контракт: тьютор — только
// ребёнку, владелец — сам ребёнок, Tools без web, долгая память выключена, аватар — инициалы;
// идемпотентность (повтор → тот же id), гонка (параллельные вызовы → одна персона), ключ
// закреплён в ChildProfile.TutorPersonaId, висящий ключ провится заново.
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
}
