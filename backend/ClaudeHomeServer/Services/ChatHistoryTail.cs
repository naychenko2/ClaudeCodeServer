using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Докачка истории чата по курсору (Перф-2 B5): клиент, у которого лента уже в памяти,
// просит только хвост — записи после последней, которую он имеет. Сервер сверяет отпечаток
// ПРЕФИКСА (хэш всех записей от начала до курсора включительно): совпал — отдаёт хвост, не
// совпал (история переписана: правка записи в середине, откат, --resume, обрезка) — признак
// reset и полная история. Без курсора — прежний контракт (плоский массив), чтобы не ломать
// офлайн-кэш, MCP и старые клиенты.
//
// Отпечаток именно префикса, а не одной записи: аккумулятор правит записи прошлых ходов
// уже после их конца (дочерний вызов доживающего фонового агента дописывает result в
// tool_use — TurnAccumulator.FindTool ищет и в _history), и отпечаток одной записи-якоря
// такую правку не увидел бы — клиент навсегда остался бы с крутящимся спиннером.
//
// Выделена из контроллера как чистая функция над уже загруженным списком — как
// ChatHistoryPaginator: тестится без файлов и I/O, контроллер остаётся тонким.
public static class ChatHistoryTail
{
    // Опции сериализации отпечатка — как у HTTP-ответа истории: web-дефолты (camelCase) +
    // enum строками (Program.cs AddJsonOptions). Клиент отпечаток не вычисляет (JS
    // JSON.stringify ≠ C# JsonSerializer), а эхоит тот, что сервер сам ему выдал, —
    // совпадение гарантировано по построению.
    private static readonly JsonSerializerOptions FpOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // Якорь — индекс последней записи, завершающей ход (result/error/interrupted). Всё до
    // неё включительно — завершённые ходы; идущий ход (хвост после якоря) нестабилен, и
    // сервер отдаёт его всегда. -1 — завершённого хода ещё не было.
    public static int AnchorIndex(IReadOnlyList<StoredMessage> history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
            if (history[i] is StoredResultMessage or StoredErrorMessage or StoredInterruptedMessage)
                return i;
        return -1;
    }

    // Отпечаток префикса history[0..upTo] включительно: SHA-256 по JSON записей, первые
    // 8 байт в hex (16 символов, 64 бита — коллизия в пределах одной истории исключена).
    public static string? PrefixFingerprint(IReadOnlyList<StoredMessage> history, int upTo) =>
        Hashes(history, upTo, -1).AtFp;

    // Все отпечатки префиксов history[0..i] за ОДИН проход: i-й элемент — SHA-256(0..i).
    // Кэш (PrefixFingerprintCache) держит этот массив по (csid, версия истории) и отдаёт его
    // Compute: atFp/anchorFp становятся O(1)-выбором, а не пересчётом хэша на каждый запрос
    // с курсором (для 2,5 МБ истории это десятки мс на запрос).
    public static string[] AllPrefixFingerprints(IReadOnlyList<StoredMessage> history)
    {
        var n = history.Count;
        var result = new string[n];
        if (n == 0) return result;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var i = 0; i < n; i++)
        {
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(history[i], FpOptions));
            hash.AppendData("\n"u8);
            result[i] = Hex(hash.GetCurrentHash());
        }
        return result;
    }

    // Один проход по префиксу даёт оба отпечатка (под курсором и под якорем): история до
    // 2,5 МБ, второй проход удвоил бы цену запроса.
    private static (string? AtFp, string? AnchorFp) Hashes(IReadOnlyList<StoredMessage> history, int at, int anchor)
    {
        var last = Math.Max(at, anchor);
        if (last < 0) return (null, null);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string? atFp = null, anchorFp = null;
        for (var i = 0; i <= last; i++)
        {
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(history[i], FpOptions));
            hash.AppendData("\n"u8);
            if (i == at) atFp = Hex(hash.GetCurrentHash());
            if (i == anchor) anchorFp = Hex(hash.GetCurrentHash());
        }
        return (atFp, anchorFp);
    }

    private static string Hex(byte[] hash) => Convert.ToHexString(hash, 0, 8).ToLowerInvariant();

    // Итог запроса с курсором. Reset=true — клиенту менять ленту целиком (Messages = полная
    // история); Reset=false — дописывать хвост (Messages = записи после курсора, может быть
    // пустым). AnchorIndex/AnchorFp — новый курсор для следующего запроса (последняя
    // завершённая запись и отпечаток её префикса); -1/null — завершённого хода нет.
    public sealed record Result(bool Reset, IReadOnlyList<StoredMessage> Messages, int AnchorIndex, string? AnchorFp);

    // after — индекс последней записи, которая у клиента уже есть; fp — отпечаток её
    // префикса (от сервера). history — полная история сервера в момент запроса.
    // prefixFps — готовые отпечатки всех префиксов (из PrefixFingerprintCache): atFp/anchorFp
    // берутся из них за O(1) вместо пересчёта хэша; null — чистый путь (тесты, без кэша).
    public static Result Compute(IReadOnlyList<StoredMessage> history, int? after, string? fp, string[]? prefixFps = null)
    {
        var total = history.Count;
        var anchor = AnchorIndex(history);

        // Отпечаток префикса до индекса i: из готового массива (O(1)) либо пересчёт (чистый путь)
        string? FpAt(int i) =>
            i < 0 ? null
            : prefixFps is not null && i < prefixFps.Length ? prefixFps[i]
            : Hashes(history, i, i).AtFp;

        // Курсора нет — прежний контракт: полная история, reset не ставим
        if (after is null)
            return new Result(false, history, anchor, FpAt(anchor));

        var at = after.Value;

        // Курсор вне истории — она стала короче (откат/обрезка) либо у клиента ленты ещё
        // нет (after=-1: «дай всё и якорь»): полная история с признаком reset
        if (at < 0 || at >= total)
            return new Result(true, history, anchor, FpAt(anchor));

        var atFp = FpAt(at);
        var anchorFp = FpAt(anchor);

        // Отпечаток префикса не совпал — что-то до курсора переписано: индексы и содержимое
        // нельзя считать общими, доверяем только полной истории
        if (!string.Equals(atFp, fp, StringComparison.Ordinal))
            return new Result(true, history, anchor, anchorFp);

        // Совпало — всё до курсора у клиента и на сервере одно и то же; отдаём только хвост
        var tail = new List<StoredMessage>(total - at - 1);
        for (var i = at + 1; i < total; i++)
            tail.Add(history[i]);
        return new Result(false, tail, anchor, anchorFp);
    }
}

// Кэш отпечатков префиксов истории (Перф-3 B8): SHA-256 префикса 2,5 МБ-истории на каждый
// запрос с курсором — десятки мс. Массив AllPrefixFingerprints считается ОДИН раз на версию
// истории и переиспользуется: версия = LastWriteUtc файла history.json (тот же признак
// актуальности, что в ProjectFileSessionsIndex) + длина списка (страховка от живого
// аккумулятора, который дописал запись ещё без записи на диск). Ключ — csid, а csid уникален
// per-чат и per-владелец, поэтому изоляция per-owner не нарушается.
public class PrefixFingerprintCache
{
    // Потолок размера: кэш — оптимизация, не источник правды. Без него записи удалённых
    // чатов жили бы вечно (Perf-3 B10, п.2). При переполнении вытесняем давно не
    // пересчитанные (см. TrimIfOverLimit) — вытесненный чат просто пересчитает отпечатки
    // при следующем доступе, корректность не страдает.
    private const int DefaultMaxEntries = 1024;

    private sealed record Entry(DateTime? Version, int Count, string[] Fps, long LastRecompute);

    private readonly ChatHistoryService _history;
    private readonly int _maxEntries;

    // Ключ — ClaudeSessionId (история лежит по нему, не по Session.Id); совпадает с
    // SessionManager.HistoryKeyOf. Сторож границ: ChatHistoryService — спинка, не вертикаль.
    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    // Монотонный счётчик пересчётов: метка «насколько свежа запись» для вытеснения
    private long _recomputeCounter;

    public PrefixFingerprintCache(ChatHistoryService history) : this(history, DefaultMaxEntries) { }

    // Для тестов: свой потолок, чтобы вытеснение проверялось без тысячи записей
    internal PrefixFingerprintCache(ChatHistoryService history, int maxEntries)
    {
        _history = history;
        _maxEntries = maxEntries;
    }

    // Для тестов: наблюдаемый размер (в проде не читается)
    internal int Count => _cache.Count;

    // Готовые отпечатки всех префиксов истории: из кэша при совпадении версии, иначе пересчёт.
    public string[] GetOrCompute(string csid, IReadOnlyList<StoredMessage> messages)
    {
        var version = _history.LastWriteUtc(csid);
        if (_cache.TryGetValue(csid, out var entry) && entry.Version == version && entry.Count == messages.Count)
            return entry.Fps;
        var fps = ChatHistoryTail.AllPrefixFingerprints(messages);
        _cache[csid] = new Entry(version, messages.Count, fps, Interlocked.Increment(ref _recomputeCounter));
        TrimIfOverLimit();
        return fps;
    }

    // Вытеснение при переполнении: убираем давно не пересчитанные (наименьшая метка) до
    // половины лимита (гистерезис — не вытесняем на каждом запросе). Метка ставится на
    // пересчёте, а не на чтении: стабильная (не пишущаяся) история — самая старая и первой
    // уходит, а активно пишущаяся — свежая и остаётся. Per-read записи нет — hot-path дёшевый.
    private void TrimIfOverLimit()
    {
        if (_cache.Count <= _maxEntries) return;
        var target = _maxEntries / 2;
        var snapshot = _cache.ToArray();
        Array.Sort(snapshot, (a, b) => a.Value.LastRecompute.CompareTo(b.Value.LastRecompute));
        for (var i = 0; i < snapshot.Length - target; i++)
            _cache.TryRemove(snapshot[i].Key, out _);
    }
}
