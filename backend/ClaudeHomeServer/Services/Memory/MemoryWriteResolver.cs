using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Memory;

// Операция разрешения записи памяти (Mem0 ADD/UPDATE/DELETE/NOOP):
//  - Add    — независимый факт, добавить рядом;
//  - Update — новый дополняет/уточняет существующий → заменить текст target на MergedText;
//  - Delete — новый делает существующий устаревшим/неверным → удалить target, добавить новый;
//  - Noop   — дубль или незначимое, не добавлять ничего.
public enum MemoryWriteOp { Add, Update, Delete, Noop }

// Решение резолвера. TargetId/MergedText значимы только для Update/Delete.
public sealed record MemoryWriteDecision(MemoryWriteOp Op, string? TargetId, string? MergedText)
{
    // Дефолт/фолбэк — просто добавить новый факт (консервативно, ничего не трогаем)
    public static readonly MemoryWriteDecision Add = new(MemoryWriteOp.Add, null, null);
}

// Близкий к новому факту кандидат из зоны конфликта [ConflictThreshold, DedupThreshold): id + текст
public readonly record struct MemoryWriteCandidate(string Id, string Text);

// Общий LLM-резолвер записи памяти для авто-пути (autolearn персоны и команды). Разрешает
// ПРОТИВОРЕЧИЯ: когда новый факт не дубль (иначе — reinforcement на стороне фасада), но близок к
// существующим записям, модель решает, дополнить (UPDATE), вытеснить устаревшее (DELETE), добавить
// рядом (ADD) или отбросить как незначимое (NOOP). Только LLM-вызов и парс решения — применение
// (у фасадов есть доступ к стору) остаётся в PersonaMemoryService/TeamMemoryService.
//
// Гейт: Enabled (конфиг Memory:ConflictResolution, дефолт true) + Available (Dify) проверяет фасад.
// Любая ошибка/мусор в ответе → консервативный фолбэк на ADD (autolearn не должен падать).
public sealed class MemoryWriteResolver(
    ICheapTextRunner cheap, IConfiguration config, ILogger<MemoryWriteResolver> log)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Разрешение противоречий включено (Memory:ConflictResolution, дефолт true)
    public bool Enabled { get; } =
        !bool.TryParse(config["Memory:ConflictResolution"], out var v) || v;

    // Резолвит операцию записи нового факта относительно близких кандидатов того же скоупа.
    // Пустой список кандидатов → ADD без вызова LLM. Ошибка/таймаут/мусор → ADD (фолбэк).
    public async Task<MemoryWriteDecision> ResolveAsync(string newText, string typeLabel,
        IReadOnlyList<MemoryWriteCandidate> candidates, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (candidates.Count == 0 || string.IsNullOrWhiteSpace(newText)) return MemoryWriteDecision.Add;
        try
        {
            // timeout игнорируется для локали (профиль каталога задаёт таймаут); для claude-пути — дефолт раннера
            var raw = await cheap.RunAsync(
                LocalActionCatalog.MemoryWriteResolve,
                BuildPrompt(newText, typeLabel, candidates),
                config["Notes:AiModel"] ?? config["Tasks:AiModel"] ?? "haiku", ct: ct);
            return ParseDecision(raw);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "memory-write-resolver: фолбэк на ADD");
            return MemoryWriteDecision.Add;
        }
    }

    // Консервативный промпт: UPDATE/DELETE — только при явном дополнении/противоречии; иначе ADD.
    private static string BuildPrompt(string newText, string typeLabel, IReadOnlyList<MemoryWriteCandidate> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты — куратор долгой памяти. Нужно решить, как записать НОВЫЙ факт относительно уже " +
                      "существующих ПОХОЖИХ записей (они близки по смыслу, но не точные дубли).");
        sb.AppendLine($"Новый факт (тип «{typeLabel}»): {newText.Replace('\n', ' ')}");
        sb.AppendLine("Существующие похожие записи (id | текст):");
        foreach (var c in candidates)
        {
            var text = c.Text.Replace('\n', ' ');
            if (text.Length > 200) text = text[..197] + "…";
            sb.AppendLine($"{c.Id} | {text}");
        }
        sb.AppendLine();
        sb.AppendLine("Выбери ОДНУ операцию:");
        sb.AppendLine("- ADD — новый факт независим от существующих, добавить отдельно.");
        sb.AppendLine("- UPDATE — новый ДОПОЛНЯЕТ/УТОЧНЯЕТ одну из записей: дай targetId и mergedText " +
                      "(полная объединённая формулировка, заменит текст той записи).");
        sb.AppendLine("- DELETE — новый ПРОТИВОРЕЧИТ одной из записей и делает её устаревшей/неверной: " +
                      "дай targetId (эта запись будет удалена, новый факт добавлен вместо неё).");
        sb.AppendLine("- NOOP — новый факт дубль существующего или незначим, ничего не добавлять.");
        sb.AppendLine("КОНСЕРВАТИВНО: применяй UPDATE/DELETE только при ЯВНОМ дополнении или противоречии. " +
                      "При любом сомнении — ADD.");
        sb.AppendLine("Ответь ТОЛЬКО JSON-объектом: {\"op\":\"ADD\"} | " +
                      "{\"op\":\"UPDATE\",\"targetId\":\"…\",\"mergedText\":\"…\"} | " +
                      "{\"op\":\"DELETE\",\"targetId\":\"…\"} | {\"op\":\"NOOP\"}.");
        return sb.ToString();
    }

    // Парс решения из ответа модели (устойчиво к преамбуле/fence). Мусор, неизвестная операция,
    // UPDATE без targetId/mergedText, DELETE без targetId → консервативный фолбэк на ADD.
    internal static MemoryWriteDecision ParseDecision(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return MemoryWriteDecision.Add;
        var json = MemoryLlmParsing.ExtractBalanced(raw, '{', '}');
        if (json is null) return MemoryWriteDecision.Add;

        DecisionRaw? d;
        try { d = JsonSerializer.Deserialize<DecisionRaw>(json, JsonOpts); }
        catch (JsonException) { return MemoryWriteDecision.Add; }
        if (d?.Op is null) return MemoryWriteDecision.Add;

        switch (d.Op.Trim().ToLowerInvariant())
        {
            case "update":
                var targetId = d.TargetId?.Trim();
                var merged = d.MergedText?.Trim();
                if (string.IsNullOrEmpty(targetId) || string.IsNullOrWhiteSpace(merged))
                    return MemoryWriteDecision.Add;   // нечем/некуда дополнять — добавляем как есть
                return new MemoryWriteDecision(MemoryWriteOp.Update, targetId, merged);
            case "delete":
                var delId = d.TargetId?.Trim();
                if (string.IsNullOrEmpty(delId)) return MemoryWriteDecision.Add;   // некого удалять
                return new MemoryWriteDecision(MemoryWriteOp.Delete, delId, null);
            case "noop":
                return new MemoryWriteDecision(MemoryWriteOp.Noop, null, null);
            default:
                return MemoryWriteDecision.Add;
        }
    }

    private sealed record DecisionRaw(string? Op, string? TargetId, string? MergedText);
}
