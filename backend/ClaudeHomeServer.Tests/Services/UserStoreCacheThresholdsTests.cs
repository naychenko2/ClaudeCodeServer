using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Хранимый личный порог «кэш скоро остынет» нормализуется на чтении users.json:
/// старые имена origin-формата в секундах (warn1hSec/warn5mSec) переводятся в минуты
/// (round) — настройка человека не теряется; значения вне диапазона — null, «не
/// настроено», фронт применит дефолты.
/// </summary>
public class UserStoreCacheThresholdsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _usersPath;

    public UserStoreCacheThresholdsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "userstore_cachemig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _usersPath = Path.Combine(_tempDir, "users.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private UserStore CreateStore() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json")
            }).Build(),
        new Helpers.FakeHostEnvironment(),
        NullLogger<UserStore>.Instance);

    // users.json с записью порога: фрагмент тела вставляется как есть
    private void SeedUsersFile(string thresholdsJson)
    {
        File.WriteAllText(_usersPath, $$"""
        {
          "Version": 1,
          "users": [
            {
              "Id": "u-1",
              "Username": "alice",
              "PasswordHash": "AQAAAA==",
              "TokenVersion": 1,
              "Role": "admin",
              "CacheWarnThresholds": {{thresholdsJson}}
            }
          ]
        }
        """);
    }

    [Fact]
    public void Load_LegacySecondNames_ConvertedToMinutes()
    {
        // 300 с → 5 мин, 180 с → 3 мин: настройка до переезда на минуты не теряется
        SeedUsersFile("""{"warn1hSec": 300, "warn5mSec": 180}""");

        var user = CreateStore().GetById("u-1");

        user!.CacheWarnThresholds.Should().Be(new CacheWarnThresholds(5, 3));
    }

    [Fact]
    public void Load_OutOfRangeValues_Nullified()
    {
        // Часовой порог выше потолка (59) — запись не «починить до границы», а снять:
        // фронт честно применит дефолты
        SeedUsersFile("""{"warn1hMin": 70, "warn5mMin": 3}""");

        var user = CreateStore().GetById("u-1");

        user!.CacheWarnThresholds.Should().BeNull();
    }

    [Fact]
    public void Load_LegacySecondsAtMigrationEdge_ClampedNotNullified()
    {
        // Край перевода: 30 с округляются к 0 мин — вне диапазона. Раньше такая запись
        // становилась null («не настроено»), хотя намерение человека было «предупредить
        // сразу»: теперь нижняя граница — 1 мин. Верхняя — потолок срока (3600 с → 59 мин)
        SeedUsersFile("""{"warn1hSec": 30, "warn5mSec": 30}""");
        CreateStore().GetById("u-1")!.CacheWarnThresholds.Should().Be(new CacheWarnThresholds(1, 1));

        SeedUsersFile("""{"warn1hSec": 3600, "warn5mSec": 300}""");
        CreateStore().GetById("u-1")!.CacheWarnThresholds.Should().Be(new CacheWarnThresholds(59, 4));
    }

    [Fact]
    public void Load_NegativeLegacySeconds_Nullified()
    {
        // Отрицательные секунды — мусор, а не край перевода: их отсекаем до clamp, иначе
        // −5 зажимались бы в 1 мин и стали валидной настройкой
        SeedUsersFile("""{"warn1hSec": -5, "warn5mSec": 180}""");

        var user = CreateStore().GetById("u-1");

        user!.CacheWarnThresholds.Should().BeNull();
    }
}
