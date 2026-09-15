using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.Spend;

/// <summary>
/// Учёт размера промпта постановки задачи по секциям (план «Оптимизация потребления
/// токенов», шаг 4.1). Одна строка = один запуск исполнителя.
///
/// ПОЧЕМУ ЖИВЁТ В data/spend/: наблюдательная метрика промптов задач, физически в
/// spend-каталоге по историческим причинам (создана в рамках того же плана). Концептуальной
/// связи с деньгами нет; выделение в отдельную вертикаль не оправдано объёмом (один
/// JSONL-файл, одна запись на запуск).
///
/// ПРИВАТНОСТЬ (инвариант, под тестом). Сюда пишутся ТОЛЬКО РАЗМЕРЫ секций в символах —
/// ни одного символа текста постановки, описания задачи или заметок из базы знаний.
/// То же правило, что у ModuleLlmUsageStore и PromptAuditService: файл лежит рядом с
/// аналитикой и попадает в выдачу API, содержимого чужих заметок там быть не может.
///
/// Формат — JSONL (data/spend/task-prompts.jsonl) по образцу ModuleLlmUsageStore:
/// учёт только растёт, дописывание строки дешевле перезаписи JSON-стора.
/// В облачный бэкап НЕ едет (BackupPaths.ShouldInclude) — это наблюдение, а не настройка.
/// </summary>
public sealed class TaskPromptMetricsStore : ITaskPromptMetricsStore
{
    public const string FileName = "task-prompts.jsonl";
    public const string DirName = "spend";

    // Замер одного запуска исполнителя. Только числа и идентификаторы разрезов.
    // Тип перенесён в Core (TaskPromptMetricsEntry в ITaskPromptMetricsStore.cs) как
    // return/arg-тип интерфейса, чтобы Main не тянул конкретную сборку Spend.

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _storePath;
    private readonly ILogger<TaskPromptMetricsStore>? _log;
    private readonly Lock _writeLock = new();

    public TaskPromptMetricsStore(IConfiguration config, ILogger<TaskPromptMetricsStore>? log = null)
        : this(ResolveDir(config), log) { }

    /// <summary>Прямой конструктор для тестов: произвольная папка.</summary>
    public TaskPromptMetricsStore(string dir, ILogger<TaskPromptMetricsStore>? log = null)
    {
        _storePath = Path.Combine(dir, FileName);
        _log = log;
    }

    private static string ResolveDir(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        return Path.Combine(dataDir, DirName);
    }

    /// <summary>Дописать замер. Учёт не должен ронять запуск задачи — сбой только в лог.</summary>
    public void Record(TaskPromptMetricsEntry entry)
    {
        try
        {
            lock (_writeLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
                File.AppendAllText(_storePath, JsonSerializer.Serialize(entry, JsonOpts) + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Не удалось записать метрики промпта задачи {TaskId} в {Path}",
                entry.TaskId, _storePath);
        }
    }

    /// <summary>
    /// Замеры задачи, новые сверху. Пустой список — задача запускалась до появления стора
    /// либо файл ещё не создан: это не ошибка, разбивку просто не покажем.
    /// </summary>
    public IReadOnlyList<TaskPromptMetricsEntry> ForTask(string taskId)
    {
        if (!File.Exists(_storePath)) return [];
        try
        {
            var result = new List<TaskPromptMetricsEntry>();
            // Чтение под тем же локом, что и запись: File.AppendAllText не атомарен
            // относительно чтения, а строка, прочитанная наполовину, свалит десериализацию
            lock (_writeLock)
            {
                foreach (var line in File.ReadLines(_storePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    TaskPromptMetricsEntry? e;
                    // Битая строка (обрыв записи при падении процесса) не должна прятать
                    // остальные замеры — пропускаем её молча
                    try { e = JsonSerializer.Deserialize<TaskPromptMetricsEntry>(line, JsonOpts); }
                    catch (JsonException) { continue; }
                    if (e is not null && e.TaskId == taskId) result.Add(e);
                }
            }
            result.Reverse();
            return result;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Не удалось прочитать метрики промпта задачи {TaskId}", taskId);
            return [];
        }
    }
}
