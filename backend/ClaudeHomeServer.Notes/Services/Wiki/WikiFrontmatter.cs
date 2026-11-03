using System.Text;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Строгий парсер и рендер шапки страницы вики (ADR-035, решение 3). Терпимый
// NotesService.ParseFrontmatter не переиспользуется: шапка вики — контракт страницы,
// и терпимый разбор пропустил бы поля, которые форма запрещает.
// Чистые функции без I/O. Строгость здесь означает: всё, что не является ровно
// форматом ниже, — не шапка: неизвестный или дублирующийся ключ, вложенное значение,
// отсутствующая ограда — ошибка разбора с причиной, а не тихий пропуск.
public sealed class WikiFrontmatter
{
    // Семь значений type (ADR-035, решение 3; седьмое — chronicle, решение 12б).
    public const string TypeOverview = "overview";
    public const string TypeTopic = "topic";
    public const string TypeEntity = "entity";
    public const string TypeConcept = "concept";
    public const string TypeDecision = "decision";
    public const string TypeSource = "source";
    public const string TypeChronicle = "chronicle";

    // Статусы считает код, не модель (ADR-035, решение 3).
    public const string StatusActive = "active";
    public const string StatusDisputed = "disputed";
    public const string StatusStale = "stale";
    public const string StatusDrift = "drift";

    public static readonly IReadOnlyList<string> TypeValues =
        [TypeOverview, TypeTopic, TypeEntity, TypeConcept, TypeDecision, TypeSource, TypeChronicle];
    public static readonly IReadOnlyList<string> StatusValues =
        [StatusActive, StatusDisputed, StatusStale, StatusDrift];

    // Поля модели: type, kind, summary, aliases, owns. Поля кода: sources, updated, status.
    // Поля карточки (только type: source): origin, ref, hash, captured, signal, written.
    private const string Fence = "---";

    // Поля, запрещённые на страницах вики (ADR-035, решение 3).
    private static readonly string[] ForbiddenKeys = ["expires", "annotates", "file"];
    // Поля карточки источника: разрешены только у type: source; origin/ref/hash/captured обязательны.
    private static readonly string[] CardKeys = ["origin", "ref", "hash", "captured", "signal", "written"];
    private static readonly string[] RequiredCardKeys = ["origin", "ref", "hash", "captured"];
    // Поля списочной формы [a, b]; прочие — скаляр на одной строке.
    private static readonly string[] ListKeys = ["aliases", "owns", "sources"];

    public required string Type { get; init; }
    public string? Kind { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public IReadOnlyList<string> Owns { get; init; } = [];
    public IReadOnlyList<string> Sources { get; init; } = [];
    public string? Updated { get; init; }
    public string Status { get; init; } = StatusActive;
    // Поля карточки источника (type: source). Значение origin проверяет WikiGate
    // (закрытый перечень — правило гейта), здесь проверяется только форма.
    public string? Origin { get; init; }
    public string? Ref { get; init; }
    public string? Hash { get; init; }
    public string? Captured { get; init; }
    public string? Signal { get; init; }
    // Когда написан сам источник (ISO 8601): время последнего сообщения диапазона чата, правки
    // заметки, записи файла. Не captured (момент фиксации): бэклог старых чатов фиксируется
    // сегодняшним числом (ADR-038, решение 4). Необязательно: карточка без него — «время неизвестно».
    public string? Written { get; init; }

    public readonly record struct Result(WikiFrontmatter? Model, IReadOnlyList<string> Errors, int CloseLineIndex);

    /// <summary>Разбирает шапку из текста страницы. Model — null при любой ошибке;
    /// CloseLineIndex — строка закрывающей ограды (-1, когда шапки нет).</summary>
    public static Result Parse(string pageText)
    {
        var errors = new List<string>();
        var lines = SplitLines(pageText);
        if (lines.Length < 2 || lines[0] != Fence)
            return new Result(null, ["шапка: нет открывающей '---'"], -1);
        var close = -1;
        for (var i = 1; i < lines.Length; i++)
            if (lines[i] == Fence) { close = i; break; }
        if (close < 0)
            return new Result(null, ["шапка: нет закрывающей '---'"], -1);

        // Плоские ключи: «key: значение» с первой строки без отступа. Двоеточие без
        // пробела — не разделитель: «type:x» не является полем.
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < close; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                errors.Add($"шапка, строка {i + 1}: пустая строка");
                continue;
            }
            if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                errors.Add($"шапка, строка {i + 1}: вложенные значения не допускаются");
                continue;
            }
            var sep = line.IndexOf(": ", StringComparison.Ordinal);
            if (sep <= 0)
            {
                errors.Add($"шапка, строка {i + 1}: не формат 'ключ: значение'");
                continue;
            }
            var key = line[..sep];
            var value = line[(sep + 2)..];
            if (!IsKey(key))
            {
                errors.Add($"шапка, строка {i + 1}: некорректный ключ '{key}'");
                continue;
            }
            if (ForbiddenKeys.Contains(key))
            {
                errors.Add($"поле '{key}' на страницах вики запрещено");
                continue;
            }
            if (!raw.TryAdd(key, value))
                errors.Add($"поле '{key}' дублируется");
        }

        var model = BuildModel(raw, errors);
        return errors.Count > 0
            ? new Result(null, errors, close)
            : new Result(model, [], close);
    }

    private static WikiFrontmatter? BuildModel(Dictionary<string, string> raw, List<string> errors)
    {
        // type обязателен и берётся из семи значений; status — из четырёх, по умолчанию active.
        string? type = null;
        if (!raw.TryGetValue("type", out var typeRaw))
            errors.Add("шапка: нет поля 'type'");
        else if (!ScalarValue(typeRaw, "type", errors))
            type = null;
        else if (!TypeValues.Contains(typeRaw))
            errors.Add($"type '{typeRaw}' не из семи разрешённых");
        else
            type = typeRaw;

        var isSource = type == TypeSource;
        foreach (var key in CardKeys)
            if (raw.ContainsKey(key) && !isSource)
                errors.Add($"поле '{key}' есть только у карточки источника (type: source)");
        if (isSource)
            foreach (var key in RequiredCardKeys)
                if (!raw.ContainsKey(key))
                    errors.Add($"карточка источника: нет поля '{key}'");

        string? origin = null, ref_ = null, hash = null, captured = null, signal = null, written = null;
        if (isSource)
        {
            if (raw.TryGetValue("origin", out var originRaw) && ScalarValue(originRaw, "origin", errors)) origin = originRaw;
            if (raw.TryGetValue("ref", out var refRaw) && ScalarValue(refRaw, "ref", errors)) ref_ = refRaw;
            if (raw.TryGetValue("hash", out var hashRaw) && ScalarValue(hashRaw, "hash", errors)) hash = hashRaw;
            if (raw.TryGetValue("captured", out var capturedRaw) && ScalarValue(capturedRaw, "captured", errors)) captured = capturedRaw;
            if (raw.TryGetValue("signal", out var signalRaw) && ScalarValue(signalRaw, "signal", errors)) signal = signalRaw;
            if (raw.TryGetValue("written", out var writtenRaw) && ScalarValue(writtenRaw, "written", errors)) written = writtenRaw;
        }
        foreach (var key in CardKeys)
            raw.Remove(key);

        var status = StatusActive;
        if (raw.TryGetValue("status", out var statusRaw))
        {
            if (ScalarValue(statusRaw, "status", errors))
            {
                if (!StatusValues.Contains(statusRaw))
                    errors.Add($"status '{statusRaw}' не из четырёх разрешённых");
                else
                    status = statusRaw;
            }
        }

        var aliases = ListValue(raw, "aliases", errors);
        var owns = ListValue(raw, "owns", errors);
        var sources = ListValue(raw, "sources", errors);

        string? kind = null, summary = null, updated = null;
        if (raw.TryGetValue("kind", out var kindRaw) && ScalarValue(kindRaw, "kind", errors)) kind = kindRaw;
        if (raw.TryGetValue("summary", out var summaryRaw) && ScalarValue(summaryRaw, "summary", errors)) summary = summaryRaw;
        if (raw.TryGetValue("updated", out var updatedRaw) && ScalarValue(updatedRaw, "updated", errors)) updated = updatedRaw;

        // Всё, что не разобрано выше, — неизвестный ключ: строгий парсер не молчит о нём.
        foreach (var key in raw.Keys.Except(KnownKeys(type == TypeSource)))
            errors.Add($"неизвестное поле '{key}'");

        return errors.Count > 0
            ? null
            : new WikiFrontmatter
            {
                Type = type!,
                Kind = kind,
                Summary = summary,
                Aliases = aliases,
                Owns = owns,
                Sources = sources,
                Updated = updated,
                Status = status,
                Origin = origin,
                Ref = ref_,
                Hash = hash,
                Captured = captured,
                Signal = signal,
                Written = written,
            };
    }

    // Детерминированный порядок ключей: рендер всегда выдаёт шапку в нём,
    // «разбор → рендер → разбор» даёт равную модель.
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append(Fence).Append('\n');
        sb.Append("type: ").Append(Type).Append('\n');
        if (Kind is { Length: > 0 }) sb.Append("kind: ").Append(Kind).Append('\n');
        if (Summary is { Length: > 0 }) sb.Append("summary: ").Append(Summary).Append('\n');
        if (Type == TypeSource)
        {
            sb.Append("origin: ").Append(Origin).Append('\n');
            sb.Append("ref: ").Append(Ref).Append('\n');
            sb.Append("hash: ").Append(Hash).Append('\n');
            sb.Append("captured: ").Append(Captured).Append('\n');
            if (Signal is { Length: > 0 }) sb.Append("signal: ").Append(Signal).Append('\n');
            if (Written is { Length: > 0 }) sb.Append("written: ").Append(Written).Append('\n');
        }
        sb.Append("aliases: ").Append(RenderList(Aliases)).Append('\n');
        sb.Append("owns: ").Append(RenderList(Owns)).Append('\n');
        sb.Append("sources: ").Append(RenderList(Sources)).Append('\n');
        if (Updated is { Length: > 0 }) sb.Append("updated: ").Append(Updated).Append('\n');
        sb.Append("status: ").Append(Status).Append('\n');
        sb.Append(Fence);
        return sb.ToString();
    }

    internal static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

    private static IEnumerable<string> KnownKeys(bool isSource)
    {
        yield return "type";
        yield return "kind";
        yield return "summary";
        yield return "aliases";
        yield return "owns";
        yield return "sources";
        yield return "updated";
        yield return "status";
        if (isSource)
            foreach (var key in CardKeys)
                yield return key;
    }

    private static bool IsKey(string key) =>
        key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static bool ScalarValue(string value, string key, List<string> errors)
    {
        if (value.Length == 0)
        {
            errors.Add($"поле '{key}': пустое значение");
            return false;
        }
        if (value.StartsWith('['))
        {
            errors.Add($"поле '{key}': скаляр, а не список");
            return false;
        }
        return true;
    }

    private static string[] ListValue(Dictionary<string, string> raw, string key, List<string> errors)
    {
        if (!raw.TryGetValue(key, out var value))
            return [];
        var items = ParseList(value);
        if (items is null)
            errors.Add($"поле '{key}': не список формы [a, b]");
        return items ?? [];
    }

    // [a, b] — элементы через ", "; пустой список — []. Элемент не содержит скобок и
    // запятых и не обрамлён пробелами: иначе рендер не воспроизводит разбор.
    private static string[]? ParseList(string value)
    {
        if (!value.StartsWith('[') || !value.EndsWith(']'))
            return null;
        var inner = value[1..^1];
        if (inner.Length == 0)
            return [];
        var items = inner.Split(", ", StringSplitOptions.None);
        foreach (var item in items)
            if (item.Length == 0 || item != item.Trim() || item.IndexOfAny([',', '[', ']']) >= 0)
                return null;
        return items;
    }

    private static string RenderList(IReadOnlyList<string> items) =>
        "[" + string.Join(", ", items) + "]";

    // Равенство — по содержанию, включая списки: круговой тест «разбор → рендер →
    // разбор» сравнивает модели, а ссылки списков после второго разбора другие.
    public bool Equals(WikiFrontmatter? other) =>
        other is not null
        && Type == other.Type && Kind == other.Kind && Summary == other.Summary
        && Aliases.SequenceEqual(other.Aliases) && Owns.SequenceEqual(other.Owns)
        && Sources.SequenceEqual(other.Sources) && Updated == other.Updated
        && Status == other.Status && Origin == other.Origin && Ref == other.Ref
        && Hash == other.Hash && Captured == other.Captured && Signal == other.Signal
        && Written == other.Written;

    public override bool Equals(object? obj) => Equals(obj as WikiFrontmatter);

    public override int GetHashCode() =>
        HashCode.Combine(
            HashCode.Combine(Type, Kind, Summary, Status, Updated, Origin, Ref, Hash),
            HashCode.Combine(Captured, Signal, Written,
                string.Join(",", Aliases), string.Join(",", Owns), string.Join(",", Sources)));
}
