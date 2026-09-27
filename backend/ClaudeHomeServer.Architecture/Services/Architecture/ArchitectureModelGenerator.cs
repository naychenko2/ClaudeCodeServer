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
    int ConnectionsAdded,
    // Проход 1 «Собрать архитектуру»: внешние системы-кандидаты L1 и «нет в коде»
    int Candidates = 0,
    int MarkedMissing = 0,
    int Unmarked = 0,
    int SkippedDeleted = 0,
    IReadOnlyList<string>? Missing = null,
    // Кандидаты ниже порога уверенности или сверх потолка — только строка в сводке
    IReadOnlyList<string>? CandidatesSkipped = null,
    // Проход 2 (withAgent): задача агенту, его персона (null — без персоны) и код отказа
    string? AgentTaskId = null,
    string? AgentPersonaId = null,
    string? AgentError = null);

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
        var externals = ArchitectureExternalScanner.Scan(root, ct);
        var generated = ArchitectureModelBuilder.Build(
            new ArchitectureInput(systemName, units, snapshot, titles, externals.Accepted));

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

            // Метаданные дописываются, а не переписываются: updatedAt/updatedBy редактора и
            // карта происхождения elements живут там же (раньше генератор их терял)
            var meta = await ArchitectureModelStore.ReadMetaAsync(metaPath, ct) ?? new JsonObject();
            var generatedAt = DateTimeOffset.UtcNow;
            var merged = ArchitectureModelMerger.Merge(existing, generated,
                meta["elements"] as JsonObject, generatedAt);
            var graphBuiltAt = snapshot.BuiltAt?.ToString("O");

            await WriteAtomicAsync(modelPath, merged.Document.ToJsonString(Indented), ct);
            meta["generator"] = "ccs-code-v1";
            meta["graphBuiltAt"] = graphBuiltAt;
            meta["generatedAt"] = generatedAt.ToString("O");
            meta["containers"] = generated.Containers.Count;
            meta["components"] = generated.Components.Count;
            meta["elements"] = merged.Elements;
            await WriteAtomicAsync(metaPath, meta.ToJsonString(Indented), ct);

            logger.LogInformation(
                "Архитектура собрана из кода: {Root}, контейнеров {Containers}, компонентов {Components}, " +
                "добавлено {Added}, сохранено {Matched}, новых связей {Connections}, кандидатов L1 {Candidates}, " +
                "нет в коде {Missing}, вернулось {Unmarked}, не воскрешено удалённых {SkippedDeleted}",
                root, generated.Containers.Count, generated.Components.Count,
                merged.Added, merged.Matched, merged.ConnectionsAdded, merged.Candidates,
                merged.Missing?.Count ?? 0, merged.Unmarked, merged.SkippedDeleted);

            return new ArchitectureGenerateResult(ModelRelPath, graphBuiltAt, generatedAt,
                generated.Containers.Count, generated.Components.Count,
                merged.Added, merged.Matched, merged.ConnectionsAdded,
                merged.Candidates, merged.MarkedMissing, merged.Unmarked, merged.SkippedDeleted,
                merged.Missing ?? [],
                SkippedSummary(externals.Rejected));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Потолок строк сводки отсеянных кандидатов — хвост сворачивается в «и ещё N».</summary>
    public const int MaxSkippedShown = 20;

    // Сводка отсеянных кандидатов: в большом проекте их сотни, а ответ читает человек и модель
    internal static List<string> SkippedSummary(IReadOnlyList<ExternalCandidate> rejected)
    {
        var shown = rejected.Take(MaxSkippedShown).Select(c => $"{c.Name} ({c.Source})").ToList();
        if (rejected.Count > MaxSkippedShown) shown.Add($"и ещё {rejected.Count - MaxSkippedShown}");
        return shown;
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

    internal static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, content + "\n", ct);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Сорвалась запись или подмена — недописанный .tmp в проекте не оставляем
            // (иначе он висит в дереве, синке знаний и git status).
            try { File.Delete(tmp); } catch { /* не удалилось — пробрасываем исходную ошибку */ }
            throw;
        }
    }
}
