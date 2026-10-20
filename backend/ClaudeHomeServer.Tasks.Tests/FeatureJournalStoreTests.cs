using System.Text.Json;
using ClaudeHomeServer.Services.Tasks.Journal;

namespace ClaudeHomeServer.Tasks.Tests;

// Тесты FeatureJournalStore (волна 3 «Обзор фич», Б8a).
// Платформонезависимые: пути от Path.GetTempPath(), ожидание — TaskCompletionSource.
public class FeatureJournalStoreTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fjs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static FeatureJournalEntry MakeEntry(
        string kind,
        string? taskId = null,
        string? sessionId = null,
        int? turnSeq = null,
        DateTime? startedAt = null,
        string? reason = null) => new()
    {
        At = DateTime.UtcNow,
        Kind = kind,
        TaskId = taskId,
        SessionId = sessionId,
        TurnSeq = turnSeq,
        StartedAt = startedAt,
        Reason = reason,
    };

    // --- Append + Read ---

    [Fact]
    public void Append_Then_Read_ReturnsEntry()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new FeatureJournalStore(dir);
            var entry = MakeEntry("created", taskId: "task-1");

            store.Append("owner1", "feat-1", entry);

            var result = store.Read("owner1", "feat-1");
            result.Entries.Should().HaveCount(1);
            result.Entries[0].Kind.Should().Be("created");
            result.Entries[0].TaskId.Should().Be("task-1");
            result.StartsWithCreated.Should().BeTrue();
            result.Truncated.Should().BeFalse();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Read_NonExistent_ReturnsEmpty()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new FeatureJournalStore(dir);
            var result = store.Read("owner1", "no-such");
            result.Entries.Should().BeEmpty();
            result.StartsWithCreated.Should().BeFalse();
            result.Truncated.Should().BeFalse();
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- StartsWithCreated ---

    [Fact]
    public void StartsWithCreated_False_WhenFirstIsNotCreated()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new FeatureJournalStore(dir);
            // Первая запись — started, не created
            store.Append("owner1", "feat-1", MakeEntry("started", taskId: "t1"));
            store.Append("owner1", "feat-1", MakeEntry("created", taskId: "t1"));

            var result = store.Read("owner1", "feat-1");
            result.StartsWithCreated.Should().BeFalse();
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- Потолок и Truncated ---

    [Fact]
    public void Cap_Exceeds_MaxLines_TurnsSkipped_TransitionsWritten_Truncated()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new FeatureJournalStore(dir);

            // Заполняем до MaxLines записями "created" (не turn — пишутся всегда)
            for (var i = 0; i < FeatureJournalStore.MaxLines; i++)
            {
                store.Append("owner1", "feat-1", MakeEntry("created", taskId: $"t{i}"));
            }

            // Теперь файл = MaxLines строк; turn не пишется
            store.Append("owner1", "feat-1", MakeEntry("turn", sessionId: "s1", turnSeq: 1));
            // Переход (started) пишется
            store.Append("owner1", "feat-1", MakeEntry("started", taskId: "t-new"));

            var result = store.Read("owner1", "feat-1");
            // MaxLines created + 1 started = MaxLines + 1 > MaxLines → truncated
            result.Truncated.Should().BeTrue();
            // Turn не записан: всего MaxLines + 1 (без turn)
            result.Entries.Should().HaveCount(FeatureJournalStore.MaxLines + 1);
            // Turn-записи нет
            result.Entries.Should().NotContain(e => e.Kind == "turn");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Cap_Below_MaxLines_TurnsWritten()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new FeatureJournalStore(dir);
            store.Append("owner1", "feat-1", MakeEntry("created", taskId: "t1"));
            store.Append("owner1", "feat-1", MakeEntry("turn", sessionId: "s1", turnSeq: 1));

            var result = store.Read("owner1", "feat-1");
            result.Entries.Should().HaveCount(2);
            result.Entries.Should().Contain(e => e.Kind == "turn");
            result.Truncated.Should().BeFalse();
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- Дедуп turn ---

    [Fact]
    public void Dedup_Turn_Same_SessionAndTurnSeq_KeepsFirst()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new FeatureJournalStore(dir);
            var at = DateTime.UtcNow;

            // Две записи turn с тем же (sessionId, turnSeq)
            store.Append("owner1", "feat-1", new FeatureJournalEntry
            {
                At = at, Kind = "turn", SessionId = "s1", TurnSeq = 5,
            });
            store.Append("owner1", "feat-1", new FeatureJournalEntry
            {
                At = at.AddSeconds(1), Kind = "turn", SessionId = "s1", TurnSeq = 5,
            });
            // Другой turnSeq — не дубль
            store.Append("owner1", "feat-1", new FeatureJournalEntry
            {
                At = at.AddSeconds(2), Kind = "turn", SessionId = "s1", TurnSeq = 6,
            });

            var result = store.Read("owner1", "feat-1");
            // 2 уникальных turn (turnSeq 5 и 6), дубль turnSeq=5 схлопнут
            result.Entries.Should().HaveCount(2);
            result.Entries[0].TurnSeq.Should().Be(5);
            result.Entries[1].TurnSeq.Should().Be(6);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Dedup_Turn_TurnSeqZero_ByStartedAt()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new FeatureJournalStore(dir);
            var at = DateTime.UtcNow;

            // Две записи turn с turnSeq=0 и тем же startedAt → дубль
            store.Append("owner1", "feat-1", new FeatureJournalEntry
            {
                At = at, Kind = "turn", SessionId = "s1", TurnSeq = 0, StartedAt = at,
            });
            store.Append("owner1", "feat-1", new FeatureJournalEntry
            {
                At = at.AddSeconds(1), Kind = "turn", SessionId = "s1", TurnSeq = 0, StartedAt = at,
            });
            // Разный startedAt → не дубль
            store.Append("owner1", "feat-1", new FeatureJournalEntry
            {
                At = at.AddSeconds(2), Kind = "turn", SessionId = "s1", TurnSeq = 0,
                StartedAt = at.AddSeconds(10),
            });

            var result = store.Read("owner1", "feat-1");
            // 2 уникальных (первый дубль схлопнут)
            result.Entries.Should().HaveCount(2);
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- Delete ---

    [Fact]
    public void Delete_RemovesFile()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new FeatureJournalStore(dir);
            store.Append("owner1", "feat-1", MakeEntry("created", taskId: "t1"));

            store.Delete("owner1", "feat-1");

            var result = store.Read("owner1", "feat-1");
            result.Entries.Should().BeEmpty();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Delete_NonExistent_NoThrow()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new FeatureJournalStore(dir);
            store.Delete("owner1", "no-such"); // не должно бросить
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- SweepOrphans ---

    [Fact]
    public void SweepOrphans_RemovesFilesWithoutLiveFeature()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new FeatureJournalStore(dir);
            store.Append("owner1", "feat-alive", MakeEntry("created", taskId: "t1"));
            store.Append("owner1", "feat-dead", MakeEntry("created", taskId: "t2"));

            // feat-alive жив, feat-dead — нет
            store.SweepOrphans("owner1", id => id == "feat-alive");

            var alive = store.Read("owner1", "feat-alive");
            alive.Entries.Should().HaveCount(1);

            var dead = store.Read("owner1", "feat-dead");
            dead.Entries.Should().BeEmpty();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SweepOrphans_NoOwnerDir_NoThrow()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new FeatureJournalStore(dir);
            store.SweepOrphans("nonexistent-owner", _ => true); // не должно бросить
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- Кривой id ---

    [Fact]
    public void InvalidId_DotDot_DoesNotCreateFile()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new FeatureJournalStore(dir);
            // ".." не проходит белый список
            store.Append("owner1", "..", MakeEntry("created", taskId: "t1"));

            // Файл не создан (ни в dir, ни "снаружу")
            var result = store.Read("owner1", "..");
            result.Entries.Should().BeEmpty();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void InvalidId_Slash_DoesNotCreateFile()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new FeatureJournalStore(dir);
            // "a/b" содержит слэш — не проходит белый список
            store.Append("owner1", "a/b", MakeEntry("created", taskId: "t1"));

            var result = store.Read("owner1", "a/b");
            result.Entries.Should().BeEmpty();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void InvalidId_TooLong_DoesNotCreateFile()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new FeatureJournalStore(dir);
            // 129 символов — больше 128
            var longId = new string('a', 129);
            store.Append("owner1", longId, MakeEntry("created", taskId: "t1"));

            var result = store.Read("owner1", longId);
            result.Entries.Should().BeEmpty();
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- Параллельный append ---

    [Fact]
    public async Task Parallel_Append_NoLinesLost()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new FeatureJournalStore(dir);
            const int total = 200;

            // 10 потоков × 20 записей каждый
            var tasks = Enumerable.Range(0, 10)
                .Select(workerId => Task.Run(async () =>
                {
                    for (var i = 0; i < 20; i++)
                    {
                        store.Append("owner1", "feat-1",
                            MakeEntry("created", taskId: $"w{workerId}-{i}"));
                        // Небольшая задержка для пересечения
                        await Task.Delay(1);
                    }
                }))
                .ToArray();

            await Task.WhenAll(tasks);

            var result = store.Read("owner1", "feat-1");
            result.Entries.Should().HaveCount(total);
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- JSON camelCase ---

    [Fact]
    public void Append_ProducesCamelCaseJson()
    {
        var dir = MakeTempDir();
        try
        {
            var store = new FeatureJournalStore(dir);
            store.Append("owner1", "feat-1", new FeatureJournalEntry
            {
                At = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc),
                Kind = "started",
                TaskId = "task-42",
                SessionId = "sess-1",
            });

            // Читаем сырой файл и проверяем camelCase
            var path = Path.Combine(dir, "owner1", "feat-1.jsonl");
            var line = File.ReadAllText(path).Trim();
            var json = JsonDocument.Parse(line);
            json.RootElement.TryGetProperty("taskId", out _).Should().BeTrue();
            json.RootElement.TryGetProperty("sessionId", out _).Should().BeTrue();
            json.RootElement.TryGetProperty("kind", out var kind).Should().BeTrue();
            kind.GetString().Should().Be("started");
        }
        finally { Directory.Delete(dir, true); }
    }
}
