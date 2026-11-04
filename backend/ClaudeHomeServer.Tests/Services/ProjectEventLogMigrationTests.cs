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

        ReadUserVersion().Should().Be(2);
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

        ReadUserVersion().Should().Be(2);
    }

    [Fact]
    public void ПовторныйСтарт_НичегоНеЛомает()
    {
        using (var first = BuildService()) { }
        SqliteConnection.ClearAllPools();

        using var second = BuildService();

        ReadUserVersion().Should().Be(2);
    }

    // Фикстура v1: таблица без actor_user_id, user_version = 1, N строк
    private void CreateV1Database(int rows)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
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
            PRAGMA user_version=1;
            """;
        cmd.ExecuteNonQuery();
        for (var i = 0; i < rows; i++)
        {
            using var ins = connection.CreateCommand();
            ins.CommandText = "INSERT INTO project_events(project_id, owner_id, ts, type, actor, summary) " +
                              "VALUES('p1','o1',@ts,'chat_turn','user',@s);";
            ins.Parameters.AddWithValue("@ts", DateTime.UtcNow.AddMinutes(-i).ToString("O"));
            ins.Parameters.AddWithValue("@s", "событие " + i);
            ins.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private long CountRows(string? where = null)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM project_events" + (where is null ? "" : " WHERE " + where);
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void БазаV1_МигрируетВV2_БезПотериСтрок_СтарыеСУказателемNull()
    {
        CreateV1Database(rows: 5);

        using var service = BuildService();

        ReadUserVersion().Should().Be(2);
        CountRows().Should().Be(5, "миграция не теряет строки");
        CountRows("actor_user_id IS NULL").Should().Be(5, "у старых строк актора нет");
        service.Query("p1", "o1").Should().HaveCount(5).And.OnlyContain(e => e.ActorUserId == null);
    }

    [Fact]
    public void НоваяЗапись_НесётАктора_ЕслиИзвестен_ИначеNull()
    {
        using var service = BuildService();

        service.Append("p1", "o1", "chat_turn", "user", "с актором", "s1", actorUserId: "u-editor");
        service.Append("p1", "o1", "chat_turn", "user", "без актора", "s2");
        service.Append("p1", "o1", "chat_turn", "user", "пустой актор", "s3", actorUserId: "");

        var byRef = service.Query("p1", "o1").ToDictionary(e => e.EntityRef!);
        byRef["s1"].ActorUserId.Should().Be("u-editor");
        byRef["s2"].ActorUserId.Should().BeNull();
        byRef["s3"].ActorUserId.Should().BeNull();
    }

    [Fact]
    public async Task ПараллельноеОткрытиеИЗаписьВоВремяМиграции_МиграцияОдна_СтрокиЦелы()
    {
        CreateV1Database(rows: 3);
        var inMigration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeCalls = 0;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();

        // Поток A мигрирует и стоит внутри миграции, пока не отпустим
        var a = Task.Run(() => new ProjectEventLogService(config, null, () =>
        {
            Interlocked.Increment(ref probeCalls);
            inMigration.SetResult();
            release.Task.GetAwaiter().GetResult();
        }));
        await inMigration.Task;

        // Поток B открывает ту же базу и пишет, пока A внутри миграции
        var b = Task.Run(() =>
        {
            var svc = new ProjectEventLogService(config, null, () => Interlocked.Increment(ref probeCalls));
            svc.Append("p1", "o1", "chat_turn", "user", "из потока B", "sB", actorUserId: "u-b");
            return svc;
        });

        release.SetResult();
        using var svcA = await a;
        using var svcB = await b;

        probeCalls.Should().Be(1, "миграция выполняется один раз под блокировкой открытия");
        ReadUserVersion().Should().Be(2);
        CountRows().Should().Be(4, "3 старые строки плюс запись потока B");
        svcA.Query("p1", "o1").Should().Contain(e => e.EntityRef == "sB" && e.ActorUserId == "u-b");
    }
}
