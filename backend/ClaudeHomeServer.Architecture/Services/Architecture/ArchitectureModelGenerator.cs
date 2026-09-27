using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Architecture;

/// <summary>Итог «Собрать из кода».</summary>
public sealed record ArchitectureGenerateResult(
    string ModelPath,
    string? GraphBuiltAt,
    DateTimeOffset GeneratedAt,
    int Containers,
    int Components,
    int Added,
    int Matched,
    int ConnectionsAdded);

/// <summary>Файл модели в проекте не читается как JSON — перезаписывать его генератор отказывается.</summary>
public sealed class ArchitectureModelCorruptException(string path, Exception inner)
    : Exception($"Файл модели архитектуры повреждён: {path}", inner);

/// <summary>
/// «Собрать из кода»: сканирует дерево проекта, строит стартовую модель из снимка графа кода,
/// сливает её с уже лежащим файлом Viaduct и пишет результат обратно. Рядом кладётся
/// файл метаданных с временем снимка графа — им помечается карта (файл Viaduct хранит
/// только persist-обёртку, чужие поля редактор при сохранении не сохранит).
/// Пути — только через <see cref="SafePath.Join"/>.
/// </summary>
public sealed class ArchitectureModelGenerator(ILogger<ArchitectureModelGenerator> logger)
{
    /// <summary>Файл модели в проекте (под git, общий с хранилищем раздела «Архитектура»).</summary>
    public const string ModelRelPath = "docs/architecture/model.viaduct.json";

    /// <summary>Метаданные последней сборки из кода (время снимка графа и пр.).</summary>
    public const string MetaRelPath = "docs/architecture/model.viaduct.meta.json";

    // Кириллица — как есть, без \uXXXX: файл модели лежит под git, дифф должен читаться
    // (так же пишут ArchitectureModelStore и ArchitectureModelEditor).
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Одна запись за раз: две кнопки подряд и сохранение из редактора
    // (ArchitectureModelStore) не должны писать файл вперемешку.
    private readonly SemaphoreSlim _gate = ArchitectureFileGate.Instance;

    public async Task<ArchitectureGenerateResult> GenerateAsync(
        string root, string systemName, CodeSnapshotInput snapshot, CancellationToken ct)
    {
        var modelPath = SafePath.Join(root, ModelRelPath);
        var metaPath = SafePath.Join(root, MetaRelPath);

        var units = ArchitectureSourceScanner.ScanUnits(root, ct);
        var titles = ArchitectureSourceScanner.ReadSubsystemTitles(snapshot, rel => TryRead(root, rel));
        var generated = ArchitectureModelBuilder.Build(new ArchitectureInput(systemName, units, snapshot, titles));

        await _gate.WaitAsync(ct);
        try
        {
            JsonNode? existing = null;
            if (File.Exists(modelPath))
            {
                var text = await File.ReadAllTextAsync(modelPath, ct);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try { existing = JsonNode.Parse(text); }
                    catch (JsonException ex) { throw new ArchitectureModelCorruptException(ModelRelPath, ex); }
                }
            }

            var merged = ArchitectureModelMerger.Merge(existing, generated);
            var generatedAt = DateTimeOffset.UtcNow;
            var graphBuiltAt = snapshot.BuiltAt?.ToString("O");

            await WriteAtomicAsync(modelPath, merged.Document.ToJsonString(Indented), ct);
            var meta = new JsonObject
            {
                ["generator"] = "ccs-code-v1",
                ["graphBuiltAt"] = graphBuiltAt,
                ["generatedAt"] = generatedAt.ToString("O"),
                ["containers"] = generated.Containers.Count,
                ["components"] = generated.Components.Count,
            };
            await WriteAtomicAsync(metaPath, meta.ToJsonString(Indented), ct);

            logger.LogInformation(
                "Архитектура собрана из кода: {Root}, контейнеров {Containers}, компонентов {Components}, " +
                "добавлено {Added}, сохранено {Matched}, новых связей {Connections}",
                root, generated.Containers.Count, generated.Components.Count,
                merged.Added, merged.Matched, merged.ConnectionsAdded);

            return new ArchitectureGenerateResult(ModelRelPath, graphBuiltAt, generatedAt,
                generated.Containers.Count, generated.Components.Count,
                merged.Added, merged.Matched, merged.ConnectionsAdded);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? TryRead(string root, string relPath)
    {
        try
        {
            var path = SafePath.Join(root, ArchitecturePathFolding.Normalize(relPath));
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content + "\n", ct);
        File.Move(tmp, path, overwrite: true);
    }
}
