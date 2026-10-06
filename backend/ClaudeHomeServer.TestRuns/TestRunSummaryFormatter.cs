using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.TestRuns;

// Итог прогона для модели. Жёсткий потолок — maxBytes (UTF-8): ответ инструмента едет в
// контекст модели целиком, полный лог лежит файлом в папке артефактов. Порядок важности:
// исход → сводка по сборкам → упавшие (сколько влезет, остальное «и ещё N») → хвост вывода
// (только когда без него непонятно, что случилось). Строка про артефакты резервируется
// первой — по ней модель дочитает полный вывод Read'ом.
public static class TestRunSummaryFormatter
{
    // Резерв под строку «…и ещё N упавших — полный список в …»
    private const int MoreReserve = 200;

    // Ширина строки сообщения/стека и ошибки сборки: одна строка-простыня (сериализованный
    // объект в сообщении assert'а) иначе вытесняла все остальные детали (L-b ревью этапа 2)
    public const int MaxLineChars = 400;

    // Escape-последовательности терминала: CSI (цвета «ESC[31m», курсор) и OSC (ссылки
    // «ESC]8;;…BEL»). Playwright кладёт цвета прямо в error.message JSON-отчёта, и
    // FORCE_COLOR=0 их не убирает — без вычистки модель и карточка видят «\u001b[2mexpect(»
    // Общая для всех движков (вывод npm-сборки чистится ею же). Потолок сопоставления сработал —
    // текст остаётся как есть: лучше escape-мусор в строке, чем повисший поток
    private static readonly Regex AnsiEscape = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])", RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(VsTestConsoleParser.RegexTimeoutMs));

    public static string StripAnsi(string text)
    {
        if (!text.Contains('\x1B')) return text;
        try { return AnsiEscape.Replace(text, ""); }
        catch (RegexMatchTimeoutException) { return text; }
    }

    // Подпись вида прогона в итоге и на карточке: «Тесты · vitest»
    public static string KindTitle(TestRunKind kind) => kind switch
    {
        TestRunKind.Vitest => "vitest",
        TestRunKind.Playwright => "Playwright",
        _ => "dotnet test",
    };

    public static string Format(TestRunResult r, int maxBytes, int ceilingSeconds)
    {
        if (r.Refusal is { } refusal) return Clamp(refusal, maxBytes);

        var trailer = r.ArtifactsPath is { } path ? $"\n\nАртефакты прогона: {path}/ ({ArtifactsNote(r)})." : "";
        var text = new Budget(maxBytes - Bytes(trailer));
        text.Add(Head(r, ceilingSeconds));

        if (r.BuildErrors.Count > 0)
        {
            text.Add($"\n\nПервые ошибки сборки ({r.BuildErrors.Count}):");
            foreach (var error in r.BuildErrors)
                if (!text.Add("\n" + ClampLine(error))) break;
        }

        if (r.Reports.Count > 0)
        {
            text.Add("\n");
            // Сборки, где фильтр ничего не нашёл, — шум: прогон по решению давал десятки строк
            // «OK: passed 0 из 0» перед нужной. Сворачиваем их в одну строку
            var withTests = r.Reports.Where(rep => rep.Total > 0).ToList();
            var empty = r.Reports.Count - withTests.Count;
            if (withTests.Count == 0 && empty == 1) text.Add("\n" + SummaryLine(r.Reports[0]));
            else if (withTests.Count == 0)
                text.Add($"\nНи в одной из {empty} {(empty % 10 == 1 && empty % 100 != 11 ? "сборки" : "сборок")} "
                    + "нет тестов под фильтр.");
            else
            {
                foreach (var report in withTests) text.Add("\n" + SummaryLine(report));
                if (empty > 0) text.Add($"\n…ещё {empty} {AssembliesWord(empty)} без подходящих тестов.");
                if (withTests.Count > 1) text.Add("\n" + TotalLine(withTests));
            }
        }
        foreach (var name in r.OversizedReports)
            text.Add($"\n\nОтчёт {name} больше {TestRunService.MaxReportBytes / 1024 / 1024} МБ — сервер его не "
                + "разбирал; упавшие ищи в нём сам (Grep по папке артефактов).");

        var failures = r.Reports.SelectMany(rep => rep.Failures).ToList();
        if (failures.Count > 0)
            AddFailures(text, failures, r);
        else if (r.Reports.Count == 0 && r.FailedFromConsole.Count > 0)
        {
            // Обрыв: отчёта нет, но упавшие по консоли уже известны
            text.Add($"\n\nУпали к моменту обрыва ({r.FailedFromConsole.Count}):");
            foreach (var name in r.FailedFromConsole)
                if (!text.Add("\n- " + ClampLine(name))) break;
        }

        if (NeedsTail(r)) AddTail(text, r.Tail);
        return Clamp(text.ToString() + trailer, maxBytes);
    }

    // Что лежит в папке артефактов: упоминаем только то, что там правда есть (на ошибке
    // сборки TRX не появляется)
    private static string ArtifactsNote(TestRunResult r)
    {
        var log = r.Kind == TestRunKind.Dotnet ? "console.log — полный вывод всех фаз" : "console.log — полный вывод";
        if (r.Reports.Count == 0 && r.OversizedReports.Count == 0) return log;
        return r.Kind == TestRunKind.Dotnet
            ? log + ", *.trx — результаты по сборкам"
            : log + $", {TestRunService.JsonReportName} — отчёт {KindTitle(r.Kind)}";
    }

    // Где лежит полный список упавших
    private static string ReportGlob(TestRunKind kind) =>
        kind == TestRunKind.Dotnet ? "*.trx" : TestRunService.JsonReportName;

    private static string Head(TestRunResult r, int ceilingSeconds)
    {
        var elapsed = FormatElapsed(r.Elapsed);
        var where = Where(r);
        var tool = KindTitle(r.Kind);
        if (r.NeverStarted)
            return (r.Cancelled
                    ? $"{tool}: прогон отменён через {elapsed}, пока ждал очереди сборок"
                    : $"{tool}: прогон не дождался очереди сборок за {elapsed} (потолок {ceilingSeconds} с)")
                + $": {TestRunService.KindName(r.Kind)} не запускался. Повтори позже или раздели прогон "
                + (r.Kind == TestRunKind.Dotnet ? "аргументом filter." : "аргументами files/filter.");
        if (r.Cancelled)
            return $"{tool}: прогон остановлен по отмене вызова через {elapsed} {where}; процесс и его дерево погашены.";
        if (r.TimedOut)
            return $"{tool}: прогон оборван серверным потолком {ceilingSeconds} с через {elapsed} {where}; "
                + (r.Kind == TestRunKind.Dotnet
                    ? "процесс погашен. Раздели прогон аргументом filter."
                    : "процесс погашен. Раздели прогон аргументами files/filter.");
        if (r.Phase == TestRunPhase.Build)
            return $"{tool}: сборка не прошла (код выхода {r.ExitCode}) за {elapsed} — тесты не запускались.";
        var failed = r.Reports.Sum(rep => rep.Failed);
        if (r.ExitCode == 0)
            return $"{tool}: все тесты прошли (код выхода 0) за {elapsed}.";
        return failed > 0
            ? $"{tool}: есть упавшие тесты (код выхода {r.ExitCode}) за {elapsed}."
            : $"{tool} завершился с кодом {r.ExitCode} за {elapsed}, но упавших тестов в результатах нет — "
              + "смотри хвост вывода (" + (r.Kind switch
              {
                  TestRunKind.Dotnet => "падение testhost, ошибка запуска, пустой фильтр",
                  TestRunKind.Vitest => "ошибка конфига, ни одного файла под фильтр",
                  _ => "ошибка конфига или webServer, ни одного теста под фильтр",
              }) + ").";
    }

    // Где оборвалось: фаза и счётчик прогона
    private static string Where(TestRunResult r) => r.Phase switch
    {
        TestRunPhase.Build => "на фазе сборки",
        TestRunPhase.List => r.Kind == TestRunKind.Vitest ? "на подсчёте файлов" : "на подсчёте тестов",
        _ => $"на {r.ProgressLabel ?? ProgressLabel(r.Counts, r.Total)}",
    };

    // «412 из 7951 · упало 2»; без общего числа — «412 · упало 2». Общее число из --list-tests —
    // оценка снизу: теорию xUnit с несериализуемыми данными список показывает одной строкой, а
    // прогон печатает исход на каждый случай. Перевалили его — «из M» было бы враньём («100 из 70»)
    public static string ProgressLabel(TestCounts counts, int? total)
    {
        var done = total is { } t && counts.Done <= t ? $"{counts.Done} из {t}" : $"{counts.Done}";
        return counts.Failed > 0 ? $"{done} · упало {counts.Failed}" : done;
    }

    // vitest считает файлы: «87 из 171 файла · упало 2»; без общего числа — «87 файлов · упало 2».
    // «Упало» — число упавших тестов, а не файлов: модели и человеку важнее оно
    public static string FilesProgressLabel(int filesDone, int failedTests, int? totalFiles)
    {
        var done = totalFiles is { } t
            ? $"{filesDone} из {t} {(t % 10 == 1 && t % 100 != 11 ? "файла" : "файлов")}"
            : $"{filesDone} {FilesWord(filesDone)}";
        return failedTests > 0 ? $"{done} · упало {failedTests}" : done;
    }

    private static string AssembliesWord(int n)
    {
        int m10 = n % 10, m100 = n % 100;
        if (m10 == 1 && m100 != 11) return "сборка";
        if (m10 is >= 2 and <= 4 && m100 is < 10 or >= 20) return "сборки";
        return "сборок";
    }

    private static string FilesWord(int n)
    {
        int m10 = n % 10, m100 = n % 100;
        if (m10 == 1 && m100 != 11) return "файл";
        if (m10 is >= 2 and <= 4 && m100 is < 10 or >= 20) return "файла";
        return "файлов";
    }

    // Процент по настоящим тестам из --list-tests; потолок 99 — «готово» говорит только
    // результат вызова. Нет общего числа — процента нет (полоса неопределённая). Перевалили общее
    // число — тоже нет: оно оказалось заниженным (см. ProgressLabel), и полоса на 99% врала бы
    public const int MaxProgressPercent = 99;

    public static int? ProgressPercent(int done, int? total) =>
        total is > 0 && done <= total ? Math.Min(MaxProgressPercent, (int)(done * 100L / total.Value)) : null;

    public static string SummaryLine(TestReport s)
    {
        var parts = new List<string>();
        if (s.Failed > 0) parts.Add($"FAILED {s.Failed}");
        parts.Add($"passed {s.Passed}");
        if (s.Skipped > 0) parts.Add($"skipped {s.Skipped}");
        var time = s.Duration is { } d ? $" за {FormatElapsed(d)}" : "";
        return $"{(s.Failed > 0 ? "" : "OK: ")}{string.Join(", ", parts)} из {s.Total}{time} — {s.Assembly}";
    }

    private static string TotalLine(IReadOnlyList<TestReport> reports)
    {
        var failed = reports.Sum(r => r.Failed);
        var skipped = reports.Sum(r => r.Skipped);
        var head = failed > 0 ? $"FAILED {failed}, " : "";
        var skip = skipped > 0 ? $", skipped {skipped}" : "";
        return $"Итого: {head}passed {reports.Sum(r => r.Passed)}{skip} из {reports.Sum(r => r.Total)} "
            + $"в {reports.Count} сборках";
    }

    private static void AddFailures(Budget text, List<TestFailure> failures, TestRunResult r)
    {
        text.Add($"\n\nУпавшие ({failures.Count}):");
        text.Reserve(MoreReserve);
        var shown = 0;
        foreach (var f in failures)
        {
            var name = ClampLine(f.Name);
            var block = new StringBuilder($"\n\n{shown + 1}. {name}");
            if (f.Message.Length > 0) block.Append('\n').Append(Indent(f.Message));
            if (f.Stack.Length > 0) block.Append('\n').Append(Indent(f.Stack));
            if (!text.Add(block.ToString()))
            {
                // Первый упавший не влез целиком — хотя бы его имя, чтобы итог не остался без
                // единого упавшего
                if (shown == 0 && text.Add($"\n\n1. {name}\n   (сообщение не влезло — смотри отчёт)")) shown++;
                break;
            }
            shown++;
        }
        text.Release(MoreReserve);
        if (shown < failures.Count)
            text.Add($"\n\n…и ещё {failures.Count - shown} упавших"
                + (r.ArtifactsPath is { } path ? $" — полный список в {path}/{ReportGlob(r.Kind)}" : "") + ".");
    }

    // Хвост нужен, когда без него исход непонятен: обрыв без TRX или ненулевой код без упавших
    private static bool NeedsTail(TestRunResult r) =>
        r.Tail.Count > 0 && r.BuildErrors.Count == 0 && !r.NeverStarted
        && (r.Reports.Count == 0 || (r.ExitCode is not 0 && r.Reports.All(rep => rep.Failed == 0)));

    // Хвост добирается с конца: итог dotnet стоит последним
    private static void AddTail(Budget text, IReadOnlyList<string> tail)
    {
        const string title = "\n\nПоследние строки вывода:\n";
        var lines = new List<string>();
        var room = text.Left - Bytes(title);
        for (var i = tail.Count - 1; i >= 0; i--)
        {
            var line = StripAnsi(tail[i]);
            var cost = Bytes(line) + 1;
            if (cost > room) break;
            room -= cost;
            lines.Add(line);
        }
        if (lines.Count == 0) return;
        lines.Reverse();
        text.Add(title + string.Join('\n', lines));
    }

    // Отступ под номером упавшего; каждая строка — не шире MaxLineChars
    private static string Indent(string text) =>
        string.Join('\n', text.Split('\n').Select(line => "   " + ClampLine(line)));

    // Сюда сходятся все строки итога из отчётов и консоли — escape-коды вычищаются здесь для
    // всех видов прогона, а не только в читателе Playwright
    private static string ClampLine(string line)
    {
        line = StripAnsi(line);
        if (line.Length <= MaxLineChars) return line;
        // Не рвём суррогатную пару на границе
        var cut = char.IsHighSurrogate(line[MaxLineChars - 1]) ? MaxLineChars - 1 : MaxLineChars;
        return line[..cut] + $"… (+{line.Length - cut} симв.)";
    }

    public static string FormatElapsed(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}:{span.Seconds:D2}" : $"{span.TotalSeconds:F1} с";

    private static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);

    // Последний рубеж потолка: режем по байтам, не разрывая суррогатную пару
    private static string Clamp(string text, int maxBytes)
    {
        if (Bytes(text) <= maxBytes) return text;
        const string cut = "\n…[обрезано]";
        var room = Math.Max(0, maxBytes - Bytes(cut));
        var sb = new StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (used + size > room) break;
            used += size;
            sb.Append(rune.ToString());
        }
        return sb.Append(cut).ToString();
    }

    // Текст под байтовым бюджетом: Add не дописывает кусок, который не влезает целиком
    private sealed class Budget(int limit)
    {
        private readonly StringBuilder _sb = new();
        private int _used;
        private int _reserved;

        public int Left => Math.Max(0, limit - _reserved - _used);

        public bool Add(string piece)
        {
            var size = Bytes(piece);
            if (size > Left) return false;
            _sb.Append(piece);
            _used += size;
            return true;
        }

        public void Reserve(int bytes) => _reserved += bytes;
        public void Release(int bytes) => _reserved -= bytes;

        public override string ToString() => _sb.ToString();
    }
}
