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
/// ставит курсор над областью содержимого (колесо) или активирует окно (стрелки), прокручивает схему
/// «в начало» (верх-лево, стоп по <see cref="FrameStability"/>), определяет область содержимого
/// (<see cref="ContentRegion"/>), листает её змейкой (<see cref="SnakePlanner"/>, знаковый сдвиг из
/// <see cref="ShiftEstimator"/>), снимает каждое окно в память, вырезает из него область
/// содержимого, собирает плитки в общий холст (<see cref="CanvasAssembler"/>) и кодирует результат
/// в PNG (<see cref="PngEncoder"/>) через <see cref="HandsProjectFiles"/>.
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
/// <para>
/// Не публикуется в первой выкладке (решение 2026-10-16, см. заметку «scan_canvas: план второго захода»).
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
    /// Потолок числа щелчков/нажатий на один шаг (M4): больше — прокрутка «не догоняет» план,
    /// дальше шаг не имеет смысла.
    /// </summary>
    private const int MaxClicksPerStep = 20;

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
    /// the cursor is placed over the content (wheel) or the window is activated (arrows), the
    /// canvas is scrolled to the top-left corner, the content region is measured, the canvas is
    /// paged in a snake (signed shift from the shift estimator), each window's content region is
    /// captured, the tiles are assembled into a single canvas and encoded as a PNG in the project.
    /// The reply carries only metadata (path, size, grid, tile count, doubtful seams, truncation,
    /// duration) — the image itself is not returned.
    /// <para>
    /// <c>scrollMethod</c> selects how the canvas is scrolled (unknown until the VDI spike):
    /// <c>wheel</c> (wheel; horizontal via hwheel, the default), <c>shift_wheel</c> (Shift+wheel for
    /// the horizontal axis) or <c>arrows</c> (arrow keys). All go through the internal overloads that
    /// re-check the target window before each input.
    /// </para>
    /// <para>
    /// The window must be activated (foreground) before the call: for <c>arrows</c> the keys go to
    /// the foreground window, and for <c>wheel</c>/<c>shift_wheel</c> the cursor is placed over the
    /// content. A scroll bar stuck to the window edge (m-B) can eat the wheel, so keep the window
    /// away from it or use <c>arrows</c>.
    /// </para>
    /// </summary>
    /// <param name="windowHandle">Window handle (decimal string from window_management). Required: the diagram window to scan; it must accept input and be the foreground window (activate it with window_management first).</param>
    /// <param name="scrollMethod">How the canvas is scrolled: "wheel" (default), "shift_wheel" or "arrows". Unknown until the VDI spike, so it is a parameter.</param>
    /// <param name="overlapPercent">Overlap between neighbouring tiles, in percent (default 15, range 10–90). Below 10 the frames stop overlapping enough to measure the shift; above 90 the step is too small.</param>
    /// <param name="saveTo">Where to save the assembled canvas: a path relative to the project root ending in .png (required — without it the canvas cannot be saved).</param>
    /// <param name="maxTiles">Maximum number of tiles (default 100, cap 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A call result: the PNG path, the canvas size, the grid (rows × columns), the tile count, the doubtful seams, the truncation flag with reason, and the duration.</returns>
    // Публикация в tools/list снята в первой выкладке (решение 2026-10-16): атрибут McpServerTool
    // (имя scan_canvas) и `partial` (определяющее объявление генерирует source-generator по
    // атрибуту) возвращаются при втором заходе, см. заметку «scan_canvas: план второго захода».
    // Класс остаётся типом инструментов ([McpServerToolType]).
    public static async Task<CallToolResult> ExecuteAsync(
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
        // K1: допустимо 10–90. Ниже 10 % кадра почти не перекрываются — сдвиг не измерить;
        // выше 90 % шаг крошечный (много плиток). Дефолт 15 % остаётся рабочим.
        var overlap = overlapPercent is null ? DefaultOverlapPercent : Math.Clamp(overlapPercent.Value, 10, 90);

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
            PlaceCursor = (wx, wy, ct) => PlaceCursorAsync(wx, wy, window, ct),
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
    /// Цикл скана: подготовка (курсор над областью / активация окна) → «в начало» (стоп по
    /// стабильности) → область содержимого → калибровка шага → змейка (знаковый сдвиг) → сборка
    /// холста → PNG → запись. Чистая логика без WinAPI: все обращения к системе — через
    /// <paramref name="d"/>.
    /// </summary>
    internal static async Task<ScanCanvasResult> RunScanAsync(ScanCanvasOptions o, ScanCanvasDeps d, CancellationToken ct)
    {
        var result = new ScanCanvasResult { StopReason = "end_of_scroll" };
        var started = Stopwatch.StartNew();

        // M5: предпроверка saveTo ДО первого действия (расширение, корень проекта, потолок,
        // свободное имя): плохой путь — отказ без единого вызова ввода/снимка.
        var saveCheck = HandsGate.Policy.CheckOutputPath(HandsTools.ScanCanvas, d.Files.ProjectRoot, o.SaveTo,
            bytes: 0, d.Files.FilesWrittenThisTurn, appendOwn: false, ownFiles: null);
        if (!saveCheck.Allowed)
            return FailResult($"saveTo '{o.SaveTo}': {saveCheck.Reason}");

        // 1. Снимок (размер окна) + подготовка курсора (M3): для колеса/shift_wheel курсор ставится
        //    НАД содержимым (центр окна — безопасная точка над контентом; region ещё неизвестен),
        //    для стрелков мышь не нужна (окно уже передним планом, как в scan_document).
        var (frame, frameW, frameH) = await d.CaptureFrame(ct);
        if (o.ScrollMethod is not ScrollMethod.Arrows)
        {
            if (!await d.PlaceCursor(frameW / 2, frameH / 2, ct))
                return FailResult("could not place the cursor over the content (the window center is not over the allowed window).");
        }

        // 2. «В начало»: прокрутка вверх-влево до края (стоп по стабильности кадра).
        // M-C: отказ ввода (смена окна, отказ гейта) — отдельная причина, не «не доехали»;
        // «time» — превышен бюджет (остановка, а не «дальше строить змейку»).
        var (frame2, atStart, refused, reason) = await ScrollToStartAsync(d, o, frame, o.MaxElapsedMs, ct);
        frame = frame2;
        if (refused)
            return FailResult($"could not reach the start of the canvas: {reason}");
        if (reason == "time")
        {
            result.Truncated = true;
            result.TruncationReason = "time";
            return result;
        }
        if (!atStart)
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

        // 4. M3: теперь region известен — курсор точно в ЦЕНТР region (внутри области), чтобы
        //    колесо/shift_wheel шло в схему, а не в рамку/ленту.
        if (o.ScrollMethod is not ScrollMethod.Arrows)
        {
            var cx = region.X + region.Width / 2;
            var cy = region.Y + region.Height / 2;
            if (!await d.PlaceCursor(cx, cy, ct))
                return FailResult("could not place the cursor at the center of the content region.");
        }

        // 5. Калибровка шага (M4): один щелчок по каждой оси, фактический сдвиг (shiftPerTick);
        //    дальше число щелчков = round(ожидаемый сдвиг / shiftPerTick).
        var (shiftPerTickX, shiftPerTickY, calibAmbiguous) = await CalibrateStep(d, o, region, frame, frameW, frameH, ct);
        if (shiftPerTickX <= 0 && shiftPerTickY <= 0)
            return FailResult("the scroll did not move the canvas (no measurable shift after one step).");

        // M-B: после калибровки («вниз, потом вверх») возврат неточен при нелинейной прокрутке —
        //    topLeft снимался бы со сдвигом (потерян верхний край). Заново «в начало» по
        //    стабильности кадра, и только потом снимаем topLeft.
        var (frame3, _, refused2, reason2) = await ScrollToStartAsync(d, o, frame, o.MaxElapsedMs, ct);
        frame = frame3;
        if (refused2)
            return FailResult($"could not return to the start after calibration: {reason2}");

        // M-D: калибровка неоднозначна (повторяющийся узор → период, а не щелчок) — предупреждение,
        //    а не молчаливый успех.
        if (calibAmbiguous)
            result.TruncationReason ??= "uncertain_geometry: step calibration is ambiguous (repeating pattern)";

        // 5. Змейка: шаги планировщика, на каждом — прокрутка (нужное число щелчков) + снимок +
        //    знаковый сдвиг + Feed. Позиция плитки = накопленные смещения на холсте.
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
        // M-D: число плиток с неоднозначным сдвигом (Ambiguous, повторяющийся узор) — для
        // uncertain_geometry (отдельно от «сомнительных швов» по низкой корреляции/расхождению).
        var ambiguousTiles = 0;

        // C2: первое окно (верх-лево холста) — первая плитка в (0,0).
        var (topLeft, topLeftW, topLeftH) = await d.CaptureFrame(ct);
        tiles.Add(MakeTile(topLeft, topLeftW, topLeftH, region, 0, 0, measuredShift: 0, expectedShift: 0));
        var lastFrame = topLeft;

        // Первый шаг — ToStart: мы уже в начале (шаг 2), поэтому кадр текущего положения (сдвиг 0).
        var step = planner.CurrentStep;
        if (step.Kind == ScanStepKind.ToStart)
        {
            step = planner.Feed(Gray(topLeft), 0, started.ElapsedMilliseconds);
        }

        while (step.Kind == ScanStepKind.Step)
        {
            ct.ThrowIfCancellationRequested();

            // Прокрутка по оси шага: нужное число щелчков (M4, калибровка).
            var expected = step.ExpectedShift;
            var perTick = step.Axis == Axis.Horizontal ? shiftPerTickX : shiftPerTickY;
            var clicks = perTick > 0 ? Math.Clamp((int)Math.Round((double)expected / perTick), 1, MaxClicksPerStep) : 1;
            // M-C: отказ ввода (смена окна, отказ гейта, исключение) посреди скана — НЕ «не доехали»
            //    и не успех: остановка с причиной «input refused». Счётчик успешных щелчков:
            //    0 — отказ сразу, < clicks — отказ посреди шага, = clicks — шаг выполнен.
            var moved = 0;
            for (var c = 0; c < clicks; c++)
            {
                if (!await ScrollAxis(d, o, step.Axis, step.Direction, ct))
                    break;
                moved++;
            }
            if (moved == 0)
                return FailResult("input refused: the scroll did not move the canvas (window changed or gate refused)");
            if (moved < clicks)
                return FailResult($"input refused mid-step: expected {clicks} scrolls, got {moved} (window changed or gate refused)");

            var (next, nextW, nextH) = await d.CaptureFrame(ct);

            // Край схемы: кадр не изменился (упёрлись) — реального движения нет, плитка не
            // добавляется (иначе это дубликат: тот же кадр, что предыдущий). Планировщик сам
            // распознаёт край по хэшу кадра (SnakePlanner) и переходит к следующему этапу:
            // для горизонтали — вниз на строку, для вертикали — Done.
            var noChange = FrameStability.AreStable(lastFrame, next, 1.0);
            var tileOffset = 0;
            if (!noChange)
            {
                // C1: знаковый сдвиг (из ShiftEstimator, «сдвиг содержимого») → смещение плитки
                // на холсте (инверсия в ОДНОМ месте, ToTileShift).
                // K1: подсказка оценщику — со ЗНАКОМ (конвенция: + = контент вправо/вниз).
                // Скролл +1 (вправо/вниз) двигает контент влево/вверх → hint отрицательный.
                // K1: подсказка = ФАКТИЧЕСКИЙ сдвиг шага (clicks × shiftPerTick), а не номинальный
                // step.ExpectedShift: число щелчков округляется, поэтому фактический сдвиг может
                // отличаться от номинала (4 щелчка × 30 = 120 ≠ 110), и окно центрируется на истинном.
                var hint = -step.Direction * (clicks * perTick);
                // K1: hint = фактический сдвиг шага (окно центрируется на истинном), minOverlap 0.05:
                // фактический сдвиг может превышать (1 − 0.10) × n (окно обрезало бы истинный),
                // поэтому допускаем перекрытие кадров до 5 % — при малом перекрытии плиток это нормально.
                var shiftResult = MeasureShiftEstimate(o, step.Axis, hint, lastFrame, next, frameW, frameH, region, minOverlapFraction: 0.05);
                if (shiftResult.Ambiguous)
                    ambiguousTiles++;
                tileOffset = ToTileShift(shiftResult.Shift);

                // C3: позиция плитки = накопленные смещения на холсте (знак учтён инверсией).
                if (step.Axis == Axis.Horizontal)
                    canvasX += tileOffset;
                else
                    canvasY += tileOffset;

                tiles.Add(MakeTile(next, nextW, nextH, region, canvasX, canvasY, tileOffset, step.ExpectedShift));

                lastFrame = next;
            }

            step = planner.Feed(Gray(next), tileOffset, started.ElapsedMilliseconds);

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

        // 6. Сборка холста по измеренным сдвигам + сомнительные швы.
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

        // M-D: высокая доля плиток с НЕОДНОЗНАЧНЫМ сдвигом (Ambiguous, >25 %) — повторяющийся
        //    узор (несколько равных пиков корреляции), геометрия неопределённа, не молчаливый успех.
        //    Считаем именно Ambiguous (а не любые «сомнительные швы» по низкой корреляции):
        //    сетчатая текстура даёт отдельные швы с низкой корреляцией, но не повторяющийся узор.
        var hasUncertainGeometry = tiles.Count > 0 && (double)ambiguousTiles / tiles.Count > 0.25;
        if (hasUncertainGeometry)
            result.TruncationReason ??= "uncertain_geometry: more than 25% of the tiles have an ambiguous shift (repeating pattern)";

        // 7. PNG (только если холст не отклонён) → запись в проект.
        if (!canvas.Truncated && canvas.Pixels.Length > 0)
        {
            var png = PngEncoder.Encode(canvas.Pixels, canvas.Width, canvas.Height);
            var (path, writeReason) = d.Files.WriteNew(HandsTools.ScanCanvas, o.SaveTo, png);
            if (path is null)
                return FailResult($"the canvas was assembled, but the file could not be created: {writeReason}");
            result.Path = path;
            result.SizeBytes = png.Length;
        }

        // M-D: неопределённая геометрия (повторяющийся узор → >25 % сомнительных швов) — это НЕ
        // чистый «Done»-успех (иначе «успех с развалившимся холстом»). Done=false + причина
        // «uncertain_geometry» (выше) — явное предупреждение, а не молчаливый успех.
        result.Done = !hasUncertainGeometry;
        result.DurationMs = started.ElapsedMilliseconds;
        return result;
    }

    /// <summary>
    /// M3: поставить курсор в точку <c>(windowX, windowY)</c> в координатах ОКНА (центр region /
    /// центр окна). Точка конвертируется в экранные (окно: <c>GetWindowRect</c> + смещение),
    /// проверяется на разрешённое окно (<see cref="HandsPolicy.AcceptsInputAt"/>) ДО перемещения
    /// (внутренняя <c>MoveAsync</c> тоже проверяет, что точка над целевым окном), и переводится
    /// внутренней перегрузкой. Для <c>arrows</c> вызывающий сам не двигает мышь.
    /// </summary>
    private static async Task<bool> PlaceCursorAsync(int windowX, int windowY, long window, CancellationToken ct)
    {
        // Экранное положение левого верхнего угла окна (координаты точки — в системе окна).
        if (!NativeMethods.GetWindowRect((nint)window, out var rect))
            return false;
        var screenX = rect.Left + windowX;
        var screenY = rect.Top + windowY;

        // Проверка ЦЕЛИ (не текущей позиции) ДО перемещения: точка над разрешённым окном.
        if (!HandsGate.Policy.AcceptsInputAt(screenX, screenY, window).Allowed)
            return false;

        var res = await WindowsToolsBase.MouseInputService.MoveAsync(screenX, screenY, (nint)window, ct);
        return res.Success;
    }

    /// <summary>
    /// «В начало»: прокрутка вверх-влево до края (стоп по стабильности кадра). Возвращает
    /// (кадр, достигли ли начала, был ли отказ ввода, причина). M-C: отказ ввода (смена окна,
    /// отказ гейта) — отдельная причина, а не «не доехали»; «time» — превышен бюджет.
    /// </summary>
    private static async Task<(byte[] Frame, bool AtStart, bool Refused, string? Reason)> ScrollToStartAsync(
        ScanCanvasDeps d, ScanCanvasOptions o, byte[] startFrame, long maxElapsedMs, CancellationToken ct)
    {
        var prev = startFrame;
        var atStart = false;
        var refused = false;
        string? reason = null;
        var started = Stopwatch.StartNew();
        for (var i = 0; i < MaxToStartSteps; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!await ScrollAxis(d, o, Axis.Vertical, -1, ct))
            {
                refused = true;
                reason = "input refused: scroll up failed (window changed or gate refused)";
                break;
            }
            if (!await ScrollAxis(d, o, Axis.Horizontal, -1, ct))
            {
                refused = true;
                reason = "input refused: scroll left failed (window changed or gate refused)";
                break;
            }
            var (cur, _, _) = await d.CaptureFrame(ct);
            // Два соседних кадра совпали — упёрлись в край (дальше некуда).
            if (FrameStability.IsEndOfScroll([Hash(prev), Hash(cur)]) || FrameStability.AreStable(prev, cur, 1.0))
            {
                atStart = true;
                prev = cur;
                break;
            }

            prev = cur;
            if (started.ElapsedMilliseconds > maxElapsedMs)
            {
                reason = "time";
                break;
            }
        }

        return (prev, atStart, refused, reason);
    }

    /// <summary>
    /// Калибровка шага (M4): один щелчок по каждой оси, фактический сдвиг (shiftPerTick).
    /// Возвращает (px per tick по X, px per tick по Y, Ambiguous); 0 — ось не двигает.
    /// M-D: подсказка оценщику — ожидаемый сдвиг на щелчок (не 0), и флаг Ambiguous не
    /// игнорируется (повторяющийся узор → период, а не щелчок).
    /// </summary>
    private static async Task<(int X, int Y, bool Ambiguous)> CalibrateStep(
        ScanCanvasDeps d, ScanCanvasOptions o, Rect region, byte[] atStart, int w, int h, CancellationToken ct)
    {
        // M4: один щелчок по каждой оси, фактический сдвиг (shiftPerTick). Подсказка 0 (окно
        // симметричное от нуля, ±35 %): один щелчок — малый сдвиг, он всегда в этом окне. M-D:
        // флаг Ambiguous НЕ игнорируем (повторяющийся узор → несколько равных пиков → период,
        // а не щелчок) → выше даём uncertain_geometry.

        // Горизонталь: один щелчок вправо, измеряем сдвиг, возврат влево.
        var shiftX = 0;
        var ambiguous = false;
        if (await ScrollAxis(d, o, Axis.Horizontal, +1, ct))
        {
            var (afterX, _, _) = await d.CaptureFrame(ct);
            var estX = MeasureShiftEstimate(o, Axis.Horizontal, 0, atStart, afterX, w, h, region);
            shiftX = Math.Abs(estX.Shift);
            ambiguous |= estX.Ambiguous;
            await ScrollAxis(d, o, Axis.Horizontal, -1, ct); // возврат
        }

        // Вертикаль: один щелчок вниз, измеряем сдвиг, возврат вверх.
        var shiftY = 0;
        if (await ScrollAxis(d, o, Axis.Vertical, +1, ct))
        {
            var (afterY, _, _) = await d.CaptureFrame(ct);
            var estY = MeasureShiftEstimate(o, Axis.Vertical, 0, atStart, afterY, w, h, region);
            shiftY = Math.Abs(estY.Shift);
            ambiguous |= estY.Ambiguous;
            await ScrollAxis(d, o, Axis.Vertical, -1, ct); // возврат
        }

        return (shiftX, shiftY, ambiguous);
    }

    /// <summary>
    /// Прокрутка на ОДИН щелчок по оси в заданном направлении, по способу <paramref name="o"/>:
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
                // M2: горизонталь — Shift удержан + вертикальное колесо; KeyUp в finally, его
                // результат не игнорируется (отказ → ошибка + отпускание всего через HeldKeyTracker).
                var shiftDown = await d.KeyDown("Shift", ct);
                if (!shiftDown)
                    return false;
                var wheelOk = false;
                try
                {
                    wheelOk = await d.WheelScroll(vertDir, 1, ct);
                }
                finally
                {
                    if (!await d.KeyUp("Shift", ct))
                    {
                        // M2: отказ KeyUp — отпустить всё (HeldKeyTracker) и пометить шаг: агент
                        // узнает об отказе, а не получит молчаливый «успех».
                        ReleaseAllHeldKeys();
                        wheelOk = false;
                    }
                }
                return wheelOk;

            case ScrollMethod.Arrows:
            default:
                if (axis == Axis.Vertical)
                    return await d.PressKey(vertDir, "None", 1, ct);
                return await d.PressKey(horizDir, "None", 1, ct);
        }
    }

    /// <summary>
    /// C1: «сдвиг содержимого между кадрами» (<see cref="ShiftEstimator"/>, положительный — контент
    /// <c>before</c> сдвинут вниз/вправо в <c>after</c>) → «смещение плитки на холсте» (положительный
    /// — окно сдвинулось вправо/вниз). Инверсия — в этом ЕДИНСТВЕННОМ месте: позиция плитки
    /// (<c>canvasX/Y += tileOffset</c>) и <c>planner.Feed</c> (контракт m-D: вправо/вниз —
    /// положительный) получают уже «смещение плитки», а не сырой сдвиг содержимого.
    /// </summary>
    private static int ToTileShift(int contentShift) => -contentShift;

    /// <summary>
    /// Знаковый сдвиг шага (из <see cref="ShiftEstimator"/>), C3: оценка ТОЛЬКО по области
    /// содержимого — <paramref name="before"/>/<paramref name="after"/> вырезаются в
    /// <paramref name="region"/> (рамка/лента/линейки не тянут оценку к нулю). Знак: «сдвиг
    /// содержимого» (положительный — контент <c>before</c> сдвинут вниз/вправо в <c>after</c>).
    /// Плоский/неопределённый сдвиг → 0. <paramref name="w"/>/<paramref name="h"/> — размер окна.
    /// </summary>
    /// <summary>
    /// Полный результат оценки сдвига шага (<see cref="ShiftResult"/>: сдвиг, NCC, Ambiguous) по
    /// области содержимого. <paramref name="expectedShift"/> — подсказка: K1 — окно поиска строится
    /// ВОКРУГ неё (не симметрично от нуля: при перекрытии &lt; 50 % истинный сдвиг &gt; 50 % размера);
    /// M-D — для калибровки это ожидаемый сдвиг на щелчок, а не 0 (иначе повторяющийся узор даёт
    /// период, а не щелчок).
    /// </summary>
    private static ShiftResult MeasureShiftEstimate(ScanCanvasOptions o, Axis axis, int expectedShift, byte[] before, byte[] after, int w, int h, Rect region, double searchFraction = 0.35, double minOverlapFraction = 0.10)
    {
        var (beforeCrop, cw, ch) = CropRegion(before, w, h, region);
        var (afterCrop, _, _) = CropRegion(after, w, h, region);
        return ShiftEstimator.EstimateAround(Gray(beforeCrop), Gray(afterCrop), cw, ch, axis, expectedShift, searchFraction, minOverlapFraction);
    }


    /// <summary>Знаковый сдвиг шага (только величина, из <see cref="MeasureShiftEstimate"/>).</summary>
    private static int MeasureShift(ScanCanvasOptions o, Axis axis, int expectedShift, byte[] before, byte[] after, int w, int h, Rect region, double searchFraction = 0.35, double minOverlapFraction = 0.10)
        => MeasureShiftEstimate(o, axis, expectedShift, before, after, w, h, region, searchFraction, minOverlapFraction).Shift;

    /// <summary>
    /// C3: вырез области содержимого из полного кадра окна: рамка/лента/линейки НЕ попадают в
    /// холст и НЕ тянут оценку сдвига. Возвращает (RGB-пиксели области, ширина, высота).
    /// </summary>
    private static (byte[] Pixels, int Width, int Height) CropRegion(byte[] rgb, int w, int h, Rect region)
    {
        var cw = Math.Max(0, region.Width);
        var ch = Math.Max(0, region.Height);
        var cropped = new byte[cw * ch * 3];
        for (var y = 0; y < ch; y++)
        {
            var srcY = region.Y + y;
            if (srcY < 0 || srcY >= h)
                continue;
            for (var x = 0; x < cw; x++)
            {
                var srcX = region.X + x;
                if (srcX < 0 || srcX >= w)
                    continue;
                var srcIdx = (srcY * w + srcX) * 3;
                var dstIdx = (y * cw + x) * 3;
                cropped[dstIdx] = rgb[srcIdx];
                cropped[dstIdx + 1] = rgb[srcIdx + 1];
                cropped[dstIdx + 2] = rgb[srcIdx + 2];
            }
        }
        return (cropped, cw, ch);
    }

    /// <summary>
    /// Плитка холста: область содержимого кадра (C3) в позиции (canvasX, canvasY) на холсте.
    /// <paramref name="measuredShift"/> — смещение плитки на холсте (C1, из ToTileShift);
    /// <paramref name="expectedShift"/> — ожидаемый сдвиг шага (для детекции сомнительных швов).
    /// </summary>
    private static Tile MakeTile(byte[] rgb, int w, int h, Rect region, int canvasX, int canvasY, int measuredShift, int expectedShift)
    {
        var (pixels, cw, ch) = CropRegion(rgb, w, h, region);
        return new Tile(
            Pixels: pixels,
            Width: cw,
            Height: ch,
            CanvasX: canvasX,
            CanvasY: canvasY,
            MeasuredShift: measuredShift,
            ExpectedShift: expectedShift,
            Score: 1.0,
            Ambiguous: false);
    }

    // ---------- реальные швы (Windows) ----------

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

    /// <summary>M2: при отказе KeyUp — отпустить ВСЕ удержанные клавиши (HeldKeyTracker).</summary>
    private static void ReleaseAllHeldKeys()
    {
        _ = WindowsToolsBase.KeyboardInputService.ReleaseAllKeysAsync(CancellationToken.None);
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
    /// C4: PNG (8 бит, RGB/RGBA) → RGB-пиксели (3 канала) через общий декодер <see cref="PngDecoder"/>
    /// (все пять фильтров, CRC, zlib). RGBA → RGB: альфа композитится на белый (простое и честное:
    /// непрозрачный фон схем — no-op, полупрозрачное не «пропадает»).
    /// </summary>
    internal static byte[] DecodePngToRgb(byte[] png)
    {
        var (decoded, w, h, channels) = PngDecoder.Decode(png);
        if (channels == 3)
            return decoded;

        // RGBA → RGB: композит на белый (alpha/255 * pixel + (255 - alpha/255) * 255).
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            var s = i * 4;
            var a = decoded[s + 3] / 255.0;
            for (var c = 0; c < 3; c++)
                rgb[i * 3 + c] = (byte)(decoded[s + c] * a + 255 * (1 - a));
        }
        return rgb;
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
    /// <c>truncated</c>+причина, длительность). Картинку в ответ НЕ возвращаем. Minor (a): ошибка
    /// (<c>StopReason=error</c>) → <c>IsError=true</c>, иначе агент не увидит провал.
    /// </summary>
    private static CallToolResult ToCallToolResult(ScanCanvasResult r)
    {
        var seams = r.DoubtfulSeams
            .Select(s => new { reason = s.Reason, tileA = s.TileA, tileB = s.TileB })
            .ToArray();

        var isError = r.StopReason == "error" || !r.Done;

        var payload = new
        {
            success = !isError,
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
            IsError = isError,
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
        /// <summary>M3: поставить курсор в точку (windowX, windowY) в координатах ОКНА (центр region/окна).</summary>
        public required Func<int, int, CancellationToken, Task<bool>> PlaceCursor { get; init; }
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
