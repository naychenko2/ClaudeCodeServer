using System.Collections.Concurrent;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Memory;

namespace ClaudeHomeServer.Services;

// Консолидация памяти команды проекта (③-3.4, эталон PersonaMemoryConsolidationService).
// Два шага: (1) LLM-merge — one-shot предлагает схлопнуть дубли/родственные записи одного типа
// (операции merge/drop), детерминированные гейты отсекают невалидное; (2) вытеснение — при
// переполнении сверх TeamMemory:MaxEntries хвост удаляется по retention-скорингу (TeamMemoryScorer
// без релевантности). Запуск: тик раз в TeamMemory:ConsolidateIntervalHours (дефолт 24) по всем
// проектам с памятью + заявка от autolearn при переполнении.
public sealed class TeamMemoryConsolidationService : BackgroundService
{
    // Как часто проверяем заявки (сама полная проходка — раз в ConsolidateIntervalHours)
    private static readonly TimeSpan PendingTick = TimeSpan.FromMinutes(5);

    private readonly TeamMemoryService _memory;
    private readonly Llm.ICheapTextRunner _cheap;
    private readonly IConfiguration _config;
    private readonly ILogger<TeamMemoryConsolidationService> _log;

    // Заявки «пора консолидировать» ((owner, project) → 0) — ставит autolearn при переполнении
    private readonly ConcurrentDictionary<(string Owner, string Project), byte> _pending = new();

    public TeamMemoryConsolidationService(TeamMemoryService memory, Llm.ICheapTextRunner cheap,
        IConfiguration config, ILogger<TeamMemoryConsolidationService> log)
    {
        _memory = memory;
        _cheap = cheap;
        _config = config;
        _log = log;
    }

    private int MaxEntries => int.TryParse(_config["TeamMemory:MaxEntries"], out var v) && v > 0 ? v : 200;
    private int SoftLimit => int.TryParse(_config["TeamMemory:SoftLimit"], out var v) && v > 0 ? v : 150;
    private TimeSpan Interval => TimeSpan.FromHours(
        double.TryParse(_config["TeamMemory:ConsolidateIntervalHours"],
            System.Globalization.CultureInfo.InvariantCulture, out var h) && h > 0 ? h : 24);

    // Отметка «пора»: ставится autolearn'ом, когда записей стало больше SoftLimit
    public void RequestConsolidation(string ownerId, string projectId) =>
        _pending[(ownerId, projectId)] = 0;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var lastFullPass = DateTime.UtcNow;   // не гоняем полную проходку сразу на старте
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(PendingTick, ct); }
            catch (OperationCanceledException) { break; }

            // Всё тело тика под catch: невыловленное исключение молча убило бы BackgroundService
            try
            {
                // Явные заявки (переполнение после autolearn)
                foreach (var (scope, _) in _pending.ToArray())
                {
                    _pending.TryRemove(scope, out _);
                    await ConsolidateSafeAsync(scope.Owner, scope.Project, ct);
                }

                // Периодическая полная проходка по всем проектам с памятью
                if (DateTime.UtcNow - lastFullPass >= Interval)
                {
                    lastFullPass = DateTime.UtcNow;
                    foreach (var (owner, project) in _memory.AllScopes())
                        await ConsolidateSafeAsync(owner, project, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Тик консолидации памяти команд");
            }
        }
    }

    private async Task ConsolidateSafeAsync(string ownerId, string projectId, CancellationToken ct)
    {
        try { await ConsolidateAsync(ownerId, projectId, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Консолидация памяти команды проекта {Project}", projectId);
        }
    }

    private async Task ConsolidateAsync(string ownerId, string projectId, CancellationToken ct)
    {
        var entries = _memory.List(ownerId, projectId);
        if (entries.Count <= SoftLimit) return;   // софт-порог: мало записей — не трогаем

        // Шаг 1: LLM-merge (невалидный/пустой ответ = no-op)
        var raw = await _cheap.RunAsync(Llm.LocalActionCatalog.TeamMemoryConsolidate,
            BuildPrompt(entries), _config["Notes:AiModel"] ?? "haiku", ct: ct);
        var ops = FilterOps(ParseOps(raw), entries);
        var merged = ops.Count > 0 ? _memory.ApplyConsolidation(ownerId, projectId, ops) : 0;

        // Шаг 2: вытеснение хвоста по retention-скорингу при переполнении сверх MaxEntries
        var after = _memory.List(ownerId, projectId);
        var evictIds = TeamMemoryScorer.SelectEvictionIds(after, MaxEntries, MemoryScoringOptions.Default, DateTime.UtcNow);
        var evicted = 0;
        if (evictIds.Count > 0)
        {
            var dropOps = evictIds
                .Select(id => new TeamMemoryConsolidationOp("drop", null, id, null, null, null))
                .ToList();
            evicted = _memory.ApplyConsolidation(ownerId, projectId, dropOps);
        }

        if (merged > 0 || evicted > 0)
            _log.LogInformation(
                "Консолидация памяти команды {Project}: {Before} записей, merge затронул {Merged}, вытеснено {Evicted}",
                projectId, entries.Count, merged, evicted);
    }

    // Промпт LLM-merge: список записей id|type|salience|text, на выходе JSON-операции
    internal static string BuildPrompt(IReadOnlyList<TeamMemoryEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты — куратор общей памяти команды проекта. Ниже список записей в формате " +
                      "id|type|salience|text. Найди дубли и родственные записи одного типа, которые стоит " +
                      "схлопнуть, а также устаревшие/противоречивые факты и явный мусор, которые стоит удалить.");
        sb.AppendLine("Правила:");
        sb.AppendLine("- merge допустим ТОЛЬКО между записями одного type (decision/convention/fact/glossary); " +
                      "сводный текст краток и сохраняет суть всех источников.");
        sb.AppendLine("- Устаревание/противоречие: если несколько записей описывают один и тот же аспект проекта, " +
                      "но он изменился (напр. сменился адрес прода/выбор технологии), оставь только актуальную — " +
                      "либо merge в актуальную формулировку, либо drop устаревших.");
        sb.AppendLine("- Не трогай записи, в которых не уверен. Лучше меньше операций, чем потеря информации.");
        sb.AppendLine("- Ответь ТОЛЬКО JSON-массивом операций: " +
                      "[{\"op\":\"merge\",\"ids\":[\"…\",\"…\"],\"type\":\"decision\",\"text\":\"…\",\"salience\":0.8}, " +
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
    // Логика — общая MemoryConsolidationCore; здесь только маппинг типов команды и сборка concrete-op.
    internal static List<TeamMemoryConsolidationOp> ParseOps(string raw) =>
        MemoryConsolidationCore.ParseOps<TeamMemoryConsolidationOp, TeamMemoryType>(
            raw, ParseType,
            (op, ids, id, type, text, salience) => new TeamMemoryConsolidationOp(op, ids, id, type, text, salience));

    // Маппинг строки типа из ответа LLM в TeamMemoryType (неизвестное → null)
    private static TeamMemoryType? ParseType(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "decision" => TeamMemoryType.Decision,
        "convention" => TeamMemoryType.Convention,
        "fact" => TeamMemoryType.Fact,
        "glossary" => TeamMemoryType.Glossary,
        _ => null,
    };

    // Детерминированные гейты поверх ответа LLM (чужие id, один тип, cap 30%) — общая
    // MemoryConsolidationCore; specifics команды — только concrete-клон merge-операции.
    internal static List<TeamMemoryConsolidationOp> FilterOps(
        IReadOnlyList<TeamMemoryConsolidationOp> ops, IReadOnlyList<TeamMemoryEntry> entries) =>
        MemoryConsolidationCore.FilterOps<TeamMemoryConsolidationOp, TeamMemoryEntry, TeamMemoryType>(
            ops, entries, (op, ids, type) => op with { Ids = ids, Type = type });
}
