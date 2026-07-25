using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Версионирование схемы SQLite (PRAGMA user_version).
//
// Одного «CREATE TABLE IF NOT EXISTS» перестанет хватать в тот день, когда в
// project_events добавится колонка: на существующей БД — в том числе восстановленной из
// бэкапа — таблица уже есть, IF NOT EXISTS промолчит, и запрос с новой колонкой упадёт не
// при обновлении, а когда кто-нибудь откроет ленту активности.
public class ProjectEventLogMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ccs-events-" + Guid.NewGuid().ToString("N")[..8]);

    public ProjectEventLogMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
        GC.SuppressFinalize(this);
    }

    private string DbPath => Path.Combine(_dir, "project-events.db");

    private ProjectEventLogService BuildService()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();
        return new ProjectEventLogService(config);
    }

    private int ReadUserVersion()
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    [Fact]
    public void СвежаяБаза_ПолучаетАктуальнуюВерсию()
    {
        using var service = BuildService();

        ReadUserVersion().Should().Be(1);
    }

    [Fact]
    public void БазаБезВерсии_ДоводитсяДоАктуальной()
    {
        // Так выглядит БД, созданная до появления лесенки (и такую же отдаёт
        // восстановление старого бэкапа): таблица есть, user_version = 0
        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE project_events (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  project_id TEXT NOT NULL,
                  owner_id TEXT NOT NULL,
                  ts TEXT NOT NULL,
                  type TEXT NOT NULL,
                  actor TEXT NOT NULL,
                  summary TEXT NOT NULL,
                  entity_ref TEXT
                );
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        ReadUserVersion().Should().Be(0);

        using var service = BuildService();

        ReadUserVersion().Should().Be(1);
    }

    [Fact]
    public void ПовторныйСтарт_НичегоНеЛомает()
    {
        using (var first = BuildService()) { }
        SqliteConnection.ClearAllPools();

        using var second = BuildService();

        ReadUserVersion().Should().Be(1);
    }
}
