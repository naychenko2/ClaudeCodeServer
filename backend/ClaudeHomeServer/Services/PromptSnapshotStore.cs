using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Снимки промпта ходов: data/prompt-snapshots/{sessionId}/{id}.json.gz.
/// Ключ — Session.Id, а НЕ ClaudeSessionId: на первом ходу второго ещё нет, а чаты,
/// созданные с resumeSessionId, делят один транскрипт — их промпты перемешались бы
/// в одной папке и вытесняли друг друга ретеншном.
/// Всё best-effort: снимок — диагностика, его сбой не имеет права ронять ход.
/// </summary>
public sealed class PromptSnapshotStore
{
    // Сколько ходов храним на чат. Id снимка живёт в history.json вечно, поэтому у постов
    // старше этого окна кнопка отдаёт 404 — UI показывает «вытеснен ретеншном».
    public const int MaxPerSession = 50;

    // Потолки: снимок — диагностика, а не архив
    private const int MaxSectionChars = 256 * 1024;
    private const int MaxTotalChars = 1_000_000;
    private const string TruncatedMark = "\n\n…обрезано";

    // Имя папки и файла становится сегментом пути — пускаем только безопасный алфавит
    // (тот же приём, что TranscriptMigrator.IsSafeSessionId: Path.GetFileName пропустил бы
    // «..», а на Linux — и «..\..\» целиком)
    private static readonly Regex SafeId = new(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _basePath;

    // Последний снимок чата, где файловая часть слоя CLI записана целиком: следующие ходы
    // с тем же содержимым ссылаются на него (CliLayerFrom) вместо копии CLAUDE.md на
    // десятки КБ. После рестарта словарь пуст — первый снимок просто запишется целиком.
    private readonly ConcurrentDictionary<string, (string Hash, string Id)> _lastFullCliLayer = new();

    // Файловые операции чата сериализуем: Save (поток хода) и AttachCliLayer (поток
    // stdout-ридера) ходят в одну папку и могут пересечься на ретеншне
    private readonly ConcurrentDictionary<string, object> _locks = new();

    public PromptSnapshotStore(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _basePath = Path.Combine(dataDir, "prompt-snapshots");
    }

    /// <summary>Записать снимок хода. Возвращает id (он же имя файла) либо null при сбое.</summary>
    public string? Save(string sessionId, PromptSnapshotDraft draft)
    {
        if (!SafeId.IsMatch(sessionId)) return null;

        try
        {
            var id = NewId();
            var snapshot = new PromptSnapshotDto(
                id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                draft.Applied, draft.InheritedFromId,
                Trim(draft.Sections), draft.CliArgs, draft.McpServers,
                draft.Model, draft.Mode, draft.CliLayer);

            snapshot = DedupCliLayer(sessionId, snapshot);

            lock (LockFor(sessionId))
            {
                var dir = Path.Combine(_basePath, sessionId);
                Directory.CreateDirectory(dir);
                WriteFile(Path.Combine(dir, id + ".json.gz"), snapshot);
                Retain(dir);
            }

            // Запоминаем донора ПОСЛЕ успешной записи: иначе следующий ход сослался бы
            // на снимок, которого нет
            if (snapshot.CliLayerFrom is null && HashOf(draft.CliLayer) is { } hash)
                _lastFullCliLayer[sessionId] = (hash, id);

            return id;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PromptSnapshotStore] Снимок не записан ({sessionId}): {ex.Message}");
            return null;
        }
    }

    /// <summary>Снимок по id. null — нет такого (в норме: вытеснен ретеншном).</summary>
    public PromptSnapshotDto? Load(string sessionId, string snapshotId)
    {
        if (!SafeId.IsMatch(sessionId) || !SafeId.IsMatch(snapshotId)) return null;

        var snapshot = ReadFile(PathFor(sessionId, snapshotId));
        if (snapshot is null) return snapshot;

        // Разворачиваем ссылку на донора файловой части. Донор всегда содержит текст
        // (ссылки на ссылку не бывает), поэтому рекурсии здесь нет. Донор вытеснен —
        // отдаём как есть: блок в UI деградирует до списка инструментов.
        if (snapshot.CliLayerFrom is { } donorId && SafeId.IsMatch(donorId)
            && ReadFile(PathFor(sessionId, donorId))?.CliLayer is { } donor)
        {
            var own = snapshot.CliLayer;
            snapshot = snapshot with
            {
                CliLayer = new CliLayerDto(
                    own?.Tools, own?.McpServers, donor.Files, donor.Skills,
                    own?.TranscriptBytes, own?.TranscriptMessages),
            };
        }

        return snapshot;
    }

    /// <summary>
    /// Дописать в готовый снимок то, что известно только после старта процесса: состав
    /// инструментов и статусы MCP-серверов из события system/init. В gzip дописать нельзя —
    /// это чтение, распаковка, правка и замена файла целиком.
    /// </summary>
    public void AttachCliLayer(string sessionId, string snapshotId,
        IReadOnlyList<string>? tools, IReadOnlyList<McpServerInfo>? mcpServers)
    {
        if (!SafeId.IsMatch(sessionId) || !SafeId.IsMatch(snapshotId)) return;

        try
        {
            lock (LockFor(sessionId))
            {
                var path = PathFor(sessionId, snapshotId);
                // Снимок уже вытеснен ретеншном (очень длинный прогон) — молча выходим
                var snapshot = ReadFile(path);
                if (snapshot is null) return;

                var layer = snapshot.CliLayer ?? new CliLayerDto();
                WriteFile(path, snapshot with
                {
                    CliLayer = layer with { Tools = tools, McpServers = mcpServers },
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PromptSnapshotStore] Слой CLI не дописан ({sessionId}/{snapshotId}): {ex.Message}");
        }
    }

    /// <summary>Удалить снимки чата (вызывается при удалении чата).</summary>
    public void DeleteAll(string sessionId)
    {
        if (!SafeId.IsMatch(sessionId)) return;
        _lastFullCliLayer.TryRemove(sessionId, out _);
        _locks.TryRemove(sessionId, out _);
        try
        {
            var dir = Path.Combine(_basePath, sessionId);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* файл занят — дочистится при следующем удалении */ }
        catch (UnauthorizedAccessException) { }
    }

    private object LockFor(string sessionId) => _locks.GetOrAdd(sessionId, _ => new object());

    private string PathFor(string sessionId, string snapshotId) =>
        Path.Combine(_basePath, sessionId, snapshotId + ".json.gz");

    // Счётчик внутри процесса — суффикс имени. Именно возрастающий, а не случайный:
    // ретеншн сортирует файлы ПО ИМЕНИ, и у снимков одной миллисекунды случайный суффикс
    // задавал бы неверный порядок — вытеснялись бы не самые старые.
    private static int _seq;

    // {unixMs}-{seq}: лексикографически сортируемо по времени. После рестарта счётчик
    // начинается заново, но старшая часть (миллисекунды) уже больше — порядок сохраняется.
    private static string NewId() =>
        $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Interlocked.Increment(ref _seq) & 0xFFFFF:x5}";

    private static void WriteFile(string path, PromptSnapshotDto snapshot)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        JsonSerializer.Serialize(gzip, snapshot, Opts);
    }

    private static PromptSnapshotDto? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<PromptSnapshotDto>(gzip, Opts);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    // Держим окно последних снимков: имена сортируемы по времени, поэтому хватает
    // обычной сортировки по имени файла
    private static void Retain(string dir)
    {
        var files = Directory.GetFiles(dir, "*.json.gz");
        if (files.Length <= MaxPerSession) return;
        Array.Sort(files, StringComparer.Ordinal);
        foreach (var stale in files.Take(files.Length - MaxPerSession))
            try { File.Delete(stale); } catch (IOException) { }
    }

    // Обрезка по секции и по сумме: recall, привязки и slice графа бывают жирными,
    // а снимков на чат — до полусотни
    private static IReadOnlyList<PromptSectionDto> Trim(IReadOnlyList<PromptSectionDto> sections)
    {
        var total = 0;
        var result = new List<PromptSectionDto>(sections.Count);
        foreach (var s in sections)
        {
            var budget = Math.Min(MaxSectionChars, Math.Max(0, MaxTotalChars - total));
            var text = s.Text.Length <= budget ? s.Text : s.Text[..budget] + TruncatedMark;
            total += Math.Min(s.Text.Length, budget);
            result.Add(s with { Text = text });
        }
        return result;
    }

    // Файловая часть слоя CLI (CLAUDE.md + скиллы) меняется редко, а весит больше всего
    // остального снимка. Совпала с прошлым ходом — пишем ссылку вместо копии.
    private PromptSnapshotDto DedupCliLayer(string sessionId, PromptSnapshotDto snapshot)
    {
        if (HashOf(snapshot.CliLayer) is not { } hash) return snapshot;
        if (!_lastFullCliLayer.TryGetValue(sessionId, out var last) || last.Hash != hash)
            return snapshot;
        // Донор мог уехать по ретеншну, пока чат жил — тогда копию оставляем
        if (!File.Exists(PathFor(sessionId, last.Id))) return snapshot;

        return snapshot with
        {
            CliLayer = snapshot.CliLayer! with { Files = null, Skills = null },
            CliLayerFrom = last.Id,
        };
    }

    // null — дедуплицировать нечего (файловой части нет)
    private static string? HashOf(CliLayerDto? layer)
    {
        if (layer is null || (layer.Files is null && layer.Skills is null)) return null;
        var json = JsonSerializer.Serialize(new { layer.Files, layer.Skills }, Opts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
