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
    public static Result Compute(IReadOnlyList<StoredMessage> history, int? after, string? fp)
    {
        var total = history.Count;
        var anchor = AnchorIndex(history);

        // Курсора нет — прежний контракт: полная история, reset не ставим
        if (after is null)
            return new Result(false, history, anchor, Hashes(history, -1, anchor).AnchorFp);

        var at = after.Value;

        // Курсор вне истории — она стала короче (откат/обрезка) либо у клиента ленты ещё
        // нет (after=-1: «дай всё и якорь»): полная история с признаком reset
        if (at < 0 || at >= total)
            return new Result(true, history, anchor, Hashes(history, -1, anchor).AnchorFp);

        var (atFp, anchorFp) = Hashes(history, at, anchor);

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
