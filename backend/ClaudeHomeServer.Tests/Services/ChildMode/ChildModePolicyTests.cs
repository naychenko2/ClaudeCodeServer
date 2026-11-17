using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

/// <summary>
/// Единая точка правды детского режима (KM-B1b, разрез §1): IChildModePolicy.For(ownerId)
/// по стору пользователей, не по claim.
///
/// Что фиксируют тесты (критерии KM-B1b):
/// 1. профиля нет (пользователя нет / ChildMode null) → null — обычный пользователь;
/// 2. профиль есть, Enabled=false → null — выключение профиля и есть «не ребёнок»;
/// 3. валидный профиль → снимок DenyAll=false с профилем из стора;
/// 4. невалидный профиль (пустой TutorPersonaId/ProviderKey/Model, окно «25:00») →
///    снимок DenyAll=true: ребёнок остаётся ребёнком, но запрещено всё (fail-closed);
/// 5. сорванное чтение профиля → DenyAll=true, наружу исключение не выходит;
/// 6. кэша нет: профиль поменяли или выключили в сторе → следующий For видит новое.
/// Мутации (критерии c2/c3): «невалидный профиль → null вместо DenyAll» краснеет
/// ProfileInvalid_DeniesAll; «кэшировать снимок» краснеет ProfileTurnedOff_NextForSeesNull.
/// </summary>
public sealed class ChildModePolicyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly UserStore _users;
    private readonly ChildModePolicy _policy;

    public ChildModePolicyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ccs-child-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();
        _users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _policy = new ChildModePolicy(_users, NullLogger<ChildModePolicy>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // Валидный профиль-образец: все обязательные поля, окно разбирается как HH:mm.
    private static ChildProfile ValidProfile() => new()
    {
        Enabled = true,
        TutorPersonaId = "tutor-1",
        ProviderKey = "pool-key",
        Model = "sonnet",
        Spaces =
        {
            ["lessons"] = new SpaceLimits
            {
                Windows = [new TimeWindow { From = "16:00", To = "19:00" }],
                DailyMinutes = 60,
            },
        },
    };

    private string AddUser(ChildProfile? profile)
    {
        var user = _users.Add("kid-" + Guid.NewGuid().ToString("N"), "pass", "user");
        user.ChildMode = profile;
        return user.Id;
    }

    [Fact]
    public void NoProfile_ReturnsNull()
    {
        var id = AddUser(null);
        _policy.For(id).Should().BeNull();
        // и пользователя-то нет
        _policy.For("нет-такого").Should().BeNull();
        // пустой ownerId — не владелец, обычный путь
        _policy.For("").Should().BeNull();
    }

    [Fact]
    public void ProfileDisabled_ReturnsNull()
    {
        var id = AddUser(new ChildProfile { Enabled = false, TutorPersonaId = "t", ProviderKey = "p", Model = "m" });
        _policy.For(id).Should().BeNull();
    }

    [Fact]
    public void ValidProfile_ReturnsSnapshotNotDenied()
    {
        var id = AddUser(ValidProfile());
        var snapshot = _policy.For(id);
        snapshot.Should().NotBeNull();
        snapshot!.OwnerId.Should().Be(id);
        snapshot.DenyAll.Should().BeFalse();
        snapshot.Profile.Should().NotBeNull();
        // окно через полночь и пустые окна пространств — валидны, не провал
        var another = AddUser(new ChildProfile
        {
            Enabled = true,
            TutorPersonaId = "t",
            ProviderKey = "p",
            Model = "m",
            Spaces =
            {
                ["lessons"] = new SpaceLimits { Windows = [new TimeWindow { From = "23:00", To = "07:00" }] },
                ["studio"] = new SpaceLimits(), // окон нет — законно (SpaceLimits), не DenyAll
            },
        });
        _policy.For(another)!.DenyAll.Should().BeFalse();
    }

    [Fact]
    public void ProfileInvalid_DeniesAll()
    {
        // пустые обязательные поля детского хода — по одному на каждое
        foreach (var mutate in new Action<ChildProfile>[]
        {
            p => p.TutorPersonaId = "",
            p => p.ProviderKey = "",
            p => p.Model = "",
        })
        {
            var profile = ValidProfile();
            mutate(profile);
            var id = AddUser(profile);
            var snapshot = _policy.For(id);
            snapshot.Should().NotBeNull("профиль есть — ребёнок остаётся ребёнком");
            snapshot!.DenyAll.Should().BeTrue();
            snapshot.Profile.Should().NotBeNull();
        }
    }

    [Fact]
    public void WindowNotParsable_DeniesAll()
    {
        var profile = ValidProfile();
        profile.Spaces["lessons"].Windows.Add(new TimeWindow { From = "25:00", To = "26:00" });
        var id = AddUser(profile);
        _policy.For(id)!.DenyAll.Should().BeTrue();

        // мусор вместо HH:mm — тот же отказ
        var junk = ValidProfile();
        junk.Spaces["lessons"].Windows[0].To = "19-00";
        var junkId = AddUser(junk);
        _policy.For(junkId)!.DenyAll.Should().BeTrue();
    }

    [Fact]
    public void StoreThrows_DeniesAllWithoutThrow()
    {
        var policy = new ChildModePolicy(new ThrowingStore(), NullLogger<ChildModePolicy>.Instance);
        var snapshot = policy.For("owner-1");
        snapshot.Should().NotBeNull();
        snapshot!.DenyAll.Should().BeTrue();
        snapshot.Profile.Should().BeNull("сорванное чтение — профиля наружу не вынести");
    }

    [Fact]
    public void ProfileTurnedOff_NextForSeesNull()
    {
        var id = AddUser(ValidProfile());
        _policy.For(id)!.DenyAll.Should().BeFalse();
        // выключили профиль в сторе — следующий вызов обязан видеть выключение, кэша нет
        _users.GetById(id)!.ChildMode!.Enabled = false;
        _policy.For(id).Should().BeNull();
    }

    [Fact]
    public void ProfileChanged_NextForSeesNew()
    {
        var id = AddUser(ValidProfile());
        _policy.For(id)!.DenyAll.Should().BeFalse();
        // профиль правят в сторе — следующий For видит новое состояние, а не прежний снимок
        _users.GetById(id)!.ChildMode!.ProviderKey = "";
        _policy.For(id)!.DenyAll.Should().BeTrue();
    }

    // Стор, который бросает на любом чтении: проверка «исключение наружу не выходит».
    private sealed class ThrowingStore : IUserStore
    {
        public User? GetById(string id) => throw new InvalidOperationException("стор лёг");
        public IReadOnlyList<User> GetAll() => throw new InvalidOperationException("стор лёг");
        public bool IsTokenVersionCurrent(string userId, int version) => throw new InvalidOperationException("стор лёг");
    }
}
