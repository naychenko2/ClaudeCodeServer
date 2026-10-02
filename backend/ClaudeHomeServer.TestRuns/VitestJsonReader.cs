using System.Text.Json;

namespace ClaudeHomeServer.Services.TestRuns;

// Чтение JSON-отчёта vitest (`--reporter=json --outputFile.json=…`, формат как у jest). Чистая
// функция над текстом, без IO. Счётчики — по самим assertionResults, а упавший при загрузке
// файл (testResults[].status = failed, assertionResults пуст, причина в message) считается
// отдельным падением: тестов в нём нет, но прогон из-за него красный.
public static class VitestJsonReader
{
    public static TestReport Read(string json, string? root = null)
    {
        using var doc = JsonDocument.Parse(json);
        var rootEl = doc.RootElement;
        int passed = 0, failed = 0, skipped = 0;
        var failures = new List<TestFailure>();

        if (rootEl.TryGetProperty("testResults", out var files) && files.ValueKind == JsonValueKind.Array)
            foreach (var file in files.EnumerateArray())
            {
                var path = Relative(Str(file, "name") ?? "?", root);
                var asserts = file.TryGetProperty("assertionResults", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a : default;
                var count = 0;
                if (asserts.ValueKind == JsonValueKind.Array)
                    foreach (var test in asserts.EnumerateArray())
                    {
                        count++;
                        switch (Str(test, "status"))
                        {
                            case "passed":
                                passed++;
                                break;
                            case "failed":
                                failed++;
                                var messages = test.TryGetProperty("failureMessages", out var m) && m.ValueKind == JsonValueKind.Array
                                    ? m.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
                                var (message, stack) = SplitStack(string.Join('\n', messages));
                                failures.Add(new TestFailure($"{path} > {Str(test, "fullName") ?? Str(test, "title") ?? "?"}",
                                    message, stack));
                                break;
                            default:
                                // skipped, pending, todo
                                skipped++;
                                break;
                        }
                    }
                if (count == 0 && Str(file, "status") == "failed")
                {
                    failed++;
                    var (message, stack) = SplitStack(Str(file, "message") ?? "");
                    failures.Add(new TestFailure($"{path} (файл не загрузился)", message, stack));
                }
            }

        TimeSpan? duration = null;
        // Время — мс от эпохи; у файлов endTime дробный («1790936152869.24»)
        if (rootEl.TryGetProperty("startTime", out var st) && st.TryGetDouble(out var start)
            && files.ValueKind == JsonValueKind.Array)
        {
            var end = files.EnumerateArray()
                .Select(f => f.TryGetProperty("endTime", out var e) && e.TryGetDouble(out var v) ? v : 0).DefaultIfEmpty(0).Max();
            if (end >= start) duration = TimeSpan.FromMilliseconds(end - start);
        }
        return new TestReport("vitest", passed + failed + skipped, passed, failed, skipped, duration, failures);
    }

    // Сообщение — до первой строки стека «    at …»; стек — первые кадры
    internal static (string Message, string Stack) SplitStack(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var at = Array.FindIndex(lines, l => l.TrimStart().StartsWith("at ", StringComparison.Ordinal));
        if (at < 0) return (TrxSummaryReader.FirstLines(text, TrxSummaryReader.MessageLines), "");
        return (TrxSummaryReader.FirstLines(string.Join('\n', lines[..at]), TrxSummaryReader.MessageLines),
            TrxSummaryReader.FirstLines(string.Join('\n', lines[at..]), TrxSummaryReader.StackFrames));
    }

    // Абсолютный путь файла (хостовый или из песочницы) — относительно каталога прогона
    internal static string Relative(string path, string? root)
    {
        var norm = path.Replace('\\', '/');
        if (root is null) return norm;
        var prefix = root.Replace('\\', '/').TrimEnd('/') + "/";
        return norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? norm[prefix.Length..] : norm;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
