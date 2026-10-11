using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace ClaudeHomeServer.Services.TestRuns;

// Чтение TRX (dotnet test пишет TRX на каждый тестовый проект) (VSTest results). Чистая функция над текстом XML, без IO.
// Счётчики берутся из самих UnitTestResult, а не из ResultSummary/Counters: у xunit пропущенный
// тест там не учтён вовсе (notExecuted="0" при одном Skipped — видно на фикстуре).
public static class TrxSummaryReader
{
    public const int MessageLines = 20;
    public const int StackFrames = 5;

    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static TestReport Read(string xml)
    {
        // DTD в TRX не бывает; запрет закрывает XXE на случай подложенного файла
        using var reader = XmlReader.Create(new StringReader(xml),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new FormatException("пустой TRX");

        int passed = 0, failed = 0, skipped = 0;
        var failures = new List<TestFailure>();
        foreach (var result in root.Descendants(Ns + "UnitTestResult"))
        {
            switch ((string?)result.Attribute("outcome"))
            {
                case "Passed":
                    passed++;
                    break;
                case "Failed" or "Error" or "Timeout" or "Aborted":
                    failed++;
                    var error = result.Element(Ns + "Output")?.Element(Ns + "ErrorInfo");
                    failures.Add(new TestFailure(
                        (string?)result.Attribute("testName") ?? "?",
                        FirstLines((string?)error?.Element(Ns + "Message"), MessageLines),
                        FirstLines((string?)error?.Element(Ns + "StackTrace"), StackFrames)));
                    break;
                default:
                    // NotExecuted, Inconclusive и прочие — тест не выполнился
                    skipped++;
                    break;
            }
        }

        return new TestReport(AssemblyName(root), passed + failed + skipped, passed, failed, skipped,
            Duration(root), failures);
    }

    // Имя сборки — из codeBase первого теста (там регистр сохранён, в storage — нет). У сборки,
    // где фильтр ничего не нашёл, тестов в TRX нет вовсе: имя тогда — из предупреждения vstest
    // «No test matches the given testcase filter `…` in <путь>.dll» (снято с живого прогона),
    // а без него — из строки xunit «Discovering: <сборка>» в выводе
    private static string AssemblyName(XElement root)
    {
        var method = root.Descendants(Ns + "TestMethod").FirstOrDefault();
        var path = (string?)method?.Attribute("codeBase")
            ?? (string?)root.Descendants(Ns + "UnitTest").FirstOrDefault()?.Attribute("storage")
            ?? root.Descendants(Ns + "RunInfo").Select(info => (string?)info.Element(Ns + "Text"))
                .Select(NoMatchPath).FirstOrDefault(p => p is not null);
        if (string.IsNullOrEmpty(path))
            return DiscoveredAssembly((string?)root.Descendants(Ns + "StdOut").FirstOrDefault()) ?? "?";
        // Путь бывает и виндовым, и линуксовым: отрезаем по обоим разделителям
        var cut = path.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? path[(cut + 1)..] : path;
    }

    // Путь сборки из «… filter `X` in <путь>»: фильтр кончается обратной кавычкой, путь — последним
    private static string? NoMatchPath(string? text)
    {
        const string marker = "` in ";
        var at = text?.LastIndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (at < 0) return null;
        var path = text![(at + marker.Length)..].Trim();
        return path.Length > 0 ? path : null;
    }

    private static string? DiscoveredAssembly(string? stdOut)
    {
        if (stdOut is null) return null;
        const string marker = "Discovering:";
        foreach (var line in stdOut.Split('\n'))
        {
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;
            var name = line[(at + marker.Length)..].Trim();
            if (name.Length > 0) return name + ".dll";
        }
        return null;
    }

    private static TimeSpan? Duration(XElement root)
    {
        var times = root.Element(Ns + "Times");
        if (!DateTimeOffset.TryParse((string?)times?.Attribute("start"), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start)
            || !DateTimeOffset.TryParse((string?)times?.Attribute("finish"), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var finish))
            return null;
        return finish >= start ? finish - start : null;
    }

    // Первые max строк текста; общая для всех читателей отчётов (TRX, vitest, Playwright)
    internal static string FirstLines(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = lines.Take(max).Select(l => l.TrimEnd());
        var joined = string.Join('\n', kept);
        return lines.Length > max ? joined + $"\n… ещё {lines.Length - max} строк" : joined;
    }
}
