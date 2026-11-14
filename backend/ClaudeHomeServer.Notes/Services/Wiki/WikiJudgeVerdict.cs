using System.Text.Json;

namespace ClaudeHomeServer.Services.Notes.Wiki;

/// <summary>
/// Разбор ответа облачного судьи вики (J-J1a): текст модели → список вердиктов. Контракт
/// ответа задаёт промпт (WikiJudgePrompts.System):
/// {"verdicts":[{"index":int,"decision":"apply"|"reject","why":"…"}]}.
///
/// Разбор — чистая функция и шов между ответом модели и применением вердиктов: лишнего
/// модель не тронет. Из ответа берутся только index / decision / why; любые другие
/// поля (переформулированный claim, «новый текст», правка страницы) игнорируются — судья по
/// промпту ничего не переписывает, и код не даёт переписанному просочиться в вику.
///
/// Пропускается (счётчик Skipped): index вне allowedIndexes (не из этого пакета); повтор
/// index — берётся только первое вхождение, последующие не получают вердикта даже если
/// первое было бракованным; decision не apply/reject (в том числе rewrite); пустой why;
/// item без полей или не объект; index не числом. why обрезается до MaxWhyChars.
///
/// Мусор, пустая строка, null, нечитаемый или несбалансированный JSON, объект без verdicts →
/// пустой список без исключения: вердиктов нет, вика не меняется. Skipped считает только
/// отброшенные item-ы внутри разобранного JSON; когда разбирать нечего, Skipped = 0.
/// </summary>
public static class WikiJudgeVerdict
{
    /// <summary>Потолок объяснения судьи (символы) — тот же, что в промпте.</summary>
    public const int MaxWhyChars = 300;

    /// <summary>Один принятый вердикт: index операции, применять ли, почему (≤ MaxWhyChars).</summary>
    public sealed record Item(int Index, bool Apply, string Why);

    /// <summary>
    /// Разобрать ответ судьи. allowedIndexes — индексы операций этого пакета: вердикт по
    /// индексу вне пакета не принимается. Ничего не бросает при плохом ответе модели.
    /// </summary>
    public static (IReadOnlyList<Item> Verdicts, int Skipped) Parse(
        string? text, IReadOnlySet<int> allowedIndexes)
    {
        ArgumentNullException.ThrowIfNull(allowedIndexes);

        var verdicts = new List<Item>();
        var skipped = 0;
        if (string.IsNullOrWhiteSpace(text))
            return (verdicts, skipped);

        var json = FirstJsonObject(text);
        if (json is null)
            return (verdicts, skipped);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("verdicts", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return (verdicts, skipped);

            // Первое вхождение индекса из пакета — единственное: повтор не получает вердикта.
            var claimed = new HashSet<int>();
            foreach (var el in arr.EnumerateArray())
            {
                var item = TryItem(el, allowedIndexes, claimed);
                if (item is null)
                {
                    skipped++;
                    continue;
                }
                verdicts.Add(item);
            }
        }
        catch (JsonException)
        {
            return (verdicts, skipped);
        }

        return (verdicts, skipped);
    }

    // Ответ модели бывает в прозе (как у ступени 2, WikiChatStage2.TryParseVerdict), поэтому
    // берётся не «первая { и последняя }» — проза после ответа могла бы утащить лишнюю }, —
    // а первый СБАЛАНСИРОВАННЫЙ объект: от первой { до закрывающей её }, со счёлом глубины
    // и с пропусками внутри строковых значений.
    private static string? FirstJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
            return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return text[start..(i + 1)];
                    break;
            }
        }

        return null;
    }

    // null — этот item пропускается: не объект, нет поля, не того типа, индекс вне пакета
    // или занят его первым вхождением, decision не apply/reject, why пустой.
    private static Item? TryItem(JsonElement el, IReadOnlySet<int> allowedIndexes, HashSet<int> claimed)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;

        // index — числом и из этого пакета; строка "3" — не угаданный индекс, а пропуск.
        if (!el.TryGetProperty("index", out var indexEl)
            || indexEl.ValueKind != JsonValueKind.Number
            || !indexEl.TryGetInt32(out var index)
            || !allowedIndexes.Contains(index))
            return null;
        // Первое вхождение индекса из пакета занято им одним: последующие не получают
        // вердикта, даже если первое оказалось бракованным (пустой why, не тот decision).
        if (!claimed.Add(index))
            return null;

        if (!el.TryGetProperty("decision", out var decisionEl)
            || decisionEl.ValueKind != JsonValueKind.String)
            return null;
        var decision = (decisionEl.GetString() ?? "").Trim();
        bool apply;
        if (decision.Equals("apply", StringComparison.OrdinalIgnoreCase)) apply = true;
        else if (decision.Equals("reject", StringComparison.OrdinalIgnoreCase)) apply = false;
        else return null;

        if (!el.TryGetProperty("why", out var whyEl) || whyEl.ValueKind != JsonValueKind.String)
            return null;
        var why = (whyEl.GetString() ?? "").Trim();
        if (why.Length == 0)
            return null;
        if (why.Length > MaxWhyChars)
            why = why[..MaxWhyChars];

        return new Item(index, apply, why);
    }
}
