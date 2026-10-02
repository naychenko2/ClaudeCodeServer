using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.TestRuns;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.TestRuns;

/// <summary>
/// Чистые разборщики run_tests на НАСТОЯЩЕМ выводе dotnet test (.NET 10 SDK, vstest, xunit 2):
/// консоль normal, --list-tests по решению из двух сборок, TRX с упавшим и пропущенным, ошибка
/// сборки. Фикстуры сняты с живого прогона — смена формата (MTP, xunit v3) уронит эти тесты
/// первыми. Плюс итог модели под потолком 10 КБ и секция подсказки в промпте.
/// </summary>
public class TestRunParsersTests
{
    private static string[] Lines(string fixture) => File.ReadAllLines(TestRunServiceTests.Fixture(fixture));

    [Fact]
    public void СписокТестов_СуммаПоВсемСборкам()
    {
        // Шапки двух сборок идут подряд, имена — общим блоком после них
        VsTestConsoleParser.CountListedTests(Lines("list-tests.txt")).Should().Be(7);
    }

    [Fact]
    public void СписокТестов_БезШапки_Null()
    {
        VsTestConsoleParser.CountListedTests(["MSBUILD : error MSB1003: Specify a project"]).Should().BeNull();
    }

    [Fact]
    public void Консоль_ИсходыТестов_БезСтрокXunitИСводки()
    {
        var outcomes = Lines("console-normal.txt").Select(VsTestConsoleParser.ParseOutcome)
            .Where(o => o is not null).Select(o => o!.Value).ToList();

        outcomes.Count(o => o.Outcome == TestLineOutcome.Passed).Should().Be(5);
        outcomes.Should().ContainSingle(o => o.Outcome == TestLineOutcome.Failed)
            .Which.Name.Should().Be("A.Tests.CalcTests.Fails", "длительность «[123 ms]» в имя не попадает");
        outcomes.Count(o => o.Outcome == TestLineOutcome.Skipped).Should().Be(1);
    }

    [Theory]
    [InlineData("[xUnit.net 00:00:00.26]     A.Tests.CalcTests.Fails [FAIL]")]
    [InlineData("     Passed: 3")]
    [InlineData("    Skipped: 1")]
    [InlineData("Passed!  - Failed: 0, Passed: 2")]
    public void Консоль_НеСтрокиИсхода(string line)
    {
        VsTestConsoleParser.ParseOutcome(line).Should().BeNull();
    }

    [Fact]
    public void ОшибкиСборки_БезПовторовИПодписиПроекта()
    {
        var errors = VsTestConsoleParser.BuildErrors(Lines("build-error.txt"));

        errors.Should().HaveCount(16, "MSBuild печатает каждую ошибку дважды");
        errors[0].Should().Contain("error CS0246").And.EndWith("reference?)", "подпись « [….csproj]» срезана");
        errors.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ОшибкиСборки_ПервыеДвадцать()
    {
        var lines = Enumerable.Range(1, 30).Select(i => $"src/F.cs({i},1): error CS1002: ; expected [src/App.csproj]");

        VsTestConsoleParser.BuildErrors(lines).Should().HaveCount(20);
    }

    [Fact]
    public void Trx_СводкаИУпавший()
    {
        var summary = TrxSummaryReader.Read(File.ReadAllText(TestRunServiceTests.Fixture("results-failed.trx")));

        summary.Assembly.Should().Be("A.Tests.dll");
        summary.Total.Should().Be(5);
        summary.Passed.Should().Be(3);
        summary.Failed.Should().Be(1);
        summary.Skipped.Should().Be(1, "у xunit пропущенный не учтён в Counters — считаем по результатам");
        summary.Duration.Should().NotBeNull();
        var failure = summary.Failures.Should().ContainSingle().Subject;
        failure.Name.Should().Be("A.Tests.CalcTests.Fails");
        failure.Message.Should().Contain("Expected: \"ожидалось\"");
        failure.Stack.Should().StartWith("   at A.Tests.Helper.Boom()");
    }

    [Fact]
    public void Trx_СтекПервыеПятьКадров_СообщениеДвадцатьСтрок()
    {
        var message = string.Join("\r\n", Enumerable.Range(1, 30).Select(i => $"строка {i}"));
        var stack = string.Join("\r\n", Enumerable.Range(1, 12).Select(i => $"   at Frame{i}()"));
        var xml = $"""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="X.Y.Z" outcome="Failed">
                  <Output><ErrorInfo><Message>{message}</Message><StackTrace>{stack}</StackTrace></ErrorInfo></Output>
                </UnitTestResult>
              </Results>
              <TestDefinitions><UnitTest storage="/src/x.tests.dll"><TestMethod codeBase="/src/X.Tests.dll" /></UnitTest></TestDefinitions>
            </TestRun>
            """;

        var failure = TrxSummaryReader.Read(xml).Failures.Single();

        failure.Stack.Split('\n').Should().HaveCount(TrxSummaryReader.StackFrames + 1).And.Contain("   at Frame5()")
            .And.NotContain("   at Frame6()");
        failure.Message.Split('\n').Should().HaveCount(TrxSummaryReader.MessageLines + 1);
        failure.Message.Should().EndWith("… ещё 10 строк");
        TrxSummaryReader.Read(xml).Assembly.Should().Be("X.Tests.dll", "линуксовый путь режется по /");
    }

    [Fact]
    public void Trx_СборкаБезПодходящихТестов_ИмяИзПредупрежденияVstest()
    {
        // Кира: в итоге по решению вместо имени стоял «?» — у пустой сборки в TRX нет тестов
        var summary = TrxSummaryReader.Read(File.ReadAllText(TestRunServiceTests.Fixture("results-empty.trx")));

        summary.Assembly.Should().Be("B.Tests.dll");
        summary.Total.Should().Be(0);
        // Без вывода xunit имя всё равно есть — из пути в предупреждении vstest
        var xml = File.ReadAllText(TestRunServiceTests.Fixture("results-empty.trx"));
        TrxSummaryReader.Read(System.Text.RegularExpressions.Regex.Replace(xml, "<Output>[\\s\\S]*</Output>", ""))
            .Assembly.Should().Be("B.Tests.dll");
    }

    [Fact]
    public void Trx_СборкаБезПодходящихТестов_БезПредупреждения_ИмяИзВыводаXunit()
    {
        var xml = File.ReadAllText(TestRunServiceTests.Fixture("results-empty.trx"));
        var withoutRunInfo = System.Text.RegularExpressions.Regex.Replace(xml, "<RunInfos>[\\s\\S]*</RunInfos>", "");

        TrxSummaryReader.Read(withoutRunInfo).Assembly.Should().Be("B.Tests.dll");
    }

    [Fact]
    public void Итог_СборкиБезТестов_СвёрнутыВОднуСтроку()
    {
        // Кира: прогон по решению давал 22 строки «OK: passed 0 из 0 — ?» перед нужной
        var empty = TrxSummaryReader.Read(File.ReadAllText(TestRunServiceTests.Fixture("results-empty.trx")));
        var real = TrxSummaryReader.Read(File.ReadAllText(TestRunServiceTests.Fixture("results-passed.trx")));
        var result = Completed(0, [.. Enumerable.Repeat(empty, 22), real]);

        var text = TestRunSummaryFormatter.Format(result, 10240, 540);

        text.Should().Contain(TestRunSummaryFormatter.SummaryLine(real))
            .And.Contain("…ещё 22 сборки без подходящих тестов.")
            .And.NotContain("из 0").And.NotContain("— ?").And.NotContain("Итого");
    }

    [Fact]
    public void Итог_ВсеСборкиБезТестов_ОднаСтрока()
    {
        var empty = new TestReport("B.Tests.dll", 0, 0, 0, 0, null, []);

        TestRunSummaryFormatter.Format(Completed(0, empty, empty, empty), 10240, 540)
            .Should().Contain("Ни в одной из 3 сборок нет тестов под фильтр.").And.NotContain("из 0");
        TestRunSummaryFormatter.Format(Completed(0, empty), 10240, 540)
            .Should().Contain("passed 0 из 0 — B.Tests.dll", "одна сборка — обычная строка с именем");
    }

    [Fact]
    public void Trx_DtdЗапрещён()
    {
        var act = () => TrxSummaryReader.Read("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><TestRun>&e;</TestRun>");

        act.Should().Throw<System.Xml.XmlException>();
    }

    [Theory]
    [InlineData(0, 7, 0)]
    [InlineData(412, 7951, 5)]
    [InlineData(7950, 7951, 99)]
    [InlineData(7951, 7951, 99)]
    [InlineData(8000, 7951, 99)]
    public void Процент_ПотолокДевяностоДевять(int done, int total, int expected)
    {
        TestRunSummaryFormatter.ProgressPercent(done, total).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void Процент_БезОбщегоЧисла_Null(int? total)
    {
        TestRunSummaryFormatter.ProgressPercent(3, total).Should().BeNull();
    }

    [Fact]
    public void Подпись_СчётчикИУпавшие()
    {
        TestRunSummaryFormatter.ProgressLabel(new TestCounts(410, 2, 0), 7951).Should().Be("412 из 7951 · упало 2");
        TestRunSummaryFormatter.ProgressLabel(new TestCounts(5, 0, 0), null).Should().Be("5");
    }

    private static TestRunResult Completed(int exit, params TestReport[] reports) =>
        new(null, exit, false, false, TimeSpan.FromSeconds(252), [])
        {
            Reports = reports,
            ArtifactsPath = ".cc-attachments/test-runs/r1",
        };

    [Fact]
    public void Итог_СтрокаСводкиПоСборке()
    {
        var report = new TestReport("App.Tests.dll", 7963, 7948, 3, 12, TimeSpan.FromSeconds(252), []);

        TestRunSummaryFormatter.SummaryLine(report).Should().Be("FAILED 3, passed 7948, skipped 12 из 7963 за 4:12 — App.Tests.dll");
    }

    [Fact]
    public void Итог_ВПотолке10Кб_ОстальныеУпавшие_ИЕщёN()
    {
        var failures = Enumerable.Range(1, 500)
            .Select(i => new TestFailure($"App.Tests.Suite.Case{i}", $"Assert.Equal() Failure: ожидалось {i}\nподробности " + new string('ж', 200),
                "   at App.Tests.Suite.Case()\n   at System.Reflection.Invoke()"))
            .ToList();
        var result = Completed(1, new TestReport("App.Tests.dll", 600, 100, 500, 0, null, failures));

        var text = TestRunSummaryFormatter.Format(result, 10240, 540);

        Encoding.UTF8.GetByteCount(text).Should().BeLessThanOrEqualTo(10240);
        var shown = failures.Count(f => text.Contains(f.Name + "\n", StringComparison.Ordinal));
        shown.Should().BeGreaterThan(0);
        text.Should().Contain($"…и ещё {500 - shown} упавших — полный список в .cc-attachments/test-runs/r1/*.trx")
            .And.EndWith("*.trx — результаты по сборкам).", "строка про артефакты зарезервирована и не режется");
    }

    [Fact]
    public void Итог_ВсеПрошли_БезХвоста()
    {
        var result = Completed(0, new TestReport("App.Tests.dll", 2, 2, 0, 0, TimeSpan.FromSeconds(1), [])) with
        {
            Tail = ["шумная строка"],
        };

        var text = TestRunSummaryFormatter.Format(result, 10240, 540);

        text.Should().Contain("все тесты прошли").And.Contain("OK: passed 2 из 2").And.NotContain("шумная строка");
    }

    [Fact]
    public void Итог_КодНеНольБезУпавших_ПоказываетХвост()
    {
        var result = Completed(1) with { Tail = ["Testhost process exited with error"] };

        TestRunSummaryFormatter.Format(result, 10240, 540).Should().Contain("Testhost process exited");
    }

    [Fact]
    public void Итог_ОбрывНаПрогоне_СчётчикИУпавшиеПоКонсоли()
    {
        var result = new TestRunResult(null, null, false, true, TimeSpan.FromSeconds(540), [])
        {
            Total = 7951,
            Counts = new TestCounts(410, 2, 0),
            FailedFromConsole = ["A.B.First", "A.B.Second"],
        };

        var text = TestRunSummaryFormatter.Format(result, 10240, 540);

        text.Should().Contain("на 412 из 7951 · упало 2").And.Contain("filter").And.Contain("A.B.Second");
    }

    // --- Раздел 0 ревью этапа 2 ---

    [Fact]
    public void Консоль_ДлинаСтрокиСверхПотолка_НеРазбирается()
    {
        // L-a: строка исхода длиннее потолка — не строка исхода; разбор не тратит на неё время
        var huge = "  Passed " + new string('x', VsTestConsoleParser.MaxOutcomeLineLength) + " [1 ms]";
        var ok = "  Passed " + new string('x', 100) + " [1 ms]";

        VsTestConsoleParser.ParseOutcome(huge).Should().BeNull();
        VsTestConsoleParser.ParseOutcome(ok)!.Value.Name.Should().Be(new string('x', 100));
    }

    [Fact]
    public void Консоль_СкобкиВИмениТеста_СрезаетсяТолькоДлительность()
    {
        var outcome = VsTestConsoleParser.ParseOutcome("  Failed A.T.Case(x: \"[a] [b]\") [12 ms]")!.Value;

        outcome.Outcome.Should().Be(TestLineOutcome.Failed);
        outcome.Name.Should().Be("A.T.Case(x: \"[a] [b]\")");
        VsTestConsoleParser.ParseOutcome("  Passed A.T.NoDuration")!.Value.Name.Should().Be("A.T.NoDuration");
    }

    [Fact]
    public void СписокТестов_ПотоковыйСчётчик_РавенПакетному()
    {
        // L-c: строки --list-tests не копятся — счёт идёт потоком
        var counter = new VsTestConsoleParser.ListCounter();
        counter.Count.Should().BeNull("до шапки список не начат");
        foreach (var line in Lines("list-tests.txt")) counter.Feed(line);

        counter.Count.Should().Be(7);
    }

    [Fact]
    public void Итог_ОгромныйПервыйУпавший_НеВытесняетОстальные()
    {
        // L-b: сообщение-простыня в одну строку раньше съедало весь бюджет — итог оставался без
        // единого упавшего. Теперь строки режутся по ширине, и упавших видно несколько
        var failures = new[]
        {
            new TestFailure("App.Tests.Huge", "Expected: " + new string('ж', 50_000), ""),
            new TestFailure("App.Tests.Second", "коротко", ""),
            new TestFailure("App.Tests.Third", "коротко", ""),
        };
        var result = Completed(1, new TestReport("App.Tests.dll", 3, 0, 3, 0, null, failures));

        var text = TestRunSummaryFormatter.Format(result, 10240, 540);

        Encoding.UTF8.GetByteCount(text).Should().BeLessThanOrEqualTo(10240);
        text.Should().Contain("1. App.Tests.Huge").And.Contain("App.Tests.Second").And.Contain("App.Tests.Third")
            .And.Contain("симв.)", "обрезанная строка помечена");
    }

    [Fact]
    public void Итог_ОшибкаСборки_НеУпоминаетTrx()
    {
        // Кира, косметика: TRX на ошибке сборки не бывает — и упоминать его нельзя
        var result = new TestRunResult(null, 1, false, false, TimeSpan.FromSeconds(3), [])
        {
            Phase = TestRunPhase.Build,
            BuildErrors = ["src/F.cs(1,1): error CS1002: ; expected"],
            ArtifactsPath = ".cc-attachments/test-runs/r1",
        };

        var text = TestRunSummaryFormatter.Format(result, 10240, 540);

        text.Should().Contain("сборка не прошла").And.Contain("console.log").And.NotContain("trx");
    }

    [Fact]
    public void Итог_ОтчётБольшеПотолка_Пометка()
    {
        var result = Completed(1) with { OversizedReports = ["results_A.trx"] };

        TestRunSummaryFormatter.Format(result, 10240, 540).Should().Contain("results_A.trx").And.Contain("не разбирал");
    }

    // --- vitest ---

    [Fact]
    public void Vitest_СтрокиФайлов_ИзЖивогоВывода()
    {
        var files = Lines("vitest-default.txt").Select(VitestReporterParser.ParseFileLine)
            .Where(f => f is not null).Select(f => f!.Value).ToList();

        files.Should().HaveCount(2, "строки тестов под файлом, сводка «Test Files» и FAIL-блоки — не строки файлов");
        files[0].Should().Be(new VitestFileLine("src/__vtprobe__/broken.test.ts", 0, 0, 0, FileFailed: true),
            "«(0 test)» у ❯ — файл не загрузился");
        files[1].Should().Be(new VitestFileLine("src/__vtprobe__/fail.test.ts", 3, 1, 1, FileFailed: true));
    }

    [Theory]
    [InlineData(" ✓ src/lib/__tests__/agentTail.test.ts (17 tests) 8ms", 17, false)]
    [InlineData(" ✓ src/lib/one.test.ts (1 test) 2ms", 1, false)]
    [InlineData(" ↓ src/lib/skipped.test.ts (5 tests | 5 skipped)", 5, false)]
    [InlineData(" ✓ src/lib/slow.test.ts (38 tests) 1.52s", 38, false)]
    public void Vitest_СтрокаФайла_Варианты(string line, int tests, bool failed)
    {
        var file = VitestReporterParser.ParseFileLine(line)!.Value;

        file.Tests.Should().Be(tests);
        file.FileFailed.Should().Be(failed);
    }

    [Theory]
    [InlineData("     ✓ проходит 1ms")]
    [InlineData(" Test Files  1 failed | 2 passed (3)")]
    [InlineData("      Tests  1 failed | 56 passed | 1 skipped (58)")]
    [InlineData(" FAIL  src/__vtprobe__/fail.test.ts > проба > падает")]
    public void Vitest_НеСтрокиФайлов(string line)
    {
        VitestReporterParser.ParseFileLine(line).Should().BeNull();
    }

    [Fact]
    public void Vitest_СписокФайлов_БезШума()
    {
        Lines("vitest-list.txt").Count(VitestReporterParser.IsListedFile).Should().Be(12);
        VitestReporterParser.IsListedFile("(node:1) Warning: something.ts").Should().BeFalse();
        VitestReporterParser.IsListedFile("").Should().BeFalse();
    }

    [Fact]
    public void Vitest_JsonОтчёт_УпавшийТестИНеЗагрузившийсяФайл()
    {
        var report = VitestJsonReader.Read(File.ReadAllText(TestRunServiceTests.Fixture("vitest-report.json")),
            "C:/GIT/ClaudeCodeServer/frontend");

        report.Assembly.Should().Be("vitest");
        report.Total.Should().Be(4);
        report.Passed.Should().Be(1);
        report.Failed.Should().Be(2, "упавший тест и файл, который не загрузился");
        report.Skipped.Should().Be(1);
        report.Failures.Select(f => f.Name).Should().Equal(
            "src/__vtprobe__/broken.test.ts (файл не загрузился)",
            "src/__vtprobe__/fail.test.ts > проба падает");
        report.Failures[0].Message.Should().Contain("Cannot find module");
        report.Failures[1].Message.Should().StartWith("AssertionError: expected { a: 1 }");
        report.Failures[1].Stack.Should().StartWith("    at ").And.Contain("fail.test.ts:4:41");
        report.Failures[1].Stack.Split('\n').Length.Should().BeLessThanOrEqualTo(TrxSummaryReader.StackFrames + 1);
    }

    [Fact]
    public void Vitest_JsonОтчёт_СмешанныйПрогон()
    {
        var report = VitestJsonReader.Read(File.ReadAllText(TestRunServiceTests.Fixture("vitest-report-mixed.json")));

        report.Total.Should().Be(58);
        report.Passed.Should().Be(56);
        report.Failed.Should().Be(1);
        report.Skipped.Should().Be(1);
        report.Duration.Should().NotBeNull();
    }

    [Theory]
    [InlineData(87, 2, 171, "87 из 171 файла · упало 2")]
    [InlineData(5, 0, 170, "5 из 170 файлов")]
    [InlineData(0, 0, 11, "0 из 11 файлов")]
    [InlineData(1, 0, null, "1 файл")]
    [InlineData(3, 1, null, "3 файла · упало 1")]
    [InlineData(12, 0, null, "12 файлов")]
    public void Vitest_ПодписьПрогрессаПоФайлам(int done, int failed, int? total, string expected)
    {
        TestRunSummaryFormatter.FilesProgressLabel(done, failed, total).Should().Be(expected);
    }

    // --- Playwright ---

    [Fact]
    public void Playwright_ЧислоТестовИИсходы_ИзЖивогоВывода()
    {
        var lines = Lines("playwright-list.txt");

        lines.Select(PlaywrightListParser.ParseTotal).Where(t => t is not null).Should().Equal(4);
        var outcomes = lines.Select(PlaywrightListParser.ParseOutcome).Where(o => o is not null).Select(o => o!.Value).ToList();
        outcomes.Select(o => o.Outcome).Should().Equal(TestLineOutcome.Passed, TestLineOutcome.Failed,
            TestLineOutcome.Skipped, TestLineOutcome.Passed);
        outcomes[1].Title.Should().Be("[chromium] › e2e\\zz-probe.spec.ts:4:3 › проба › падает", "длительность срезана");
        outcomes.Select(o => o.Index).Should().Equal(1, 2, 3, 4);
    }

    [Theory]
    [InlineData("    - Expected  - 1")]
    [InlineData("    -   \"a\": 2,")]
    [InlineData("  1 failed")]
    [InlineData("  2 passed (1.2s)")]
    [InlineData("    -   1 item")]
    public void Playwright_НеСтрокиИсхода(string line)
    {
        PlaywrightListParser.ParseOutcome(line).Should().BeNull();
    }

    [Fact]
    public void Playwright_Повтор_ТотЖеТест()
    {
        var first = PlaywrightListParser.ParseOutcome("  x  1 [chromium] › a.spec.ts:3:3 › t (12ms)")!.Value;
        var retry = PlaywrightListParser.ParseOutcome("  ✓  2 [chromium] › a.spec.ts:3:3 › t (retry #1) (10ms)")!.Value;

        retry.Retry.Should().BeTrue();
        retry.Title.Should().Be(first.Title, "счётчик ведётся по тестам, а не по попыткам");
        retry.Outcome.Should().Be(TestLineOutcome.Passed);
    }

    [Fact]
    public void Playwright_JsonОтчёт_Упавший()
    {
        var report = PlaywrightJsonReader.Read(File.ReadAllText(TestRunServiceTests.Fixture("playwright-report.json")));

        report.Assembly.Should().Be("playwright");
        (report.Total, report.Passed, report.Failed, report.Skipped).Should().Be((4, 2, 1, 1));
        var failure = report.Failures.Should().ContainSingle().Subject;
        failure.Name.Should().Be("[chromium] zz-probe.spec.ts › проба › падает (строка 4)");
        failure.Message.Should().Contain("toEqual");
        report.Duration.Should().NotBeNull();
    }

    [Fact]
    public void Playwright_JsonОтчёт_ГлобальнаяОшибкаИFlaky()
    {
        const string json = """
            {"suites":[{"title":"a.spec.ts","specs":[{"title":"t","tests":[{"status":"flaky","results":[]}]}]}],
             "errors":[{"message":"Error: webServer упал","stack":"Error: webServer упал\n    at run (x.js:1:1)"}],
             "stats":{"duration":12}}
            """;

        var report = PlaywrightJsonReader.Read(json);

        report.Passed.Should().Be(1, "flaky — прошёл повтором, как считает и сам Playwright");
        report.Failed.Should().Be(1);
        report.Failures.Single().Message.Should().Contain("webServer");
        report.Failures.Single().Stack.Should().StartWith("    at run");
    }

    [Fact]
    public void Playwright_JsonОтчёт_ЦветаТерминалаВычищены()
    {
        // Кира: Playwright кладёт ANSI прямо в error.message, FORCE_COLOR=0 не помогает
        var report = PlaywrightJsonReader.Read(File.ReadAllText(TestRunServiceTests.Fixture("playwright-report-ansi.json")));

        var failure = report.Failures.Should().ContainSingle().Subject;
        failure.Message.Should().NotContain("\u001b").And.Contain("expect(received).toBe(expected)")
            .And.Contain("Expected: 418").And.Contain("Received: 200");
        failure.Stack.Should().NotContain("\u001b").And.Contain("at e2e/rt3.spec.ts:16:22");
    }

    [Fact]
    public void Итог_ЦветаТерминала_ВычищеныДляВсехВидов()
    {
        // Второй рубеж: escape-коды из любого отчёта и хвоста консоли до модели не доезжают
        var failure = new TestFailure("\u001b[31mA.Tests.Fails\u001b[39m", "\u001b[2mExpected\u001b[22m: 1",
            "\u001b]8;;file:///x\u0007at X()\u001b]8;;\u0007");
        var failed = Completed(1, new TestReport("App.Tests.dll", 1, 0, 1, 0, null, [failure]));
        var tailOnly = Completed(1) with { Tail = ["\u001b[1mTesthost process exited\u001b[0m"] };

        var text = TestRunSummaryFormatter.Format(failed, 10240, 540) + TestRunSummaryFormatter.Format(tailOnly, 10240, 540);

        text.Should().NotContain("\u001b").And.Contain("A.Tests.Fails").And.Contain("Expected: 1")
            .And.Contain("at X()").And.Contain("Testhost process exited");
    }

    [Fact]
    public void Подсказка_БезСервераTests_СекцииНет_ТекстПостоянный()
    {
        var contributor = new TestRunsHintContributor();
        var session = new Session { OwnerId = "u1", ProjectId = "p1" };

        contributor.IsEnabled(new PromptSessionContext(session, "u1", null, "/r", HasTestsMcp: false)).Should().BeFalse();
        contributor.IsEnabled(new PromptSessionContext(session, "u1", null, "/r", HasTestsMcp: true)).Should().BeTrue();

        var first = contributor.BuildAsync(new PromptSessionContext(session, "u1", null, "/r", HasTestsMcp: true), "прогони тесты")
            .GetAwaiter().GetResult()!.Sections.Single();
        var second = contributor.BuildAsync(new PromptSessionContext(session, "u1", null, "/r", HasTestsMcp: true), null)
            .GetAwaiter().GetResult()!.Sections.Single();
        first.Should().Be(second, "текст от хода не зависит — системный блок стабилен");
        first.InTurnTail.Should().BeFalse("постоянная подсказка едет системным блоком, а не хвостом хода");
        first.Text.Should().Contain("mcp__tests__run_tests").And.Contain("select:mcp__tests__run_tests")
            .And.Contain("vitest").And.Contain("playwright").And.Contain("target");
        contributor.Group.Should().Be("mcp");
    }
}
