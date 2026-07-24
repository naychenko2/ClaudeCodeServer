using System.Data;
using Microsoft.Data.Sqlite;

namespace ClaudeHomeServer.Services;

// Хранилище лога расхода токенов на SQLite (по паттерну ProjectEventLogService).
// Пишем каждый ход чата, one-shot вызов, fal.ai генерацию и бесплатный вызов локали/OpenRouter.
// Агрегация: 90 дней детальных записей, старше — дневные итоги (spend_daily).
public sealed class SpendLogService : IDisposable
{
    private readonly string _connStr;
    private readonly Lock _writeLock = new();
    private readonly ILogger<SpendLogService>? _log;
    private const int RetentionDays = 90;
    private DateTime _lastPruneUtc = DateTime.MinValue;

    public SpendLogService(IConfiguration config, ILogger<SpendLogService>? log = null)
    {
        _log = log;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        var dbPath = config["SpendDbPath"] ?? Path.Combine(dataDir, "spend-log.db");
        _connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        try { Init(); }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Не удалось инициализировать лог расхода ({DbPath})", dbPath);
        }
    }

    private void Init()
    {
        using var c = OpenConnection();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, "PRAGMA busy_timeout=5000;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS spend_log (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              owner_id TEXT NOT NULL,
              project_id TEXT,
              session_id TEXT,
              task_id TEXT,
              persona_id TEXT,
              provider TEXT NOT NULL,
              model TEXT NOT NULL,
              source TEXT NOT NULL,
              ts TEXT NOT NULL,
              input_tokens INTEGER NOT NULL,
              output_tokens INTEGER NOT NULL,
              cache_read_tokens INTEGER NOT NULL,
              cache_creation_tokens INTEGER NOT NULL,
              cost_usd REAL,
              duration_ms INTEGER,
              completed INTEGER NOT NULL DEFAULT 1,
              entity_ref TEXT
            );
        """);
        Exec(c, """
            CREATE TABLE IF NOT EXISTS spend_daily (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              owner_id TEXT NOT NULL,
              project_id TEXT,
              provider TEXT NOT NULL,
              model TEXT NOT NULL,
              source TEXT NOT NULL,
              date TEXT NOT NULL,
              input_tokens INTEGER NOT NULL,
              output_tokens INTEGER NOT NULL,
              cache_read_tokens INTEGER NOT NULL,
              cache_creation_tokens INTEGER NOT NULL,
              cost_usd REAL,
              turn_count INTEGER NOT NULL,
              completed_count INTEGER NOT NULL
            );
        """);
        Exec(c, "CREATE INDEX IF NOT EXISTS idx_spend_owner_ts ON spend_log(owner_id, ts DESC);");
        Exec(c, "CREATE INDEX IF NOT EXISTS idx_spend_project_ts ON spend_log(project_id, ts DESC);");
        Exec(c, "CREATE INDEX IF NOT EXISTS idx_spend_session ON spend_log(session_id, ts DESC);");
        Exec(c, "CREATE INDEX IF NOT EXISTS idx_spend_owner_date ON spend_log(owner_id, ts);");
        Exec(c, "CREATE UNIQUE INDEX IF NOT EXISTS idx_spend_daily_key ON spend_daily(owner_id, project_id, provider, model, source, date);");
    }

    private SqliteConnection OpenConnection()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        Exec(c, "PRAGMA busy_timeout=5000;");
        return c;
    }

    public void Dispose() => SqliteConnection.ClearPool(new SqliteConnection(_connStr));

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // Запись одной строки расхода. Все оси разреза опциональны, кроме owner_id.
    public long Append(string ownerId, string? projectId, string? sessionId, string? taskId,
        string? personaId, string provider, string model, string source, string ts,
        int inputTokens, int outputTokens, int cacheReadTokens, int cacheCreationTokens,
        double? costUsd, long? durationMs, bool completed, string? entityRef)
    {
        lock (_writeLock)
        {
            try
            {
                using var c = OpenConnection();
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO spend_log(owner_id, project_id, session_id, task_id, persona_id,
                        provider, model, source, ts,
                        input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
                        cost_usd, duration_ms, completed, entity_ref)
                    VALUES(@owner_id, @project_id, @session_id, @task_id, @persona_id,
                        @provider, @model, @source, @ts,
                        @input, @output, @cache_read, @cache_creation,
                        @cost, @duration, @completed, @ref);
                    SELECT last_insert_rowid();
                """;
                AddParam(cmd, "@owner_id", ownerId);
                AddParam(cmd, "@project_id", projectId);
                AddParam(cmd, "@session_id", sessionId);
                AddParam(cmd, "@task_id", taskId);
                AddParam(cmd, "@persona_id", personaId);
                AddParam(cmd, "@provider", provider);
                AddParam(cmd, "@model", model);
                AddParam(cmd, "@source", source);
                AddParam(cmd, "@ts", ts);
                AddParam(cmd, "@input", inputTokens);
                AddParam(cmd, "@output", outputTokens);
                AddParam(cmd, "@cache_read", cacheReadTokens);
                AddParam(cmd, "@cache_creation", cacheCreationTokens);
                AddParam(cmd, "@cost", costUsd);
                AddParam(cmd, "@duration", durationMs);
                AddParam(cmd, "@completed", completed ? 1 : 0);
                AddParam(cmd, "@ref", entityRef);
                var id = (long)cmd.ExecuteScalar()!;
                PruneIfDue(c);
                return id;
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "Не удалось записать расход ({Source}/{Model})", source, model);
                return -1;
            }
        }
    }

    // Прунинг и агрегация: записи старше 90 дней схлопываются в spend_daily, затем удаляются.
    private void PruneIfDue(SqliteConnection c)
    {
        var now = DateTime.UtcNow;
        if (now - _lastPruneUtc < TimeSpan.FromHours(24)) return;
        _lastPruneUtc = now;
        try
        {
            // Агрегация: переносим дневные итоги в spend_daily (INSERT OR IGNORE — только новые)
            var cutoff = now.AddDays(-RetentionDays).ToString("O");
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT OR IGNORE INTO spend_daily(owner_id, project_id, provider, model, source, date,
                        input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
                        cost_usd, turn_count, completed_count)
                    SELECT owner_id, project_id, provider, model, source, SUBSTR(ts, 1, 10) AS date,
                        SUM(input_tokens), SUM(output_tokens),
                        SUM(cache_read_tokens), SUM(cache_creation_tokens),
                        SUM(cost_usd), COUNT(*), SUM(completed)
                    FROM spend_log
                    WHERE ts < @cutoff AND ts >= @cutoff_old
                    GROUP BY owner_id, project_id, provider, model, source, date;
                """;
                cmd.Parameters.AddWithValue("@cutoff", cutoff);
                cmd.Parameters.AddWithValue("@cutoff_old", now.AddDays(-RetentionDays * 2).ToString("O"));
                cmd.ExecuteNonQuery();
            }
            // Удаление старых детальных записей
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM spend_log WHERE ts < @cutoff;";
                cmd.Parameters.AddWithValue("@cutoff", cutoff);
                var removed = cmd.ExecuteNonQuery();
                if (removed > 0)
                    _log?.LogInformation("Лог расхода: удалено {Count} записей старше {Days} дн.", removed, RetentionDays);
            }
        }
        catch (Exception ex) { _log?.LogWarning(ex, "Прунинг лога расхода не удался"); }
    }

    // --- Чтение: агрегаты ---

    public record SpendAggregate(
        long InputTokens, long OutputTokens, long CacheReadTokens, long CacheCreationTokens,
        double? CostUsd, int TurnCount, int CompletedCount)
    {
        public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
        public double? CacheHitRate => (InputTokens + CacheCreationTokens) > 0
            ? (double)CacheReadTokens / (InputTokens + CacheCreationTokens + CacheReadTokens) : null;
    }

    public SpendAggregate? QueryAggregate(string ownerId, DateTime from, DateTime to,
        string? projectId = null, string? provider = null, string? model = null)
    {
        var sql = @"SELECT COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0),
            COALESCE(SUM(cache_read_tokens),0), COALESCE(SUM(cache_creation_tokens),0),
            SUM(cost_usd), COUNT(*), SUM(completed)
            FROM spend_log WHERE owner_id=@oid AND ts>=@from AND ts<@to";
        var filterParams = new List<(string name, object? value)>();
        if (projectId != null) { sql += " AND project_id=@pid"; filterParams.Add(("@pid", projectId)); }
        if (provider != null) { sql += " AND provider=@prov"; filterParams.Add(("@prov", provider)); }
        if (model != null) { sql += " AND model=@model"; filterParams.Add(("@model", model)); }

        return QueryOne<SpendAggregate>(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("@oid", ownerId);
            cmd.Parameters.AddWithValue("@from", from.ToString("O"));
            cmd.Parameters.AddWithValue("@to", to.ToString("O"));
            foreach (var (n, v) in filterParams) AddParam(cmd, n, v);
        });
    }

    public record DailyPoint(string Date, long InputTokens, long OutputTokens,
        long CacheReadTokens, long CacheCreationTokens, double? CostUsd, int TurnCount, int CompletedCount)
    {
        public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
    }

    public IReadOnlyList<DailyPoint> QueryDaily(string ownerId, DateTime from, DateTime to,
        string? projectId = null, string? provider = null)
    {
        var sql = @"SELECT SUBSTR(ts,1,10), SUM(input_tokens), SUM(output_tokens),
            SUM(cache_read_tokens), SUM(cache_creation_tokens),
            SUM(cost_usd), COUNT(*), SUM(completed)
            FROM spend_log
            WHERE owner_id=@oid AND ts>=@from AND ts<@to";
        if (projectId != null) sql += " AND project_id=@pid";
        if (provider != null) sql += " AND provider=@prov";
        sql += " GROUP BY SUBSTR(ts,1,10) ORDER BY SUBSTR(ts,1,10);";

        return QueryList(sql, r =>
        {
            var (input, output, cacheRead, cacheCreate) = (r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
            return new DailyPoint(
                r.GetString(0), input, output, cacheRead, cacheCreate,
                r.IsDBNull(5) ? null : r.GetDouble(5),
                r.GetInt32(6), r.GetInt32(7));
        }, cmd =>
        {
            cmd.Parameters.AddWithValue("@oid", ownerId);
            cmd.Parameters.AddWithValue("@from", from.ToString("O"));
            cmd.Parameters.AddWithValue("@to", to.ToString("O"));
            if (projectId != null) cmd.Parameters.AddWithValue("@pid", projectId);
            if (provider != null) cmd.Parameters.AddWithValue("@prov", provider);
        });
    }

    public record ProjectSummary(string? ProjectId, string? ProjectName,
        long InputTokens, long OutputTokens, long CacheReadTokens, long CacheCreationTokens,
        double? CostUsd, int TurnCount)
    {
        public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
    }

    public IReadOnlyList<ProjectSummary> QueryByProject(string ownerId, DateTime from, DateTime to)
    {
        var sql = @"SELECT project_id, SUM(input_tokens), SUM(output_tokens),
            SUM(cache_read_tokens), SUM(cache_creation_tokens), SUM(cost_usd), COUNT(*)
            FROM spend_log
            WHERE owner_id=@oid AND ts>=@from AND ts<@to
            GROUP BY project_id ORDER BY SUM(cost_usd) DESC;";

        return QueryList(sql, r => new ProjectSummary(
            r.IsDBNull(0) ? null : r.GetString(0), null,
            r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4),
            r.IsDBNull(5) ? null : r.GetDouble(5), r.GetInt32(6)), cmd =>
        {
            cmd.Parameters.AddWithValue("@oid", ownerId);
            cmd.Parameters.AddWithValue("@from", from.ToString("O"));
            cmd.Parameters.AddWithValue("@to", to.ToString("O"));
        });
    }

    public record ModelSummary(string Provider, string Model,
        long InputTokens, long OutputTokens, long CacheReadTokens, long CacheCreationTokens,
        double? CostUsd, int TurnCount)
    {
        public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
    }

    public IReadOnlyList<ModelSummary> QueryByModel(string ownerId, DateTime from, DateTime to)
    {
        var sql = @"SELECT provider, model, SUM(input_tokens), SUM(output_tokens),
            SUM(cache_read_tokens), SUM(cache_creation_tokens), SUM(cost_usd), COUNT(*)
            FROM spend_log
            WHERE owner_id=@oid AND ts>=@from AND ts<@to
            GROUP BY provider, model ORDER BY SUM(cost_usd) DESC;";

        return QueryList(sql, r => new ModelSummary(
            r.GetString(0), r.GetString(1),
            r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5),
            r.IsDBNull(6) ? null : r.GetDouble(6), r.GetInt32(7)), cmd =>
        {
            cmd.Parameters.AddWithValue("@oid", ownerId);
            cmd.Parameters.AddWithValue("@from", from.ToString("O"));
            cmd.Parameters.AddWithValue("@to", to.ToString("O"));
        });
    }

    public record SpendEntry(long Id, string OwnerId, string? ProjectId, string? SessionId,
        string? TaskId, string? PersonaId, string Provider, string Model, string Source,
        string Ts, int InputTokens, int OutputTokens, int CacheReadTokens, int CacheCreationTokens,
        double? CostUsd, long? DurationMs, bool Completed, string? EntityRef)
    {
        public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
    }

    public IReadOnlyList<SpendEntry> QueryEntries(string ownerId, DateTime from, DateTime to,
        string? projectId = null, string? sessionId = null, string? source = null,
        int limit = 100, int offset = 0)
    {
        var sql = @"SELECT id, owner_id, project_id, session_id, task_id, persona_id,
            provider, model, source, ts,
            input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
            cost_usd, duration_ms, completed, entity_ref
            FROM spend_log
            WHERE owner_id=@oid AND ts>=@from AND ts<@to";
        if (projectId != null) sql += " AND project_id=@pid";
        if (sessionId != null) sql += " AND session_id=@sid";
        if (source != null) sql += " AND source=@src";
        sql += " ORDER BY ts DESC LIMIT @lim OFFSET @off;";

        return QueryList(sql, r => new SpendEntry(
            r.GetInt64(0), r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5),
            r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9),
            r.GetInt32(10), r.GetInt32(11), r.GetInt32(12), r.GetInt32(13),
            r.IsDBNull(14) ? null : r.GetDouble(14),
            r.IsDBNull(15) ? null : r.GetInt64(15),
            r.GetInt32(16) == 1,
            r.IsDBNull(17) ? null : r.GetString(17)), cmd =>
        {
            cmd.Parameters.AddWithValue("@oid", ownerId);
            cmd.Parameters.AddWithValue("@from", from.ToString("O"));
            cmd.Parameters.AddWithValue("@to", to.ToString("O"));
            if (projectId != null) cmd.Parameters.AddWithValue("@pid", projectId);
            if (sessionId != null) cmd.Parameters.AddWithValue("@sid", sessionId);
            if (source != null) cmd.Parameters.AddWithValue("@src", source);
            cmd.Parameters.AddWithValue("@lim", Math.Clamp(limit, 1, 500));
            cmd.Parameters.AddWithValue("@off", Math.Max(0, offset));
        });
    }

    // Админ: агрегат по всем пользователям
    public record UserAggregate(string OwnerId, long InputTokens, long OutputTokens,
        long CacheReadTokens, long CacheCreationTokens, double? CostUsd, int TurnCount);

    public IReadOnlyList<UserAggregate> QueryAdminAggregate(DateTime from, DateTime to)
    {
        var sql = @"SELECT owner_id, SUM(input_tokens), SUM(output_tokens),
            SUM(cache_read_tokens), SUM(cache_creation_tokens), SUM(cost_usd), COUNT(*)
            FROM spend_log
            WHERE ts>=@from AND ts<@to
            GROUP BY owner_id ORDER BY SUM(cost_usd) DESC;";

        return QueryList(sql, r => new UserAggregate(
            r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4),
            r.IsDBNull(5) ? null : r.GetDouble(5), r.GetInt32(6)), cmd =>
        {
            cmd.Parameters.AddWithValue("@from", from.ToString("O"));
            cmd.Parameters.AddWithValue("@to", to.ToString("O"));
        });
    }

    // Первая дата учёта (граница «учёт ведётся с …»)
    public DateTime? QueryBoundary(string ownerId)
    {
        return QueryOne<DateTime?>("SELECT MIN(ts) FROM spend_log WHERE owner_id=@oid", cmd =>
        {
            cmd.Parameters.AddWithValue("@oid", ownerId);
        }, r => r.IsDBNull(0) ? null : DateTime.Parse(r.GetString(0), null,
            System.Globalization.DateTimeStyles.RoundtripKind));
    }

    // --- Хелперы чтения ---

    private T? QueryOne<T>(string sql, Action<SqliteCommand> setup, Func<SqliteDataReader, T>? map = null)
    {
        try
        {
            using var c = OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            setup(cmd);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return default;
            if (map != null) return map(r);
            // Для SpendAggregate — читаем напрямую
            var input = r.GetInt64(0);
            var output = r.GetInt64(1);
            var cacheRead = r.GetInt64(2);
            var cacheCreate = r.GetInt64(3);
            var cost = r.IsDBNull(4) ? (double?)null : r.GetDouble(4);
            var count = r.GetInt32(5);
            var completed = r.GetInt32(6);
            return (T)(object)new SpendAggregate(input, output, cacheRead, cacheCreate, cost, count, completed);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Ошибка запроса агрегата расхода");
            return default;
        }
    }

    private List<T> QueryList<T>(string sql, Func<SqliteDataReader, T> map, Action<SqliteCommand> setup)
    {
        try
        {
            using var c = OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            setup(cmd);
            var list = new List<T>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(map(r));
            return list;
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Ошибка запроса списка расхода");
            return [];
        }
    }

    private static void AddParam(SqliteCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
