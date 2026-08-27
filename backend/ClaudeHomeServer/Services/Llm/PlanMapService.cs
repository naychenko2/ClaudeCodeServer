using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

// Карта этого текста плана уже собирается (повторный клик) → 409 у контроллера
public sealed class PlanMapInProgressException() : Exception("Карта этого плана уже собирается");

// Запись кэша карт: карта + момент сборки (по нему выпадают старые при переполнении потолка)
public sealed class PlanMapCacheEntry
{
    public PlanMap Map { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Карта плана (план «Контекстные замечания к плану + визуальный разворот», часть B):
// структурный слепок markdown-плана, по которому фронт рисует разворот схемой. One-shot
// место plan-map по кнопке «Собрать схему» — тот же паттерн дешёвого вызова, что у
// ChatDigestService: кэш per-owner по SHA-256 текста плана (слот моделей у каждого
// владельца свой — чужую карту не показываем), очередь один поток на владельца, защита
// от повторного клика.
//
// Любой сбой → null, не исключение: карта — улучшение, а не условие работы (замечания на
// разделах работают и без неё), фронт молча остаётся на тексте. Две валидации после
// разбора: потолок блоков с флагами внимания (иначе первый экран вырождается в
// оглавление) и отбрасывание блоков с якорем, которого нет среди заголовков плана
// (иначе провал в детали ведёт в пустоту).
public class PlanMapService
{
    public const string CacheFileName = "plan-maps.json";

    // Потолок блоков с непустыми Flags: первый экран разворота отвечает «что мешает
    // согласовать», а не пересказывает весь план
    public const int MaxFlaggedBlocks = 5;

    // Потолок кэша на владельца: карта восстановима одним вызовом по кнопке — хранить
    // сотни старых версий незачем, при переполнении выпадают самые старые
    private const int CacheEntriesPerOwner = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Модель может отдать ключи в любом регистре — промпт просит camelCase
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICheapTextRunner _cheap;
    private readonly ILogger<PlanMapService> _logger;
    private readonly string _filePath;
    private readonly Dictionary<string, Dictionary<string, PlanMapCacheEntry>> _cache;
    private readonly object _lock = new();

    // Карты в сборке — защита от параллельных кликов по образцу ChatDigestService._inFlight:
    // второй клик получает 409, а не вторую оплату модели. Ключ «владелец:хеш» — один и тот
    // же план у двух владельцев собирается независимо
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    // Очередь «1 поток на владельца»: карты одного владельца идут строго по одной
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ownerGates = new();

    public PlanMapService(IConfiguration config, ICheapTextRunner cheap,
        ILogger<PlanMapService> logger)
    {
        _cheap = cheap;
        _logger = logger;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _filePath = Path.Combine(dataDir, CacheFileName);
        _cache = JsonFileStore.Load<Dictionary<string, Dictionary<string, PlanMapCacheEntry>>>(
            _filePath, JsonOptions) ?? [];
    }

    public async Task<PlanMap?> BuildMapAsync(string ownerId, string planText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(planText)) return null;
        var key = HashOf(planText);

        if (FromCache(ownerId, key) is { } cached) return cached;

        if (!_inFlight.TryAdd(InFlightKey(ownerId, key), 0))
            throw new PlanMapInProgressException();

        var gate = _ownerGates.GetOrAdd(ownerId, _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(ct);
            try
            {
                // Пока стояли в очереди, карту могли собрать (клик-двойка по кнопке):
                // отдаём кэш, модель не зовём
                if (FromCache(ownerId, key) is { } built) return built;

                string raw;
                try
                {
                    raw = await _cheap.RunAsync(LocalActionCatalog.PlanMap, BuildPrompt(planText),
                        ownerId: ownerId, jsonFormat: "json", ct: ct);
                }
                // Отмена — не сбой модели, осознанный обрыв: наверх
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Карта плана {Hash}: вызов модели не удался — фронт остаётся на тексте", key[..12]);
                    return null;
                }
                if (string.IsNullOrWhiteSpace(raw)) return null;

                var map = ParseAndValidate(raw, planText);
                if (map is null)
                {
                    _logger.LogInformation("Карта плана {Hash}: ответ модели не разобран", key[..12]);
                    return null;
                }
                Store(ownerId, key, map);
                _logger.LogInformation("Карта плана {Hash} собрана ({Blocks} блоков)", key[..12], map.Blocks.Count);
                return map;
            }
            finally { gate.Release(); }
        }
        finally
        {
            _inFlight.TryRemove(InFlightKey(ownerId, key), out _);
        }
    }

    private static string InFlightKey(string ownerId, string hash) => $"{ownerId}:{hash}";

    private PlanMap? FromCache(string ownerId, string key)
    {
        lock (_lock)
            return _cache.TryGetValue(ownerId, out var bag) && bag.TryGetValue(key, out var hit)
                ? hit.Map : null;
    }

    private void Store(string ownerId, string key, PlanMap map)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(ownerId, out var bag))
                _cache[ownerId] = bag = [];
            bag[key] = new PlanMapCacheEntry { Map = map, CreatedAt = DateTime.UtcNow };
            foreach (var oldest in bag.OrderBy(e => e.Value.CreatedAt)
                         .Take(Math.Max(0, bag.Count - CacheEntriesPerOwner))
                         .Select(e => e.Key).ToList())
                bag.Remove(oldest);
            JsonFileStore.Save(_filePath, _cache, JsonOptions);
        }
    }

    // Разбор и две обязательные валидации. Любой сбой → null (см. шапку класса).
    internal static PlanMap? ParseAndValidate(string raw, string planText)
    {
        var json = ExtractJsonObject(raw);
        if (json is null) return null;
        PlanMap map;
        try { map = JsonSerializer.Deserialize<PlanMap>(json, JsonOptions) ?? new PlanMap(); }
        catch (JsonException) { return null; }

        map.Genre = PlanMapValues.Genres.Contains(map.Genre) ? map.Genre : "feature";
        map.OneLine = (map.OneLine ?? "").Trim();
        map.Numbers = (map.Numbers ?? [])
            .Where(n => n is not null && !string.IsNullOrWhiteSpace(n.Value) && !string.IsNullOrWhiteSpace(n.Label))
            .Select(n => new PlanMapNumber { Value = n.Value.Trim(), Label = n.Label.Trim() })
            .ToList();

        // Валидация 2: якорь обязан быть заголовком плана — блок с битым якорем ведёт
        // в пустоту, отбрасываем целиком
        var headings = ExtractHeadings(planText);
        var kept = new List<PlanMapBlock>();
        var index = 0;
        foreach (var block in map.Blocks ?? [])
        {
            if (block is null) continue;
            var anchor = (block.Anchor ?? "").Trim();
            if (anchor.Length == 0 || !headings.Contains(anchor)) continue;
            kept.Add(new PlanMapBlock
            {
                Id = string.IsNullOrWhiteSpace(block.Id) ? $"b{++index}" : block.Id.Trim(),
                Title = (block.Title ?? "").Trim(),
                Type = PlanMapValues.BlockTypes.Contains(block.Type) ? block.Type : "step",
                Flags = (block.Flags ?? []).Where(PlanMapValues.BlockFlags.Contains).Distinct().ToList(),
                Anchor = anchor,
                DependsOn = (block.DependsOn ?? [])
                    .Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).ToList(),
            });
        }
        // Карта без блоков бесполезна — фронт остаётся на тексте
        if (kept.Count == 0) return null;

        // DependsOn чиним по факту: ссылка на отброшенный блок не должна висеть мёртвой
        var liveIds = kept.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var block in kept)
            block.DependsOn.RemoveAll(id => !liveIds.Contains(id));

        // Валидация 1: не более MaxFlaggedBlocks блоков с флагами — лишние флаги
        // снимаются, блоки остаются (порядок массива = порядок модели, самые важные
        // у неё первые)
        var flagged = 0;
        foreach (var block in kept)
        {
            if (block.Flags.Count == 0) continue;
            flagged++;
            if (flagged > MaxFlaggedBlocks) block.Flags = [];
        }

        map.Blocks = kept;
        return map;
    }

    // Заголовки markdown-плана: текст строк, начинающихся с решёток, вне кодовых заборов.
    // Сравнение точное (Ordinal) — якорь это адрес прыжка в раздел, «примерно совпало»
    // здесь означало бы прыжок в никуда
    internal static HashSet<string> ExtractHeadings(string markdown)
    {
        var headings = new HashSet<string>(StringComparer.Ordinal);
        var inFence = false;
        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence || !trimmed.StartsWith('#')) continue;
            var text = trimmed.TrimStart('#').Trim();
            if (text.Length > 0) headings.Add(text);
        }
        return headings;
    }

    private static string HashOf(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // Ответ модели может приехать в ```-заборе или с болтовнёй вокруг: берём объект от
    // первой { до парной ей } (приём ProjectDoodleTile.ExtractJsonObject)
    private static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        if (start < 0) return null;
        int depth = 0;
        bool inStr = false, esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return raw[start..(i + 1)];
        }
        return null;
    }

    internal static string BuildPrompt(string planText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ниже — план в markdown. Построй по нему структурную карту — один JSON-объект, без пояснений вокруг.");
        sb.AppendLine();
        sb.AppendLine("""
Формат (ключи и значения — только как в схеме):
{
  "genre": "feature | fix | choice | audit | framework | operation",
  "oneLine": "суть плана одной фразой",
  "numbers": [ { "value": "3", "label": "шага" } ],
  "blocks": [
    { "id": "b1", "title": "название блока одной строкой",
      "type": "step | decision | fork | risk | criterion | boundary",
      "flags": ["blocking", "needs-decision"],
      "anchor": "точный текст заголовка раздела плана",
      "dependsOn": [] }
  ]
}
""");
        sb.AppendLine("Правила:");
        sb.AppendLine("- genre: feature — новая функциональность, fix — починка, choice — выбор из вариантов, audit — разбор/проверка, framework — каркас/основание, operation — регламент/обслуживание.");
        sb.AppendLine("- numbers: 2–4 пары о масштабе плана (шаги, волны, затрагиваемые файлы, сроки).");
        sb.AppendLine("- blocks: значимые разделы плана; anchor — ТОЧНЫЙ текст заголовка раздела как в markdown, без решёток и без перефразирования — блок с неточным якорем отбрасывается.");
        sb.AppendLine("- flags: только то, что требует внимания человека — blocking (блокирует согласование), needs-decision (нужно решение), expands-scope (раздувает объём), has-cost (деньги/ресурсы), review-fix (правка по итогам ревью). Флаги не более чем у 5 блоков, у остальных пустой список.");
        sb.AppendLine("- dependsOn: id блоков, которые должны закрыться раньше этого.");
        sb.AppendLine("- Не выдумывай разделы, которых нет в плане.");
        sb.AppendLine();
        sb.AppendLine("План:");
        sb.AppendLine(planText);
        return sb.ToString();
    }
}
