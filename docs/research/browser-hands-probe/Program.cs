using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BrowserHandsProbe;

/// <summary>
/// Пробник спайка браузерной руки (docs/research/browser-hands-spike-2026-09.md), пункты 1, 2, 3, 5
/// чек-листа и «профиль занят». Chrome управляется по CDP через анонимные трубы, без порта,
/// без Node и без Playwright.
/// </summary>
static class Program
{
    const string TestPage = "data:text/html,%3Ctitle%3Eprobe%3C/title%3E%3Cbutton%3EHello%3C/button%3E";
    const string SecondPage = "data:text/html,%3Ctitle%3Esecond-launch%3C/title%3Esecond";

    static readonly Report R = new();
    static string _tempRoot = "";
    static int _profileCounter;

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        bool noPause = args.Contains("--no-pause");
        Launcher.Headless = args.Contains("--headless");
        int chromeArg = Array.IndexOf(args, "--chrome");

        R.Line("Пробник браузерной руки: Chrome по CDP через анонимные трубы");
        R.Line($"Время: {DateTime.Now:yyyy-MM-dd HH:mm:ss}; ОС: {Environment.OSVersion}; .NET {Environment.Version}; x64: {Environment.Is64BitProcess}");
        R.Line("Окна Chrome будут открываться и закрываться сами, около минуты. Не трогайте их.");

        _tempRoot = Path.Combine(Path.GetTempPath(), "hands-browser-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
        try
        {
            Launcher.ChromePath = chromeArg >= 0 && chromeArg + 1 < args.Length ? args[chromeArg + 1] : Launcher.FindChrome() ?? "";
            if (Launcher.ChromePath == "")
            {
                R.Line("Chrome не найден: ни App Paths в реестре, ни Program Files, ни %LOCALAPPDATA%. " +
                       "Укажите путь: BrowserHandsProbe.exe --chrome \"C:\\...\\chrome.exe\" или переменная CHROME_PATH.");
                R.Verdict("Chrome", "FAIL", "не найден");
                return 1;
            }
            R.Line($"Chrome: {Launcher.ChromePath}, версия файла {Launcher.ChromeFileVersion(Launcher.ChromePath) ?? "?"}" +
                   (Launcher.Headless ? ", режим --headless" : ", с окном (headed)"));
            R.Line($"Временный каталог профилей: {_tempRoot}");

            await Check1IoPipes();
            await Check2Job();
            await Check3PipeClose();
            await Check4ProfileBusy();
            await Check5ColdStart();
        }
        catch (Exception ex)
        {
            R.Line("НЕОБРАБОТАННАЯ ОШИБКА: " + ex);
            R.Verdict("Пробник", "FAIL", ex.Message);
        }
        finally
        {
            Cleanup();
            R.PrintSummary();
            R.Line();
            R.Line("Отчёт сохранён: " + R.Save(AppContext.BaseDirectory));
        }

        if (!noPause)
        {
            Console.WriteLine("Нажмите Enter, чтобы закрыть окно.");
            Console.ReadLine();
        }
        return 0;
    }

    // ---------- 1 ----------

    static async Task Check1IoPipes()
    {
        R.Section("1. --remote-debugging-io-pipes: getVersion → createTarget → navigate → getFullAXTree (без Job)");
        var (profile, log) = NewProfile();
        BrowserInstance? b = null;
        try
        {
            var sw = Stopwatch.StartNew();
            b = Launcher.Launch(profile, log, "about:blank", inJob: false);
            R.Line($"  CreateProcess: pid {b.Pid}, {b.PipeInfo}, {sw.ElapsedMilliseconds} мс");
            var page = await FirstPage(b, sw);
            foreach (var s in page.Steps) R.Line("  " + s);
            if (page.Error == null && page.ButtonFound)
            {
                R.Verdict("1. io-pipes", "PASS",
                    $"{page.Product}: первый ответ {page.FirstReplyMs} мс, страница + AX-дерево {page.PageAxMs} мс, узлов {page.AxNodes}");
            }
            else
            {
                Diagnose(b, page);
                R.Verdict("1. io-pipes", "FAIL",
                    $"Chrome {Launcher.ChromeFileVersion(Launcher.ChromePath)}, шаг {page.FailedStep ?? "проверка AX"}: {page.Error ?? "кнопка Hello не найдена в AX-дереве"}");
            }
        }
        catch (Exception ex)
        {
            R.Verdict("1. io-pipes", "FAIL", $"запуск: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await Shutdown(b);
        }
    }

    // ---------- 2 ----------

    static async Task Check2Job()
    {
        R.Section("2. Chrome в Job с KILL_ON_JOB_CLOSE: песочница и TerminateJobObject");
        if (!OperatingSystem.IsWindows())
        {
            R.Verdict("2. Job", "SKIP", "только Windows");
            return;
        }
        var (profile, log) = NewProfile();
        BrowserInstance? b = null;
        try
        {
            var globalBefore = CountByName();
            R.Line($"  До запуска во всей системе: {globalBefore} (включая ваш собственный Chrome, если открыт)");
            var sw = Stopwatch.StartNew();
            b = Launcher.Launch(profile, log, "about:blank", inJob: true);
            var page = await FirstPage(b, sw);
            foreach (var s in page.Steps) R.Line("  " + s);
            if (page.Error != null) Diagnose(b, page);
            await SandboxPage(b);
            await Task.Delay(1500); // дать подняться crashpad и утилитам

            var tree = b.Track();
            var (total, active, _) = Win32.JobCounters(b.Job);
            R.Line($"  Дерево браузера ({tree.Count}): {ProcessTree.Describe(tree)}");
            R.Line($"  Счётчики Job: всего процессов {total}, живых {active}");
            bool renderer = tree.Any(t => t.Kind == "renderer");
            var sandboxErrors = LogLines(log, l => l.Contains("sandbox", StringComparison.OrdinalIgnoreCase)
                                                   && (l.Contains("ERROR") || l.Contains("FATAL")));
            foreach (var l in sandboxErrors) R.Line("  лог: " + l);
            bool sandboxOk = page.Error == null && page.ButtonFound && renderer && sandboxErrors.Count == 0 && !b.Proc.HasExited;
            R.Verdict("2a. Песочница в Job", sandboxOk ? "PASS" : "FAIL",
                sandboxOk
                    ? $"страница и AX-дерево получены, рендереров {tree.Count(t => t.Kind == "renderer")}, ошибок песочницы в логе нет"
                    : $"страница: {page.Error ?? (page.ButtonFound ? "ок" : "нет кнопки")}, рендерер: {renderer}, " +
                      $"ошибок песочницы в логе: {sandboxErrors.Count}, браузер жив: {!b.Proc.HasExited}");

            var kill = Stopwatch.StartNew();
            Win32.TerminateJob(b.Job);
            bool allDead = await WaitUntil(() => tree.All(t => !t.Alive) && OperatingSystem.IsWindows() && Win32.JobCounters(b.Job).Active == 0, 10000);
            long ms = kill.ElapsedMilliseconds;
            var survivors = tree.Where(t => t.Alive).ToList();
            var (_, activeAfter, terminated) = Win32.JobCounters(b.Job);
            R.Line($"  После TerminateJobObject: живых в Job {activeAfter}, завершено {terminated}; из дерева выжили: " +
                   (survivors.Count == 0 ? "никто" : ProcessTree.Describe(survivors)));
            R.Line($"  Во всей системе после: {CountByName()}");
            R.Verdict("2b. TerminateJobObject", allDead ? "PASS" : "FAIL",
                allDead
                    ? $"все {tree.Count} процессов ({ProcessTree.Describe(tree)}) погашены за {ms} мс"
                    : $"за 10 с не погасли: {ProcessTree.Describe(survivors)}; живых в Job {activeAfter}");
        }
        catch (Exception ex)
        {
            R.Verdict("2. Job", "FAIL", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await Shutdown(b);
        }
    }

    static async Task SandboxPage(BrowserInstance b)
    {
        // Справочно: chrome://sandbox перечисляет процессы и их уровни целостности.
        try
        {
            var t = await b.Cdp.Call("Target.createTarget", w => w.WriteString("url", "chrome://sandbox"));
            var session = (await b.Cdp.Call("Target.attachToTarget", w =>
            {
                w.WriteString("targetId", t.GetProperty("targetId").GetString());
                w.WriteBoolean("flatten", true);
            })).GetProperty("sessionId").GetString();
            await Task.Delay(1500);
            var ax = await b.Cdp.Call("Accessibility.getFullAXTree", session: session);
            var names = AxNames(ax);
            string text = string.Join(" | ", names);
            R.Line($"  chrome://sandbox: {names.Count} подписей, «Untrusted» встречается {Count(text, "Untrusted")} раз, «Low» — {Count(text, "Low")} (сырой текст — в приложении отчёта)");
            R.Appendix("chrome://sandbox (подписи AX-дерева)", text);
        }
        catch (Exception ex)
        {
            R.Line($"  chrome://sandbox прочитать не удалось: {ex.Message}");
        }
    }

    // ---------- 3 ----------

    static async Task Check3PipeClose()
    {
        R.Section("3. Закрытие труб без Job: выходит ли Chrome сам");
        var (profile, log) = NewProfile();
        BrowserInstance? b = null;
        try
        {
            var sw = Stopwatch.StartNew();
            b = Launcher.Launch(profile, log, "about:blank", inJob: false);
            var page = await FirstPage(b, sw);
            if (page.Error != null)
            {
                Diagnose(b, page);
                R.Verdict("3. Закрытие труб", "FAIL", $"CDP не поднялся: {page.Error}");
                return;
            }
            await Task.Delay(1500);
            var tree = b.Track();
            R.Line($"  Дерево до закрытия ({tree.Count}): {ProcessTree.Describe(tree)}");

            var close = Stopwatch.StartNew();
            b.Cdp.CloseWriteEnd();
            long? eofMs = null, rootMs = null;
            var eofTask = b.Cdp.Closed.ContinueWith(_ => eofMs = close.ElapsedMilliseconds);
            bool rootExited = await WaitUntil(() => b.Proc.HasExited, 20000);
            if (rootExited) rootMs = close.ElapsedMilliseconds;
            bool allExited = await WaitUntil(() => tree.All(t => !t.Alive), 20000);
            long allMs = close.ElapsedMilliseconds;
            await Task.WhenAny(eofTask, Task.Delay(1000));
            var survivors = tree.Where(t => t.Alive).ToList();

            R.Line($"  Закрыли свой пишущий конец. EOF на нашем читающем: {(eofMs is { } e ? e + " мс" : "нет")}; " +
                   $"браузер вышел: {(rootMs is { } r ? r + " мс, код " + b.Proc.ExitCode : "нет за 20 с")}; " +
                   $"всё дерево: {(allExited ? allMs + " мс" : "нет за 20 с")}");
            R.Verdict("3. Закрытие труб", allExited ? "PASS" : "FAIL",
                allExited
                    ? $"Chrome вышел сам: браузер за {rootMs} мс, всё дерево ({tree.Count}) за {allMs} мс"
                    : $"не вышли за 20 с: {ProcessTree.Describe(survivors)}");
        }
        catch (Exception ex)
        {
            R.Verdict("3. Закрытие труб", "FAIL", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await Shutdown(b);
        }
    }

    // ---------- 4 ----------

    static async Task Check4ProfileBusy()
    {
        R.Section("4. Профиль занят: второй запуск с тем же --user-data-dir при живом первом");
        var (profile, logA) = NewProfile();
        string logB = logA + ".second.log";
        BrowserInstance? a = null, second = null;
        try
        {
            var before = Launcher.ProfileLock(profile);
            R.Line($"  Проверка занятости до запуска: {(before.Busy ? "ЗАНЯТ" : "свободен")} ({before.How})");

            a = Launcher.Launch(profile, logA, "about:blank", inJob: OperatingSystem.IsWindows());
            var v = await a.Cdp.Call("Browser.getVersion", timeoutMs: 20000);
            await Task.Delay(500);
            var pagesBefore = await Pages(a);
            R.Line($"  Первый Chrome (pid {a.Pid}) отвечает: {v.GetProperty("product").GetString()}, вкладок {pagesBefore.Count}");

            var during = Launcher.ProfileLock(profile);
            R.Line($"  Проверка занятости при живом первом: {(during.Busy ? "ЗАНЯТ" : "свободен")} ({during.How})");

            var sw = Stopwatch.StartNew();
            second = Launcher.Launch(profile, logB, SecondPage, inJob: OperatingSystem.IsWindows());
            string outcome;
            try
            {
                var v2 = await second.Cdp.Call("Browser.getVersion", timeoutMs: 8000);
                outcome = $"ОТВЕТИЛ за {sw.ElapsedMilliseconds} мс: {v2.GetProperty("product").GetString()}";
            }
            catch (Exception ex)
            {
                outcome = $"{ex.Message} (через {sw.ElapsedMilliseconds} мс)";
            }
            bool exited = second.Proc.WaitExit(10000);
            int? secondExit = exited ? second.Proc.ExitCode : null;
            R.Line($"  Второй Chrome (pid {second.Pid}): Browser.getVersion → {outcome}");
            R.Line($"  Второй процесс: {(exited ? $"вышел, код {secondExit}, к {sw.ElapsedMilliseconds} мс от запуска" : "жив через 10 с")}; " +
                   $"труба: {(second.Cdp.Closed.IsCompleted ? second.Cdp.Closed.Result : "открыта")}");

            await Task.Delay(1000);
            var pagesAfter = await Pages(a);
            bool handedOff = pagesAfter.Any(u => u.Contains("second-launch"));
            R.Line($"  Первый Chrome после второго запуска: вкладок {pagesAfter.Count}, " +
                   $"URL второго запуска {(handedOff ? "ОТКРЫЛСЯ в первом процессе (передача синглтону)" : "не появился")}");

            await Shutdown(second);
            second = null;
            await Shutdown(a);
            a = null;
            var after = Launcher.ProfileLock(profile);
            R.Line($"  Проверка занятости после гашения первого: {(after.Busy ? "ЗАНЯТ" : "свободен")} ({after.How})");

            bool detect = !before.Busy && during.Busy && !after.Busy;
            R.Verdict("4. Профиль занят", detect ? "PASS" : "FAIL",
                (detect ? "занятость распознаётся до запуска" : $"распознавание: до={before.Busy}, при живом={during.Busy}, после={after.Busy}") +
                $"; второй запуск: {outcome}; {(exited ? $"вышел с кодом {secondExit}" : "не вышел")}; " +
                (handedOff ? "URL ушёл в первый процесс" : "URL в первый процесс не ушёл"));
        }
        catch (Exception ex)
        {
            R.Verdict("4. Профиль занят", "FAIL", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await Shutdown(second);
            await Shutdown(a);
        }
    }

    // ---------- 5 ----------

    static async Task Check5ColdStart()
    {
        R.Section($"5. Холодный старт{(Launcher.Headless ? " (headless)" : " с окном")}: запуск → первая страница с AX-деревом, 3 замера, свежий профиль");
        var first = new List<long>();
        var full = new List<long>();
        for (int i = 1; i <= 3; i++)
        {
            var (profile, log) = NewProfile();
            BrowserInstance? b = null;
            try
            {
                var sw = Stopwatch.StartNew();
                b = Launcher.Launch(profile, log, "about:blank", inJob: OperatingSystem.IsWindows());
                var page = await FirstPage(b, sw);
                if (page.Error != null || !page.ButtonFound)
                {
                    R.Line($"  замер {i}: ОШИБКА на шаге {page.FailedStep}: {page.Error}");
                    continue;
                }
                first.Add(page.FirstReplyMs);
                full.Add(page.PageAxMs);
                R.Line($"  замер {i}: первый ответ CDP {page.FirstReplyMs} мс, страница + AX-дерево {page.PageAxMs} мс ({page.AxNodes} узлов)");
            }
            catch (Exception ex)
            {
                R.Line($"  замер {i}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                await Shutdown(b);
            }
        }
        R.Verdict("5. Холодный старт", full.Count == 3 ? "PASS" : "FAIL",
            full.Count == 0
                ? "ни одного удачного замера"
                : $"страница + AX-дерево {string.Join(" / ", full)} мс (медиана {Median(full)}), первый ответ {string.Join(" / ", first)} мс; удачных {full.Count} из 3");
    }

    // ---------- общее ----------

    sealed class PageResult
    {
        public long FirstReplyMs, PageAxMs;
        public string? Product, Error, FailedStep;
        public int AxNodes;
        public bool ButtonFound;
        public readonly List<string> Steps = [];
    }

    static async Task<PageResult> FirstPage(BrowserInstance b, Stopwatch sw)
    {
        var r = new PageResult();
        string step = "Browser.getVersion";
        try
        {
            var v = await b.Cdp.Call(step, timeoutMs: 20000);
            r.FirstReplyMs = sw.ElapsedMilliseconds;
            r.Product = v.GetProperty("product").GetString();
            r.Steps.Add($"{step}: {r.FirstReplyMs} мс — {r.Product}, протокол {v.GetProperty("protocolVersion").GetString()}");

            step = "Target.createTarget";
            var t = await b.Cdp.Call(step, w => w.WriteString("url", "about:blank"));
            string targetId = t.GetProperty("targetId").GetString()!;
            r.Steps.Add($"{step}: {sw.ElapsedMilliseconds} мс — targetId {targetId}");

            step = "Target.attachToTarget";
            var a = await b.Cdp.Call(step, w =>
            {
                w.WriteString("targetId", targetId);
                w.WriteBoolean("flatten", true);
            });
            string session = a.GetProperty("sessionId").GetString()!;

            step = "Page.enable";
            await b.Cdp.Call(step, session: session);

            step = "Page.navigate";
            var load = b.Cdp.WaitEvent("Page.loadEventFired", session);
            var nav = await b.Cdp.Call(step, w => w.WriteString("url", TestPage), session);
            if (nav.TryGetProperty("errorText", out var navErr)) throw new CdpException(navErr.GetString() ?? "errorText");
            r.Steps.Add($"{step}: {sw.ElapsedMilliseconds} мс");

            step = "Page.loadEventFired";
            await load.WaitAsync(TimeSpan.FromSeconds(15));

            step = "Accessibility.getFullAXTree";
            var ax = await b.Cdp.Call(step, session: session);
            r.PageAxMs = sw.ElapsedMilliseconds;
            r.AxNodes = ax.GetProperty("nodes").GetArrayLength();
            r.ButtonFound = ax.GetProperty("nodes").EnumerateArray().Any(n =>
                Str(n, "role") == "button" && Str(n, "name") == "Hello");
            r.Steps.Add($"{step}: {r.PageAxMs} мс — узлов {r.AxNodes}, кнопка «Hello» {(r.ButtonFound ? "найдена" : "НЕ найдена")}");
        }
        catch (Exception ex)
        {
            r.FailedStep = step;
            r.Error = ex is TimeoutException && step == "Page.loadEventFired" ? "нет события за 15 с" : ex.Message;
            r.Steps.Add($"{step}: ОШИБКА через {sw.ElapsedMilliseconds} мс — {ex.GetType().Name}: {r.Error}");
        }
        return r;
    }

    static string? Str(JsonElement node, string prop) =>
        node.TryGetProperty(prop, out var p) && p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    static List<string> AxNames(JsonElement ax) =>
        ax.GetProperty("nodes").EnumerateArray()
            .Select(n => Str(n, "name"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .ToList();

    static async Task<List<string>> Pages(BrowserInstance b)
    {
        var t = await b.Cdp.Call("Target.getTargets");
        return t.GetProperty("targetInfos").EnumerateArray()
            .Where(i => i.GetProperty("type").GetString() == "page")
            .Select(i => i.GetProperty("url").GetString() ?? "")
            .ToList();
    }

    static void Diagnose(BrowserInstance b, PageResult page)
    {
        R.Line($"  Диагностика: браузер {(b.Proc.HasExited ? $"ВЫШЕЛ, код {b.Proc.ExitCode}" : "жив")}; " +
               $"труба: {(b.Cdp.Closed.IsCompleted ? b.Cdp.Closed.Result : "открыта")}; ключ: {b.PipeInfo}");
        var lines = LogLines(b.LogFile, l =>
            l.Contains("devtools", StringComparison.OrdinalIgnoreCase) || l.Contains("pipe", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("remote", StringComparison.OrdinalIgnoreCase) || l.Contains("ERROR") || l.Contains("FATAL"));
        foreach (var l in lines.TakeLast(30)) R.Line("  лог: " + l);
        R.Appendix($"chrome_debug.log ({Path.GetFileName(b.LogFile)}), хвост", string.Join("\n", ReadLog(b.LogFile).TakeLast(200)));
    }

    static List<string> ReadLog(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        }
        catch
        {
            return [];
        }
    }

    static List<string> LogLines(string path, Func<string, bool> filter) => ReadLog(path).Where(filter).ToList();

    /// <summary>Штатно — Browser.close, не вышел за 5 с — гасим всё, что видели.</summary>
    static async Task Shutdown(BrowserInstance? b)
    {
        if (b == null) return;
        try
        {
            if (!b.Proc.HasExited)
            {
                b.Track();
                if (!b.Cdp.Closed.IsCompleted)
                {
                    try { await b.Cdp.Call("Browser.close", timeoutMs: 3000); } catch { }
                }
                await WaitUntil(() => b.Proc.HasExited, 5000);
            }
            b.KillAll();
            await WaitUntil(() => b.Seen.All(t => !t.Alive), 5000);
        }
        finally
        {
            lock (Launcher.All) Launcher.All.Remove(b);
            b.Dispose();
        }
    }

    static void Cleanup()
    {
        List<BrowserInstance> left;
        lock (Launcher.All) left = [.. Launcher.All];
        foreach (var b in left)
        {
            try { b.KillAll(); b.Dispose(); } catch { }
        }
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
                R.Line();
                R.Line($"Временные профили удалены: {_tempRoot}");
                return;
            }
            catch
            {
                Thread.Sleep(500);
            }
        }
        R.Line();
        R.Line($"Временные профили удалить не удалось (занято?): {_tempRoot} — удалите вручную.");
    }

    static (string Profile, string Log) NewProfile()
    {
        int n = Interlocked.Increment(ref _profileCounter);
        string profile = Path.Combine(_tempRoot, $"profile-{n}");
        Directory.CreateDirectory(profile);
        return (profile, Path.Combine(_tempRoot, $"chrome-{n}.log"));
    }

    static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    static string CountByName()
    {
        var snap = ProcessTree.Snapshot();
        int chrome = snap.Count(p => p.Exe.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase));
        int crashpad = snap.Count(p => p.Exe.Equals("crashpad_handler.exe", StringComparison.OrdinalIgnoreCase));
        return $"chrome.exe ×{chrome}, crashpad_handler.exe ×{crashpad}";
    }

    static int Count(string text, string word)
    {
        int c = 0;
        for (int i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + word.Length, StringComparison.Ordinal)) c++;
        return c;
    }

    static long Median(List<long> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }
}
