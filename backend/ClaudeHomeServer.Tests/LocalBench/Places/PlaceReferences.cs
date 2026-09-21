using System.Text.Json;
using ClaudeHomeServer.Services.ProjectIcons;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tts;

namespace ClaudeHomeServer.Tests.LocalBench.Places;

/// <summary>
/// Сличители мест с ЭТАЛОНОМ банка — вторая метрика замера (Батарея II, ось A).
///
/// Лежат отдельно от оракулов намеренно: оракул судит КОНТРАКТ и обязан остаться
/// независимым от банка, иначе правка эталонов меняла бы вердикт по формату. Здесь же
/// собран другой вопрос — «разумен ли выбор», — и собран В ОДНОМ файле, чтобы рядом было
/// видно главное различие между местами: где эталон однозначен, а где он всего лишь один
/// из верных ответов.
///
/// Сила эталона по местам:
///  • ОДНОЗНАЧЕН (несовпадение = ошибка) — <c>task-dedup</c> (тот же id либо null),
///    <c>persona-voice</c> (пол голоса) и <c>task-normalize-title</c> (распознан ли срок:
///    «завтра» в заголовке либо есть, либо нет — вкусу тут места не остаётся);
///  • НИЖНЯЯ ГРАНИЦА (несовпадение = «нужен взгляд человека») — <c>project-icon</c>,
///    <c>project-background</c> и <c>task-classify</c>: у значка, цвета и приоритета
///    разумных вариантов несколько, и живые данные продукта лишь один из них.
///
/// Места <c>notes-tags</c> здесь нет: эталона у его банка нет вовсе (теги заметок в базе
/// проставлены неравномерно), и метрика по нему честно не считается — выдумать эталон
/// значило бы мерить собственную выдумку.
///
/// Разбор ответов идёт ПРОДУКТОВЫМИ методами, как и в оракулах: свой парсер сличал бы
/// эталон с тем, чего место человеку не показывало.
/// </summary>
public static class PlaceReferences
{
    // ---------- persona-voice: пол голоса, эталон однозначен ----------

    /// <summary>
    /// Сверка голоса персоны. Сличается ПОЛ, а не точное имя: «алёна против джейн» —
    /// вопрос вкуса (оба женских голоса персоне идут), а мужской голос женской персоне —
    /// именно ошибка, и она однозначна. Совпало ли имя ровно, видно в детали строки.
    /// </summary>
    public static readonly LocalBenchReference PersonaVoice = new(
        LocalBenchReferenceStrength.Exact, "пол голоса", (c, turns) =>
        {
            var expectedVoice = TaskClassifyLocalBenchTests.Text(c.Expect, "voice");
            if (string.IsNullOrWhiteSpace(expectedVoice)) return LocalBenchMatch.None;

            var expectedGender = GenderOf(expectedVoice);
            if (expectedGender is null) return LocalBenchMatch.None;

            var picked = PersonaVoiceOracle.Picked(turns.Last.RawAnswer);
            if (picked is null)
                return LocalBenchMatch.Differs($"голос не выбран, эталон {expectedVoice}");

            var gender = GenderOf(picked.Value.Voice);
            if (gender is null)
                return LocalBenchMatch.Differs($"голос «{picked.Value.Voice}» вне каталога");

            if (gender != expectedGender)
                return LocalBenchMatch.Differs(
                    $"пол не тот: {Ru(gender.Value)} вместо {Ru(expectedGender.Value)} "
                    + $"(эталон {expectedVoice})");

            var same = string.Equals(TtsVoiceCatalog.Canonical(picked.Value.Voice),
                TtsVoiceCatalog.Canonical(expectedVoice), StringComparison.OrdinalIgnoreCase);
            return LocalBenchMatch.Same(same
                ? "тот же голос"
                : $"пол верный, голос другой (эталон {expectedVoice})");
        });

    private static TtsVoiceCatalog.Gender? GenderOf(string? voice)
    {
        var canonical = TtsVoiceCatalog.Canonical(voice);
        if (canonical is null) return null;
        return TtsVoiceCatalog.All
            .FirstOrDefault(v => v.Voice.Equals(canonical, StringComparison.OrdinalIgnoreCase))
            ?.Gender;
    }

    private static string Ru(TtsVoiceCatalog.Gender gender) =>
        gender == TtsVoiceCatalog.Gender.Female ? "женский" : "мужской";

    // ---------- task-dedup: тот же дубль, эталон однозначен ----------

    /// <summary>
    /// Сверка найденного дубля. Эталон однозначен в обе стороны: пропустить настоящий
    /// дубль и назвать дублем чужую задачу — обе ошибки, и обе видны только так.
    /// </summary>
    public static readonly LocalBenchReference TaskDedup = new(
        LocalBenchReferenceStrength.Exact, "тот же дубль", (c, turns) =>
        {
            // Эталон «дубля нет» записан отсутствием ключа либо JSON-null — половина
            // банка именно такая, и считать её «эталона нет» значило бы выкинуть из
            // знаменателя ровно те кейсы, где место чаще всего и выдумывает дубль.
            var expected = TaskClassifyLocalBenchTests.Text(c.Expect, "duplicateId");
            var found = DuplicateId(turns.Last.RawAnswer);

            if (string.Equals(found, expected, StringComparison.Ordinal))
                return LocalBenchMatch.Same(expected is null ? "дубля нет — верно" : "тот же id");

            return LocalBenchMatch.Differs(expected is null
                ? "дубля нет, а место его нашло"
                : found is null
                    ? "дубль пропущен"
                    : "назван другой id");
        });

    // Что место вынет из ответа как id дубля. null — «дубля нет» (в том числе когда
    // продукт молча отбросил выдуманный id: для человека это и есть «дубля нет»).
    private static string? DuplicateId(string? rawAnswer)
    {
        if (string.IsNullOrWhiteSpace(rawAnswer)) return null;
        var json = TaskAiService.ExtractJsonObject(rawAnswer);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("duplicateId", out var d)
                || d.ValueKind != JsonValueKind.String) return null;
            var id = d.GetString()?.Trim();
            return string.IsNullOrEmpty(id) || id.Equals("null", StringComparison.OrdinalIgnoreCase)
                ? null
                : id;
        }
        catch (JsonException) { return null; }
    }

    // ---------- task-normalize-title: распознан ли срок, эталон однозначен ----------

    /// <summary>
    /// Сверка намёка на срок. Сличается ФАКТ распознавания, а не формулировка: «завтра» и
    /// «к завтрашнему дню» — один и тот же срок, а вот потерянный срок либо выдуманный на
    /// пустом месте однозначны и стоят человеку пропущенной даты.
    /// </summary>
    public static readonly LocalBenchReference TaskNormalizeTitle = new(
        LocalBenchReferenceStrength.Exact, "распознан ли срок", (c, turns) =>
        {
            if (c.Expect?.TryGetProperty("hasDueHint", out var el) != true
                || el.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return LocalBenchMatch.None;

            var expected = el.ValueKind == JsonValueKind.True;
            var actual = DueHint(turns.Last.RawAnswer) is not null;

            if (actual == expected)
                return LocalBenchMatch.Same(expected ? "срок распознан" : "срока нет — верно");
            return LocalBenchMatch.Differs(expected
                ? "срок в заголовке потерян"
                : "срок выдуман на пустом месте");
        });

    private static string? DueHint(string? rawAnswer)
    {
        if (string.IsNullOrWhiteSpace(rawAnswer)) return null;
        var json = TaskAiService.ExtractJsonObject(rawAnswer);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("dueHint", out var d)
                || d.ValueKind != JsonValueKind.String) return null;
            var hint = d.GetString()?.Trim();
            return string.IsNullOrEmpty(hint) ? null : hint;
        }
        catch (JsonException) { return null; }
    }

    // ---------- task-classify: приоритет, эталон — нижняя граница ----------

    /// <summary>
    /// Сверка приоритета с проставленным в трекере. НИЖНЯЯ ГРАНИЦА: «высокий вместо
    /// срочного» у половины живых задач спорен и для человека, поэтому несовпадение здесь
    /// означает «посмотреть глазами», а не «место ошиблось». Метки идут деталью — они
    /// свободный список, и требовать от места точного попадания в три слова владельца
    /// значило бы мерить телепатию.
    /// </summary>
    public static readonly LocalBenchReference TaskClassify = new(
        LocalBenchReferenceStrength.LowerBound, "приоритет", (c, turns) =>
        {
            var expected = TaskClassifyLocalBenchTests.Text(c.Expect, "priority");
            if (string.IsNullOrWhiteSpace(expected)) return LocalBenchMatch.None;

            var json = turns.Last.RawAnswer is null
                ? null
                : TaskAiService.ExtractJsonObject(turns.Last.RawAnswer);
            var (priority, labels) = ClassifyAnswer(json);
            var labelNote = LabelOverlap(labels, c.Expect);

            if (priority is null)
                return LocalBenchMatch.Differs($"приоритета нет, в трекере {expected}{labelNote}");

            return string.Equals(priority, expected, StringComparison.OrdinalIgnoreCase)
                ? LocalBenchMatch.Same($"приоритет как в трекере{labelNote}")
                : LocalBenchMatch.Differs($"{priority} против {expected} в трекере{labelNote}");
        });

    private static (string? Priority, IReadOnlyList<string> Labels) ClassifyAnswer(string? json)
    {
        if (json is null) return (null, []);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var priority = root.TryGetProperty("priority", out var p)
                           && p.ValueKind == JsonValueKind.String
                ? p.GetString()?.Trim()
                : null;
            var labels = root.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
                ? l.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList()
                : [];
            return (string.IsNullOrEmpty(priority) ? null : priority, labels);
        }
        catch (JsonException) { return (null, []); }
    }

    // Сколько меток ответа совпало с проставленными в трекере — деталью, не вердиктом.
    private static string LabelOverlap(IReadOnlyList<string> labels, JsonElement? expect)
    {
        if (expect?.TryGetProperty("labels", out var el) != true
            || el.ValueKind != JsonValueKind.Array) return "";
        var expected = el.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expected.Count == 0) return "";
        var hit = labels.Count(x => expected.Contains(x));
        return $"; меток из трекера {hit}/{expected.Count}";
    }

    // ---------- project-icon: значок, эталон — нижняя граница ----------

    /// <summary>
    /// Сверка значка. НИЖНЯЯ ГРАНИЦА и ярче всего именно здесь: «folder» вместо «flag» у
    /// проекта про страну — не ошибка, а другой разумный выбор, и оценивать по такому
    /// эталону место нельзя. Совпадение засчитывается, если эталонное имя оказалось СРЕДИ
    /// названных: место отдаёт несколько кандидатов, и человек выбирает из них.
    /// </summary>
    public static readonly LocalBenchReference ProjectIcon = new(
        LocalBenchReferenceStrength.LowerBound, "значок", (c, turns) =>
        {
            var expected = TaskClassifyLocalBenchTests.Text(c.Expect, "glyph");
            if (string.IsNullOrWhiteSpace(expected)) return LocalBenchMatch.None;

            var names = ProjectIconOracle.Names(turns);
            if (names.Count == 0)
                return LocalBenchMatch.Differs($"значка нет, у проекта {expected}");

            return names.Contains(expected, StringComparer.OrdinalIgnoreCase)
                ? LocalBenchMatch.Same($"«{expected}» среди названных")
                : LocalBenchMatch.Differs($"другой выбор, у проекта {expected}");
        });

    // ---------- project-background: цвет, эталон — нижняя граница ----------

    /// <summary>
    /// Сверка ключа цвета с выбранным у проекта. НИЖНЯЯ ГРАНИЦА: синий вместо зелёного —
    /// вкус, а не ошибка. Метрика тут говорит ровно одно: как часто место попадает в тот
    /// же цвет, что выбрал человек.
    /// </summary>
    public static readonly LocalBenchReference ProjectBackground = new(
        LocalBenchReferenceStrength.LowerBound, "цвет", (c, turns) =>
        {
            var expected = TaskClassifyLocalBenchTests.Text(c.Expect, "colorKey");
            if (string.IsNullOrWhiteSpace(expected)) return LocalBenchMatch.None;

            var (_, colorKey) = ProjectBackgroundOracle.Summary(turns.Last.RawAnswer);
            if (colorKey is null)
                return LocalBenchMatch.Differs($"цвет не предложен, у проекта {expected}");

            return string.Equals(colorKey, expected, StringComparison.OrdinalIgnoreCase)
                ? LocalBenchMatch.Same($"цвет как у проекта ({expected})")
                : LocalBenchMatch.Differs($"{colorKey} против {expected} у проекта");
        });
}
