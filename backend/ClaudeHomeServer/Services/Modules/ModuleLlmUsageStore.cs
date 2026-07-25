using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.Modules;

/// <summary>
/// Учёт вызовов LLM-канала модулей (контракт §10.5, ТЗ R13) в разрезе (moduleId, action, sub).
/// Пишется ФАКТ каждого вызова (включая неудачные) с маршрутом; токены и стоимость — только
/// когда их отдал провайдер (локаль и прямой адаптер метрик не дают — там поля пустые).
/// Формат — JSONL (data/module-llm-usage.jsonl): учёт только растёт, дописывание строки
/// дешевле перезаписи всего JSON-стора. Тело промпта сюда не попадает (§10.5, приватность).
/// </summary>
public sealed class ModuleLlmUsageStore
{
    public sealed record Entry(
        [property: JsonPropertyName("at")] DateTime At,
        [property: JsonPropertyName("moduleId")] string ModuleId,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("sub")] string Sub,
        [property: JsonPropertyName("route")] string Route,
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("durationMs")] long DurationMs,
        [property: JsonPropertyName("model")] string? Model = null,
        [property: JsonPropertyName("inputTokens")] long? InputTokens = null,
        [property: JsonPropertyName("outputTokens")] long? OutputTokens = null,
        [property: JsonPropertyName("costUsd")] double? CostUsd = null);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _storePath;
    private readonly ILogger<ModuleLlmUsageStore> _log;
    private readonly Lock _writeLock = new();

    public ModuleLlmUsageStore(IConfiguration config, ILogger<ModuleLlmUsageStore> log)
    {
        _log = log;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _storePath = Path.Combine(dataDir, "module-llm-usage.jsonl");
    }

    /// <summary>Дописать факт вызова. Учёт не должен ронять сам вызов — сбой только в лог.</summary>
    public void Record(Entry entry)
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
            _log.LogError(ex, "Не удалось записать учёт LLM-вызова модуля в {Path}", _storePath);
        }
    }
}
