using System.Text.Json;

namespace ClaudeHomeServer.Services.Memory;

// Общее ядро консолидации памяти: разбор ответа LLM-merge (ParseOps) и детерминированные гейты
// поверх него (FilterOps). Персона и команда отличаются только enum'ом типа и concrete-записью
// операции — эти различия задаются делегатами. Эталон — PersonaMemoryConsolidationService.
public static class MemoryConsolidationCore
{
    // Гейт: за один прогон LLM-merge может затронуть не больше этой доли записей
    internal const double MaxAffectedShare = 0.30;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Сырой формат операции из ответа LLM (общий для персоны и команды)
    private sealed record OpRaw(string? Op, List<string>? Ids, string? Id,
        string? Type, string? Text, double? Salience);

    // Парс ответа LLM: первый сбалансированный JSON-массив → операции; мусор → пусто (no-op).
    // Тип категории парсится делегатом parseType (свой switch у каждого стека), concrete-запись
    // операции собирает makeOp.
    public static List<TOp> ParseOps<TOp, TType>(string raw,
        Func<string?, TType?> parseType,
        Func<string, List<string>?, string?, TType?, string?, double?, TOp> makeOp)
        where TType : struct
    {
        var json = ExtractJsonArray(raw);
        if (json is null) return [];
        List<OpRaw>? parsed;
        try { parsed = JsonSerializer.Deserialize<List<OpRaw>>(json, JsonOpts); }
        catch (JsonException) { return []; }
        if (parsed is null) return [];

        var result = new List<TOp>();
        foreach (var op in parsed)
        {
            if (op?.Op is null) continue;
            result.Add(makeOp(op.Op.Trim(), op.Ids, op.Id, parseType(op.Type), op.Text, op.Salience));
        }
        return result;
    }

    // Детерминированные гейты поверх ответа LLM:
    // - неизвестные id игнорируются (merge с <2 валидными источниками отбрасывается);
    // - merge только внутри одного типа (и заявленный type должен совпадать с источниками);
    // - одна запись участвует максимум в одной операции;
    // - суммарно затронуто не больше MaxAffectedShare записей за прогон.
    // withMergeFields — concrete-клон merge-операции с провалидированными Ids и общим типом источников
    // (record `with`, недоступный на уровне интерфейса).
    public static List<TOp> FilterOps<TOp, TEntry, TType>(
        IReadOnlyList<TOp> ops, IReadOnlyList<TEntry> entries,
        Func<TOp, List<string>, TType, TOp> withMergeFields)
        where TOp : IMemoryConsolidationOp<TType>
        where TEntry : IMemoryEntry<TType>
        where TType : struct
    {
        var byId = entries.ToDictionary(e => e.Id);
        var cap = (int)Math.Floor(entries.Count * MaxAffectedShare);
        var affected = new HashSet<string>();
        var result = new List<TOp>();

        foreach (var op in ops)
        {
            if (op.IsMerge)
            {
                if (string.IsNullOrWhiteSpace(op.Text)) continue;
                var ids = (op.Ids ?? [])
                    .Distinct()
                    .Where(id => byId.ContainsKey(id) && !affected.Contains(id))
                    .ToList();
                if (ids.Count < 2) continue;
                // Только внутри одного типа
                var types = ids.Select(id => byId[id].Type).Distinct().ToList();
                if (types.Count != 1) continue;
                if (op.Type is not null && !EqualityComparer<TType>.Default.Equals(op.Type.Value, types[0])) continue;
                if (affected.Count + ids.Count > cap) continue;
                affected.UnionWith(ids);
                result.Add(withMergeFields(op, ids, types[0]));
            }
            else if (op.IsDrop)
            {
                if (op.Id is null || !byId.ContainsKey(op.Id) || affected.Contains(op.Id)) continue;
                if (affected.Count + 1 > cap) continue;
                affected.Add(op.Id);
                result.Add(op);
            }
        }
        return result;
    }

    // Первый сбалансированный JSON-массив из ответа модели (устойчиво к преамбуле/fence)
    private static string? ExtractJsonArray(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('[');
        if (start < 0) return null;
        int depth = 0; bool inStr = false, esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return raw[start..(i + 1)];
        }
        return null;
    }
}
