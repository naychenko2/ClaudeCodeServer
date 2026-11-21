using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Авто-фейловер пула подписок при НЕДОСТУПНОМ ключе сессии. Инцидент 21.11: подписка claude-3
/// отключена (токен снят, запись с DisplayName/Tier осталась), 401 её чат при продолжении
/// стартовал CLI в ~/.claude — транскрипт искался не в sub-claude-3, каждый ход падал
/// «No conversation found», а классификатор гнал цепочку фолбэка. Здесь: резолв профиля
/// отключённого ключа остаётся sub-{key}, фейловер срабатывает по самому факту
/// отключённой/удалённой записи (не только по исчерпанию) и переводит чат на живую подписку
/// с переносом транскрипта; живой адаптер помечается устаревшим.
/// </summary>
public class CliRoutePoolFailoverTests : IDisposable
{
    private const string Csid = "3f487ee3-e49e-4dde-acae-0b213dc9aa7f";

    private readonly string _tempDir;
    private readonly string _dataPath;
    private readonly string _profilesDir;
    private readonly string _userProfileDir;
    private readonly LlmProviderRegistry _providers;
    private readonly ClaudeSubscriptionPool _pool;
    private readonly SessionStore _store;
    private readonly UserStore _users;
    private readonly CliRoute _route;
    private readonly string _ownerId;

    public CliRoutePoolFailoverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cli_route_failover_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // Профили CLI лежат рядом с DataPath: {data}/claude-profiles/{key}
        _dataPath = Path.Combine(_tempDir, "data", "projects.json");
        _profilesDir = Path.Combine(_tempDir, "data", "claude-profiles");
        _userProfileDir = Path.Combine(_tempDir, "user-claude");

        var values = BaseValues();
        // Живая подписка пула: claude с токеном
        values[$"{ClaudeSubscriptionPool.Section}:claude:OAuthToken"] = "token-claude";
        values[$"{ClaudeSubscriptionPool.Section}:claude:Tier"] = "max";
        // Отключённая: записи без токена нет в All, но она конфигурирована (DisplayName/Tier)
        values[$"{ClaudeSubscriptionPool.Section}:claude-3:DisplayName"] = "Claude 3";
        values[$"{ClaudeSubscriptionPool.Section}:claude-3:Tier"] = "max5";
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        _users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.Add("owner", "p", "user").Id;
        _providers = new LlmProviderRegistry(config);
        _pool = new ClaudeSubscriptionPool(config);
        _store = BuildStore(config);

        _route = BuildRoute(config);
    }

    public void Dispose() => TestFs.DeleteDirectoryResilient(_tempDir);

    // Общие ключи всех конфигов теста: один DataPath — users.json и claude-profiles
    // разделяются между вариантами пула, владелец чата один и тот же
    private Dictionary<string, string?> BaseValues() => new()
    {
        ["DataPath"] = _dataPath,
        ["Session:AutoSaveSeconds"] = "0",
        ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
        ["ClaudeUserProfileDir"] = _userProfileDir,
    };

    private SessionStore BuildStore(IConfiguration config)
    {
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projects = new ProjectManager(config, users, appSettings);
        return new SessionStore(config, projects, new TestSessionBroadcaster(),
            NullLogger<SessionStore>.Instance);
    }

    // Маршрут на своём конфиге пула; пользователи и профили — из общего DataPath
    private CliRoute BuildRoute(IConfiguration config, ClaudeSubscriptionPool? pool = null)
    {
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projects = new ProjectManager(config, users, appSettings);
        var homes = new UserHomeResolver(config, appSettings);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(
            config, NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        var assignments = new ModelAssignmentResolver(appSettings,
            new LocalActionOverridesStore(config), new UserModelTierResolver(users, appSettings));
        return new CliRoute(
            new LlmProviderRegistry(config), pool ?? new ClaudeSubscriptionPool(config),
            assignments, users, homes, TestLauncherFactory.Instance,
            sandbox, projects, _store,
            new ArchivedTranscriptStore(config),
            NullLogger<CliRoute>.Instance);
    }

    private SessionEntry SeedChat(string provider, string? csid = Csid)
    {
        var session = new Session
        {
            Id = "chat-" + Guid.NewGuid().ToString("N")[..8],
            OwnerId = _ownerId,
            Provider = provider,
            Model = "sonnet",
            ClaudeSessionId = csid,
        };
        var entry = new SessionEntry { Info = session };
        _store.Sessions[session.Id] = entry;
        return entry;
    }

    // Транскрипт в профиле подписки: {профиль}/projects/{уплощённый cwd}/{csid}.jsonl
    private string SeedTranscript(string profileKey, string cwd, string csid = Csid)
    {
        var dir = Path.Combine(_profilesDir, profileKey, "projects",
            TranscriptMigrator.FlattenCwd(cwd));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, csid + ".jsonl");
        File.WriteAllText(file, "{\"type\":\"user\"}\n");
        return file;
    }

    [Fact]
    public void ОтключённаяПодписка_РезолвПрофиля_ОстаётсяВSubКлюче()
    {
        // Транскрипты чатов claude-3 лежат в sub-claude-3 и после отключения подписки:
        // источник переноса обязан резолвиться туда, а не в ~/.claude (корень дефекта)
        _route.ConfigRootFor(_ownerId, "claude-3")
            .Should().Be(Path.Combine(_profilesDir, "sub-claude-3"));
        // Живые ключи резолвятся как раньше
        _route.ConfigRootFor(_ownerId, "claude")
            .Should().Be(Path.Combine(_profilesDir, "sub-claude"));
        _route.ConfigRootFor(_ownerId, null).Should().Be(_userProfileDir);
    }

    [Fact]
    public void ОтключённаяПодписка_ПервыйХод_ПереезжаетНаЖивуюСТранскриптом()
    {
        var entry = SeedChat("claude-3");
        var cwd = _route.TryResolveCwd(entry.Info)!;
        SeedTranscript("sub-claude-3", cwd);

        _route.TryPoolFailover(entry.Info.Id, entry);

        entry.Info.Provider.Should().Be("claude", "отключённый ключ непригоден для запуска CLI");
        var moved = Path.Combine(_profilesDir, "sub-claude", "projects",
            TranscriptMigrator.FlattenCwd(cwd), Csid + ".jsonl");
        File.Exists(moved).Should().BeTrue("транскрипт перенесён в профиль живой подписки");
        // Миграция исходники не удаляет — обратный переезд дешевле
        Directory.EnumerateFiles(Path.Combine(_profilesDir, "sub-claude-3"), "*.jsonl",
            SearchOption.AllDirectories).Should().ContainSingle();
    }

    [Fact]
    public void ОтключённаяПодписка_ЖивойАдаптер_ПомеченУстаревшим()
    {
        // Живой адаптер держит корень профиля старого ключа: без пометки следующий ход
        // пошёл бы в профиль отключённой подписки (EnsureProcessAsync пересоздаёт адаптер)
        var entry = SeedChat("claude-3");
        var cwd = _route.TryResolveCwd(entry.Info)!;
        SeedTranscript("sub-claude-3", cwd);
        entry.Process = new Mock<ILlmSessionAdapter>().Object;

        _route.TryPoolFailover(entry.Info.Id, entry);

        entry.AdapterStale.Should().BeTrue("адаптер со старым корнем больше не используется");
    }

    [Fact]
    public void ОтключённаяПодписка_БезТранскрипта_ПереездВсёРавно()
    {
        // Ходов в чате не было (csid нет) — переносить нечего, но сидеть на отключённом
        // ключе чат тоже не должен
        var entry = SeedChat("claude-3", csid: null);

        _route.TryPoolFailover(entry.Info.Id, entry);

        entry.Info.Provider.Should().Be("claude");
    }

    [Fact]
    public void УдалённаяИзКонфигаЗапись_ПрофильНаДиске_ФейловерСПереносом()
    {
        // Запись убрали из конфига целиком, но профиль sub-claude-3 с транскриптами
        // остался: ключ узнаётся по папке профиля
        var values = BaseValues();
        values[$"{ClaudeSubscriptionPool.Section}:claude:OAuthToken"] = "token-claude";
        var route2 = BuildRoute(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        var entry = SeedChat("claude-3");
        var cwd = route2.TryResolveCwd(entry.Info)!;
        SeedTranscript("sub-claude-3", cwd);

        route2.TryPoolFailover(entry.Info.Id, entry);

        entry.Info.Provider.Should().Be("claude", "записи в конфиге нет — ключ узнан по папке профиля");
        File.Exists(Path.Combine(_profilesDir, "sub-claude", "projects",
            TranscriptMigrator.FlattenCwd(cwd), Csid + ".jsonl")).Should().BeTrue();
    }

    [Fact]
    public void ЖиваяПодписка_БезПричины_ФейловераНет()
    {
        // Штатный путь не меняется: живой ключ с токеном остаётся при чате
        var values = LiveClaude3Values();
        var route2 = BuildRoute(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        var entry = SeedChat("claude-3");

        route2.TryPoolFailover(entry.Info.Id, entry);

        entry.Info.Provider.Should().Be("claude-3");
        entry.AdapterStale.Should().BeFalse();
    }

    [Fact]
    public void ИсчерпаннаяПодписка_ФейловерКакРаньше()
    {
        // Регресс прежнего пути: исчерпание по-прежнему уводит чат на живую подписку
        var config = new ConfigurationBuilder().AddInMemoryCollection(LiveClaude3Values()).Build();
        var pool2 = new ClaudeSubscriptionPool(config);
        pool2.MarkExhausted("claude-3");
        var route2 = BuildRoute(config, pool2);
        var entry = SeedChat("claude-3");
        var cwd = route2.TryResolveCwd(entry.Info)!;
        SeedTranscript("sub-claude-3", cwd);

        route2.TryPoolFailover(entry.Info.Id, entry);

        entry.Info.Provider.Should().Be("claude");
    }

    [Fact]
    public void ЛокальныйВходБезЗаписи_ФейловераНет()
    {
        // Provider="claude" (PrimaryKey) без записи в конфиге — локальный вход через ~/.claude:
        // он работает сам по себе, переводить его на подписку пула нельзя
        var values = BaseValues();
        values[$"{ClaudeSubscriptionPool.Section}:acc-a:OAuthToken"] = "token-a";
        var route2 = BuildRoute(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        var entry = SeedChat("claude");

        route2.TryPoolFailover(entry.Info.Id, entry);

        entry.Info.Provider.Should().Be("claude", "локальный вход — не фейловер-случай");
    }

    // claude-3 с токеном: живая подписка (для контрольных тестов «без причины»/«исчерпание»)
    private Dictionary<string, string?> LiveClaude3Values()
    {
        var values = BaseValues();
        values[$"{ClaudeSubscriptionPool.Section}:claude:OAuthToken"] = "token-claude";
        values[$"{ClaudeSubscriptionPool.Section}:claude-3:OAuthToken"] = "token-3";
        values[$"{ClaudeSubscriptionPool.Section}:claude-3:Tier"] = "max5";
        return values;
    }
}
