using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.TestRuns;

// Итог одного тестового файла в строке default-репортера vitest
public readonly record struct VitestFileLine(string File, int Tests, int Failed, int Skipped, bool FileFailed);

// Разбор вывода vitest 4 без TTY (CI=1, NO_COLOR=1). Чистые функции без IO, образцы живого
// вывода — фикстуры тестов. Замер 2026-10-02 (vitest 4.1.9): default-репортер вне TTY пишет
// по строке на КАЖДЫЙ завершённый файл — « ✓ src/x.test.ts (38 tests) 15ms», у файла с
// падениями « ❯ src/y.test.ts (3 tests | 1 failed | 1 skipped) 9ms» и под ней тесты с
// отступом, у файла, который не загрузился, « ❯ src/z.test.ts (0 test)». Отсюда прогресс по
// файлам; общее число файлов даёт `vitest list --filesOnly` (~1,2 с на 171 файл).
public static partial class VitestReporterParser
{
    // Отступ ровно в один пробел, значок исхода файла, путь и скобка со счётчиками. Тесты под
    // файлом идут с отступом в 5 пробелов — сюда не попадают
    [GeneratedRegex(@"^ (✓|❯|×|↓) (\S.*?) \((\d+) tests?((?: \| \d+ \w+)*)\)(?: [\d.]+m?s)?$")]
    private static partial Regex FileLine();

    [GeneratedRegex(@"\| (\d+) (\w+)")]
    private static partial Regex CounterPart();

    // Строки длиннее не разбираем: путь файла столько не весит
    public const int MaxLineLength = 4096;

    public static VitestFileLine? ParseFileLine(string raw)
    {
        if (raw.Length > MaxLineLength || !raw.StartsWith(' ') || raw.StartsWith("  ", StringComparison.Ordinal))
            return null;
        var match = FileLine().Match(raw.TrimEnd('\r'));
        if (!match.Success) return null;
        int failed = 0, skipped = 0;
        foreach (Match part in CounterPart().Matches(match.Groups[4].Value))
        {
            var n = int.Parse(part.Groups[1].Value);
            switch (part.Groups[2].Value)
            {
                case "failed": failed += n; break;
                case "skipped" or "todo": skipped += n; break;
            }
        }
        var tests = int.Parse(match.Groups[3].Value);
        // «❯ файл (0 test)» — файл не загрузился (ошибка импорта, синтаксиса): это падение
        var fileFailed = match.Groups[1].Value is "❯" or "×" && (failed > 0 || tests == 0);
        return new VitestFileLine(match.Groups[2].Value, tests, failed, skipped, fileFailed);
    }

    // Строка вывода `vitest list --filesOnly` — путь тестового файла. Предупреждения node и
    // прочий шум в общем потоке отсекаются: путь без отступа и с расширением исходника
    public static bool IsListedFile(string raw)
    {
        var line = raw.TrimEnd('\r');
        return line.Length is > 0 and < MaxLineLength && !char.IsWhiteSpace(line[0])
            && !line.StartsWith('(') && !line.Contains("Warning", StringComparison.Ordinal)
            && SourceFile().IsMatch(line);
    }

    [GeneratedRegex(@"\.[cm]?[jt]sx?$")]
    private static partial Regex SourceFile();
}
