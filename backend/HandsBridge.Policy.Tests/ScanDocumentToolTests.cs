using System.Reflection;
using System.Text;
using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Ядро <c>scan_document</c> (A5 «руки без UIA»): цикл страниц на фейках (снимок → OCR →
/// склейка → запись) вызывается напрямую (<c>RunScanAsync</c>, internal) — WinAPI в нём нет,
/// поэтому Linux-тесты покрывают: прогон 3 страниц до повтора кадра, конец по повтору,
/// <c>.v2</c> при коллизии имени, обрезку ответа до 4000, нарезку <c>{done:false, next}</c> и
/// продолжение, отказ без <c>saveTo</c>, ошибку OCR на второй странице (частичный файл), maxPages.
/// Мост грузится рефлексией (тот же путь, что <see cref="HandsBridgeGateRuntimeTests"/>).
/// </summary>
[Collection(HandsBridgeGateRuntimeTests.Collection)]
public class ScanDocumentToolTests
{
    private static Assembly Bridge => HandsBridgeGateRuntimeTests.Bridge.Value;

    private static Type ToolType => Bridge.GetType("Sbroenne.WindowsMcp.Tools.ScanDocumentTool", throwOnError: true)!;
    private static Type OptionsType => ToolType.GetNestedType("ScanOptions", BindingFlags.NonPublic | BindingFlags.Public)!;
    private static Type DepsType => ToolType.GetNestedType("ScanDeps", BindingFlags.NonPublic | BindingFlags.Public)!;
    private static Type OcrOutcomeType => Bridge.GetType("ClaudeHomeServer.HandsBridge.OcrOutcome", throwOnError: true)!;

    /// <summary>Итог <c>RunScanAsync</c>, прочитанный рефлексией (поля <c>ScanResult</c>).</summary>
    private sealed class Out
    {
        public bool Done;
        public string? Path;
        public int Pages;
        public int TotalPages;
        public int TotalChars;
        public int Stitches;
        public string StopReason = "";
        public string? Error;
        public string? Next;
        public string Existing = "";
    }

    /// <summary>
    /// Документ из N страниц: <c>NextFrame</c> отдаёт кадры по порядку, а после последней страницы
    /// повторяет её (конец = повтор кадра). Контент кадра = байты текста страницы (уникален).
    /// </summary>
    private sealed class FakeDoc
    {
        private readonly string[] _pages;
        private int _next;
        public FakeDoc(params string[] pages) => _pages = pages;

        public byte[] NextFrame()
        {
            var idx = Math.Min(_next, _pages.Length - 1);
            _next = Math.Min(_next + 1, _pages.Length);
            return Encoding.UTF8.GetBytes(_pages[idx]);
        }
    }

    private static object OcrSuccess(string text) =>
        OcrOutcomeType.GetMethod("Success")!.Invoke(null, [text, "m", 1L, 1, 1])!;

    private static object OcrFail(string reason, string message) =>
        OcrOutcomeType.GetMethod("Fail")!.Invoke(null, [reason, message])!;

    private static object NewProjectFiles(string root)
    {
        var t = Bridge.GetType("ClaudeHomeServer.HandsBridge.HandsProjectFiles", throwOnError: true)!;
        return Activator.CreateInstance(t, (object?)root)!;
    }

    private static object MakeOptions(string saveTo, int maxPages, int pagesPerCall, string? continueFrom)
    {
        var o = Activator.CreateInstance(OptionsType)!;
        Set(o, "Window", 1L);
        Set(o, "SaveTo", saveTo);
        Set(o, "StartKey", "Ctrl+Home");
        Set(o, "PageKey", "PageDown");
        Set(o, "MaxPages", maxPages);
        Set(o, "ContinueFrom", continueFrom);
        Set(o, "PagesPerCall", pagesPerCall);
        Set(o, "MaxCallMs", 300_000);
        return o;
    }

    private static object MakeDeps(
        Func<CancellationToken, Task<byte[]>> capture,
        Func<byte[], CancellationToken, Task<object>> recognize,
        Func<byte[], ulong> hash,
        Func<string, CancellationToken, Task<bool>> press,
        Func<CancellationToken, Task> waitStable,
        object files)
    {
        var d = Activator.CreateInstance(DepsType)!;
        Set(d, "CapturePage", capture);
        Set(d, "Recognize", recognize);
        Set(d, "HashFrame", hash);
        Set(d, "PressKey", press);
        Set(d, "WaitStable", waitStable);
        Set(d, "Files", files);
        return d;
    }

    private static void Set(object o, string prop, object? value) =>
        o.GetType().GetProperty(prop)!.SetValue(o, value);

    private static async Task<Out> Run(object options, object deps)
    {
        var method = ToolType.GetMethod("RunScanAsync", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
        var task = (Task)method.Invoke(null, [options, deps, CancellationToken.None])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var t = result.GetType();
        return new Out
        {
            Done = (bool)t.GetField("Done")!.GetValue(result)!,
            Path = (string?)t.GetField("Path")!.GetValue(result),
            Pages = (int)t.GetField("Pages")!.GetValue(result)!,
            TotalPages = (int)t.GetField("TotalPages")!.GetValue(result)!,
            TotalChars = (int)t.GetField("TotalChars")!.GetValue(result)!,
            Stitches = (int)t.GetField("DoubtfulStitches")!.GetValue(result)!,
            StopReason = (string)t.GetField("StopReason")!.GetValue(result)!,
            Error = (string?)t.GetField("Error")!.GetValue(result),
            Next = (string?)t.GetField("Next")!.GetValue(result),
            Existing = (string)t.GetField("Existing")!.GetValue(result)!,
        };
    }

    /// <summary>Швы для фейкового документа: capture/recognize по <paramref name="doc"/>, хэш = FNV по кадру.</summary>
    private static (object Deps, FakeDoc Doc) MakeFakeDeps(FakeDoc doc, object files)
    {
        var deps = MakeDeps(
            capture: _ => Task.FromResult(doc.NextFrame()),
            recognize: (frame, _) => Task.FromResult((object)OcrSuccess(Encoding.UTF8.GetString(frame))),
            hash: Fnv,
            press: (combo, _) => Task.FromResult(true),
            waitStable: _ => Task.CompletedTask,
            files);
        return (deps, doc);
    }

    private static ulong Fnv(byte[] data)
    {
        const ulong Offset = 14695981039346656037UL;
        const ulong Prime = 1099511628211;
        var h = Offset;
        foreach (var b in data)
            h = (h ^ b) * Prime;
        return h;
    }

    private static string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hands-a5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------- прогон страниц ----------

    /// <summary>
    /// Прогон 3 страниц до повтора кадра: 3 уникальных кадра → OCR → склейка → файл;
    /// 4-й кадр повторяет 3-й → конец (end_of_document), 4-я страница НЕ дописывается.
    /// </summary>
    [Fact]
    public async Task Three_pages_until_frame_repeat()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var doc = new FakeDoc("alpha page one", "beta page two", "gamma page three");
            var (deps, _) = MakeFakeDeps(doc, files);
            var options = MakeOptions("doc.md", 300, 10, null);

            var r = await Run(options, deps);

            Assert.Equal("end_of_document", r.StopReason);
            Assert.True(r.Done);
            Assert.Equal(3, r.TotalPages);
            Assert.Equal("doc.md", r.Path);
            // Файл = склейка трёх страниц (с маркерами швов), не 4-я (повтор).
            var fileText = File.ReadAllText(Path.Combine(root, "doc.md"), Encoding.UTF8);
            Assert.Contains("alpha page one", fileText);
            Assert.Contains("beta page two", fileText);
            Assert.Contains("gamma page three", fileText);
            // Три страницы без перекрытия → ровно 2 шва (маркер на каждом стыке): точное число
            // сомнительных швов и ровно 2 маркера в файле. Мутация «тривиальная склейка»
            // (без маркера) красит обе проверки: число маркеров упадёт до 0.
            Assert.Equal(2, r.Stitches);
            Assert.Equal(2, CountOccurrences(fileText, "<!-- шов: проверить -->"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Конец по повтору кадра: два одинаковых кадра подряд после <c>pageKey</c> останавливают цикл
    /// ДО OCR/записи повторной страницы. Мутация: убрать проверку повтора → тест краснеет.
    /// </summary>
    [Fact]
    public async Task End_by_frame_repeat_does_not_append_the_duplicate()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            // Одна страница, затем повтор: кадр1 → OCR, кадр2 == кадр1 → конец.
            var doc = new FakeDoc("only page");
            var (deps, _) = MakeFakeDeps(doc, files);
            var options = MakeOptions("one.md", 300, 10, null);

            var r = await Run(options, deps);

            Assert.Equal("end_of_document", r.StopReason);
            Assert.Equal(1, r.TotalPages);
            var fileText = File.ReadAllText(Path.Combine(root, "one.md"), Encoding.UTF8);
            // Страница одна, повтор не дописан: ровно одно вхождение.
            Assert.Equal(1, CountOccurrences(fileText, "only page"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// <c>.v2</c> при коллизии имени: файл уже существует → <c>WriteNew</c> берёт следующее
    /// свободное имя, не перезаписывая.
    /// </summary>
    [Fact]
    public async Task V2_on_name_collision()
    {
        var root = NewRoot();
        try
        {
            // Занять имя: файл уже есть в корне проекта.
            File.WriteAllText(Path.Combine(root, "taken.md"), "pre-existing content");

            var files = NewProjectFiles(root);
            var doc = new FakeDoc("fresh page");
            var (deps, _) = MakeFakeDeps(doc, files);
            var options = MakeOptions("taken.md", 300, 10, null);

            var r = await Run(options, deps);

            Assert.Equal("taken.v2.md", r.Path);
            Assert.True(File.Exists(Path.Combine(root, "taken.v2.md")));
            // Старый файл не перезаписан.
            Assert.Equal("pre-existing content", File.ReadAllText(Path.Combine(root, "taken.md"), Encoding.UTF8));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Обрезка ответа до 4000: накопленный текст длиннее — в ответе хвост ≤ 4000,
    /// в файле — всё.
    /// </summary>
    [Fact]
    public async Task Response_truncated_to_4000()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            // Одна «страница» с длинным текстом (5000 символов).
            var longText = new string('x', 5000);
            var doc = new FakeDoc(longText);
            var (deps, _) = MakeFakeDeps(doc, files);
            var options = MakeOptions("long.md", 300, 10, null);

            var r = await Run(options, deps);

            Assert.Equal(5000, r.TotalChars);
            // Existing — полный (5000); ответ обрежет до 4000 (проверяется в ToCallToolResult,
            // здесь фиксируем, что накопленный текст длиннее потолка).
            Assert.True(r.Existing.Length > 4000);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Нарезка <c>{done:false, next}</c> и продолжение: бюджет 2 страницы, документ 4 страницы.
    /// Первый вызов — 2 страницы + курсор; второй (с <c>continueFrom</c>) дописывает ТОТ ЖЕ файл
    /// через AppendOwn и доводит до конца.
    /// </summary>
    [Fact]
    public async Task Chunking_and_continuation_append_the_same_file()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var doc = new FakeDoc("p1", "p2", "p3", "p4");

            // Первый вызов: бюджет 2 → 2 страницы, done:false, курсор.
            var (deps1, _) = MakeFakeDeps(doc, files);
            var options1 = MakeOptions("multi.md", 300, 2, null);
            var r1 = await Run(options1, deps1);
            Assert.Equal("budget", r1.StopReason);
            Assert.False(r1.Done);
            Assert.Equal(2, r1.TotalPages);
            Assert.NotNull(r1.Next);
            Assert.Equal("multi.md", r1.Path);

            // Второй вызов: continueFrom = курсор → дописывает p3, p4 в тот же файл.
            var (deps2, _) = MakeFakeDeps(doc, files);
            var options2 = MakeOptions("multi.md", 300, 10, r1.Next);
            var r2 = await Run(options2, deps2);
            Assert.Equal("end_of_document", r2.StopReason);
            Assert.True(r2.Done);
            Assert.Equal(4, r2.TotalPages);
            Assert.Equal("multi.md", r2.Path);

            // Файл один, содержит все 4 страницы (склейка), без дублей.
            var fileText = File.ReadAllText(Path.Combine(root, "multi.md"), Encoding.UTF8);
            foreach (var page in new[] { "p1", "p2", "p3", "p4" })
                Assert.Equal(1, CountOccurrences(fileText, page));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Мост перезапущен → continueFrom отказывает: первый вызов (бюджет 2) доводит до курсора;
    /// второй вызов на СВЕЖЕМ экземпляре <c>HandsProjectFiles</c> (файл не создан этим ходом) —
    /// <c>AppendOwn</c> отказывает «начните заново»: файл не перезаписан и <c>.v2</c> с мусором
    /// не создан. Мутация «снять границу AppendOwn» (дописывать в любой файл) красит тест.
    /// </summary>
    [Fact]
    public async Task Bridge_restarted_continuation_refuses_to_append()
    {
        var root = NewRoot();
        try
        {
            var files1 = NewProjectFiles(root);
            var doc = new FakeDoc("p1", "p2", "p3", "p4");

            // Первый вызов: бюджет 2 → 2 страницы, done:false, курсор.
            var (deps1, _) = MakeFakeDeps(doc, files1);
            var options1 = MakeOptions("multi.md", 300, 2, null);
            var r1 = await Run(options1, deps1);
            Assert.Equal("budget", r1.StopReason);
            Assert.False(r1.Done);
            Assert.Equal(2, r1.TotalPages);
            Assert.NotNull(r1.Next);
            Assert.Equal("multi.md", r1.Path);

            // Второй вызов: СВЕЖИЙ экземпляр моста (файл не создан этим ходом) + тот же курсор.
            var files2 = NewProjectFiles(root);
            var (deps2, _) = MakeFakeDeps(doc, files2);
            var options2 = MakeOptions("multi.md", 300, 10, r1.Next);
            var r2 = await Run(options2, deps2);

            // AppendOwn отказывает: файл создан другим экземпляром моста.
            Assert.Equal("error", r2.StopReason);
            Assert.NotNull(r2.Error);
            Assert.Contains("appended", r2.Error);

            // Файл не перезаписан (ровно 2 страницы первого вызова, без p3) и .v2 с мусором нет.
            var fileText = File.ReadAllText(Path.Combine(root, "multi.md"), Encoding.UTF8);
            Assert.Equal(1, CountOccurrences(fileText, "p1"));
            Assert.Equal(1, CountOccurrences(fileText, "p2"));
            Assert.DoesNotContain("p3", fileText);
            Assert.False(File.Exists(Path.Combine(root, "multi.v2.md")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Отказ без <c>saveTo</c>: пустой путь — файл некуда писать, честная ошибка (до OCR).
    /// </summary>
    [Fact]
    public async Task Refuse_without_saveTo()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var doc = new FakeDoc("x");
            var (deps, _) = MakeFakeDeps(doc, files);
            var options = MakeOptions("", 300, 10, null);

            var r = await Run(options, deps);

            Assert.Equal("error", r.StopReason);
            Assert.NotNull(r.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Ошибка OCR на второй странице (не-повторяемый отказ, например too_large): первая страница
    /// уже записана (частичный файл), статус — error, файл существует с первой страницей.
    /// </summary>
    [Fact]
    public async Task Ocr_error_on_second_page_leaves_partial_file()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var doc = new FakeDoc("good first page", "second page");

            // OCR: первая страница — успех, вторая — отказ too_large (не повторяем).
            var recognize = new Func<byte[], CancellationToken, Task<object>>((frame, _) =>
            {
                var text = Encoding.UTF8.GetString(frame);
                return text.StartsWith("good")
                    ? Task.FromResult((object)OcrSuccess(text))
                    : Task.FromResult((object)OcrFail("too_large", "the page is too large"));
            });

            var deps = MakeDeps(
                capture: _ => Task.FromResult(doc.NextFrame()),
                recognize,
                hash: Fnv,
                press: (combo, _) => Task.FromResult(true),
                waitStable: _ => Task.CompletedTask,
                files);

            var options = MakeOptions("part.md", 300, 10, null);
            var r = await Run(options, deps);

            Assert.Equal("error", r.StopReason);
            Assert.Equal("too large", (r.Error ?? "").Replace("the page is", "the page is").Contains("large") ? "too large" : r.Error);
            Assert.Equal(1, r.TotalPages); // только первая страница записана
            Assert.True(File.Exists(Path.Combine(root, "part.md")));
            var fileText = File.ReadAllText(Path.Combine(root, "part.md"), Encoding.UTF8);
            Assert.Contains("good first page", fileText);
            Assert.DoesNotContain("second page", fileText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Повтор OCR (503 busy) — happy-path: страница «p1» отвечает [busy, busy, success] →
    /// прочитана после 2 повторов (3 попытки), «p2» — сразу; склейка и запись идут, продолжение
    /// доводит до конца. Мутация «не повторять busy» (сразу стоп) красит счётчик попыток.
    /// </summary>
    [Fact]
    public async Task Ocr_busy_retried_then_success_continues()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var doc = new FakeDoc("p1", "p2");

            // OCR: «p1» → [busy, busy, success]; «p2» → [success]. Счётчик вызовов по тексту.
            var calls = new Dictionary<string, int>();
            var recognize = new Func<byte[], CancellationToken, Task<object>>((frame, _) =>
            {
                var text = Encoding.UTF8.GetString(frame);
                var n = calls.TryGetValue(text, out var c) ? c : 0;
                calls[text] = n + 1;
                if (text == "p1" && n < 2)
                    return Task.FromResult((object)OcrFail("busy", "the OCR server is busy; try again later"));
                return Task.FromResult((object)OcrSuccess(text));
            });

            var deps = MakeDeps(
                capture: _ => Task.FromResult(doc.NextFrame()),
                recognize,
                hash: Fnv,
                press: (combo, _) => Task.FromResult(true),
                waitStable: _ => Task.CompletedTask,
                files);

            var options = MakeOptions("busy.md", 300, 10, null);
            var r = await Run(options, deps);

            // «p1» прочитана после 2 повторов (3 попытки), «p2» сразу; конец по повтору кадра.
            Assert.Equal("end_of_document", r.StopReason);
            Assert.True(r.Done);
            Assert.Equal(2, r.TotalPages);
            Assert.Equal(3, calls["p1"]); // 2 busy + 1 success
            Assert.Equal(1, calls["p2"]); // сразу успех
            var fileText = File.ReadAllText(Path.Combine(root, "busy.md"), Encoding.UTF8);
            Assert.Contains("p1", fileText);
            Assert.Contains("p2", fileText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Повтор OCR исчерпан: страница «p2» отвечает [busy, busy, busy] (все 3 попытки) → стоп
    /// с частичным файлом (первая страница записана) и статусом error. Мутация «повторять
    /// бесконечно» (не стопить после 2 повторов) красит: страница прочитается, а не остановит.
    /// </summary>
    [Fact]
    public async Task Ocr_busy_exhausts_retries_stops_with_partial_file()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var doc = new FakeDoc("p1", "p2");

            // OCR: «p1» → success; «p2» → [busy, busy, busy] (все 3 попытки — busy) → стоп.
            var calls = new Dictionary<string, int>();
            var recognize = new Func<byte[], CancellationToken, Task<object>>((frame, _) =>
            {
                var text = Encoding.UTF8.GetString(frame);
                var n = calls.TryGetValue(text, out var c) ? c : 0;
                calls[text] = n + 1;
                if (text == "p2")
                    return Task.FromResult((object)OcrFail("busy", "the OCR server is busy; try again later"));
                return Task.FromResult((object)OcrSuccess(text));
            });

            var deps = MakeDeps(
                capture: _ => Task.FromResult(doc.NextFrame()),
                recognize,
                hash: Fnv,
                press: (combo, _) => Task.FromResult(true),
                waitStable: _ => Task.CompletedTask,
                files);

            var options = MakeOptions("busy2.md", 300, 10, null);
            var r = await Run(options, deps);

            // «p2» не прочитана (3 busy) → стоп с частичным файлом (только «p1») и статусом error.
            Assert.Equal("error", r.StopReason);
            Assert.NotNull(r.Error);
            Assert.Equal(1, r.TotalPages); // только «p1» записана
            Assert.Equal(3, calls["p2"]); // 3 попытки, все busy
            Assert.True(File.Exists(Path.Combine(root, "busy2.md")));
            var fileText = File.ReadAllText(Path.Combine(root, "busy2.md"), Encoding.UTF8);
            Assert.Contains("p1", fileText);
            Assert.DoesNotContain("p2", fileText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// maxPages: потолок 2, документ длиннее → остановка по max_pages после 2 страниц (done).
    /// </summary>
    [Fact]
    public async Task MaxPages_stops_at_the_cap()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var doc = new FakeDoc("a", "b", "c", "d", "e");
            var (deps, _) = MakeFakeDeps(doc, files);
            var options = MakeOptions("capped.md", 2, 10, null);

            var r = await Run(options, deps);

            Assert.Equal("max_pages", r.StopReason);
            Assert.True(r.Done);
            Assert.Equal(2, r.TotalPages);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var from = 0;
        while ((from = haystack.IndexOf(needle, from, StringComparison.Ordinal)) >= 0)
        {
            count++;
            from += needle.Length;
        }
        return count;
    }
}
