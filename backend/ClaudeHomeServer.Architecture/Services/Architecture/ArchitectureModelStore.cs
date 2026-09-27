using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Architecture;

/// <summary>«Кто и когда обновил модель» — из файла метаданных рядом с моделью.</summary>
public sealed record ArchitectureModelAuthor(DateTimeOffset? UpdatedAt, string? UpdatedBy);

/// <summary>Содержимое файла модели как есть + версия (SHA-256 байтов; null — файла нет).</summary>
public sealed record ArchitectureModelSnapshot(string? Content, string? Version, ArchitectureModelAuthor Author);

/// <summary>Итог записи: либо новая версия, либо конфликт с текущим состоянием файла.</summary>
public sealed record ArchitectureSaveOutcome(bool Saved, ArchitectureModelSnapshot Current);

/// <summary>Тело PUT не годится в модель (не JSON-объект или слишком большое).</summary>
public sealed class ArchitectureModelInvalidException(string message) : Exception(message);

/// <summary>
/// Общий замок записи файлов модели: генератор («Собрать из кода») и сохранение из
/// редактора пишут один и тот же файл, и сверка версии с записью должна быть атомарной
/// относительно обоих. Записи редкие и короткие — одного замка на процесс хватает.
/// </summary>
internal static class ArchitectureFileGate
{
    public static readonly SemaphoreSlim Instance = new(1, 1);
}

/// <summary>
/// Хранилище C4-модели проекта: <c>docs/architecture/model.viaduct.json</c> в корне проекта
/// (файл под git — его бэкап и история). Лежит ровно persist-обёртка стора Viaduct: GET
/// отдаёт байты файла без переформатирования, PUT пишет тело как есть. Версия — SHA-256
/// содержимого; запись с устаревшей версией — конфликт (контроллер отвечает 409).
/// «Кто и когда» — поля <c>updatedAt</c>/<c>updatedBy</c> в файле метаданных рядом
/// (<see cref="ArchitectureModelGenerator.MetaRelPath"/>): сохранения из редактора не
/// коммитятся, поэтому git-лог файла на этот вопрос не отвечает.
/// Пути — только через <see cref="SafePath.Join"/>.
/// </summary>
public sealed class ArchitectureModelStore
{
    /// <summary>Потолок размера модели: канва на тысячах узлов всё равно не живёт.</summary>
    public const int MaxContentBytes = 10 * 1024 * 1024;

    /// <summary>Автор записи генератором (у его метаданных нет updatedBy).</summary>
    public const string GeneratorAuthor = "Сборка из кода";

    // Без \u-экранирования кириллицы: метаданные читают в git diff глазами
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<ArchitectureModelSnapshot> ReadAsync(string root, CancellationToken ct)
    {
        var (modelPath, metaPath) = Paths(root);
        var bytes = File.Exists(modelPath) ? await File.ReadAllBytesAsync(modelPath, ct) : null;
        return new ArchitectureModelSnapshot(
            bytes is null ? null : Utf8NoBom.GetString(bytes),
            bytes is null ? null : VersionOf(bytes),
            await ReadAuthorAsync(metaPath, ct));
    }

    /// <summary>
    /// Пишет модель, если файл всё ещё в версии <paramref name="baseVersion"/>
    /// (null — «файла ещё нет»). Иначе ничего не трогает и отдаёт текущее состояние.
    /// </summary>
    public async Task<ArchitectureSaveOutcome> WriteAsync(
        string root, string content, string? baseVersion, string updatedBy, CancellationToken ct)
    {
        Validate(content);
        var (modelPath, metaPath) = Paths(root);
        var bytes = Utf8NoBom.GetBytes(content);

        await ArchitectureFileGate.Instance.WaitAsync(ct);
        try
        {
            var current = File.Exists(modelPath) ? await File.ReadAllBytesAsync(modelPath, ct) : null;
            var currentVersion = current is null ? null : VersionOf(current);
            if (!string.Equals(currentVersion, baseVersion, StringComparison.OrdinalIgnoreCase))
            {
                return new ArchitectureSaveOutcome(false, new ArchitectureModelSnapshot(
                    current is null ? null : Utf8NoBom.GetString(current),
                    currentVersion,
                    await ReadAuthorAsync(metaPath, ct)));
            }

            await WriteAtomicAsync(modelPath, bytes, ct);
            var author = new ArchitectureModelAuthor(DateTimeOffset.UtcNow, updatedBy);
            await WriteAuthorAsync(metaPath, author, ct);
            return new ArchitectureSaveOutcome(true, new ArchitectureModelSnapshot(content, VersionOf(bytes), author));
        }
        finally
        {
            ArchitectureFileGate.Instance.Release();
        }
    }

    public static string VersionOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static (string Model, string Meta) Paths(string root) => (
        SafePath.Join(root, ArchitectureModelGenerator.ModelRelPath),
        SafePath.Join(root, ArchitectureModelGenerator.MetaRelPath));

    private static void Validate(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxContentBytes)
            throw new ArchitectureModelInvalidException($"Модель больше {MaxContentBytes / 1024 / 1024} МБ");
        try
        {
            if (JsonNode.Parse(content) is not JsonObject)
                throw new ArchitectureModelInvalidException("Модель должна быть JSON-объектом");
        }
        catch (JsonException ex)
        {
            throw new ArchitectureModelInvalidException($"Модель не разбирается как JSON: {ex.Message}");
        }
    }

    private static async Task<ArchitectureModelAuthor> ReadAuthorAsync(string metaPath, CancellationToken ct)
    {
        var meta = await ReadMetaAsync(metaPath, ct);
        if (meta is null) return new ArchitectureModelAuthor(null, null);
        var updatedAt = TryDate(meta["updatedAt"]);
        var generatedAt = TryDate(meta["generatedAt"]);
        // Генератор переписывает метаданные целиком и updatedBy не знает: свежая сборка
        // из кода — тоже «обновление», её автор — сама сборка
        if (generatedAt is not null && (updatedAt is null || generatedAt > updatedAt))
            return new ArchitectureModelAuthor(generatedAt, GeneratorAuthor);
        return new ArchitectureModelAuthor(updatedAt, meta["updatedBy"]?.GetValue<string>());
    }

    private static async Task WriteAuthorAsync(string metaPath, ArchitectureModelAuthor author, CancellationToken ct)
    {
        // Чужие поля (время снимка графа от генератора) сохраняем
        var meta = await ReadMetaAsync(metaPath, ct) ?? new JsonObject();
        meta["updatedAt"] = author.UpdatedAt?.ToString("O");
        meta["updatedBy"] = author.UpdatedBy;
        await WriteAtomicAsync(metaPath, Utf8NoBom.GetBytes(meta.ToJsonString(Indented) + "\n"), ct);
    }

    private static async Task<JsonObject?> ReadMetaAsync(string metaPath, CancellationToken ct)
    {
        if (!File.Exists(metaPath)) return null;
        try
        {
            return JsonNode.Parse(await File.ReadAllTextAsync(metaPath, ct)) as JsonObject;
        }
        catch (JsonException)
        {
            // Битые метаданные не должны ронять модель — «кто и когда» просто неизвестно
            return null;
        }
    }

    private static DateTimeOffset? TryDate(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && DateTimeOffset.TryParse(s, out var d) ? d : null;

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, path, overwrite: true);
    }
}
