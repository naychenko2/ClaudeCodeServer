using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Консолидация долгой памяти персон (P4, флаг persona-memory-consolidation).
// Два шага: (1) LLM-merge — one-shot предлагает схлопнуть дубли/родственные записи
// (операции merge/drop), детерминированные гейты отсекают невалидное; (2) вытеснение —
// при переполнении сверх Persona:MemoryMaxEntries хвост удаляется по retention-скорингу
// (PersonaMemoryScorer без релевантности). Запуск: тик раз в Persona:ConsolidateIntervalHours
// (дефолт 24) по всем персонам + заявка от autolearn при переполнении. Рабочий фокус
// не участвует — он не является записью памяти.
public sealed class PersonaMemoryConsolidationService : BackgroundService
{
    // Как часто проверяем заявки (сама полная проходка — раз в ConsolidateIntervalHours)
    private static readonly TimeSpan PendingTick = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LlmTimeout = TimeSpan.FromSeconds(120);
    // Гейт: за один прогон LLM-merge может затронуть не больше этой доли записей
    internal const double MaxAffectedShare = 0.30;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly PersonaMemoryService _memory;
    private readonly PersonaManager _personas;
    private readonly Llm.OneShotClaudeRunner _runner;
    private readonly IConfiguration _config;
    private readonly ILogger<PersonaMemoryConsolidationService> _log;

    // Заявки «пора консолидировать» (personaId → ownerId) — ставит autolearn при переполнении
    private readonly ConcurrentDictionary<string, string> _pending = new();

    public PersonaMemoryConsolidationService(PersonaMemoryService memory, PersonaManager personas,
        Llm.OneShotClaudeRunner runner, IConfiguration config,
        ILogger<PersonaMemoryConsolidationService> log)
    {
        _memory = memory;
        _personas = personas;
        _runner = runner;
        _config = config;
        _log = log;
    }

    private int MaxEntries => int.TryParse(_config["Persona:MemoryMaxEntries"], out var v) ? v : 150;
    private int MaxEpisodic => int.TryParse(_config["Persona:MemoryMaxEpisodic"], out var v) ? v : 40;
    private int SoftLimit => int.TryParse(_config["Persona:MemorySoftLimit"], out var v) ? v : 100;
    private TimeSpan Interval => TimeSpan.FromHours(
        double.TryParse(_config["Persona:ConsolidateIntervalHours"],
            System.Globalization.CultureInfo.InvariantCulture, out var h) && h > 0 ? h : 24);

    // Отметка «пора»: ставится autolearn'ом, когда записей стало больше MemoryMaxEntries
    public void RequestConsolidation(string ownerId, string personaId) =>
        _pending[personaId] = ownerId;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var lastFullPass = DateTime.UtcNow;   // не гоняем полную проходку сразу на старте
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(PendingTick, ct); }
            catch (OperationCanceledException) { break; }

            // Всё тело тика под catch: невыловленное исключение (за пределами
            // ConsolidateSafeAsync) молча убило бы BackgroundService до рестарта сервера
            try
            {
                // Явные заявки (переполнение после autolearn)
                foreach (var (personaId, ownerId) in _pending.ToArray())
                {
                    _pending.TryRemove(personaId, out _);
                    var persona = _personas.Get(personaId, ownerId);
                    if (persona is not null) await ConsolidateSafeAsync(persona, ct);
                }

                // Периодическая полная проходка по всем персонам
                if (DateTime.UtcNow - lastFullPass >= Interval)
                {
                    lastFullPass = DateTime.UtcNow;
                    foreach (var persona in _personas.GetAllInternal())
                        await ConsolidateSafeAsync(persona, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Тик консолидации памяти персон");
            }
        }
    }

    private async Task ConsolidateSafeAsync(Persona persona, CancellationToken ct)
    {
        try { await ConsolidateAsync(persona, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Консолидация памяти персоны {Persona}", persona.Id);
        }
    }

    private async Task ConsolidateAsync(Persona persona, CancellationToken ct)
    {
        // Гейт: включённая память персоны
        if (!persona.MemoryEnabled) return;

        var entries = _memory.List(persona.OwnerId, persona.Id, null);
        if (entries.Count <= SoftLimit) return;   // софт-порог: мало записей — не трогаем

        // Шаг 1: LLM-merge (невалидный/пустой ответ = no-op)
        var model = _runner.NormalizeModel(_config["Notes:AiModel"] ?? "haiku");
        var raw = await _runner.RunAsync(BuildPrompt(entries), model, LlmTimeout, ct);
        var ops = FilterOps(ParseOps(raw), entries);
        var merged = ops.Count > 0 ? _memory.ApplyConsolidation(persona.OwnerId, persona.Id, ops) : 0;

        // Шаг 2: вытеснение хвоста по retention-скорингу при переполнении
        // (общий потолок + под-лимит эпизодов); общая логика — PersonaMemoryScorer.SelectEvictionIds
        var after = _memory.List(persona.OwnerId, persona.Id, null);
        var evictIds = PersonaMemoryScorer.SelectEvictionIds(
            after, MaxEntries, MaxEpisodic, MemoryScoringOptions.Default, DateTime.UtcNow);
        var evicted = 0;
        if (evictIds.Count > 0)
        {
            var dropOps = evictIds
                .Select(id => new MemoryConsolidationOp("drop", null, id, null, null, null))
                .ToList();
            evicted = _memory.ApplyConsolidation(persona.OwnerId, persona.Id, dropOps);
        }

        if (merged > 0 || evicted > 0)
            _log.LogInformation(
                "Консолидация памяти {Persona}: {Before} записей, merge затронул {Merged}, вытеснено {Evicted}",
                persona.Id, entries.Count, merged, evicted);
    }

    // Промпт LLM-merge: нумерованный список записей, на выходе JSON-операции
    internal static string BuildPrompt(IReadOnlyList<PersonaMemoryEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты — куратор долгой памяти ИИ-персоны. Ниже список записей её памяти в формате " +
                      "id|type|salience|text. Найди дубли и родственные записи, которые стоит схлопнуть, " +
                      "устаревшие факты и явный мусор, которые стоит удалить.");
        sb.AppendLine("Правила:");
        sb.AppendLine("- merge допустим ТОЛЬКО между записями одного type; сводный текст краток и сохраняет суть всех источников.");
        sb.AppendLine("- Устаревание/противоречие: если несколько записей описывают один и тот же атрибут " +
                      "пользователя, но факт изменился во времени (напр. «живёт в Питере» и «переехал в Москву»), " +
                      "оставь только актуальную — либо merge в актуальную формулировку, либо drop устаревших.");
        sb.AppendLine("- Не трогай записи, в которых не уверен. Лучше меньше операций, чем потеря информации.");
        sb.AppendLine("- Ответь ТОЛЬКО JSON-массивом операций: " +
                      "[{\"op\":\"merge\",\"ids\":[\"…\",\"…\"],\"type\":\"semantic\",\"text\":\"…\",\"salience\":0.8}, " +
                      "{\"op\":\"drop\",\"id\":\"…\"}]. Если делать нечего — [].");
        sb.AppendLine();
        sb.AppendLine("Записи:");
        foreach (var e in entries)
        {
            var text = e.Text.Replace('\n', ' ');
            if (text.Length > 200) text = text[..197] + "…";
            sb.AppendLine($"{e.Id}|{e.Type.ToString().ToLowerInvariant()}|{e.Salience:0.##}|{text}");
        }
        return sb.ToString();
    }

    // Парс ответа LLM: первый сбалансированный JSON-массив → операции; мусор → пусто (no-op)
    internal static List<MemoryConsolidationOp> ParseOps(string raw)
    {
        var json = ExtractJsonArray(raw);
        if (json is null) return [];
        List<OpRaw>? parsed;
        try { parsed = JsonSerializer.Deserialize<List<OpRaw>>(json, JsonOpts); }
        catch (JsonException) { return []; }
        if (parsed is null) return [];

        var result = new List<MemoryConsolidationOp>();
        foreach (var op in parsed)
        {
            if (op?.Op is null) continue;
            PersonaMemoryType? type = op.Type?.Trim().ToLowerInvariant() switch
            {
                "semantic" => PersonaMemoryType.Semantic,
                "episodic" => PersonaMemoryType.Episodic,
                "procedural" => PersonaMemoryType.Procedural,
                _ => null,
            };
            result.Add(new MemoryConsolidationOp(op.Op.Trim(), op.Ids, op.Id, type, op.Text, op.Salience));
        }
        return result;
    }

    // Детерминированные гейты поверх ответа LLM:
    // - неизвестные id игнорируются (merge с <2 валидными источниками отбрасывается);
    // - merge только внутри одного типа (и заявленный type должен совпадать с источниками);
    // - одна запись участвует максимум в одной операции;
    // - суммарно затронуто не больше MaxAffectedShare записей за прогон.
    internal static List<MemoryConsolidationOp> FilterOps(
        IReadOnlyList<MemoryConsolidationOp> ops, IReadOnlyList<PersonaMemoryEntry> entries)
    {
        var byId = entries.ToDictionary(e => e.Id);
        var cap = (int)Math.Floor(entries.Count * MaxAffectedShare);
        var affected = new HashSet<string>();
        var result = new List<MemoryConsolidationOp>();

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
                if (op.Type is not null && op.Type != types[0]) continue;
                if (affected.Count + ids.Count > cap) continue;
                affected.UnionWith(ids);
                result.Add(op with { Ids = ids, Type = types[0] });
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

    private sealed record OpRaw(string? Op, List<string>? Ids, string? Id,
        string? Type, string? Text, double? Salience);
}
