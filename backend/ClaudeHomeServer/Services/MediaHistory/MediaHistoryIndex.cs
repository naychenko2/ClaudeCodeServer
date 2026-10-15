using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.MediaHistory;

// Индекс истории медиа-генераций (ADR-030 §7): 7 файлов дня читаются при старте, новые события
// дописывает писатель (ComfyRunHistory) через IMediaHistoryEventSink. Записи — по prompt_id,
// события (enqueued/started/finished) склеиваются в одну. Фильтры и пагинация — в памяти.
public sealed class MediaHistoryIndex : IMediaHistoryEventSink, IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, MediaHistoryRecord> _records = new(StringComparer.Ordinal);
    private readonly string _dir;
    private readonly UserStore _users;
    private readonly TimeProvider _time;
    private bool _loaded;

    // Web-конвенции: camelCase, как в ComfyRunHistory (строки журнала)
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    public MediaHistoryIndex(IConfiguration config, UserStore users, TimeProvider? time = null)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dir = Path.Combine(Path.GetDirectoryName(dataPath)!, "local-media-history");
        _users = users;
        _time = time ?? TimeProvider.System;
    }

    // Каталог журнала (data/local-media-history) — для миниатюр и ротации
    public string Dir => _dir;

    // Загрузка 7 файлов дня; идемпотентна. Битые строки и незнакомая версия пропускаются
    public void Load()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            if (!Directory.Exists(_dir)) return;
            var cutoff = _time.GetUtcNow().Date.AddDays(-7);
            foreach (var file in Directory.GetFiles(_dir, "*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!DateTime.TryParse(name, out var day) || day < cutoff) continue;
                foreach (var line in File.ReadLines(file))
                    ParseLine(line);
            }
        }
    }

    // Событие от писателя: дописать в запись по prompt_id
    public void Append(MediaHistoryEvent e)
    {
        lock (_lock)
            Apply(e.PromptId, e.Type, e.Ts, e.Origin, e.Model, e.Prompt, e.Params, e.Outcome, e.RunSeconds, e.Error);
    }

    // Запись по prompt_id; null — нет. Копия: вызывающий не мутирует индекс
    public MediaHistoryRecord? Get(string promptId)
    {
        lock (_lock)
            return _records.TryGetValue(promptId, out var r) ? Clone(r) : null;
    }

    // Страница: фильтры → счётчики → сортировка (время desc, promptId) → курсор → лимит
    public MediaHistoryPage Query(MediaHistoryQuery q)
    {
        List<MediaHistoryRecord> all;
        lock (_lock)
            all = _records.Values.Select(Clone).ToList();

        var filtered = all.Where(r =>
            (q.Kind is null || r.Kind == q.Kind)
            && (q.Model is null || r.Model == q.Model)
            && (q.Outcome is null || StatusOf(r) == q.Outcome)
            && (q.Login is null || OwnerLogin(r) == q.Login)
            && (q.From is null || SortTime(r) >= q.From)
            && (q.To is null || SortTime(r) <= q.To)
        ).ToList();

        var counters = new MediaHistoryCounters
        {
            Kinds = filtered.GroupBy(r => r.Kind ?? "unknown").ToDictionary(g => g.Key, g => g.Count()),
            Outcomes = filtered.GroupBy(r => StatusOf(r)).ToDictionary(g => g.Key, g => g.Count()),
        };

        var sorted = filtered
            .OrderByDescending(r => SortTime(r))
            .ThenBy(r => r.PromptId)
            .ToList();

        var list = sorted;
        if (q.Cursor is { } cursor && TryDecodeCursor(cursor, out var cTime, out var cId))
        {
            var idx = list.FindIndex(r => SortTime(r) < cTime || (SortTime(r) == cTime && r.PromptId.CompareTo(cId) > 0));
            if (idx >= 0) list = list.GetRange(idx, list.Count - idx);
        }

        var limit = Math.Clamp(q.Limit, 1, MediaHistoryQuery.PageLimit);
        var page = list.Take(limit).ToList();
        var nextCursor = page.Count == limit && list.Count > limit
            ? EncodeCursor(SortTime(page[^1]), page[^1].PromptId)
            : null;

        return new MediaHistoryPage
        {
            Records = page.Select(ToDto).ToList(),
            NextCursor = nextCursor,
            Counters = counters,
        };
    }

    // ── Внутреннее ─────────────────────────────────────────────────────────────────────

    // Строка v:1 → поля события. Битая строка или незнакомая версия — null (файл не роняем)
    private void ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("v", out var v) && v.GetInt32() != 1) return;
            if (!root.TryGetProperty("t", out var t) || !root.TryGetProperty("ts", out var ts)) return;
            if (!root.TryGetProperty("id", out var id)) return;
            var origin = root.TryGetProperty("origin", out var o) && o.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<LocalJobOrigin>(o.GetRawText(), Wire)
                : null;
            var model = root.TryGetProperty("model", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetString() : null;
            var prompt = root.TryGetProperty("prompt", out var p) && p.ValueKind != JsonValueKind.Null ? p.GetString() : null;
            var paramsNode = root.TryGetProperty("params", out var pa) && pa.ValueKind == JsonValueKind.Object ? (JsonObject)JsonNode.Parse(pa.GetRawText())! : null;
            var outcome = root.TryGetProperty("outcome", out var oc) && oc.ValueKind != JsonValueKind.Null ? oc.GetString() : null;
            var runSeconds = root.TryGetProperty("runSeconds", out var rs) && rs.ValueKind == JsonValueKind.Number ? (int?)rs.GetInt32() : null;
            var error = root.TryGetProperty("error", out var er) && er.ValueKind != JsonValueKind.Null ? er.GetString() : null;
            var tsValue = DateTimeOffset.Parse(ts.GetString()!, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
            Apply(id.GetString()!, t.GetString()!, tsValue, origin, model, prompt, paramsNode, outcome, runSeconds, error);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or OverflowException)
        {
            // Битая строка — пропускаем, файл целиком не роняем
        }
    }

    // Склейка события в запись: enqueued/started/finished пишут свои поля, повтор не дублирует
    private void Apply(string promptId, string type, DateTimeOffset ts, LocalJobOrigin? origin,
        string? model, string? prompt, JsonObject? @params, string? outcome, int? runSeconds, string? error)
    {
        if (!_records.TryGetValue(promptId, out var r))
            _records[promptId] = r = new MediaHistoryRecord(promptId);
        if (origin is not null) r.Origin = origin;
        switch (type)
        {
            case "enqueued":
                r.EnqueuedAt ??= ts;
                if (model is not null) r.Model = model;
                if (prompt is not null) r.Prompt = prompt;
                if (@params is not null) r.Params = @params;
                break;
            case "started":
                r.StartedAt ??= ts;
                break;
            case "finished":
                r.FinishedAt ??= ts;
                r.Outcome = outcome;
                r.RunSeconds = runSeconds;
                r.Error = error;
                break;
        }
    }

    // Время сортировки: завершение, иначе начало, иначе постановка (в процессе — по последнему известному)
    private static DateTimeOffset SortTime(MediaHistoryRecord r) =>
        r.FinishedAt ?? r.StartedAt ?? r.EnqueuedAt ?? DateTimeOffset.MinValue;

    // Исход для фильтра: завершённые — по outcome, в процессе — running/queued
    private static string StatusOf(MediaHistoryRecord r) => r.Status;

    // Логин владельца: OwnerId → username (UserStore); нет — null
    private string? OwnerLogin(MediaHistoryRecord r) =>
        r.Origin?.OwnerId is { } owner ? _users.GetById(owner)?.Username : null;

    private MediaHistoryRecordDto ToDto(MediaHistoryRecord r) => new()
    {
        Id = r.PromptId,
        Kind = r.Kind,
        Op = r.Op,
        Model = r.Model,
        Prompt = r.Prompt,
        Params = r.Params,
        Owner = OwnerLogin(r),
        Status = r.Status,
        Outcome = r.FinishedAt is not null ? r.Outcome : null,
        Error = r.Error,
        RunSeconds = r.RunSeconds,
        EnqueuedAt = r.EnqueuedAt,
        StartedAt = r.StartedAt,
        FinishedAt = r.FinishedAt,
        Source = r.Origin?.Source,
    };

    // Курсор = base64("{ticks}|{promptId}"): стабильный ключ (время desc, promptId)
    private static string EncodeCursor(DateTimeOffset time, string promptId) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{time.Ticks}|{promptId}"));

    private static bool TryDecodeCursor(string cursor, out DateTimeOffset time, out string promptId)
    {
        time = default;
        promptId = "";
        try
        {
            var raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var sep = raw.LastIndexOf('|');
            if (sep < 0 || !long.TryParse(raw[..sep], out var ticks)) return false;
            time = new DateTimeOffset(ticks, TimeSpan.Zero);
            promptId = raw[(sep + 1)..];
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static MediaHistoryRecord Clone(MediaHistoryRecord r) => new(r.PromptId)
    {
        Origin = r.Origin,
        Model = r.Model,
        Prompt = r.Prompt,
        Params = r.Params,
        Outcome = r.Outcome,
        RunSeconds = r.RunSeconds,
        Error = r.Error,
        EnqueuedAt = r.EnqueuedAt,
        StartedAt = r.StartedAt,
        FinishedAt = r.FinishedAt,
    };

    public void Dispose()
    {
        lock (_lock)
            _records.Clear();
    }
}
