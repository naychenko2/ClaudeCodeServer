using System.Collections.Concurrent;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm;

// Состояние GPU-арбитра (GET /state, deploy/video/bonsai/gpu-arbiter.py) для движка с `StateUrl`.
// Опрашивается по запросу, фонового цикла нет: арбитра спрашивают дорожка статистики (статус
// «уступила») и блок «Дежурство» — только когда на них смотрят.
//
// Ответ арбитра — недоверенный (разрез c4b4c1cc §2, ADR-029):
//   • тело больше 64 КБ — отказ без исключения, хвост не читается (тело — поток);
//   • не больше 32 причин и 32 аренд; каждая причина — недоверенный текст на входе и
//     обрезается: `lease:` (произвольная строка из POST /lease от любого в сети) — до 64 символов,
//     `gpu:` (pid + командная строка процесса) и `comfy:` (id задачи) — общим потолком 80;
//   • неверный тип поля обнуляет только это поле (элемент неверного типа в массиве пропускается);
//   • неизвестное значение `bonsai` → `unknown`; `mode` вне exclusive/shared → null;
//   • часы Video и CCS расходятся: возраст состояния меряется часами арбитра (ts − since) и
//     переносится на нашу ось — `sinceAt = now − (ts − since)`; сам `ts` наружу не отдаётся;
//     абсурдные часы (отрицательный возраст или вне диапазона) обнуляют только `sinceAt`.
//
// На каждый движок: кэш 1 с с single-flight (два опроса в пределах секунды → один HTTP),
// последний удачный снимок: при отказе `reachable: false` с полями последнего удачного
// (до первого успеха — все null). HTTP-клиент — существующий тихий `local-engine-ready`,
// общий бюджет 800 мс на заголовки и тело: арбитр отвечает мгновенно, дольше — его нет.
public sealed record EngineArbiterSnapshot(
    bool Reachable,
    string? Bonsai,                     // ready | yielded | starting | waiting-vram | unknown
    IReadOnlyList<string>? Reasons,
    DateTimeOffset? SinceAt,           // наша ось: last state change
    int? Yields,
    int? Returns,
    double? LastYieldS,
    double? LastReturnS,
    string? Mode,                      // exclusive | shared
    double? QuietLeftS,                // «вернётся через», пока арбитр ждёт паузы
    IReadOnlyList<EngineArbiterLease>? Leases,
    DateTimeOffset? SeenAt)            // когда снимок получен (наша ось)
{
    public const string StateUnknown = "unknown";
    public const string StateReady = "ready";
    public const string StateYielded = "yielded";
    public const string StateStarting = "starting";
    public const string StateWaitingVram = "waiting-vram";
}

// Одна аренда из `leases`: id наружу не отдаётся (его создал аноним), только текст и остаток.
public sealed record EngineArbiterLease(string? Reason, double? LeftS);

public sealed class LocalEngineStateClient : IEngineArbiterStateReader
{
    private const int CacheMs = 1000;
    private const int RequestTimeoutMs = 800;
    private const int MaxBodyBytes = 64 * 1024;
    private const int MaxItems = 32;
    private const int MaxUntrustedLen = 64;  // текст «lease:» — произвольная строка из сети
    private const int MaxReasonLen = 80;     // прочие причины: «gpu:» pid+командная строка, «comfy:» id

    private static readonly string[] BonsaiStates =
        [EngineArbiterSnapshot.StateReady, EngineArbiterSnapshot.StateYielded,
         EngineArbiterSnapshot.StateStarting, EngineArbiterSnapshot.StateWaitingVram,
         EngineArbiterSnapshot.StateUnknown];
    private static readonly string[] Modes = ["exclusive", "shared"];

    private sealed class Slot
    {
        public Task<EngineArbiterSnapshot>? InFlight;   // single-flight: общий запрос на окно кэша
        public long StartedTicks;
        public EngineArbiterSnapshot? Cached;           // результат последнего завершённого запроса
        public EngineArbiterSnapshot? LastGood;         // последний удачный снимок
    }

    private readonly NamedLocalEngines _engines;
    private readonly IHttpClientFactory _http;
    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);

    public LocalEngineStateClient(NamedLocalEngines engines, IHttpClientFactory http)
    {
        _engines = engines;
        _http = http;
    }

    // null — у движка нет `StateUrl` (арбитра нет) или сам движок неизвестен.
    // Исключение — только явная отмена вызывающего (`ct`); всё остальное — снимок с reachable.
    public async Task<EngineArbiterSnapshot?> GetAsync(string engineName, CancellationToken ct = default)
    {
        var url = _engines.Get(engineName)?.Spec.StateUrl;
        if (string.IsNullOrWhiteSpace(url)) return null;

        var slot = _slots.GetOrAdd(engineName, _ => new Slot());
        Task<EngineArbiterSnapshot> task;
        lock (slot)
        {
            var now = Environment.TickCount64;
            if (slot.Cached is not null && now - slot.StartedTicks < CacheMs)
                return slot.Cached;
            if (slot.InFlight is null || now - slot.StartedTicks >= CacheMs)
            {
                slot.StartedTicks = now;
                slot.InFlight = FetchAsync(slot, url);
            }
            task = slot.InFlight!;
        }
        // Общий запрос окна кэша ведётся без ct вызывающего: чужая отмена не даёт остальным
        // ложный reachable:false. Свой ct — своё решение: отменившийся получает
        // OperationCanceledException здесь (само ожидание ограничено бюджетом запроса).
        var result = await task;
        ct.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<EngineArbiterSnapshot> FetchAsync(Slot slot, string url)
    {
        EngineArbiterSnapshot? good = null;
        // Один бюджет 800 мс на заголовки и тело: при ResponseHeadersRead client.Timeout
        // держит только заголовки, медленное тело обрывает этот cts.
        using var budget = new CancellationTokenSource(RequestTimeoutMs);
        try
        {
            var client = _http.CreateClient(NamedLocalEngines.HttpClientName);
            client.Timeout = TimeSpan.FromMilliseconds(RequestTimeoutMs);
            using var r = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            if (r.IsSuccessStatusCode)
            {
                var body = await ReadCappedAsync(r.Content, budget.Token);
                if (body is not null) good = Parse(body);
            }
        }
        catch (Exception)
        {
            // Нет соединения, таймаут, 5xx, обрыв тела — в этот раз арбитр не ответил.
        }

        lock (slot)
        {
            if (good is not null) slot.LastGood = good;
            var result = good
                ?? (slot.LastGood is not null
                    ? slot.LastGood with { Reachable = false }
                    // До первого успеха: reachable:false и все поля null — выдумывать нечего.
                    : new EngineArbiterSnapshot(false, null, null, null, null, null, null, null, null, null, null, null));
            slot.Cached = result;
            return result;
        }
    }

    // Тело больше 64 КБ — отказ (null), не исключение: такой ответ не читали.
    // Чтение обрывается на лимите: поток закрывается раньше, чем соединение опустошится до конца.
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int total = 0, read;
        await using (var stream = await content.ReadAsStreamAsync(ct))
        {
            while ((read = await stream.ReadAsync(buf, ct)) > 0)
            {
                if (total + read > MaxBodyBytes) return null;
                ms.Write(buf, 0, read);
                total += read;
            }
        }
        return ms.ToArray();
    }

    private static EngineArbiterSnapshot? Parse(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // Возраст состояния — по часам арбитра (ts − since), перенос на нашу ось.
            var sinceAt = AgeToOurAxis(GetDouble(root, "ts"), GetDouble(root, "since"));

            return new EngineArbiterSnapshot(
                Reachable: true,
                Bonsai: GetBonsai(root),
                Reasons: GetReasons(root),
                SinceAt: sinceAt,
                Yields: GetInt(root, "yields"),
                Returns: GetInt(root, "returns"),
                LastYieldS: GetDouble(root, "last_yield_s"),
                LastReturnS: GetDouble(root, "last_return_s"),
                Mode: GetMode(root),
                QuietLeftS: GetDouble(root, "quiet_left_s"),
                Leases: GetLeases(root),
                SeenAt: DateTimeOffset.UtcNow);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Абсурдные часы арбитра обнуляют только sinceAt — снимок остаётся живым:
    // since > ts (возраст отрицательный) или возраст вне диапазона DateTimeOffset.
    private static DateTimeOffset? AgeToOurAxis(double? ts, double? since)
    {
        if (ts is null || since is null) return null;
        var age = ts.Value - since.Value;
        if (age < 0) return null; // часы разошлись: отрицательный возраст выдумывать нечего.
        try
        {
            return DateTimeOffset.UtcNow.AddSeconds(-age);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // возраст вне диапазона часов.
        }
    }

    private static string? GetBonsai(JsonElement o, string name = "bonsai")
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString()!;
        // Значение вне списка состояний — не выдумываем: «unknown» (арбитр так же отвечает до первого цикла).
        return Array.IndexOf(BonsaiStates, s) >= 0 ? s : EngineArbiterSnapshot.StateUnknown;
    }

    private static string? GetMode(JsonElement o)
    {
        if (!o.TryGetProperty("mode", out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString()!;
        return Array.IndexOf(Modes, s) >= 0 ? s : null;
    }

    private static IReadOnlyList<string>? GetReasons(JsonElement root)
    {
        if (!root.TryGetProperty("reasons", out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (list.Count >= MaxItems) break;
            if (item.ValueKind != JsonValueKind.String) continue;
            // Все причины — недоверенный текст на входе: «lease:» (строка из сети) — в потолок 64,
            // «gpu:»/«comfy:» и всё без префикса — общим потолком 80 (командная строка не пойдёт в UI).
            list.Add(TruncateReason(item.GetString()!));
        }
        return list;
    }

    private static IReadOnlyList<EngineArbiterLease>? GetLeases(JsonElement root)
    {
        if (!root.TryGetProperty("leases", out var v) || v.ValueKind != JsonValueKind.Object) return null;
        var list = new List<EngineArbiterLease>();
        foreach (var prop in v.EnumerateObject())
        {
            if (list.Count >= MaxItems) break;
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            // Текст аренды — та же произвольная строка из сети: потолок 64, как у «lease:».
            var reason = prop.Value.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? Truncate(r.GetString()!, MaxUntrustedLen)
                : null;
            list.Add(new EngineArbiterLease(reason, GetDouble(prop.Value, "left_s")));
        }
        return list;
    }

    // Единое правило на входе: «lease:» — в более строгий потолок 64, всё остальное — общий потолок 80.
    private static string TruncateReason(string s) =>
        s.StartsWith("lease:", StringComparison.Ordinal) ? Truncate(s, MaxUntrustedLen) : Truncate(s, MaxReasonLen);

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen];

    private static double? GetDouble(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? d : null;

    private static int? GetInt(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i : null;
}
