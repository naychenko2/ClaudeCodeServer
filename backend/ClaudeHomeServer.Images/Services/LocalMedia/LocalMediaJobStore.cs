using System.Text.Json;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

public static class LocalMediaOps
{
    public const string GenerateImage = "generate_image";
    public const string EditImage = "edit_image";
    public const string FaceDetail = "face_detail";
    public const string ImageToVideo = "image_to_video";

    public static bool IsKnown(string? op) => op is GenerateImage or EditImage or FaceDetail or ImageToVideo;
}

public static class LocalMediaStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static bool IsTerminal(string status) => status is Completed or Failed;
}

// Готовый файл результата: путь от корня проекта (через «/»), размеры для показа
public sealed class LocalMediaOutput
{
    public string Path { get; set; } = "";
    public string ContentType { get; set; } = "";
    public int? Width { get; set; }
    public int? Height { get; set; }
}

public sealed class LocalMediaJob
{
    public string Id { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string? SessionId { get; set; }
    public string Op { get; set; } = "";
    public string PromptId { get; set; } = "";
    public string Status { get; set; } = LocalMediaStatuses.Queued;
    public long Seed { get; set; }
    // Ожидаемые размеры видео (у картинок размеры читаются из файла результата)
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? DurationSeconds { get; set; }
    public string? Error { get; set; }
    public List<LocalMediaOutput> Outputs { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}

// Задачи локальной генерации: data/local-media-jobs.json. Переживает рестарт бэкенда, пока
// задача стоит в очереди ComfyUI: коллектор после старта доберёт результат сам.
//
// Изоляция per-owner: чтение по id — только вместе с владельцем, чужая задача неотличима
// от несуществующей. Бэкап: файл едет в архив по общему правилу — это метаданные (id задач,
// пути результатов в проекте), секретов в нём нет. Ломающее изменение структуры = инкремент
// FormatVersion (правило BackupSchema).
public sealed class LocalMediaJobStore
{
    public const int FormatVersion = 1;
    public const string FileName = "local-media-jobs.json";

    public sealed class JobsFile
    {
        public int Version { get; set; } = FormatVersion;
        public List<LocalMediaJob> Jobs { get; set; } = [];
    }

    private readonly string _storePath;
    private readonly ILogger<LocalMediaJobStore>? _log;
    private readonly Lock _writeLock = new();
    private List<LocalMediaJob> _jobs = [];

    public LocalMediaJobStore(IConfiguration config, ILogger<LocalMediaJobStore>? log = null)
    {
        _log = log;
        // Путь — только от DataPath, как у соседних сторов: рядом с исполняемым файл
        // потерялся бы при деплое
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _storePath = Path.Combine(Path.GetDirectoryName(dataPath)!, FileName);
        Load();
    }

    public void Add(LocalMediaJob job)
    {
        lock (_writeLock)
        {
            _jobs = [.. _jobs, Clone(job)];
            Save();
        }
    }

    // Чужой владелец — null, как у несуществующей задачи
    public LocalMediaJob? Get(string id, string ownerId)
    {
        lock (_writeLock)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id && j.OwnerId == ownerId);
            return job is null ? null : Clone(job);
        }
    }

    public int ActiveCount(string ownerId)
    {
        lock (_writeLock)
            return _jobs.Count(j => j.OwnerId == ownerId && !LocalMediaStatuses.IsTerminal(j.Status));
    }

    public IReadOnlyList<LocalMediaJob> Active()
    {
        lock (_writeLock)
            return _jobs.Where(j => !LocalMediaStatuses.IsTerminal(j.Status)).Select(Clone).ToList();
    }

    // Изменение задачи под локом; null — задачи нет
    public LocalMediaJob? Update(string id, Action<LocalMediaJob> change)
    {
        lock (_writeLock)
        {
            var index = _jobs.FindIndex(j => j.Id == id);
            if (index < 0) return null;
            var next = Clone(_jobs[index]);
            change(next);
            var copy = _jobs.ToList();
            copy[index] = next;
            _jobs = copy;
            Save();
            return Clone(next);
        }
    }

    // Забыть завершённые задачи старше срока: файлы в проекте остаются, теряется только
    // возможность сослаться на задачу по job_id
    public int Prune(TimeSpan olderThan, DateTime now)
    {
        lock (_writeLock)
        {
            var keep = _jobs.Where(j => !LocalMediaStatuses.IsTerminal(j.Status)
                || (j.FinishedAt ?? j.CreatedAt) > now - olderThan).ToList();
            var removed = _jobs.Count - keep.Count;
            if (removed == 0) return 0;
            _jobs = keep;
            Save();
            return removed;
        }
    }

    private static readonly JsonSerializerOptions CloneOptions = new();

    private static LocalMediaJob Clone(LocalMediaJob job) =>
        JsonSerializer.Deserialize<LocalMediaJob>(JsonSerializer.Serialize(job, CloneOptions), CloneOptions)!;

    // Только под _writeLock
    private void Save()
    {
        try
        {
            JsonFileStore.Save(_storePath, new JobsFile { Version = FormatVersion, Jobs = _jobs });
        }
        catch (Exception ex)
        {
            // Состояние уже в памяти — до рестарта теряется только персистентность
            _log?.LogError(ex, "Не удалось записать {Path}", _storePath);
        }
    }

    private static readonly JsonSerializerOptions LoadOptions = new() { PropertyNameCaseInsensitive = true };

    private void Load()
    {
        var file = JsonFileStore.Load<JobsFile>(_storePath, LoadOptions, _log);
        if (file is null) return;
        if (file.Version > FormatVersion)
        {
            _log?.LogWarning("{File} имеет формат {FileVersion} новее поддерживаемого {Version} — стартую без задач",
                FileName, file.Version, FormatVersion);
            return;
        }
        _jobs = (file.Jobs ?? [])
            .Where(j => !string.IsNullOrWhiteSpace(j.Id) && !string.IsNullOrWhiteSpace(j.OwnerId)
                && LocalMediaOps.IsKnown(j.Op))
            .ToList();
    }
}
