using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.TestRuns;

// Исход теста в строке list-репортера Playwright: номер, название и признак повтора
public readonly record struct PlaywrightOutcomeLine(TestLineOutcome Outcome, int Index, string Title, bool Retry);

// Разбор list-репортера Playwright без TTY (FORCE_COLOR=0). Чистые функции без IO, образец
// живого вывода — фикстура теста. Замер 2026-10-02 (Playwright 1.60): первой строкой
// «Running 4 tests using 1 worker» — общее число известно сразу, процент честный без
// отдельного --list; дальше по строке на тест «  ok 1 [chromium] › e2e\x.spec.ts:3:3 › … (5ms)»,
// «  x  2 …», «  -  3 …» (пропущен). С включёнными retries повтор того же теста приходит
// строкой с «(retry #1)» — счётчик ведётся по тестам, а не по попыткам.
public static partial class PlaywrightListParser
{
    [GeneratedRegex(@"^\s*Running (\d+) tests? using \d+ workers?", RegexOptions.None, VsTestConsoleParser.RegexTimeoutMs)]
    private static partial Regex RunningLine();

    // ok/✓ — прошёл, x/✘ — упал, -/° — пропущен; номер, затем название до хвоста «(5ms)»
    [GeneratedRegex(@"^\s{2,}(ok|✓|x|✘|-|°)\s+(\d+)\s+(\S.*)$", RegexOptions.None, VsTestConsoleParser.RegexTimeoutMs)]
    private static partial Regex OutcomeLine();

    [GeneratedRegex(@"\s+\((?:retry #\d+|[\d.]+m?s)\)$", RegexOptions.None, VsTestConsoleParser.RegexTimeoutMs)]
    private static partial Regex TrailingParen();

    public const int MaxLineLength = 8192;

    // «Running N tests using …» → N; иначе null (и когда сработал потолок сопоставления)
    public static int? ParseTotal(string raw)
    {
        if (raw.Length > MaxLineLength) return null;
        try
        {
            var match = RunningLine().Match(raw);
            return match.Success ? int.Parse(match.Groups[1].Value) : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    public static PlaywrightOutcomeLine? ParseOutcome(string raw)
    {
        try { return ParseOutcomeCore(raw); }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static PlaywrightOutcomeLine? ParseOutcomeCore(string raw)
    {
        if (raw.Length > MaxLineLength) return null;
        var match = OutcomeLine().Match(raw.TrimEnd('\r'));
        if (!match.Success) return null;
        var outcome = match.Groups[1].Value switch
        {
            "ok" or "✓" => TestLineOutcome.Passed,
            "x" or "✘" => TestLineOutcome.Failed,
            _ => TestLineOutcome.Skipped,
        };
        var title = match.Groups[3].Value;
        // Название теста list-репортер всегда пишет через «›» (проект › файл › тест): строка
        // диффа в тексте ошибки вида «    -   1 item» исходом не считается
        if (!title.Contains(" › ", StringComparison.Ordinal)) return null;
        var retry = title.Contains("(retry #", StringComparison.Ordinal);
        // Хвостов бывает два подряд: «… (retry #1) (5ms)»
        for (var i = 0; i < 2; i++) title = TrailingParen().Replace(title, "");
        return new PlaywrightOutcomeLine(outcome, int.Parse(match.Groups[2].Value), title, retry);
    }
}
