using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Services.ImageEditor.Versioning;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.ImageEditor;

// Записи версий LoRA персонажа (ADR-026, 6.2): <проект>/characters/<slug>/lora/runs/<runId>.json.
// Владение проектом проверяет контроллер, сюда приходит уже корень. «Подключена» не хранится:
// это сравнение loraFile с providers.local.lora персонажа (IsConnected).
public sealed class CharacterLoraRuns(ILogger<CharacterLoraRuns> log)
{
    public const int MaxVersions = 5;
    public const string RunsFolder = "lora/runs";

    public static class Status
    {
        public const string Training = "training", Ready = "ready", Failed = "failed",
            Cancelled = "cancelled", Deleting = "deleting";
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();
    private readonly KeyedLocks _locks = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Root, string Slug), byte> _deletingIndex = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Root, string Slug), byte> _trainingIndex = new();

    // Одна блокировка на персонажа: пару «прочитать манифест → записать» берут и доводчик, и ручки версий,
    // иначе автоподключение перезатирает выбор человека. Ключ — корень проекта без регистра разделителей.
    public Task<IDisposable> LockCharacterAsync(string projectRoot, string slug, CancellationToken ct = default) =>
        _locks.AcquireAsync(Path.GetFullPath(projectRoot) + "|" + slug, ct);

    // Тот же компонент для блокировок старта обучения: ключи другого вида (owner|project|slug), пересечения нет
    public Task<IDisposable> LockKeyAsync(string key, CancellationToken ct = default) => _locks.AcquireAsync(key, ct);

    // Персонажи, у которых есть записи «deleting»: доводчик обходит только их (плюс один полный проход при старте)
    public IReadOnlyList<(string Root, string Slug)> CharactersWithDeleting() => [.. _deletingIndex.Keys];

    public void RegisterDeleting(string projectRoot, string slug) => _deletingIndex[(projectRoot, slug)] = 0;

    // Снимает персонажа с индекса, если «deleting» больше нет; проверка под тем же замком, что и регистрация в Update
    public void UnregisterDeletingIfEmpty(string projectRoot, string slug)
    {
        lock (_lock)
        {
            if (ReadAll(projectRoot, slug).All(r => r.Status != Status.Deleting)) _deletingIndex.TryRemove((projectRoot, slug), out _);
        }
    }

    // Персонажи, у которых есть записи «training»: доводчик каждый круг проверяет только их (плюс один полный проход при старте)
    public IReadOnlyList<(string Root, string Slug)> CharactersWithTraining() => [.. _trainingIndex.Keys];

    public void RegisterTraining(string projectRoot, string slug) => _trainingIndex[(projectRoot, slug)] = 0;

    // Снимает персонажа с индекса, если «training» больше нет; под тем же замком, что и регистрация
    public void UnregisterTrainingIfEmpty(string projectRoot, string slug)
    {
        lock (_lock)
        {
            if (ReadAll(projectRoot, slug).All(r => r.Status != Status.Training)) _trainingIndex.TryRemove((projectRoot, slug), out _);
        }
    }

    // Без deleting, новые сверху
    public IReadOnlyList<CharacterLoraRun> List(string projectRoot, string slug) =>
        [.. ReadAll(projectRoot, slug).Where(r => r.Status != Status.Deleting).OrderByDescending(r => r.CreatedAt)];

    public IReadOnlyList<CharacterLoraRun> ListDeleting(string projectRoot, string slug) =>
        [.. ReadAll(projectRoot, slug).Where(r => r.Status == Status.Deleting)];

    public CharacterLoraRun? Get(string projectRoot, string slug, string runId)
    {
        var path = PathOf(projectRoot, slug, runId);
        return path is null ? null : TryRead(path);
    }

    // false — runId/slug недопустимы или запись уже есть
    public bool Create(string projectRoot, string slug, CharacterLoraRun run)
    {
        var path = PathOf(projectRoot, slug, run.RunId);
        if (path is null) return false;
        lock (_lock)
        {
            if (File.Exists(path)) return false;
            Write(path, run);
            if (run.Status == Status.Training) RegisterTraining(projectRoot, slug);
            return true;
        }
    }

    // Правка под замком: изменение применяется к свежепрочитанной записи; null — записи нет
    public CharacterLoraRun? Update(string projectRoot, string slug, string runId, Func<CharacterLoraRun, CharacterLoraRun> change)
    {
        var path = PathOf(projectRoot, slug, runId);
        if (path is null) return null;
        lock (_lock)
        {
            if (TryRead(path) is not { } current) return null;
            var updated = change(current) with { RunId = runId };
            Write(path, updated);
            if (updated.Status == Status.Deleting) RegisterDeleting(projectRoot, slug);
            if (updated.Status == Status.Training) RegisterTraining(projectRoot, slug);
            else if (current.Status == Status.Training) UnregisterTrainingIfEmpty(projectRoot, slug);
            return updated;
        }
    }

    public bool Erase(string projectRoot, string slug, string runId)
    {
        var path = PathOf(projectRoot, slug, runId);
        if (path is null) return false;
        lock (_lock)
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
    }

    // Для предела версий: failed/cancelled/deleting не считаются
    public int CountTowardsLimit(string projectRoot, string slug) =>
        ReadAll(projectRoot, slug).Count(r => r.Status is Status.Ready or Status.Training);

    // Даты обучения готовых LoRA всех персонажей проекта: полное имя файла → TrainedAt (при повторе имени — свежая)
    public IReadOnlyDictionary<string, DateTimeOffset> TrainedDates(string projectRoot)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var character in CharacterStore.List(projectRoot))
            foreach (var run in ReadAll(projectRoot, character.Slug))
            {
                if (run.Status != Status.Ready || run.LoraFile is not { Length: > 0 } file || run.TrainedAt is not { } at) continue;
                if (!result.TryGetValue(file, out var known) || at > known) result[file] = at;
            }
        return result;
    }

    public static bool IsConnected(CharacterLoraRun run, string? activeLoraName) =>
        run.LoraFile is { Length: > 0 } && string.Equals(run.LoraFile, activeLoraName, StringComparison.Ordinal);

    private List<CharacterLoraRun> ReadAll(string projectRoot, string slug)
    {
        var result = new List<CharacterLoraRun>();
        var dir = DirOf(projectRoot, slug);
        if (dir is null || !Directory.Exists(dir)) return result;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            if (!LoraNamespace.IsValidRunId(Path.GetFileNameWithoutExtension(file))) continue;
            if (TryRead(file) is { } run) result.Add(run);
        }
        return result;
    }

    private CharacterLoraRun? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var run = JsonSerializer.Deserialize<CharacterLoraRun>(File.ReadAllText(path), Json);
            // Имя файла и runId внутри обязаны совпадать, иначе запись чужая
            if (run is not null && LoraNamespace.IsValidRunId(run.RunId)
                && run.RunId == Path.GetFileNameWithoutExtension(path)) return run;
            log.LogWarning("Запись версии LoRA {Path} не соответствует имени файла, пропущена", path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Запись версии LoRA {Path} не прочитана, пропущена", path);
        }
        return null;
    }

    // Через временный файл: оборванная запись не должна оставить полузаписанный json
    private static void Write(string path, CharacterLoraRun run)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(run, Json));
        File.Move(temp, path, overwrite: true);
    }

    private static string? DirOf(string projectRoot, string slug)
    {
        if (!CharacterStore.IsValidSlug(slug)) return null;
        try { return SafePath.Join(projectRoot, $"{CharacterStore.Folder}/{slug}/{RunsFolder}"); }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException) { return null; }
    }

    private static string? PathOf(string projectRoot, string slug, string runId)
    {
        if (!LoraNamespace.IsValidRunId(runId)) return null;
        var dir = DirOf(projectRoot, slug);
        if (dir is null) return null;
        try { return SafePath.Join(dir, runId + ".json"); }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException) { return null; }
    }
}

public sealed record LoraRunError(string Code, string Message);

public sealed record LoraRunRejection(string Name, string Reason);

public sealed record CharacterLoraRun(
    int Version, string RunId, string? JobId, string Status, string? Title, string Preset, string Trigger,
    int PhotosCount, string PhotosHash, DateTimeOffset CreatedAt, DateTimeOffset? TrainedAt,
    string? LoraFile, int? PhotosKept, IReadOnlyList<LoraRunRejection>? Rejected, LoraRunError? Error);
