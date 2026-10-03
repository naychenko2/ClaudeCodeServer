using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.TestRuns;

// Исход одного теста в строке консоли dotnet test (логгер console;verbosity=normal)
public enum TestLineOutcome { Passed, Failed, Skipped }

public readonly record struct TestOutcomeLine(TestLineOutcome Outcome, string Name);

// Разбор вывода dotnet test (vstest, английский UI — DOTNET_CLI_UI_LANGUAGE=en). Чистые
// функции без IO: образцы реального вывода лежат фикстурами в тестах и сторожат смену
// формата (переход на Microsoft.Testing.Platform или xunit v3 сломает их первыми).
public static partial class VsTestConsoleParser
{
    // Шапка списка тестов из `dotnet test --list-tests`. У решения с несколькими сборками
    // шапки идут подряд, а имена — общим блоком после них (vstest пишет сборки параллельно)
    public const string ListHeader = "The following Tests are available:";

    // Строка исхода теста: ровно два пробела отступа, слово исхода и имя, в конце длительность
    // в квадратных скобках. Строки xunit «[xUnit.net …] X [FAIL]» и итоговые «Passed: 3» сюда
    // не попадают: у первых нет отступа, у вторых двоеточие сразу после слова.
    // Без ленивой группы и необязательного хвоста: та пара давала квадратичный перебор на
    // длинной строке (L-a ревью этапа 2). Хвост « [12 ms]» срезается руками, за один проход
    [GeneratedRegex(@"^  (Passed|Failed|Skipped) (\S.*)$", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex OutcomeLine();

    // Строка длиннее — не строка исхода (имя теста с аргументами столько не весит), разбор не
    // тратит на неё время вовсе
    public const int MaxOutcomeLineLength = 8192;

    // Потолок одного сопоставления для ВСЕХ регулярок разбора вывода в TestRuns: вывод пишет код
    // агента, и шаблон, пропущенный ревью, не должен повесить поток чтения. Сработал —
    // RegexMatchTimeoutException, и разбор считает строку нераспознанной. Настоящая строка
    // разбирается за микросекунды, потолок — с запасом на загруженную машину
    public const int RegexTimeoutMs = 200;

    // Ошибка сборки MSBuild/компилятора: «путь(стр,кол): error CS0246: текст [проект]»
    [GeneratedRegex(@":\s*error\s+[A-Z]+\d*\s*:", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex BuildErrorLine();

    // Хвост « [C:\…\App.csproj]», которым MSBuild подписывает ошибку проектом
    [GeneratedRegex(@"\s+\[[^\[\]]+\.(?:cs|fs|vb)proj\]$", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex ProjectSuffix();

    // Исход теста в строке (с именем теста) или null, если это не строка исхода
    public static TestOutcomeLine? ParseOutcome(string line)
    {
        if (line.Length > MaxOutcomeLineLength || !line.StartsWith("  ", StringComparison.Ordinal)) return null;
        Match match;
        try { match = OutcomeLine().Match(line.TrimEnd('\r')); }
        catch (RegexMatchTimeoutException) { return null; }
        if (!match.Success) return null;
        var outcome = match.Groups[1].Value switch
        {
            "Passed" => TestLineOutcome.Passed,
            "Failed" => TestLineOutcome.Failed,
            _ => TestLineOutcome.Skipped,
        };
        return new TestOutcomeLine(outcome, StripDuration(match.Groups[2].Value));
    }

    // « [12 ms]» в конце — длительность, а не часть имени
    private static string StripDuration(string name)
    {
        if (!name.EndsWith(']')) return name;
        var open = name.LastIndexOf(" [", StringComparison.Ordinal);
        return open > 0 && name.IndexOf(']', open) == name.Length - 1 ? name[..open] : name;
    }

    // Сколько тестов перечислил `--list-tests`: имена — строки с отступом в 4 пробела после
    // первой шапки (по всем сборкам сразу). null — шапки не было: список не получился
    public static int? CountListedTests(IEnumerable<string> lines)
    {
        var counter = new ListCounter();
        foreach (var line in lines) counter.Feed(line);
        return counter.Count;
    }

    // Тот же подсчёт потоком, по строке: список в тысячи имён не копится в памяти (L-c)
    public sealed class ListCounter
    {
        private bool _seenHeader;
        private int _count;

        public int? Count => _seenHeader ? _count : null;

        public void Feed(string raw)
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim() == ListHeader)
            {
                _seenHeader = true;
                return;
            }
            if (_seenHeader && line.StartsWith("    ", StringComparison.Ordinal) && line.Trim().Length > 0)
                _count++;
        }
    }

    // Первые ошибки сборки без повторов: MSBuild печатает каждую дважды — по ходу и в сводке
    // в конце. Подпись проектом срезается: модели хватает файла и строки
    public static IReadOnlyList<string> BuildErrors(IEnumerable<string> lines, int max = 20)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in lines)
        {
            string line;
            try
            {
                if (!BuildErrorLine().IsMatch(raw)) continue;
                line = ProjectSuffix().Replace(raw.Trim(), "");
            }
            catch (RegexMatchTimeoutException) { continue; }
            if (!seen.Add(line)) continue;
            result.Add(line);
            if (result.Count >= max) break;
        }
        return result;
    }
}
