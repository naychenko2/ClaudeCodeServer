using System.Data;
using System.Text;
using ClaudeHomeServer.Models;
using Microsoft.Data.Sqlite;

namespace ClaudeHomeServer.Services;

// Лог событий проекта на SQLite (см. ProjectEvent). Append-only: пишем по одному событию
// из точек мутации (ход чата, задача, память, база, заметка, состав команды). Читаем для
// активность-ленты командного центра и дайджеста. Изоляция per-owner: все запросы фильтруются
// по OwnerId (как у остальных сторов), GET /api/projects/{id}/events резолвит проект и
// проверяет владельца на уровне контроллера.
//
// Первая подсистема на SQLite. WAL + busy_timeout — конкурентные записи (одновременные ходы,
// задачи) не падают с «database is locked»; записи сериализуются локом дополнительно.
public class ProjectEventLogService
{
    private readonly string _connStr;
    private readonly Lock _writeLock = new();
    private readonly ILogger<ProjectEventLogService>? _log;

    public ProjectEventLogService(IConfiguration config, ILogger<ProjectEventLogService>? log = null)
    {
        _log = log;
        // Каталог данных — как у всех сервисов (DataPath указывает на projects.json в dataDir).
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        var dbPath = config["ProjectEventsDbPath"] ?? Path.Combine(dataDir, "project-events.db");
        _connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        try { Init(); }
        catch (Exception ex)
        {
            // Не роняем старт сервера из-за лога — фича деградирует до «нет событий»
            _log?.LogError(ex, "Не удалось инициализировать лог событий проекта ({DbPath})", dbPath);
        }
    }

    private void Init()
    {
        using var c = OpenConnection();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, "PRAGMA busy_timeout=5000;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS project_events (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              project_id TEXT NOT NULL,
              owner_id TEXT NOT NULL,
              ts TEXT NOT NULL,
              type TEXT NOT NULL,
              actor TEXT NOT NULL,
              summary TEXT NOT NULL,
              entity_ref TEXT
            );
        """);
        // Составной индекс покрывает главный кейс «лента проекта по владельцу, свежие сверху»
        Exec(c, "CREATE INDEX IF NOT EXISTS ix_events_owner_project_ts ON project_events(owner_id, project_id, ts DESC);");
        Exec(c, "CREATE INDEX IF NOT EXISTS ix_events_owner_project_type ON project_events(owner_id, project_id, type, ts DESC);");
    }

    private SqliteConnection OpenConnection()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // Записать событие. null-результат — событие не относится к проекту (нет projectId/ownerId):
    // личные задачи/чаты/память глобальных персон в проектный лог не попадают.
    public ProjectEvent? Append(string projectId, string ownerId, string type, string actor, string summary, string? entityRef = null)
    {
        if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(ownerId)) return null;
        var ev = new ProjectEvent
        {
            ProjectId = projectId,
            OwnerId = ownerId,
            Ts = DateTime.UtcNow,
            Type = type,
            Actor = string.IsNullOrEmpty(actor) ? "system" : actor,
            Summary = summary,
            EntityRef = entityRef,
        };
        lock (_writeLock)
        {
            try
            {
                using var c = OpenConnection();
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO project_events(project_id, owner_id, ts, type, actor, summary, entity_ref)
                    VALUES(@pid, @oid, @ts, @type, @actor, @summary, @ref);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("@pid", ev.ProjectId);
                cmd.Parameters.AddWithValue("@oid", ev.OwnerId);
                cmd.Parameters.AddWithValue("@ts", ev.Ts.ToString("O"));
                cmd.Parameters.AddWithValue("@type", ev.Type);
                cmd.Parameters.AddWithValue("@actor", ev.Actor);
                cmd.Parameters.AddWithValue("@summary", ev.Summary);
                cmd.Parameters.AddWithValue("@ref", (object?)ev.EntityRef ?? DBNull.Value);
                ev.Id = (long)cmd.ExecuteScalar()!;
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "Не удалось записать событие проекта ({Type})", ev.Type);
                return null;
            }
        }
        return ev;
    }

    // Лента событий проекта (свежие сверху). Фильтры опциональны. limit ограничен [1..500].
    public IReadOnlyList<ProjectEvent> Query(
        string projectId, string ownerId, DateTime? since = null,
        string? type = null, string? actor = null, int limit = 100)
    {
        var sb = new StringBuilder(
            "SELECT id, project_id, owner_id, ts, type, actor, summary, entity_ref " +
            "FROM project_events WHERE owner_id=@oid AND project_id=@pid");
        if (since.HasValue) sb.Append(" AND ts > @since");
        if (!string.IsNullOrEmpty(type)) sb.Append(" AND type=@type");
        if (!string.IsNullOrEmpty(actor)) sb.Append(" AND actor=@actor");
        sb.Append(" ORDER BY ts DESC LIMIT @limit;");

        try
        {
            using var c = OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sb.ToString();
            cmd.Parameters.AddWithValue("@oid", ownerId);
            cmd.Parameters.AddWithValue("@pid", projectId);
            if (since.HasValue) cmd.Parameters.AddWithValue("@since", since.Value.ToString("O"));
            if (!string.IsNullOrEmpty(type)) cmd.Parameters.AddWithValue("@type", type);
            if (!string.IsNullOrEmpty(actor)) cmd.Parameters.AddWithValue("@actor", actor);
            cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 500));

            var list = new List<ProjectEvent>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new ProjectEvent
                {
                    Id = r.GetInt64(0),
                    ProjectId = r.GetString(1),
                    OwnerId = r.GetString(2),
                    Ts = DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    Type = r.GetString(4),
                    Actor = r.GetString(5),
                    Summary = r.GetString(6),
                    EntityRef = r.IsDBNull(7) ? null : r.GetString(7),
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Не удалось прочитать события проекта {ProjectId}", projectId);
            return [];
        }
    }
}
