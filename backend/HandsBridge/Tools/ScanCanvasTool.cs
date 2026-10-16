using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.HandsBridge;
using ClaudeHomeServer.HandsBridge.Policy;
using ClaudeHomeServer.HandsBridge.Scanning.Imaging;
using ClaudeHomeServer.HandsBridge.Scanning.Pixels;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sbroenne.WindowsMcp.Capture;
using Sbroenne.WindowsMcp.Models;
using Sbroenne.WindowsMcp.Native;

namespace Sbroenne.WindowsMcp.Tools;

/// <summary>
/// Склейка большой схемы (Visio, в разы больше окна) в ОДИН PNG — шаг C1 «руки без UIA».
/// Схема открыта в окне без UIA (Citrix/VDI): доступны только клавиши, мышь и снимки. Инструмент
/// убирает курсор из области содержимого, прокручивает схему «в начало» (верх-лево, стоп по
/// <see cref="FrameStability"/>), определяет область содержимого (<see cref="ContentRegion"/>),
/// листает её змейкой (<see cref="SnakePlanner"/>, знаковый сдвиг из <see cref="ShiftEstimator"/>),
/// снимает каждое окно в память, собирает плитки в общий холст (<see cref="CanvasAssembler"/>) и
/// кодирует результат в PNG (<see cref="PngEncoder"/>) через <see cref="HandsProjectFiles"/>.
/// <para>
/// Способ прокрутки (<c>scrollMethod</c>) неизвестен до спайка на VDI, поэтому параметр:
/// <c>wheel</c> (колесо; по горизонтали — hwheel), <c>shift_wheel</c> (Shift+колесо для горизонтали)
/// или <c>arrows</c> (стрелки). Все три — через ВНУТРЕННИЕ перегрузки с проверкой окна/точки.
/// </para>
/// <para>
/// Ответ модели — только метаданные (путь, размер, сетка, число плиток, сомнительные швы,
/// <c>truncated</c>+причина, длительность): картинку в ответ НЕ возвращаем. Потолки библиотеки
/// (сторона ≤ 16 384, ≤ 64 Мп, ≤ 100 плиток, ≤ 5 мин) → <c>truncated</c> с причиной.
/// </para>
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static partial class ScanCanvasTool
{
    /// <summary>Потолок <c>maxTiles</c>: 100 плиток за схему.</summary>
    private const int MaxTilesCap = 100;

    /// <summary>Доля перекрытия по умолчанию (15 %).</summary>
    private const int DefaultOverlapPercent = 15;

    /// <summary>Бюджет времени одного вызова: ~5 минут (время приходит извне, часы не читаем).</summary>
    private const long DefaultMaxElapsedMs = 300_000;

    /// <summary>Потолок стороны полотна, px.</summary>
    private const int DefaultMaxSide = 16384;

    /// <summary>Потолок площади полотна, px² (64 Мп).</summary>
    private const long DefaultMaxArea = 64_000_000;

    /// <summary>Гард «в начало»: не больше N шагов вверх-влево (защита от бесконечной прокрутки).</summary>
    private const int MaxToStartSteps = 200;

    /// <summary>
    /// Снимки для скана: в бою — <c>WindowsToolsBase.ScreenshotService</c> (тот же снимок окна,
    /// что <c>screenshot_control</c>: PNG в памяти, на диск не пишется); в тестах — фейк.
    /// </summary>
    internal static ScreenshotService? CaptureService { get; set; }

    private static ScreenshotService Capture => CaptureService ?? WindowsToolsBase.ScreenshotService;

    /// <summary>Способ прокрутки (неизвестен до спайка на VDI → параметр инструмента).</summary>
    public enum ScrollMethod
    {
        /// <summary>Колесо: вертикаль — wheel, горизонталь — hwheel.</summary>
        Wheel,
        /// <summary>Shift+колесо для горизонтали; вертикаль — обычное колесо.</summary>
        ShiftWheel,
        /// <summary>Стрелки (клавиатура).</summary>
        Arrows,
    }

    /// <summary>
    /// Scans a diagram larger than the window (Visio in Citrix/VDI, no UIA tree) into ONE PNG:
    /// the cursor is moved out of the content, the canvas is scrolled to the top-left corner, the
    /// content region is measured, the canvas is paged in a snake (signed shift from the shift
    /// estimator), each window is captured, the tiles are assembled into a single canvas and
    /// encoded as a PNG in the project. The reply carries only metadata (path, size, grid, tile
    /// count, doubtful seams, truncation, duration) — the image itself is not returned.
    /// <para>
    /// <c>scrollMethod</c> selects how the canvas is scrolled (unknown until the VDI spike):
    /// <c>wheel</c> (wheel; horizontal via hwheel, the default), <c>shift_wheel</c> (Shift+wheel for
    /// the horizontal axis) or <c>arrows</c> (arrow keys). All go through the internal overloads that
    /// re-check the target window before each input.
    /// </para>
    /// </summary>
    /// <param name="windowHandle">Window handle (decimal string from window_management). Required: the diagram window to scan; it must accept input and be the foreground window (activate it with window_management first).</param>
    /// <param name="scrollMethod">How the canvas is scrolled: "wheel" (default), "shift_wheel" or "arrows". Unknown until the VDI spike, so it is a parameter.</param>
    /// <param name="overlapPercent">Overlap between neighbouring tiles, in percent (default 15, range 0–99).</param>
    /// <param name="saveTo">Where to save the assembled canvas: a path relative to the project root ending in .png (required — without it the canvas cannot be saved).</param>
    /// <param name="maxTiles">Maximum number of tiles (default 100, cap 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A call result: the PNG path, the canvas size, the grid (rows × columns), the tile count, the doubtful seams, the truncation flag with reason, and the duration.</returns>
    [McpServerTool(Name = "scan_canvas", Title = "Scan Canvas", Destructive = true, OpenWorld = false)]
    public static async partial Task<CallToolResult> ExecuteAsync(
        [DefaultValue(null)] string? windowHandle,
        [DefaultValue(null)] string? scrollMethod,
        [DefaultValue(null)] int? overlapPercent,
        string saveTo,
        [DefaultValue(null)] int? maxTiles,
        CancellationToken cancellationToken)
    {
        // windowHandle обязателен (как у scan_document): без него ввод улетит в чужое окно.
        if (string.IsNullOrWhiteSpace(windowHandle))
            return Fail("scan_canvas requires windowHandle: the diagram window to scan. " +
                        "Call window_management(action='list') to get it.");

        if (!WindowHandleParser.TryParse(windowHandle, out var parsed) || parsed == IntPtr.Zero)
            return Fail($"Invalid windowHandle: '{windowHandle}'. Expected a decimal string from window_management.");

        var window = parsed.ToInt64();

        // Гейт: окно обязано принимать ввод и быть передним планом (запрещённое приложение — отказ
        // ДО первого действия). «press» — действие ввода, поэтому гейт проверяет окно как у клавиатуры.
        var gate = HandsGate.Policy.CheckKeyboard(window, "press");
        if (!gate.Allowed)
            return HandsGate.Deny(HandsTools.ScanCanvas, gate);

        // Машина занята другим ходом — ответ этого вызова: действие не идёт, ход продолжается.
        var action = "scan";
        if (HandsGate.Acted(HandsTools.ScanCanvas, action) is { } busy)
            return busy;

        var method = ParseScrollMethod(scrollMethod);
        var overlap = overlapPercent is null ? DefaultOverlapPercent : Math.Clamp(overlapPercent.Value, 0, 99);

        var options = new ScanCanvasOptions
        {
            Window = window,
            SaveTo = saveTo,
            ScrollMethod = method,
            OverlapFraction = overlap / 100.0,
            MaxTiles = maxTiles is null ? MaxTilesCap : Math.Clamp(maxTiles.Value, 1, MaxTilesCap),
            MaxSide = DefaultMaxSide,
            MaxArea = DefaultMaxArea,
            MaxElapsedMs = DefaultMaxElapsedMs,
        };

        var deps = new ScanCanvasDeps
        {
            MovePointer = (x, y, ct) => MovePointerAsync(x, y, window, ct),
            WheelScroll = (dir, amount, ct) => WheelScrollAsync(dir, amount, window, ct),
            PressKey = (key, mods, repeat, ct) => PressKeyAsync(key, mods, repeat, window, ct),
            KeyDown = (key, ct) => KeyDownAsync(key, window, ct),
            KeyUp = (key, ct) => KeyUpAsync(key, window, ct),
            CaptureFrame = ct => CaptureWindowAsync(windowHandle, ct),
            Files = HandsGate.ProjectFiles,
        };

        try
        {
            var result = await RunScanAsync(options, deps, cancellationToken);
            return ToCallToolResult(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(WindowsToolsBase.SerializeToolError("scan_canvas", ex));
        }
    }

    // ---------- ядро цикла (internal: тесты зовут его напрямую на Linux) ----------

    /// <summary>
    /// Цикл скана: подготовка (курсор из области) → «в начало» (стоп по стабильности) → область
    /// содержимого → змейка (знаковый сдвиг) → сборка холста → PNG → запись. Чистая логика без
    /// WinAPI: все обращения к системе — через <paramref name="d"/>.
    /// </summary>
    internal static async Task<ScanCanvasResult> RunScanAsync(ScanCanvasOptions o, ScanCanvasDeps d, CancellationToken ct)
    {
        var result = new ScanCanvasResult { StopReason = "end_of_scroll" };
        var started = Stopwatch.StartNew();

        // 1. Подготовка: убрать курсор из области содержимого (в угол) — только мышью, без клавиш.
        if (!await d.MovePointer(2, 2, ct))
            return FailResult("could not move the cursor out of the content area.");

        // 2. «В начало»: прокрутка вверх-влево до края (стоп по стабильности кадра).
        var (frame, frameW, frameH) = await d.CaptureFrame(ct);
        var prev = frame;
        var atStart = false;
        for (var i = 0; i < MaxToStartSteps; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!await ScrollAxis(d, o, Axis.Vertical, -1, ct)) break;   // up
            if (!await ScrollAxis(d, o, Axis.Horizontal, -1, ct)) break;  // left
            (frame, _, _) = await d.CaptureFrame(ct);
            if (FrameStability.IsEndOfScroll([Hash(prev), Hash(frame)]) || FrameStability.AreStable(prev, frame, 1.0))
            {
                atStart = true;
                break;
            }

            prev = frame;
            if (started.ElapsedMilliseconds > o.MaxElapsedMs)
            {
                result.Truncated = true;
                result.TruncationReason = "time";
                break;
            }
        }

        if (!atStart && started.ElapsedMilliseconds <= o.MaxElapsedMs)
            return FailResult("could not reach the start of the canvas (it kept scrolling).");

        // 3. Область содержимого: шаг вниз, пара кадров → ContentRegion; затем возврат вверх.
        var before = frame;
        var region = new Rect(0, 0, 0, 0);
        if (await ScrollAxis(d, o, Axis.Vertical, +1, ct))
        {
            var (after, afterW, afterH) = await d.CaptureFrame(ct);
            var determined = ContentRegion.Determine(Gray(before), Gray(after), afterW, afterH, tolerance: 8.0);
            if (determined.Ok)
                region = determined.Region;
            // Возврат в верх (к началу змейки).
            await ScrollAxis(d, o, Axis.Vertical, -1, ct);
        }

        if (region.Width <= 0 || region.Height <= 0)
            return FailResult("the content region could not be determined (the canvas did not move).");

        // 4. Змейка: шаги планировщика, на каждом — прокрутка + снимок + знаковый сдвиг + Feed.
        var planner = new SnakePlanner(
            region.Width, region.Height,
            overlapFraction: o.OverlapFraction,
            maxTiles: o.MaxTiles,
            maxSide: o.MaxSide,
            maxArea: o.MaxArea,
            maxElapsedMs: o.MaxElapsedMs);

        var tiles = new List<Tile>();
        var canvasX = 0;
        var canvasY = 0;
        var lastFrame = frame;

        // Первый шаг — ToStart: мы уже в начале (шаг 2), поэтому кадр текущего положения.
        var step = planner.CurrentStep;
        if (step.Kind == ScanStepKind.ToStart)
        {
            var (topLeft, _, _) = await d.CaptureFrame(ct);
            var shift = MeasureShift(o, Axis.Vertical, 0, lastFrame, topLeft, frameW, frameH);
            lastFrame = topLeft;
            step = planner.Feed(Gray(topLeft), shift, started.ElapsedMilliseconds);
        }

        while (step.Kind == ScanStepKind.Step)
        {
            ct.ThrowIfCancellationRequested();

            // Прокрутка по оси шага в заданном направлении.
            if (!await ScrollAxis(d, o, step.Axis, step.Direction, ct))
                break;

            var (next, nextW, nextH) = await d.CaptureFrame(ct);

            // Знаковый сдвиг (из ShiftEstimator): положительный — контент before сдвинут в after.
            var measured = MeasureShift(o, step.Axis, step.ExpectedShift, lastFrame, next, frameW, frameH);

            // Позиция плитки = накопленные ИЗМЕРЕННЫЕ сдвиги (знак учитывается).
            if (step.Axis == Axis.Horizontal)
                canvasX += measured;
            else
                canvasY += measured;

            tiles.Add(new Tile(
                Pixels: next,
                Width: nextW,
                Height: nextH,
                CanvasX: canvasX,
                CanvasY: canvasY,
                MeasuredShift: measured,
                ExpectedShift: step.ExpectedShift,
                Score: 1.0,
                Ambiguous: false));

            lastFrame = next;
            step = planner.Feed(Gray(next), measured, started.ElapsedMilliseconds);

            // Потолок: автомат в Done, Truncated с причиной.
            if (planner.Truncated)
            {
                result.Truncated = true;
                result.TruncationReason = planner.TruncationReason;
                break;
            }
        }

        if (tiles.Count == 0)
            return FailResult("no tiles were captured (the canvas did not move).");

        // 5. Сборка холста по измеренным сдвигам + сомнительные швы.
        var canvas = CanvasAssembler.Assemble(tiles);
        if (canvas.Truncated)
        {
            result.Truncated = true;
            result.TruncationReason ??= canvas.TruncationReason;
        }

        result.Width = canvas.Width;
        result.Height = canvas.Height;
        result.Rows = canvas.Rows;
        result.Columns = canvas.Columns;
        result.Tiles = tiles.Count;
        result.DoubtfulSeams = canvas.DoubtfulSeams;

        // 6. PNG (только если холст не отклонён) → запись в проект.
        if (!canvas.Truncated && canvas.Pixels.Length > 0)
        {
            var png = PngEncoder.Encode(canvas.Pixels, canvas.Width, canvas.Height);
            var (path, reason) = d.Files.WriteNew(HandsTools.ScanCanvas, o.SaveTo, png);
            if (path is null)
                return FailResult($"the canvas was assembled, but the file could not be created: {reason}");
            result.Path = path;
            result.SizeBytes = png.Length;
        }

        result.Done = true;
        result.DurationMs = started.ElapsedMilliseconds;
        return result;
    }

    /// <summary>
    /// Прокрутка на один шаг по оси в заданном направлении, по способу <paramref name="o"/>:
    /// <c>wheel</c> (колесо/hwheel), <c>shift_wheel</c> (Shift+колесо для горизонтали) или
    /// <c>arrows</c> (стрелки). Все — через внутренние перегрузки с проверкой окна/точки.
    /// </summary>
    private static async Task<bool> ScrollAxis(ScanCanvasDeps d, ScanCanvasOptions o, Axis axis, int direction, CancellationToken ct)
    {
        var vertDir = direction < 0 ? "Up" : "Down";
        var horizDir = direction < 0 ? "Left" : "Right";
        switch (o.ScrollMethod)
        {
            case ScrollMethod.Wheel:
                if (axis == Axis.Vertical)
                    return await d.WheelScroll(vertDir, 1, ct);
                return await d.WheelScroll(horizDir, 1, ct);

            case ScrollMethod.ShiftWheel:
                if (axis == Axis.Vertical)
                    return await d.WheelScroll(vertDir, 1, ct);
                // Горизонталь: Shift удержан + вертикальное колесо.
                if (!await d.KeyDown("Shift", ct))
                    return false;
                var ok = await d.WheelScroll(vertDir, 1, ct);
                await d.KeyUp("Shift", ct);
                return ok;

            case ScrollMethod.Arrows:
            default:
                if (axis == Axis.Vertical)
                    return await d.PressKey(vertDir, "None", 1, ct);
                return await d.PressKey(horizDir, "None", 1, ct);
        }
    }

    /// <summary>
    /// Знаковый сдвиг шага (из <see cref="ShiftEstimator"/>): <paramref name="before"/>/<paramref
    /// name="after"/> — кадры до/после шага; знак: положительный — контент <c>before</c> сдвинут
    /// вниз/вправо в <c>after</c>. Плоский/неопределённый сдвиг → 0. <paramref name="w"/>/<paramref
    /// name="h"/> — размер окна (все кадры одного скана — одного размера).
    /// </summary>
    private static int MeasureShift(ScanCanvasOptions o, Axis axis, int expectedShift, byte[] before, byte[] after, int w, int h)
    {
        var est = ShiftEstimator.Estimate(Gray(before), Gray(after), w, h, axis, expectedShift);
        return est.Shift;
    }

    // ---------- реальные швы (Windows) ----------

    /// <summary>Перевод курсора в точку (внутренняя перегрузка с проверкой окна/точки).</summary>
    private static async Task<bool> MovePointerAsync(int x, int y, long window, CancellationToken ct)
    {
        var res = await WindowsToolsBase.MouseInputService.MoveAsync(x, y, (nint)window, ct);
        return res.Success;
    }

    /// <summary>Колесо/hwheel (внутренняя перегрузка с проверкой окна/точки).</summary>
    private static async Task<bool> WheelScrollAsync(string direction, int amount, long window, CancellationToken ct)
    {
        var dir = direction switch
        {
            "Up" => ScrollDirection.Up,
            "Down" => ScrollDirection.Down,
            "Left" => ScrollDirection.Left,
            _ => ScrollDirection.Right,
        };
        var res = await WindowsToolsBase.MouseInputService.ScrollAsync(dir, amount, null, null, (nint)window, ct);
        return res.Success;
    }

    /// <summary>Нажатие клавиши (внутренняя перегрузка с expectedForegroundWindow).</summary>
    private static async Task<bool> PressKeyAsync(string key, string modifierName, int repeat, long window, CancellationToken ct)
    {
        var mods = modifierName switch
        {
            "Ctrl" => ModifierKey.Ctrl,
            "Shift" => ModifierKey.Shift,
            "Alt" => ModifierKey.Alt,
            "Win" => ModifierKey.Win,
            _ => ModifierKey.None,
        };
        var res = await WindowsToolsBase.KeyboardInputService.PressKeyAsync(key, mods, repeat, (nint)window, ct);
        return res.Success;
    }

    /// <summary>Key down (внутренняя перегрузка с expectedForegroundWindow).</summary>
    private static async Task<bool> KeyDownAsync(string key, long window, CancellationToken ct)
    {
        var res = await WindowsToolsBase.KeyboardInputService.KeyDownAsync(key, (nint)window, ct);
        return res.Success;
    }

    /// <summary>Key up (внутренняя перегрузка с expectedForegroundWindow).</summary>
    private static async Task<bool> KeyUpAsync(string key, long window, CancellationToken ct)
    {
        var res = await WindowsToolsBase.KeyboardInputService.KeyUpAsync(key, (nint)window, ct);
        return res.Success;
    }

    /// <summary>Снимок окна: PNG в памяти (на диск не пишется) → сырые пиксели RGB и размеры.</summary>
    private static async Task<(byte[] Pixels, int Width, int Height)> CaptureWindowAsync(string windowHandle, CancellationToken ct)
    {
        var request = new ScreenshotControlRequest
        {
            Action = ScreenshotAction.Capture,
            Target = CaptureTarget.Window,
            WindowHandle = windowHandle,
            ImageFormat = ImageFormat.Png,
            OutputMode = OutputMode.Inline,
        };
        var capture = await Capture.ExecuteAsync(request, ct);
        if (!capture.Success || capture.ImageData is null)
            throw new InvalidOperationException($"window capture failed: {capture.Message}");
        var png = Convert.FromBase64String(capture.ImageData);
        var pixels = DecodePngToRgb(png);
        return (pixels, capture.Width ?? 0, capture.Height ?? 0);
    }

    /// <summary>
    /// Декод PNG (8 бит, RGB/RGBA) → RGB-пиксели (3 канала). Минимальный декодер: сигнатура, IHDR,
    /// IDAT (zlib), фильтры None/Sub (те, что пишет <see cref="PngEncoder"/>).
    /// </summary>
    internal static byte[] DecodePngToRgb(byte[] png)
    {
        // IHDR: width (4), height (4), depth (1), colorType (1) — смещение 16.
        var width = ReadUInt32Big(png, 16);
        var height = ReadUInt32Big(png, 20);
        var colorType = png[25];
        var channels = colorType == 6 ? 4 : 3; // 2 = RGB, 6 = RGBA

        // IDAT: все чанки IDAT подряд (zlib-поток).
        var idat = new List<byte>();
        var offset = 8;
        while (offset + 8 <= png.Length)
        {
            var len = ReadUInt32Big(png, offset);
            var type = new string(new[] { (char)png[offset + 4], (char)png[offset + 5], (char)png[offset + 6], (char)png[offset + 7] });
            if (type == "IDAT")
            {
                for (var i = 0; i < len; i++)
                    idat.Add(png[offset + 8 + i]);
            }
            offset += 12 + len; // length(4) + type(4) + data(len) + crc(4)
            if (type == "IEND")
                break;
        }

        var raw = DecompressZlib(idat.ToArray());
        var rowLen = width * channels;
        var pixels = new byte[width * height * 3];
        var prev = new byte[rowLen];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (1 + rowLen)];
            var src = raw.AsSpan().Slice(y * (1 + rowLen) + 1, rowLen);
            var dst = pixels.AsSpan().Slice(y * rowLen, rowLen);
            for (var i = 0; i < rowLen; i++)
            {
                var a = src[i];
                var b = filter == 1 ? (byte)(a + prev[i]) : a; // Sub: a + left
                dst[i] = b;
                prev[i] = b;
            }

            // RGBA → RGB: альфа отбрасывается (фон схем — непрозрачный).
            if (channels == 4)
            {
                for (var x = 0; x < width; x++)
                {
                    var s = (y * width + x) * 4;
                    var d = (y * width + x) * 3;
                    pixels[d] = pixels[s];
                    pixels[d + 1] = pixels[s + 1];
                    pixels[d + 2] = pixels[s + 2];
                }
            }
        }

        return pixels;
    }

    private static int ReadUInt32Big(byte[] b, int offset) =>
        (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];

    private static byte[] DecompressZlib(byte[] data)
    {
        using var input = new System.IO.MemoryStream(data);
        using var zlib = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new System.IO.MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>RGB → серый (среднее каналов), для ContentRegion/ShiftEstimator/стабильности.</summary>
    private static byte[] Gray(byte[] rgb)
    {
        var n = rgb.Length / 3;
        var gray = new byte[n];
        for (var i = 0; i < n; i++)
        {
            var o = i * 3;
            gray[i] = (byte)((rgb[o] + rgb[o + 1] + rgb[o + 2]) / 3);
        }

        return gray;
    }

    /// <summary>Хэш кадра: FNV-1a 64 (детерминированный, для признака конца прокрутки).</summary>
    private static ulong Hash(byte[] data)
    {
        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211;
        var hash = FnvOffset;
        foreach (var b in data)
            hash = (hash ^ b) * FnvPrime;
        return hash;
    }

    /// <summary>Разбор <c>scrollMethod</c>: по умолчанию <c>wheel</c>; неизвестное — тоже <c>wheel</c>.</summary>
    private static ScrollMethod ParseScrollMethod(string? scrollMethod)
    {
        return scrollMethod?.ToLowerInvariant() switch
        {
            "shift_wheel" or "shift-wheel" or "shiftwheel" => ScrollMethod.ShiftWheel,
            "arrows" or "arrow" or "keys" => ScrollMethod.Arrows,
            _ => ScrollMethod.Wheel,
        };
    }

    // ---------- ответ ----------

    /// <summary>
    /// Ответ модели: только метаданные (путь, размер, сетка, число плиток, сомнительные швы,
    /// <c>truncated</c>+причина, длительность). Картинку в ответ НЕ возвращаем.
    /// </summary>
    private static CallToolResult ToCallToolResult(ScanCanvasResult r)
    {
        var seams = r.DoubtfulSeams
            .Select(s => new { reason = s.Reason, tileA = s.TileA, tileB = s.TileB })
            .ToArray();

        var payload = new
        {
            success = true,
            path = r.Path,
            width = r.Width,
            height = r.Height,
            grid = new { rows = r.Rows, columns = r.Columns },
            tiles = r.Tiles,
            sizeBytes = r.SizeBytes,
            doubtfulSeams = seams,
            doubtfulSeamCount = r.DoubtfulSeams.Count,
            truncated = r.Truncated,
            truncationReason = r.TruncationReason,
            stopReason = r.StopReason,
            error = r.Error,
            durationMs = r.DurationMs,
        };

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, WindowsToolsBase.JsonOptions) }],
            IsError = false,
        };
    }

    private static CallToolResult Fail(string message) => new()
    {
        Content = [new TextContentBlock
        {
            Text = JsonSerializer.Serialize(new { success = false, error = message, isError = true }, JsonOptions),
        }],
        IsError = true,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static ScanCanvasResult FailResult(string message) => new()
    {
        Done = false,
        StopReason = "error",
        Error = message,
    };

    // ---------- структуры ----------

    /// <summary>Параметры скана (внутренние: тесты задают маленький бюджет).</summary>
    internal sealed class ScanCanvasOptions
    {
        public required long Window { get; init; }
        public required string SaveTo { get; init; }
        public required ScrollMethod ScrollMethod { get; init; }
        public double OverlapFraction { get; init; } = 0.15;
        public int MaxTiles { get; init; } = MaxTilesCap;
        public int MaxSide { get; init; } = DefaultMaxSide;
        public long MaxArea { get; init; } = DefaultMaxArea;
        public long MaxElapsedMs { get; init; } = DefaultMaxElapsedMs;
    }

    /// <summary>
    /// Швы цикла: курсор, колесо, клавиши, снимок, запись. В бою — Windows-сервисы, в тестах —
    /// фейки. Все — внутренние перегрузки с проверкой окна/точки.
    /// </summary>
    internal sealed class ScanCanvasDeps
    {
        /// <summary>Перевести курсор в точку (x, y) с проверкой окна/точки.</summary>
        public required Func<int, int, CancellationToken, Task<bool>> MovePointer { get; init; }
        /// <summary>Колесо/hwheel: направление ("Up"/"Down"/"Left"/"Right") и 1 notch.</summary>
        public required Func<string, int, CancellationToken, Task<bool>> WheelScroll { get; init; }
        /// <summary>Нажать клавишу: имя клавиши, имя модификатора ("None"/"Ctrl"/"Shift"/"Alt"), repeat раз.</summary>
        public required Func<string, string, int, CancellationToken, Task<bool>> PressKey { get; init; }
        /// <summary>Удерживать клавишу (key down).</summary>
        public required Func<string, CancellationToken, Task<bool>> KeyDown { get; init; }
        /// <summary>Отпустить клавишу (key up).</summary>
        public required Func<string, CancellationToken, Task<bool>> KeyUp { get; init; }
        /// <summary>Снимок окна в память: сырые пиксели RGB и его размеры (ширина, высота).</summary>
        public required Func<CancellationToken, Task<(byte[] Pixels, int Width, int Height)>> CaptureFrame { get; init; }
        /// <summary>Запись в проект (WriteNew).</summary>
        public required HandsProjectFiles Files { get; init; }
    }

    /// <summary>Итог прогона: метаданные (без картинки).</summary>
    internal sealed class ScanCanvasResult
    {
        public bool Done;
        public string? Path;
        public int Width;
        public int Height;
        public int Rows;
        public int Columns;
        public int Tiles;
        public int SizeBytes;
        public IReadOnlyList<DoubtfulSeam> DoubtfulSeams = Array.Empty<DoubtfulSeam>();
        public bool Truncated;
        public string? TruncationReason;
        public string StopReason = "";
        public string? Error;
        public long DurationMs;
    }
}
