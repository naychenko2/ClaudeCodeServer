using ClaudeHomeServer.Services.ProjectIcons;

namespace ClaudeHomeServer.Tests.LocalBench.Places;

/// <summary>
/// Машинный оракул контракта места <c>project-icon</c> (Батарея II, ось A).
///
/// Место ДВУХХОДОВОЕ (ADR-009, «меню вместо памяти»): ход 1 — слова-понятия
/// <c>{"words":[…]}</c>, сервер отбирает по ним реальные имена lucide, ход 2 — выбор из
/// этого меню <c>{"glyphs":[{"name":"…"}]}</c>; отвергнутый выбор даёт ровно один повтор.
/// Судится ВСЯ цепочка: оборванный ход слов портит меню так же молча, как негодный выбор.
///
/// Оба хода разбираются ПРОДУКТОВЫМИ методами — <c>ParseWords</c> и
/// <see cref="ProjectIconGlyphService.Parse"/>.
///
/// Тонкость про меру продукта: <c>Parse</c> проверяет имя по белому списку lucide, а
/// боевой путь дополнительно требует, чтобы имя было В МЕНЮ этого проекта. Оракул судит
/// по белому списку — ровно как записано в контракте места, — а «имя настоящее, но мимо
/// меню» остаётся видимым иначе: у такого кейса появляется третий ход (повтор), и если
/// место всё равно осталось ни с чем, это печатается в колонке результата.
/// </summary>
public static class ProjectIconOracle
{
    /// <summary>
    /// Чем нарушен контракт цепочки. null — оба хода по контракту.
    /// </summary>
    /// <param name="turns">Все ходы кейса по порядку: слова, выбор и, если был, повтор.</param>
    public static string? Violation(LocalBenchTurns turns)
    {
        if (!turns.Any) return "место не сходило в модель";

        var wordsRaw = turns.Shots[0].RawAnswer;
        if (string.IsNullOrWhiteSpace(wordsRaw)) return "ход слов: пустой ответ";
        var (words, wordsReject) = ProjectIconGlyphService.ParseWords(wordsRaw);
        if (wordsReject is not null) return $"ход слов отвергнут: {wordsReject}";
        if (words.Count == 0) return "ход слов: слов не названо";

        // Единственный ход означает, что до выбора дело не дошло: по словам не нашлось
        // ни одной иконки набора. Слова формально по контракту, но значка у проекта нет.
        if (turns.Count == 1)
            return $"ход выбора не состоялся: по словам ({string.Join(", ", words)}) "
                   + "в наборе нет иконок";

        var pickRaw = turns.Last.RawAnswer;
        if (string.IsNullOrWhiteSpace(pickRaw)) return "ход выбора: пустой ответ";
        var pick = ProjectIconGlyphService.Parse(pickRaw);
        return pick.Ok ? null : $"ход выбора отвергнут: {pick.FailReason}";
    }

    /// <summary>Имена, названные последним ходом выбора, — для колонки результата.</summary>
    public static IReadOnlyList<string> Names(LocalBenchTurns turns) =>
        turns.Count < 2
            ? []
            : ProjectIconGlyphService.Parse(turns.Last.RawAnswer).Candidates
                .Select(c => c.Name).ToList();
}
