using System.Text.Json;

namespace ClaudeHomeServer.Services.TestRuns;

// Чтение JSON-отчёта Playwright (`--reporter=…,json`, PLAYWRIGHT_JSON_OUTPUT_NAME). Чистая
// функция над текстом, без IO. Отчёт — дерево suites → specs → tests → results; итог теста —
// tests[].status: expected (прошёл), unexpected (упал), flaky (упал, потом прошёл повтором —
// считаем прошедшим, как и сам Playwright), skipped. Глобальные ошибки (конфиг, webServer,
// падение воркера) лежат в errors верхнего уровня — это тоже падение прогона.
public static class PlaywrightJsonReader
{
    public static TestReport Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tally = new Tally();
        if (root.TryGetProperty("suites", out var suites) && suites.ValueKind == JsonValueKind.Array)
            foreach (var suite in suites.EnumerateArray()) Walk(suite, [], tally);

        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            foreach (var error in errors.EnumerateArray())
            {
                tally.Failed++;
                var (message, stack) = ErrorText(error);
                tally.Failures.Add(new TestFailure("ошибка прогона (вне тестов)", message, stack));
            }

        TimeSpan? duration = root.TryGetProperty("stats", out var stats)
            && stats.TryGetProperty("duration", out var d) && d.TryGetDouble(out var ms) && ms >= 0
                ? TimeSpan.FromMilliseconds(ms) : null;
        return new TestReport("playwright", tally.Passed + tally.Failed + tally.Skipped, tally.Passed, tally.Failed,
            tally.Skipped, duration, tally.Failures);
    }

    private sealed class Tally
    {
        public int Passed, Failed, Skipped;
        public List<TestFailure> Failures { get; } = [];
    }

    // Название — файл › describe › тест, как в выводе list-репортера
    private static void Walk(JsonElement suite, List<string> path, Tally tally)
    {
        var here = new List<string>(path);
        if (Str(suite, "title") is { Length: > 0 } title) here.Add(title);

        if (suite.TryGetProperty("specs", out var specs) && specs.ValueKind == JsonValueKind.Array)
            foreach (var spec in specs.EnumerateArray())
            {
                if (!spec.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Array) continue;
                foreach (var test in tests.EnumerateArray())
                {
                    switch (Str(test, "status"))
                    {
                        case "expected" or "flaky":
                            tally.Passed++;
                            break;
                        case "skipped":
                            tally.Skipped++;
                            break;
                        default:
                            tally.Failed++;
                            var name = string.Join(" › ", [.. here, Str(spec, "title") ?? "?"]);
                            if (Str(test, "projectName") is { Length: > 0 } project) name = $"[{project}] {name}";
                            if (spec.TryGetProperty("line", out var line) && line.TryGetInt32(out var l))
                                name += $" (строка {l})";
                            tally.Failures.Add(LastError(test, name));
                            break;
                    }
                }
            }

        if (suite.TryGetProperty("suites", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) Walk(child, here, tally);
    }

    // Ошибка последней попытки: она и решила исход теста
    private static TestFailure LastError(JsonElement test, string name)
    {
        if (test.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            && results.GetArrayLength() > 0)
        {
            var last = results[results.GetArrayLength() - 1];
            if (last.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var (message, stack) = ErrorText(error);
                return new TestFailure(name, message, stack);
            }
            if (Str(last, "status") is { } status)
                return new TestFailure(name, $"Статус попытки: {status}", "");
        }
        return new TestFailure(name, "", "");
    }

    // Цвета терминала Playwright пишет прямо в message/stack («\u001b[31m»), FORCE_COLOR=0 их
    // не убирает — вычищаем до разбора, иначе коды едут модели и на карточку
    private static (string Message, string Stack) ErrorText(JsonElement error)
    {
        var message = TrxSummaryReader.FirstLines(
            TestRunSummaryFormatter.StripAnsi(Str(error, "message") ?? ""), TrxSummaryReader.MessageLines);
        // Стек Playwright начинается с той же строки сообщения — берём только кадры «at …»
        var (_, stack) = VitestJsonReader.SplitStack(TestRunSummaryFormatter.StripAnsi(Str(error, "stack") ?? ""));
        return (message, stack);
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
