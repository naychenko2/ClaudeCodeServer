using System.Collections.Concurrent;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Memory;

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

    private readonly PersonaMemoryService _memory;
    private readonly PersonaManager _personas;
    private readonly Llm.ICheapTextRunner _cheap;
    private readonly IConfiguration _config;
    private readonly ILogger<PersonaMemoryConsolidationService> _log;

    // Заявки «пора консолидировать» (personaId → ownerId) — ставит autolearn при переполнении
    private readonly ConcurrentDictionary<string, string> _pending = new();

    public PersonaMemoryConsolidationService(PersonaMemoryService memory, PersonaManager personas,
        Llm.ICheapTextRunner cheap, IConfiguration config,
        ILogger<PersonaMemoryConsolidationService> log)
    {
        _memory = memory;
        _personas = personas;
        _cheap = cheap;
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
        var raw = await _cheap.RunAsync(Llm.LocalActionCatalog.PersonaMemoryConsolidate,
            BuildPrompt(entries), _config["Notes:AiModel"] ?? "haiku", ct: ct);
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

    // Парс ответа LLM: первый сбалансированный JSON-массив → операции; мусор → пусто (no-op).
    // Логика — общая MemoryConsolidationCore; здесь только маппинг типов персоны и сборка concrete-op.
    internal static List<MemoryConsolidationOp> ParseOps(string raw) =>
        MemoryConsolidationCore.ParseOps<MemoryConsolidationOp, PersonaMemoryType>(
            raw, ParseType,
            (op, ids, id, type, text, salience) => new MemoryConsolidationOp(op, ids, id, type, text, salience));

    // Маппинг строки типа из ответа LLM в PersonaMemoryType (неизвестное → null)
    private static PersonaMemoryType? ParseType(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "semantic" => PersonaMemoryType.Semantic,
        "episodic" => PersonaMemoryType.Episodic,
        "procedural" => PersonaMemoryType.Procedural,
        _ => null,
    };

    // Детерминированные гейты поверх ответа LLM (чужие id, один тип, cap 30%) — общая
    // MemoryConsolidationCore; specifics персоны — только concrete-клон merge-операции.
    internal static List<MemoryConsolidationOp> FilterOps(
        IReadOnlyList<MemoryConsolidationOp> ops, IReadOnlyList<PersonaMemoryEntry> entries) =>
        MemoryConsolidationCore.FilterOps<MemoryConsolidationOp, PersonaMemoryEntry, PersonaMemoryType>(
            ops, entries, (op, ids, type) => op with { Ids = ids, Type = type });
}
