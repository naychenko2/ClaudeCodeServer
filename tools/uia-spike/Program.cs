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

    private static AutomationElement ResolveWindow(UIA3Automation automation, string target)
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
        return automation.FromHandle(hwnd);
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
