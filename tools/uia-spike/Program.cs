// Спайк пригодности UIA-снапшота для модели (ADR-008).
// Консольная утилита вне основного решения: измеряет объём дерева, стабильность
// отпечатка, частоту ложной неоднозначности в допусках, прогрев accessibility в
// Chromium, стоимость построения и одновременность захвата кадра + снапшота.
//
// Зависимости: FlaUI.UIA3 (обёртка над UIA3 COM). В продакшен-код не идёт.

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;
using Interop.UIAutomationClient;

namespace UiaSpike;

internal static class Program
{
    // Actionable-паттерны: элемент считается «рукоприменимым», если поддерживает хотя бы один.
    // Состав — по смыслу control patterns UIA (Invoke/Value/Selection/Toggle/...).
    private static readonly HashSet<string> ActionablePatternIds = new(StringComparer.Ordinal)
    {
        "Invoke", "Value", "RangeValue", "Selection", "SelectionItem",
        "Scroll", "ScrollItem", "ExpandCollapse", "Toggle", "GridItem",
        "TableItem", "MultipleView", "Transform", "Transform2", "Dock",
        "ItemContainer", "VirtualizedItem", "Drag", "DropTarget",
        "SpreadsheetItem", "Annotation", "TextEdit",
    };

    private static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                [] => PrintUsage(),
                ["windows", ..] => CmdWindows(),
                ["capture", var target, ..] => CmdCapture(target, ArgsOptions(args, 2)),
                ["count", var target, ..] => CmdCount(target),
                ["series", var target, ..] => CmdSeries(target, ArgsOptions(args, 2)),
                ["collisions", var target, ..] => CmdCollisions(target, ArgsOptions(args, 2)),
                ["warmup", var target, ..] => CmdWarmup(target, ArgsOptions(args, 2)),
                ["som", var target, ..] => CmdSom(target, ArgsOptions(args, 2)),
                ["dynamics", var target, ..] => CmdDynamics(target, ArgsOptions(args, 2)),
                ["cache", var target, ..] => CmdCache(target, ArgsOptions(args, 2)),
                ["panel", var target, ..] => CmdPanel(target, ArgsOptions(args, 2)),
                _ => PrintUsage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 99;
        }
    }

    // ---- разбор --key value после позиционных ----
    private static Dictionary<string, string> ArgsOptions(string[] args, int start)
    {
        var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = start; i + 1 <= args.Length;)
        {
            if (i < args.Length && args[i].StartsWith("--") && i + 1 < args.Length)
            {
                opt[args[i][2..]] = args[i + 1];
                i += 2;
            }
            else i++;
        }
        return opt;
    }

    private static int PrintUsage()
    {
        Console.WriteLine("""
            uia-spike — спайк пригодности UIA-снапшота (ADR-008)

            Команды:
              windows                       список top-level окон (pid, заголовок, класс, процесс)
              capture <pid|title> [opts]    полный снапшот: счёт узлов, фильтр, время, строки
                  --out <file.json>         выгрузить NodeInfo[] в JSON
                  --lines                   напечатать строки формата ADR
              count <pid|title>             только быстрый счёт узлов (без свойств)
              series <pid|title> [opts]     N снимков для стабильности отпечатка
                  --count <N>               число снимков (по умолч. 3)
                  --delay <ms>              пауза между снимками (по умолч. 800)
                  --action none|scroll|tab  безобидное действие между снимками
              collisions <pid|title>        доля элементов с >1 кандидатом в допусках ADR
              warmup <pid|title> [opts]     два снимка с интервалом (прогрев Chromium)
                  --delay <ms>              пауза (по умолч. 3000)
              som <pid|title> [opts]        кадр+снапшот одним захватом, метки на PNG
                  --out <file.png>
              dynamics <pid|title> [opts]   часть 2: снимок -> действие -> снимок, метрики правила
                  --action none|scroll|end|resize|tab|panel|newtab
                  --wheel <N>               тиков колеса за скролл (по умолч. 3, вниз)
                  --settle <ms>             пауза после действия (по умолч. 1200)
                  --chord <keys>            аккорд для action=tab/panel: ctrl+tab|ctrl+pgdn|ctrl+b
              cache <pid|title> [opts]      часть 2: capture без кеша vs с CacheRequest
                  --mode <csv>              plain,walker,subtree (по умолч. все)
                  --repeat <N>              повторов на режим (по умолч. 3)
              panel <pid|title> [opts]      часть 2: снапшот поддерева (панели) vs окно
                  --match <substr>          подстрока Name/AutomationId якоря панели
            """);
        return 1;
    }

    // ======================= NodeInfo =======================

    // Один узел дерева со снятыми свойствами. Геометрия — в экранных координатах.
    public sealed class NodeInfo
    {
        public string ControlType { get; init; } = "";
        public string Name { get; init; } = "";
        public string AutomationId { get; init; } = "";
        public double X { get; init; }
        public double Y { get; init; }
        public double W { get; init; }
        public double H { get; init; }
        public bool IsOffscreen { get; init; }
        public string Patterns { get; init; } = "";
        public bool Actionable { get; init; }

        // RuntimeId UIA — уникален для живого элемента в пределах сессии; oracle «тот же
        // элемент» для замеров динамики (часть 2). Пустой, если провайдер не отдал.
        public string RuntimeId { get; init; } = "";

        // Ключ состава для сравнения снимков между режимами чтения (часть 2, CacheRequest).
        public string Key => $"{ControlType}|{Name}|{(int)X},{(int)Y}|{(int)W}x{(int)H}";
        public bool HasName => !string.IsNullOrWhiteSpace(Name);
        public double Cx => X + W / 2.0;
        public double Cy => Y + H / 2.0;

        // Отпечаток ADR: роль + имя + геометрия(размер) + позиция в порядке чтения.
        // Позиция передаётся снаружи (зависит от состава среза).
        public string Fingerprint(int order) =>
            $"{ControlType}|{Name}|{Fmt(W)}x{Fmt(H)}|#{order}";

        // Размер, округлённый до 1 знака — отпечаток не должен дрожать на субпикселях.
        private static string Fmt(double v) => Math.Round(v, 0).ToString("0");
    }

    // ======================= Движок снапшота =======================

    private sealed class SnapshotEngine
    {
        private readonly UIA3Automation _automation;
        public SnapshotEngine(UIA3Automation automation) => _automation = automation;

        // Быстрый счёт узлов ControlView-дерева: только walker, без свойств.
        public int CountNodes(AutomationElement root)
        {
            var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
            int count = 0;
            WalkCount(root, walker, ref count);
            return count;
        }

        private static void WalkCount(AutomationElement el, ITreeWalker walker, ref int count)
        {
            count++;
            var child = walker.GetFirstChild(el);
            while (child != null)
            {
                WalkCount(child, walker, ref count);
                child = walker.GetNextSibling(child);
            }
        }

        // Полный обход со снятием свойств. Возвращает узлы в порядке чтения (pre-order DFS).
        public List<NodeInfo> Capture(AutomationElement root)
        {
            var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
            var list = new List<NodeInfo>(1024);
            WalkCapture(root, walker, list);
            return list;
        }

        private void WalkCapture(AutomationElement el, ITreeWalker walker, List<NodeInfo> list)
        {
            list.Add(ToNodeInfo(el));
            var child = walker.GetFirstChild(el);
            while (child != null)
            {
                WalkCapture(child, walker, list);
                child = walker.GetNextSibling(child);
            }
        }

        private NodeInfo ToNodeInfo(AutomationElement el)
        {
            // Свойства UIA могут бросать PropertyNotSupportedException: не все провайдеры
            // поддерживают Name/AutomationId/ControlType. Safe-access — это сама реальность.
            double x = 0, y = 0, w = 0, h = 0;
            try { var r = el.BoundingRectangle; x = r.X; y = r.Y; w = r.Width; h = r.Height; } catch { }
            string[] patternNames = Safe(el, () => (el.GetSupportedPatterns() ?? [])
                .Select(p => (p?.Name ?? "").Replace("Pattern", ""))
                .Where(n => !string.IsNullOrEmpty(n)).ToArray(), Array.Empty<string>());
            bool actionable = patternNames.Any(ActionablePatternIds.Contains);

            return new NodeInfo
            {
                ControlType = Safe(el, () => el.ControlType.ToString(), ""),
                Name = Safe(el, () => el.Name ?? "", ""),
                AutomationId = Safe(el, () => el.AutomationId ?? "", ""),
                X = x, Y = y, W = w, H = h,
                IsOffscreen = Safe(el, () => el.IsOffscreen, false),
                Patterns = string.Join(",", patternNames),
                Actionable = actionable,
                RuntimeId = Safe(el, () => string.Join(".", el.FrameworkAutomationElement.RuntimeId ?? Array.Empty<int>()), ""),
            };
        }

        private static T Safe<T>(AutomationElement el, Func<T> get, T fallback)
        {
            try { return get(); }
            catch { return fallback; }
        }
    }

    // ======================= Перечисление окон (Win32 EnumWindows) =======================

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // Все видимые top-level окна сессии с непустым заголовком.
    private static List<(IntPtr hwnd, uint pid, string title, string cls)> EnumTopWindows()
    {
        var list = new List<(IntPtr, uint, string, string)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            var title = new StringBuilder(512);
            GetWindowText(h, title, title.Capacity);
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(h, out var pid);
            var cls = new StringBuilder(256);
            GetClassName(h, cls, cls.Capacity);
            list.Add((h, pid, title.ToString(), cls.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ======================= Поиск окна =======================

    private static AutomationElement ResolveWindow(UIA3Automation automation, string target) =>
        automation.FromHandle(FindTargetHwnd(target));

    // HWND top-level окна по pid или подстроке заголовка (для SendInput/SetWindowPos).
    private static IntPtr FindTargetHwnd(string target)
    {
        var wins = EnumTopWindows();
        IntPtr hwnd;
        if (int.TryParse(target, out var pid))
        {
            hwnd = wins.FirstOrDefault(w => w.pid == (uint)pid).hwnd;
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"Окно с ProcessId={pid} не найдено среди {wins.Count} видимых top-level окон.");
        }
        else
        {
            hwnd = wins.FirstOrDefault(w => w.title.Contains(target, StringComparison.OrdinalIgnoreCase)).hwnd;
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"Окно с заголовком, содержащим «{target}», не найдено. Видимых окон: {wins.Count}.");
        }
        return hwnd;
    }

    // ======================= Команды =======================

    private static int CmdWindows()
    {
        var wins = EnumTopWindows();
        Console.WriteLine($"# видимых top-level окон: {wins.Count}");
        Console.WriteLine($"{"PID",-8} {"PROCESS",-24} {"CLS",-22} TITLE");
        foreach (var (hwnd, pid, title, cls) in wins)
        {
            var proc = SafeProcessName((int)pid);
            Console.WriteLine($"{pid,-8} {Trunc(proc, 24),-24} {Trunc(cls, 22),-22} {Trunc(title, 56)}");
        }
        return 0;
    }

    private static int CmdCount(string target)
    {
        using var automation = new UIA3Automation();
        var window = ResolveWindow(automation, target);
        var engine = new SnapshotEngine(automation);
        var sw = Stopwatch.StartNew();
        int total = engine.CountNodes(window);
        sw.Stop();
        Console.WriteLine($"window={Trunc(WindowName(window),40)} pid={WindowPid(window)}");
        Console.WriteLine($"controlview_nodes_total = {total}");
        Console.WriteLine($"count_elapsed_ms = {sw.ElapsedMilliseconds}");
        return 0;
    }

    private static int CmdCapture(string target, Dictionary<string, string> opt)
    {
        using var automation = new UIA3Automation();
        var window = ResolveWindow(automation, target);
        var engine = new SnapshotEngine(automation);

        // быстрый счёт (без свойств)
        var swCount = Stopwatch.StartNew();
        int total = engine.CountNodes(window);
        swCount.Stop();

        // полный захват со свойствами
        var swCap = Stopwatch.StartNew();
        var all = engine.Capture(window);
        swCap.Stop();

        // фильтр ADR: actionable ИЛИ собственный текст, исключая offscreen
        var filtered = all
            .Where(n => !n.IsOffscreen && n.W > 0 && n.H > 0)
            .Where(n => n.Actionable || n.HasName)
            .ToList();
        int actionableOnly = filtered.Count(n => n.Actionable);
        int nameOnly = filtered.Count(n => !n.Actionable);
        int withAutoId = filtered.Count(n => !string.IsNullOrWhiteSpace(n.AutomationId));

        Console.WriteLine($"window={Trunc(WindowName(window),40)} pid={WindowPid(window)}");
        Console.WriteLine($"controlview_nodes_total = {total}");
        Console.WriteLine($"captured_with_props = {all.Count}");
        Console.WriteLine($"after_filter = {filtered.Count}");
        Console.WriteLine($"  actionable_only = {actionableOnly}");
        Console.WriteLine($"  with_name_only = {nameOnly}");
        Console.WriteLine($"  with_automationid = {withAutoId} ({Pct(withAutoId, filtered.Count)}%)");
        Console.WriteLine($"count_elapsed_ms = {swCount.ElapsedMilliseconds}");
        Console.WriteLine($"capture_elapsed_ms = {swCap.ElapsedMilliseconds}");
        Console.WriteLine($"fits_120_budget = {(filtered.Count <= 120)}");

        if (opt.TryGetValue("lines", out _))
        {
            Console.WriteLine("--- строки формата ADR (первые 40) ---");
            int i = 0;
            foreach (var n in filtered.Take(40))
            {
                var state = n.IsOffscreen ? "offscreen" : "enabled";
                Console.WriteLine($"#{i++} {n.ControlType.ToLowerInvariant()} {Quote(n.Name)} {state} {(int)n.W}x{(int)n.H} {(n.Actionable ? "[act]" : "")}");
            }
            if (filtered.Count > 40) Console.WriteLine($"... свёрнуто ещё {filtered.Count - 40}");
        }

        if (opt.TryGetValue("out", out var outFile))
        {
            File.WriteAllText(outFile, JsonSerializer.Serialize(new
            {
                window = window.Name,
                pid = WindowPid(window),
                controlview_nodes_total = total,
                after_filter = filtered.Count,
                count_elapsed_ms = swCount.ElapsedMilliseconds,
                capture_elapsed_ms = swCap.ElapsedMilliseconds,
                nodes = filtered,
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"json -> {outFile}");
        }
        return 0;
    }

    private static int CmdSeries(string target, Dictionary<string, string> opt)
    {
        int count = int.Parse(opt.GetValueOrDefault("count", "3"));
        int delay = int.Parse(opt.GetValueOrDefault("delay", "800"));
        string action = opt.GetValueOrDefault("action", "none");

        using var automation = new UIA3Automation();
        var window = ResolveWindow(automation, target);
        var engine = new SnapshotEngine(automation);

        var snapshots = new List<List<NodeInfo>>();
        for (int i = 0; i < count; i++)
        {
            var sw = Stopwatch.StartNew();
            var all = engine.Capture(window);
            sw.Stop();
            var filtered = all.Where(n => !n.IsOffscreen && n.W > 0 && n.H > 0 && (n.Actionable || n.HasName)).ToList();
            snapshots.Add(filtered);
            Console.WriteLine($"snapshot[{i}] filtered={filtered.Count} ms={sw.ElapsedMilliseconds}");
            if (i < count - 1)
            {
                DoAction(window, action);
                Thread.Sleep(delay);
            }
        }

        // стабильность относительно первого снимка.
        // Отпечаток И AutomationId НЕ уникальны (дубли «UpButton», одинаковые Pane) —
        // поэтому мультикарты, а не словари.
        var baseSnap = snapshots[0];
        var fpBase = baseSnap.Select((n, i) => (n, i))
            .ToLookup(p => p.n.Fingerprint(p.i), p => p.n);
        var autoIdBase = baseSnap.Where(n => !string.IsNullOrWhiteSpace(n.AutomationId))
            .ToLookup(n => n.AutomationId!, n => n);
        // дубли AutomationId в базовом снимке — отдельная диагностика
        var dupAutoIds = autoIdBase.Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Console.WriteLine("--- стабильность относительно snapshot[0] ---");
        Console.WriteLine($"snapshot0_duplicate_autoids = {dupAutoIds.Count} ({string.Join(", ", dupAutoIds.Take(8))})");
        for (int s = 1; s < snapshots.Count; s++)
        {
            var cur = snapshots[s];
            // по отпечатку
            int fpMatch = 0;
            foreach (var (n, i) in cur.Select((n, i) => (n, i)))
            {
                if (fpBase.Contains(n.Fingerprint(i))) fpMatch++;
            }
            // по AutomationId
            int idMatch = 0, idTotal = 0;
            foreach (var n in cur)
            {
                if (!string.IsNullOrWhiteSpace(n.AutomationId))
                {
                    idTotal++;
                    if (autoIdBase.Contains(n.AutomationId)) idMatch++;
                }
            }
            // инвалитация по цели (роль+имя, ±5% размер, смещение центра ≤ полувысоты)
            int invMatch = CountInvalidateMatches(baseSnap, cur);
            Console.WriteLine($"snapshot[{s}] fp_match={fpMatch}/{cur.Count} ({Pct(fpMatch, cur.Count)}%) " +
                              $"invalidate_match={invMatch}/{cur.Count} ({Pct(invMatch, cur.Count)}%) " +
                              $"autoid_match={idMatch}/{idTotal}");
        }

        // доля непустого AutomationId в первом снимке
        int aid = baseSnap.Count(n => !string.IsNullOrWhiteSpace(n.AutomationId));
        Console.WriteLine($"autoid_present_snapshot0 = {aid}/{baseSnap.Count} ({Pct(aid, baseSnap.Count)}%)");
        return 0;
    }

    // Инвалидация по цели ADR: совпали роль+имя, размер в ±5%, смещение центра ≤ полувысоты.
    // Для каждого элемента base ищем хотя бы одного кандидата в cur.
    private static int CountInvalidateMatches(List<NodeInfo> baseSnap, List<NodeInfo> cur)
    {
        int match = 0;
        foreach (var b in baseSnap)
        {
            bool found = false;
            foreach (var c in cur)
            {
                if (!string.Equals(b.ControlType, c.ControlType, StringComparison.Ordinal)) continue;
                if (!string.Equals(b.Name, c.Name, StringComparison.Ordinal)) continue;
                if (b.W <= 0 || b.H <= 0) continue;
                if (Math.Abs(c.W - b.W) / b.W > 0.05) continue;
                if (Math.Abs(c.H - b.H) / b.H > 0.05) continue;
                double dx = Math.Abs(c.Cx - b.Cx);
                double dy = Math.Abs(c.Cy - b.Cy);
                if (dy > b.H / 2.0) continue;
                // смещение по X — ADR не оговаривает; берём нестрого: dx ≤ полуширины
                if (dx > b.W / 2.0) continue;
                found = true;
                break;
            }
            if (found) match++;
        }
        return match;
    }

    private static int CmdCollisions(string target, Dictionary<string, string> opt)
    {
        using var automation = new UIA3Automation();
        var window = ResolveWindow(automation, target);
        var engine = new SnapshotEngine(automation);
        var all = engine.Capture(window);
        var snap = all.Where(n => !n.IsOffscreen && n.W > 0 && n.H > 0 && (n.Actionable || n.HasName)).ToList();

        int multi = 0, maxCands = 0;
        var examples = new List<(NodeInfo node, int cands)>();
        for (int i = 0; i < snap.Count; i++)
        {
            var b = snap[i];
            if (b.W <= 0 || b.H <= 0) continue;
            int cands = 0;
            for (int j = 0; j < snap.Count; j++)
            {
                if (i == j) continue;
                var c = snap[j];
                if (!string.Equals(b.ControlType, c.ControlType, StringComparison.Ordinal)) continue;
                if (!string.Equals(b.Name, c.Name, StringComparison.Ordinal)) continue;
                if (Math.Abs(c.W - b.W) / b.W > 0.05) continue;
                if (Math.Abs(c.H - b.H) / b.H > 0.05) continue;
                double dy = Math.Abs(c.Cy - b.Cy);
                double dx = Math.Abs(c.Cx - b.Cx);
                if (dy > b.H / 2.0) continue;
                if (dx > b.W / 2.0) continue;
                cands++;
            }
            if (cands > 0)
            {
                multi++;
                examples.Add((b, cands + 1));
            }
            if (cands + 1 > maxCands) maxCands = cands + 1;
        }

        Console.WriteLine($"window={Trunc(WindowName(window),40)} filtered={snap.Count}");
        Console.WriteLine($"elements_with_multiple_candidates = {multi} ({Pct(multi, snap.Count)}%)");
        Console.WriteLine($"max_candidates_in_tolerance = {maxCands}");
        Console.WriteLine("--- примеры (до 15) ---");
        foreach (var (node, c) in examples.Take(15))
            Console.WriteLine($"  {node.ControlType} {Quote(Trunc(node.Name, 30))} {(int)node.W}x{(int)node.H} @({(int)node.X},{(int)node.Y}) -> {c} кандидата");
        return 0;
    }

    private static int CmdWarmup(string target, Dictionary<string, string> opt)
    {
        int delay = int.Parse(opt.GetValueOrDefault("delay", "3000"));
        using var automation = new UIA3Automation();
        var window = ResolveWindow(automation, target);
        var engine = new SnapshotEngine(automation);

        var sw1 = Stopwatch.StartNew();
        var snap1 = engine.Capture(window);
        sw1.Stop();
        var filt1 = snap1.Where(n => !n.IsOffscreen && n.W > 0 && n.H > 0 && (n.Actionable || n.HasName)).ToList();
        Console.WriteLine($"snapshot[0] (сразу): total={snap1.Count} filtered={filt1.Count} ms={sw1.ElapsedMilliseconds}");

        Thread.Sleep(delay);

        var sw2 = Stopwatch.StartNew();
        var snap2 = engine.Capture(window);
        sw2.Stop();
        var filt2 = snap2.Where(n => !n.IsOffscreen && n.W > 0 && n.H > 0 && (n.Actionable || n.HasName)).ToList();
        Console.WriteLine($"snapshot[1] (через {delay}ms): total={snap2.Count} filtered={filt2.Count} ms={sw2.ElapsedMilliseconds}");

        int deltaTotal = snap2.Count - snap1.Count;
        int deltaFilt = filt2.Count - filt1.Count;
        Console.WriteLine($"delta_total = {deltaTotal:+#;-#;0}");
        Console.WriteLine($"delta_filtered = {deltaFilt:+#;-#;0}");
        Console.WriteLine($"composition_stable = {(deltaTotal == 0 && deltaFilt == 0)}");
        return 0;
    }

    private static int CmdSom(string target, Dictionary<string, string> opt)
    {
        using var automation = new UIA3Automation();
        var window = ResolveWindow(automation, target);
        var engine = new SnapshotEngine(automation);

        var rect = window.BoundingRectangle;
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new InvalidOperationException("Окно без видимого прямоугольника.");

        var overall = Stopwatch.StartNew();

        // 1) Снимаем кадр окна (BitBlt через Graphics.CopyFromScreen).
        var tShot = Stopwatch.StartNew();
        using var bmp = new Bitmap((int)rect.Width, (int)rect.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen((int)rect.X, (int)rect.Y, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        }
        tShot.Stop();

        // 2) Сразу же — UIA-снапшот того же окна. Зазор = артефакт рассинхрона.
        var tUia = Stopwatch.StartNew();
        var all = engine.Capture(window);
        tUia.Stop();
        overall.Stop();

        var filtered = all.Where(n => !n.IsOffscreen && n.W > 0 && n.H > 0 && (n.Actionable || n.HasName)).ToList();

        // Наносим метки: прямоугольник + индекс в центре.
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(255, 230, 0, 80), 3);
            using var fill = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
            using var txt = new SolidBrush(Color.White);
            using var font = new Font("Segoe UI", 11, FontStyle.Bold);
            for (int i = 0; i < Math.Min(filtered.Count, 200); i++)
            {
                var n = filtered[i];
                // координаты UIA — экранные; на кадре сдвигаем на origin окна
                var lx = (int)(n.X - rect.X);
                var ly = (int)(n.Y - rect.Y);
                if (lx < 0 || ly < 0) continue;
                var lw = (int)n.W; var lh = (int)n.H;
                g.DrawRectangle(pen, lx, ly, lw, lh);
                var label = i.ToString();
                var sz = g.MeasureString(label, font);
                g.FillRectangle(fill, lx, ly, sz.Width + 6, sz.Height + 2);
                g.DrawString(label, font, txt, lx + 3, ly + 1);
            }
        }

        var outFile = opt.GetValueOrDefault("out", "som.png");
        bmp.Save(outFile, ImageFormat.Png);

        // Проверка совпадения координат: доля элементов, чей прямоугольник целиком внутри кадра.
        int inside = filtered.Count(n => n.X >= rect.X && n.Y >= rect.Y && n.X + n.W <= rect.X + rect.Width && n.Y + n.H <= rect.Y + rect.Height);

        Console.WriteLine($"window={Trunc(WindowName(window),40)} rect=({(int)rect.X},{(int)rect.Y}) {(int)rect.Width}x{(int)rect.Height}");
        Console.WriteLine($"screenshot_ms = {tShot.ElapsedMilliseconds}");
        Console.WriteLine($"uia_capture_ms = {tUia.ElapsedMilliseconds}");
        Console.WriteLine($"gap_shot_to_uia_ms = {tShot.ElapsedMilliseconds}"); // снимок первый -> зазор ~ 0
        Console.WriteLine($"total_ms = {overall.ElapsedMilliseconds}");
        Console.WriteLine($"filtered_marked = {Math.Min(filtered.Count, 200)} of {filtered.Count}");
        Console.WriteLine($"coords_inside_frame = {inside}/{filtered.Count} ({Pct(inside, filtered.Count)}%)");
        Console.WriteLine($"png -> {outFile}");
        Console.WriteLine($"single_capture_feasible = {(tUia.ElapsedMilliseconds < 500)}");
        return 0;
    }

    // ======================= часть 2: программный ввод (SendInput/SetWindowPos) =======================

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11, VK_TAB = 0x09, VK_PGDN = 0x22, VK_END = 0x23, VK_T = 0x54, VK_B = 0x42;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION u; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    // Вывести окно на передний план (ввод пойдёт туда). Свёрнутое — развернуть.
    // Windows даёт foreground тому, кто получил последний ввод, — поэтому сперва
    // «нажимаем» ALT (легальный обход foreground lock), затем AttachThreadInput.
    private static void BringToFront(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        SendChord(0x12 /* VK_MENU */);
        SetForegroundWindow(hwnd);
        if (GetForegroundWindow() != hwnd)
        {
            var fg = GetForegroundWindow();
            var fgThread = GetWindowThreadProcessId(fg, out _);
            var thisThread = GetCurrentThreadId();
            AttachThreadInput(thisThread, fgThread, true);
            SetForegroundWindow(hwnd);
            AttachThreadInput(thisThread, fgThread, false);
        }
        Thread.Sleep(350);
        if (GetForegroundWindow() != hwnd)
            throw new InvalidOperationException("Не удалось вывести окно на передний план (foreground lock).");
    }

    // Колесо мыши: тики вниз (ticks>0) или вверх, курсор — в центр окна.
    private static void SendWheel(IntPtr hwnd, int ticks)
    {
        GetWindowRect(hwnd, out var r);
        SetCursorPos((r.left + r.right) / 2, (r.top + r.bottom) / 2);
        Thread.Sleep(120);
        var wheel = new INPUT
        {
            type = INPUT_MOUSE,
            u = new() { mi = new MOUSEINPUT { mouseData = unchecked((uint)(-ticks * 120)), dwFlags = MOUSEEVENTF_WHEEL } },
        };
        for (int i = 0; i < Math.Abs(ticks); i++)
        {
            SendInput(1, new[] { wheel }, Marshal.SizeOf<INPUT>());
            Thread.Sleep(60);
        }
        Thread.Sleep(120);
    }

    // Аккорд клавиш: последовательные down, затем up в обратном порядке.
    private static void SendChord(params ushort[] vks)
    {
        foreach (var vk in vks)
        {
            SendInput(1, new[] { new INPUT { type = INPUT_KEYBOARD, u = new() { ki = new KEYBDINPUT { wVk = vk } } } }, Marshal.SizeOf<INPUT>());
            Thread.Sleep(40);
        }
        foreach (var vk in vks.Reverse())
        {
            SendInput(1, new[] { new INPUT { type = INPUT_KEYBOARD, u = new() { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } } }, Marshal.SizeOf<INPUT>());
            Thread.Sleep(40);
        }
        Thread.Sleep(80);
    }

    // Ресайз окна на долю k (0.1 = +10%). Возвращает прежние (w, h) для восстановления.
    private static (int w, int h) ResizeBy(IntPtr hwnd, double k)
    {
        GetWindowRect(hwnd, out var r);
        int w = r.right - r.left, h = r.bottom - r.top;
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w + (int)(w * k), h + (int)(h * k), 0x2 /*SWP_NOMOVE*/ | 0x4 /*SWP_NOZORDER*/);
        return (w, h);
    }

    private static void RestoreSize(IntPtr hwnd, int w, int h) =>
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h, 0x2 | 0x4);

    // ======================= часть 2: динамика отпечатка под действием =======================

    private static int CmdDynamics(string target, Dictionary<string, string> opt)
    {
        string action = opt.GetValueOrDefault("action", "scroll");
        int settleMs = int.Parse(opt.GetValueOrDefault("settle", "1200"));
        int wheelTicks = int.Parse(opt.GetValueOrDefault("wheel", "3"));

        var hwnd = FindTargetHwnd(target);
        using var automation = new UIA3Automation();
        var window = automation.FromHandle(hwnd);
        var engine = new SnapshotEngine(automation);

        BringToFront(hwnd);

        var snapA = Filtered(engine.Capture(window));
        var tA = snapA.Count;

        (int w, int h) oldSize = default;
        if (action == "resize") oldSize = ResizeBy(hwnd, 0.10);
        else ApplyAction(hwnd, action, wheelTicks, opt);
        Thread.Sleep(settleMs);

        var snapB = Filtered(engine.Capture(window));
        if (action == "resize") { RestoreSize(hwnd, oldSize.w, oldSize.h); Thread.Sleep(400); }

        // Oracle «тот же элемент» — RuntimeId: уникален для живого элемента, переживает
        // перемещение и смену геометрии, умирает вместе с элементом (виртуализация).
        var byRid = new Dictionary<string, NodeInfo>();
        foreach (var n in snapB)
            if (n.RuntimeId.Length > 0 && !byRid.ContainsKey(n.RuntimeId)) byRid[n.RuntimeId] = n;
        var keysA = snapA.Select(n => n.Key).ToHashSet();
        var keysB = snapB.Select(n => n.Key).ToHashSet();

        int ridKnown = 0, alive = 0, held = 0, lostFalse = 0, silentSub = 0, goneCorrect = 0;
        int subSameRect = 0, subDiffRect = 0; // подмена в пределах той же геометрии vs со смещением
        var examplesSame = new List<(NodeInfo a, NodeInfo b)>();
        var examplesDiff = new List<(NodeInfo a, NodeInfo b)>();
        foreach (var a in snapA)
        {
            bool known = a.RuntimeId.Length > 0;
            if (known) ridKnown++;
            bool oracleAlive = known && byRid.ContainsKey(a.RuntimeId);
            if (oracleAlive) alive++;
            var cand = FindAdrCandidate(a, snapB);
            if (cand != null)
            {
                // правило сказало «тот же элемент»: верно, только если RuntimeId совпал
                if (known && a.RuntimeId == cand.RuntimeId) held++;
                else
                {
                    silentSub++;
                    // совпадающий прямоугольник (±2px) — клик уйдёт в ту же точку,
                    // разный — модель промахнётся по цели молча
                    bool sameRect = Math.Abs(cand.X - a.X) <= 2 && Math.Abs(cand.Y - a.Y) <= 2 &&
                                    Math.Abs(cand.W - a.W) <= 2 && Math.Abs(cand.H - a.H) <= 2;
                    if (sameRect) { subSameRect++; if (examplesSame.Count < 6) examplesSame.Add((a, cand)); }
                    else { subDiffRect++; if (examplesDiff.Count < 6) examplesDiff.Add((a, cand)); }
                }
            }
            else if (oracleAlive) lostFalse++;
            else goneCorrect++;
        }

        int found = held + silentSub;
        Console.WriteLine($"window={Trunc(WindowName(window), 40)} action={action} settle={settleMs}ms");
        Console.WriteLine($"snapshotA_filtered = {tA}  snapshotB_filtered = {snapB.Count}  " +
                          $"(composition: +{keysB.Except(keysA).Count()} new / -{keysA.Except(keysB).Count()} gone)");
        Console.WriteLine($"rid_known_A = {ridKnown}/{tA}  oracle_alive_in_B = {alive}  oracle_gone = {ridKnown - alive}");
        Console.WriteLine("--- правило ADR против oracle (RuntimeId) ---");
        Console.WriteLine($"held                = {held}/{tA} ({Pct(held, tA)}%) — правило нашло тот же элемент");
        Console.WriteLine($"lost_false          = {lostFalse}/{alive} ({Pct(lostFalse, alive)}%) — цель жива, правило её потеряло");
        Console.WriteLine($"silent_substitution = {silentSub}/{tA} ({Pct(silentSub, tA)}%) — правило указало на ДРУГОЙ элемент");
        Console.WriteLine($"  sub_same_rect     = {subSameRect}/{tA} ({Pct(subSameRect, tA)}%) — другой элемент, та же точка (клик не промахнётся)");
        Console.WriteLine($"  sub_diff_rect     = {subDiffRect}/{tA} ({Pct(subDiffRect, tA)}%) — другой элемент ДРУГОЙ геометрии (тихий промах)");
        Console.WriteLine($"silent_of_found     = {silentSub}/{found} ({Pct(silentSub, found)}%) — условная доля подмены среди найденных");
        Console.WriteLine($"gone_correct        = {goneCorrect}/{ridKnown - alive} ({Pct(goneCorrect, ridKnown - alive)}%) — исчезнувшие, правило честно сказало «изменился»");
        if (examplesDiff.Count > 0)
        {
            Console.WriteLine("--- примеры подмены с другой геометрией (тихий промах) ---");
            foreach (var (a, b) in examplesDiff)
                Console.WriteLine($"  base {a.ControlType} {Quote(Trunc(a.Name, 28))} {(int)a.W}x{(int)a.H} @({(int)a.X},{(int)a.Y}) rid={a.RuntimeId}\n" +
                                  $"   -> {b.ControlType} {Quote(Trunc(b.Name, 28))} {(int)b.W}x{(int)b.H} @({(int)b.X},{(int)b.Y}) rid={b.RuntimeId}");
        }
        if (examplesSame.Count > 0)
        {
            Console.WriteLine("--- примеры подмены в той же точке ---");
            foreach (var (a, b) in examplesSame)
                Console.WriteLine($"  base {a.ControlType} {Quote(Trunc(a.Name, 28))} {(int)a.W}x{(int)a.H} @({(int)a.X},{(int)a.Y}) rid={a.RuntimeId}\n" +
                                  $"   -> {b.ControlType} {Quote(Trunc(b.Name, 28))} {(int)b.W}x{(int)b.H} @({(int)b.X},{(int)b.Y}) rid={b.RuntimeId}");
        }
        return 0;
    }

    private static void ApplyAction(IntPtr hwnd, string action, int wheelTicks, Dictionary<string, string> opt)
    {
        switch (action)
        {
            case "none": break; // контроль: oracle на окне в покое
            case "scroll": SendWheel(hwnd, wheelTicks); break;
            case "end": SendChord(VK_CONTROL, VK_END); break; // прокрутка до конца списка/страницы
            case "newtab": SendChord(VK_CONTROL, VK_T); break; // открыть новую вкладку (NTP)
            case "tab":
            case "panel":
                var chord = ParseChord(opt.GetValueOrDefault("chord", action == "tab" ? "ctrl+tab" : "ctrl+b"));
                SendChord(chord);
                break;
            default: throw new InvalidOperationException($"Неизвестное действие: {action}");
        }
    }

    private static ushort[] ParseChord(string chord) => chord.Split('+').Select<string, ushort>(part => part.Trim().ToLowerInvariant() switch
    {
        "ctrl" => VK_CONTROL,
        "tab" => VK_TAB,
        "pgdn" => VK_PGDN,
        "end" => VK_END,
        "t" => VK_T,
        "b" => VK_B,
        _ => throw new InvalidOperationException($"Неизвестная клавиша аккорда: {part}"),
    }).ToArray();

    // Правило ADR: роль+имя, размер ±5%, смещение центра ≤ полувысоты (и ≤ полуширины —
    // оговорка прототипа части 1). Среди прошедших допуск — ближайший по центру
    // (эмуляция разрешения hit-test'ом центра, как в обновлённом ADR).
    private static NodeInfo? FindAdrCandidate(NodeInfo b, List<NodeInfo> cur)
    {
        NodeInfo? best = null;
        double bestDist = double.MaxValue;
        foreach (var c in cur)
        {
            if (!string.Equals(b.ControlType, c.ControlType, StringComparison.Ordinal)) continue;
            if (!string.Equals(b.Name, c.Name, StringComparison.Ordinal)) continue;
            if (b.W <= 0 || b.H <= 0 || c.W <= 0 || c.H <= 0) continue;
            if (Math.Abs(c.W - b.W) / b.W > 0.05) continue;
            if (Math.Abs(c.H - b.H) / b.H > 0.05) continue;
            if (Math.Abs(c.Cy - b.Cy) > b.H / 2.0) continue;
            if (Math.Abs(c.Cx - b.Cx) > b.W / 2.0) continue;
            double d = Math.Abs(c.Cx - b.Cx) + Math.Abs(c.Cy - b.Cy);
            if (d < bestDist) { bestDist = d; best = c; }
        }
        return best;
    }

    private static List<NodeInfo> Filtered(List<NodeInfo> all) =>
        all.Where(n => !n.IsOffscreen && n.W > 0 && n.H > 0 && (n.Actionable || n.HasName)).ToList();

    // ======================= часть 2: CacheRequest =======================

    // Actionable-паттерны в raw-идах UIA — тот же состав, что ActionablePatternIds,
    // но для кеша: GetSupportedPatterns живым чтением не кешируется.
    private static readonly (int id, string name)[] ActionablePatternsRaw =
    {
        ((int)UIA_PatternIds.UIA_InvokePatternId, "Invoke"),
        ((int)UIA_PatternIds.UIA_ValuePatternId, "Value"),
        ((int)UIA_PatternIds.UIA_RangeValuePatternId, "RangeValue"),
        ((int)UIA_PatternIds.UIA_SelectionPatternId, "Selection"),
        ((int)UIA_PatternIds.UIA_SelectionItemPatternId, "SelectionItem"),
        ((int)UIA_PatternIds.UIA_ScrollPatternId, "Scroll"),
        ((int)UIA_PatternIds.UIA_ScrollItemPatternId, "ScrollItem"),
        ((int)UIA_PatternIds.UIA_ExpandCollapsePatternId, "ExpandCollapse"),
        ((int)UIA_PatternIds.UIA_TogglePatternId, "Toggle"),
        ((int)UIA_PatternIds.UIA_GridItemPatternId, "GridItem"),
        ((int)UIA_PatternIds.UIA_TableItemPatternId, "TableItem"),
        ((int)UIA_PatternIds.UIA_MultipleViewPatternId, "MultipleView"),
        ((int)UIA_PatternIds.UIA_TransformPatternId, "Transform"),
        ((int)UIA_PatternIds.UIA_TransformPattern2Id, "Transform2"),
        ((int)UIA_PatternIds.UIA_DockPatternId, "Dock"),
        ((int)UIA_PatternIds.UIA_ItemContainerPatternId, "ItemContainer"),
        ((int)UIA_PatternIds.UIA_VirtualizedItemPatternId, "VirtualizedItem"),
        ((int)UIA_PatternIds.UIA_DragPatternId, "Drag"),
        ((int)UIA_PatternIds.UIA_DropTargetPatternId, "DropTarget"),
        ((int)UIA_PatternIds.UIA_SpreadsheetItemPatternId, "SpreadsheetItem"),
        ((int)UIA_PatternIds.UIA_AnnotationPatternId, "Annotation"),
        ((int)UIA_PatternIds.UIA_TextEditPatternId, "TextEdit"),
    };

    // CachedControlType (int) -> имя, совпадающее с ControlType.ToString() FlaUI.
    private static readonly Dictionary<int, string> ControlTypeNames =
        new Dictionary<int, string> { { 0, "Unknown" } };

    static Program()
    {
        foreach (var field in typeof(UIA_ControlTypeIds).GetFields())
        {
            var name = field.Name; // UIA_ButtonControlTypeId
            if (name.StartsWith("UIA_") && name.EndsWith("ControlTypeId"))
            {
                var shortName = name["UIA_".Length..^"ControlTypeId".Length];
                ControlTypeNames[(int)field.GetRawConstantValue()!] = shortName;
            }
        }
    }

    private static IUIAutomationCacheRequest BuildCacheRequest(IUIAutomation raw, Interop.UIAutomationClient.TreeScope scope)
    {
        var cr = raw.CreateCacheRequest();
        cr.TreeScope = scope;
        cr.AddProperty(UIA_PropertyIds.UIA_ControlTypePropertyId);
        cr.AddProperty(UIA_PropertyIds.UIA_NamePropertyId);
        cr.AddProperty(UIA_PropertyIds.UIA_AutomationIdPropertyId);
        cr.AddProperty(UIA_PropertyIds.UIA_BoundingRectanglePropertyId);
        cr.AddProperty(UIA_PropertyIds.UIA_IsOffscreenPropertyId);
        // CachedRuntimeId в этом interop-генерации отсутствует (живой GetRuntimeId есть),
        // поэтому rid в кеш не кладём: сравнение составов идёт по NodeInfo.Key.
        foreach (var (id, _) in ActionablePatternsRaw) cr.AddPattern(id);
        return cr;
    }

    private static IUIAutomationElement NativeOf(AutomationElement el) =>
        ((UIA3FrameworkAutomationElement)el.FrameworkAutomationElement).NativeElement;

    // Кеш-захват, режим walker: тот же ControlView-обход, что и без кеша, но каждый
    // узел приходит УЖЕ с кешем свойств (Get*ElementBuildCache) — живых чтений нет.
    private static List<NodeInfo> CaptureCachedWalker(UIA3Automation automation, AutomationElement window)
    {
        var raw = automation.NativeAutomation;
        var cr = BuildCacheRequest(raw, Interop.UIAutomationClient.TreeScope.TreeScope_Element);
        var walker = raw.ControlViewWalker;
        var list = new List<NodeInfo>(1024);
        WalkCachedWalker(NativeOf(window), walker, cr, list);
        return list;
    }

    private static void WalkCachedWalker(IUIAutomationElement el, IUIAutomationTreeWalker walker, IUIAutomationCacheRequest cr, List<NodeInfo> list)
    {
        list.Add(ToNodeInfoCached(el));
        var child = walker.GetFirstChildElementBuildCache(el, cr);
        while (child != null)
        {
            WalkCachedWalker(child, walker, cr, list);
            child = walker.GetNextSiblingElementBuildCache(child, cr);
        }
    }

    // Кеш-захват, режим subtree: ElementFromHandleBuildCache с TreeScope_Subtree строит
    // кеш всего поддерева за один вызов, дальше — локальная рекурсия по GetCachedChildren
    // (нуль живых чтений и нуль пошаговых walker-переходов).
    private static List<NodeInfo> CaptureCachedSubtree(UIA3Automation automation, IntPtr hwnd)
    {
        var raw = automation.NativeAutomation;
        var cr = BuildCacheRequest(raw, Interop.UIAutomationClient.TreeScope.TreeScope_Subtree);
        cr.TreeFilter = raw.ControlViewCondition; // тот же view, что у walker-режима
        var root = raw.ElementFromHandleBuildCache(hwnd, cr);
        var list = new List<NodeInfo>(1024);
        WalkCachedChildren(root, list);
        return list;
    }

    private static void WalkCachedChildren(IUIAutomationElement el, List<NodeInfo> list)
    {
        list.Add(ToNodeInfoCached(el));
        var children = el.GetCachedChildren();
        if (children == null) return;
        for (int i = 0; i < children.Length; i++)
            WalkCachedChildren(children.GetElement(i), list);
    }

    private static NodeInfo ToNodeInfoCached(IUIAutomationElement el)
    {
        var r = SafeRaw(() => el.CachedBoundingRectangle, default(tagRECT));
        var patterns = ActionablePatternsRaw.Where(p => SafeRaw(() => el.GetCachedPattern(p.id), null) != null)
            .Select(p => p.name).ToArray();
        return new NodeInfo
        {
            ControlType = ControlTypeNames.GetValueOrDefault(SafeRaw(() => (int)el.CachedControlType, 0), "Unknown"),
            Name = SafeRaw(() => el.CachedName ?? "", ""),
            AutomationId = SafeRaw(() => el.CachedAutomationId ?? "", ""),
            X = r.left, Y = r.top, W = r.right - r.left, H = r.bottom - r.top,
            IsOffscreen = SafeRaw(() => el.CachedIsOffscreen != 0, false),
            Patterns = string.Join(",", patterns),
            Actionable = patterns.Length > 0,
            RuntimeId = "",
        };
    }

    private static T SafeRaw<T>(Func<T> get, T fallback)
    {
        try { return get(); }
        catch { return fallback; }
    }

    private static int CmdCache(string target, Dictionary<string, string> opt)
    {
        int repeat = int.Parse(opt.GetValueOrDefault("repeat", "3"));
        string modes = opt.GetValueOrDefault("mode", "plain,walker,subtree");

        var hwnd = FindTargetHwnd(target);
        using var automation = new UIA3Automation();
        var window = automation.FromHandle(hwnd);
        var engine = new SnapshotEngine(automation);

        // Прогрев: Chromium строит accessibility-дерево лениво при чтении свойств —
        // без прогрева первый режим платил бы за материализацию, сравнение было бы грязным.
        var swWarm = Stopwatch.StartNew();
        _ = engine.Capture(window);
        swWarm.Stop();
        Console.WriteLine($"window={Trunc(WindowName(window), 40)}");
        Console.WriteLine($"warmup_plain_capture_ms = {swWarm.ElapsedMilliseconds} (холодный, выброшен)");

        HashSet<string>? plainKeys = null;
        int plainFiltered = 0;
        foreach (var mode in modes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var times = new List<long>();
            List<NodeInfo> last = new();
            for (int i = 0; i < repeat; i++)
            {
                var sw = Stopwatch.StartNew();
                last = mode switch
                {
                    "plain" => engine.Capture(window),
                    "walker" => CaptureCachedWalker(automation, window),
                    "subtree" => CaptureCachedSubtree(automation, hwnd),
                    _ => throw new InvalidOperationException($"Неизвестный режим: {mode}"),
                };
                sw.Stop();
                times.Add(sw.ElapsedMilliseconds);
            }
            var filtered = Filtered(last);
            times.Sort();
            Console.WriteLine($"[{mode,-7}] total={last.Count,-5} filtered={filtered.Count,-4} " +
                              $"ms={string.Join("/", times.Select(t => t.ToString()))} med={times[times.Count / 2]}");
            var keys = filtered.Select(n => n.Key).ToList();
            if (mode == "plain")
            {
                plainKeys = keys.ToHashSet();
                plainFiltered = filtered.Count;
            }
            else if (plainKeys != null)
            {
                var missing = keys.Count(k => !plainKeys.Contains(k));   // в кеш-режиме есть, в plain нет
                var extra = plainKeys.Count(k => !keys.Contains(k));    // в plain есть, в кеш-режиме пропал
                Console.WriteLine($"          состав против plain: пропало {extra}/{plainFiltered} ({Pct(extra, plainFiltered)}%), " +
                                  $"добавилось {missing}; покрытие {Pct(plainFiltered - extra, plainFiltered)}%");
            }
        }
        return 0;
    }

    // ======================= часть 2: снапшот региона/панели =======================

    private static int CmdPanel(string target, Dictionary<string, string> opt)
    {
        string match = opt.GetValueOrDefault("match", "");
        if (match.Length == 0) throw new InvalidOperationException("Укажите --match <подстрока Name/AutomationId якоря панели>.");

        var hwnd = FindTargetHwnd(target);
        using var automation = new UIA3Automation();
        var window = automation.FromHandle(hwnd);
        var engine = new SnapshotEngine(automation);

        _ = engine.Capture(window); // прогрев (лень Chromium)

        var swFull = Stopwatch.StartNew();
        var full = Filtered(engine.Capture(window));
        swFull.Stop();

        var anchor = FindElement(automation, window, match) ?? throw new InvalidOperationException($"Якорь панели «{match}» не найден.");
        var anchorName = SafeEl(anchor, () => anchor.Name ?? "", "");

        var swSub = Stopwatch.StartNew();
        var sub = Filtered(engine.Capture(anchor));
        swSub.Stop();

        Console.WriteLine($"window={Trunc(WindowName(window), 40)}");
        Console.WriteLine($"anchor = {Trunc(anchorName, 40)}");
        Console.WriteLine($"full_window: filtered={full.Count} ms={swFull.ElapsedMilliseconds}");
        Console.WriteLine($"panel_subtree: filtered={sub.Count} ms={swSub.ElapsedMilliseconds}");
        Console.WriteLine($"speedup = {swFull.ElapsedMilliseconds / (double)Math.Max(1, swSub.ElapsedMilliseconds):0.0}x, " +
                          $"покрытие состава окна = {Pct(sub.Count, full.Count)}%");
        return 0;
    }

    // Первый элемент ControlView-поддерева (кроме корня), чей Name или AutomationId
    // содержит подстроку — якорь панели для CmdPanel.
    private static AutomationElement? FindElement(UIA3Automation automation, AutomationElement root, string substr)
    {
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var el = stack.Pop();
            var child = walker.GetFirstChild(el);
            while (child != null)
            {
                var name = SafeEl(child, () => child.Name ?? "", "");
                var autoId = SafeEl(child, () => child.AutomationId ?? "", "");
                if (name.Contains(substr, StringComparison.OrdinalIgnoreCase) ||
                    autoId.Contains(substr, StringComparison.OrdinalIgnoreCase))
                    return child;
                stack.Push(child);
                child = walker.GetNextSibling(child);
            }
        }
        return null;
    }

    // ======================= утилиты =======================

    private static void DoAction(AutomationElement window, string action)
    {
        // Динамика (скролл/ресайз/переключение вкладки) вносится ВНЕ прототипа —
        // внешним шагом между запусками capture (PgDn через PowerShell, ресайз окном).
        // Внутри серии снимки идут в статике: это показывает дрожание отпечатка
        // «на ровном месте», без подмены состава действиями пользователя.
        _ = window; _ = action;
    }

    private static void SendKeys(string keys, bool ctrl) { /* placeholder: спайк не инжектирует ввод */ }

    // Safe-access к свойствам AutomationElement вне SnapshotEngine (часть 2).
    private static T SafeEl<T>(AutomationElement el, Func<T> get, T fallback)
    {
        try { return get(); }
        catch { return fallback; }
    }

    private static string SafeProcessName(int pid)
    {
        try { return System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
        catch { return "?"; }
    }

    private static string Trunc(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : (s.Length == 0 ? "\"\"" : s);

    private static string WindowName(AutomationElement el)
    {
        try { return el.Name ?? ""; }
        catch { return ""; }
    }

    private static int WindowPid(AutomationElement el)
    {
        try { return (int)el.Properties.ProcessId; }
        catch { return 0; }
    }

    private static string Pct(int part, int whole) => whole <= 0 ? "0" : Math.Round(100.0 * part / whole, 1).ToString("0.0");
}
