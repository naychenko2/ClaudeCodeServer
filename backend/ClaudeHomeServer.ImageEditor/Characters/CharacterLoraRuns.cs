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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _characterLocks = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Root, string Slug), byte> _deletingIndex = new();

    // Одна блокировка на персонажа: пару «прочитать манифест → записать» берут и доводчик, и ручки версий,
    // иначе автоподключение перезатирает выбор человека. Ключ — корень проекта без регистра разделителей.
    public async Task<IDisposable> LockCharacterAsync(string projectRoot, string slug, CancellationToken ct = default)
    {
        var gate = _characterLocks.GetOrAdd(Path.GetFullPath(projectRoot) + "|" + slug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }

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
