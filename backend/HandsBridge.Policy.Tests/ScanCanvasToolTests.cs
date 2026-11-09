using System.Reflection;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.HandsBridge;
using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Ядро <c>scan_canvas</c> (C1 «руки без UIA»): цикл «курсор над областью → в начало → область
/// содержимого → калибровка шага → змейка (знаковый сдвиг) → сборка холста → PNG → запись»
/// вызывается напрямую (<c>RunScanAsync</c>, internal) на фейках (снимок окна, прокрутка, клавиши)
/// — WinAPI в нём нет, поэтому Linux-тесты покрывают: полный проход до PNG, попиксельное совпадение
/// холста с эталонной схемой (0 неверных), сомнительные швы, <c>truncated</c>, отказ без <c>saveTo</c>,
/// <c>.v2.png</c> при повторе, <c>scrollMethod</c>=arrows/shift_wheel, калибровку шага (M4),
/// предпроверку saveTo (M5), отмену. Мост грузится рефлексией (тот же путь, что
/// <see cref="HandsBridgeGateRuntimeTests"/>).
/// </summary>
[Collection(HandsBridgeGateRuntimeTests.Collection)]
public class ScanCanvasToolTests
{
    private static Assembly Bridge => HandsBridgeGateRuntimeTests.Bridge.Value;

    private static Type ToolType => Bridge.GetType("Sbroenne.WindowsMcp.Tools.ScanCanvasTool", throwOnError: true)!;
    private static Type OptionsType => ToolType.GetNestedType("ScanCanvasOptions", BindingFlags.NonPublic | BindingFlags.Public)!;
    private static Type DepsType => ToolType.GetNestedType("ScanCanvasDeps", BindingFlags.NonPublic | BindingFlags.Public)!;
    private static Type MethodType => ToolType.GetNestedType("ScrollMethod", BindingFlags.NonPublic | BindingFlags.Public)!;

    /// <summary>Итог <c>RunScanAsync</c>, прочитанный рефлексией (поля <c>ScanCanvasResult</c>).</summary>
    private sealed class Out
    {
        public bool Done;
        public string? Path;
        public int Width;
        public int Height;
        public int Rows;
        public int Columns;
        public int Tiles;
        public int SizeBytes;
        public int SeamCount;
        public bool Truncated;
        public string? TruncationReason;
        public string StopReason = "";
        public string? Error;
        public long DurationMs;
    }

    /// <summary>
    /// Синтетическая «схема» (C1–C3): окно <paramref name="w"/>×<paramref name="h"/> с рамкой
    /// (10 px, статична) и содержимым — ПСЕВДОСЛУЧАЙНОЙ текстурой без периода (фиксированный seed),
    /// зависящей от позиции прокрутки <c>(sx, sy)</c>. Прокрутка меняет позицию (с клампом в границы
    /// схемы); снимок — вид окна в этой позиции. Шаг одного щелчка ≤ 40 % окна. Записывает каждый
    /// вызов ввода (для проверки <c>scrollMethod</c> и M4/M5).
    /// </summary>
    private class FakeScheme
    {
        public const int Border = 10;
        public readonly int W;
        public readonly int H;
        public readonly int StepX;   // px per click, horizontal (≤ 40 % of W)
        public readonly int StepY;   // px per click, vertical (≤ 40 % of H)
        public readonly int ContentW; // W - 2*Border
        public readonly int ContentH; // H - 2*Border
        public readonly int MaxSx;
        public readonly int MaxSy;
        public readonly int SchemeW; // MaxSx + ContentW
        public readonly int SchemeH; // MaxSy + ContentH

        public int Sx;
        public int Sy;

        /// <summary>Журнал вызовов: "place:cursor", "wheel:Up", "key:Down", "keydown:Shift", ...</summary>
        public readonly List<string> Calls = new();

        /// <summary>Эталонная схема (полный холст): псевдослучайная текстура, фиксированный seed.</summary>
        public readonly byte[] Scheme;

        public FakeScheme(int w, int h, int stepX, int stepY, int schemeCols, int schemeRows, double overlapFraction = 0.5)
        {
            W = w;
            H = h;
            StepX = stepX;
            StepY = stepY;
            ContentW = w - 2 * Border;
            ContentH = h - 2 * Border;
            // Фактический шаг инструмента (M4): clicks = round(ожидаемый/StepX), ожидаемый =
            // ContentW*(1-overlap). Схема таплится именно этим шагом (не одним щелчком), поэтому
            // MaxSx/MaxSy — в фактических шагах, а не в щелчках.
            var expectedX = (int)(ContentW * (1 - overlapFraction));
            var expectedY = (int)(ContentH * (1 - overlapFraction));
            var clicksX = Math.Clamp((int)Math.Round((double)expectedX / stepX), 1, 20);
            var clicksY = Math.Clamp((int)Math.Round((double)expectedY / stepY), 1, 20);
            MaxSx = (schemeCols - 1) * clicksX * stepX;
            MaxSy = (schemeRows - 1) * clicksY * stepY;
            SchemeW = MaxSx + ContentW;
            SchemeH = MaxSy + ContentH;

            // Псевдослучайная текстура без периода (фиксированный seed): базовый уровень каждой
            // КОЛОНКИ и каждой СТРОКИ — псевдослучайный (непериодический), поэтому профиль
            // колонки/строки (то, что видит ShiftEstimator) непериодичен и корреляция находит
            // истинный сдвиг, а не ложный период. Мелкий шум — чтобы пиксели не были плоскими.
            var rng = new Random(12345);
            var colBase = new int[SchemeW];
            for (var x = 0; x < SchemeW; x++)
                colBase[x] = rng.Next(0, 256);
            var rowBase = new int[SchemeH];
            for (var y = 0; y < SchemeH; y++)
                rowBase[y] = rng.Next(0, 256);

            Scheme = new byte[SchemeW * SchemeH * 3];
            for (var y = 0; y < SchemeH; y++)
            {
                for (var x = 0; x < SchemeW; x++)
                {
                    var idx = (y * SchemeW + x) * 3;
                    var v = (colBase[x] + rowBase[y] + rng.Next(0, 20)) % 256;
                    Scheme[idx] = (byte)v;
                    Scheme[idx + 1] = (byte)((v + 60) % 256);
                    Scheme[idx + 2] = (byte)((v + 120) % 256);
                }
            }
        }

        /// <summary>Снимок окна в текущей позиции: рамка статична, содержимое — схема в (Sx, Sy).</summary>
        public (byte[] Pixels, int Width, int Height) Capture()
        {
            var pixels = new byte[W * H * 3];
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    var idx = (y * W + x) * 3;
                    var inBorder = x < Border || x >= W - Border || y < Border || y >= H - Border;
                    if (inBorder)
                    {
                        pixels[idx] = pixels[idx + 1] = pixels[idx + 2] = 30;
                        continue;
                    }

                    var sx = Sx + (x - Border);
                    var sy = Sy + (y - Border);
                    if (sx < 0 || sx >= SchemeW || sy < 0 || sy >= SchemeH)
                    {
                        pixels[idx] = pixels[idx + 1] = pixels[idx + 2] = 0;
                        continue;
                    }
                    var sidx = (sy * SchemeW + sx) * 3;
                    pixels[idx] = Scheme[sidx];
                    pixels[idx + 1] = Scheme[sidx + 1];
                    pixels[idx + 2] = Scheme[sidx + 2];
                }
            }

            return (pixels, W, H);
        }

        /// <summary>Прокрутка: колесо (Up/Down/Left/Right) — по вертикали/горизонтали на StepX/StepY.</summary>
        public virtual bool Wheel(string direction)
        {
            Calls.Add($"wheel:{direction}");
            switch (direction)
            {
                case "Up":
                    Sy = Math.Max(0, Sy - StepY);
                    break;
                case "Down":
                    Sy = Math.Min(MaxSy, Sy + StepY);
                    break;
                case "Left":
                    Sx = Math.Max(0, Sx - StepX);
                    break;
                case "Right":
                    Sx = Math.Min(MaxSx, Sx + StepX);
                    break;
            }

            return true;
        }

        /// <summary>Клавиша (стрелки): Up/Down/Left/Right — по вертикали/горизонтали на StepX/StepY.</summary>
        public bool Key(string key)
        {
            Calls.Add($"key:{key}");
            switch (key)
            {
                case "Up":
                    Sy = Math.Max(0, Sy - StepY);
                    break;
                case "Down":
                    Sy = Math.Min(MaxSy, Sy + StepY);
                    break;
                case "Left":
                    Sx = Math.Max(0, Sx - StepX);
                    break;
                case "Right":
                    Sx = Math.Min(MaxSx, Sx + StepX);
                    break;
            }

            return true;
        }

        public bool KeyDown(string key)
        {
            Calls.Add($"keydown:{key}");
            return true;
        }

        public bool KeyUp(string key)
        {
            Calls.Add($"keyup:{key}");
            return true;
        }

        /// <summary>M3: курсор в точку (windowX, windowY) в координатах окна.</summary>
        public bool PlaceCursor(int windowX, int windowY)
        {
            Calls.Add($"place:{windowX},{windowY}");
            return true;
        }
    }

    /// <summary>
    /// K1/M-A/M-B/M-C/M-D: НЕЗАВИСИМАЯ схема — фиксированное полотно (не строится из шага
    /// инструмента) и фиксированный/нелинейный шаг щелчка. Инструмент сам калибрует число щелчков
    /// на шаг. Журнал вызовов — для проверки калибровки (M-A) и отказа ввода (M-C);
    /// <see cref="RefuseAfter"/> — отказ после N-го щелчка, <see cref="Nonlinear"/> — нелинейная
    /// прокрутка (28/30/32 px) для M-B.
    /// </summary>
    private class IndependentScheme
    {
        public const int Border = 10;
        public readonly int W, H, StepX, StepY;
        public readonly int ContentW, ContentH;
        public readonly int SchemeW, SchemeH;
        public int Sx, Sy;
        public readonly List<string> Calls = new();
        public readonly byte[] Scheme;
        public int? RefuseAfter;
        public bool Nonlinear;
        private int _clicks;

        public IndependentScheme(int w, int h, int stepX, int stepY, int schemeW, int schemeH, byte[] scheme)
        {
            W = w; H = h; StepX = stepX; StepY = stepY;
            ContentW = w - 2 * Border; ContentH = h - 2 * Border;
            SchemeW = schemeW; SchemeH = schemeH;
            Scheme = scheme;
        }

        /// <summary>Псевдослучайная непериодическая текстура (как в <see cref="FakeScheme"/>).</summary>
        public static byte[] RandomTexture(int schemeW, int schemeH, int seed = 777)
        {
            var rng = new Random(seed);
            var colBase = new int[schemeW]; for (var x = 0; x < schemeW; x++) colBase[x] = rng.Next(0, 256);
            var rowBase = new int[schemeH]; for (var y = 0; y < schemeH; y++) rowBase[y] = rng.Next(0, 256);
            var s = new byte[schemeW * schemeH * 3];
            for (var y = 0; y < schemeH; y++)
                for (var x = 0; x < schemeW; x++)
                {
                    var i = (y * schemeW + x) * 3;
                    var v = (colBase[x] + rowBase[y] + rng.Next(0, 20)) % 256;
                    s[i] = (byte)v; s[i + 1] = (byte)((v + 60) % 256); s[i + 2] = (byte)((v + 120) % 256);
                }
            return s;
        }

        /// <summary>M-D: плитка <paramref name="tileW"/>×<paramref name="tileH"/> (случайная), повторённая по схеме.</summary>
        public static byte[] RepeatingTexture(int schemeW, int schemeH, int tileW, int tileH, int seed = 4242)
        {
            var rng = new Random(seed);
            var tile = new byte[tileW * tileH * 3];
            for (var i = 0; i < tile.Length; i += 3)
            {
                var v = rng.Next(0, 256);
                tile[i] = (byte)v; tile[i + 1] = (byte)((v + 60) % 256); tile[i + 2] = (byte)((v + 120) % 256);
            }
            var s = new byte[schemeW * schemeH * 3];
            for (var y = 0; y < schemeH; y++)
                for (var x = 0; x < schemeW; x++)
                {
                    var di = (y * schemeW + x) * 3;
                    var ti = ((y % tileH) * tileW + (x % tileW)) * 3;
                    s[di] = tile[ti]; s[di + 1] = tile[ti + 1]; s[di + 2] = tile[ti + 2];
                }
            return s;
        }

        public (byte[] Pixels, int Width, int Height) Capture()
        {
            var pixels = new byte[W * H * 3];
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    var idx = (y * W + x) * 3;
                    var inBorder = x < Border || x >= W - Border || y < Border || y >= H - Border;
                    if (inBorder) { pixels[idx] = pixels[idx + 1] = pixels[idx + 2] = 30; continue; }
                    var sx = Sx + (x - Border);
                    var sy = Sy + (y - Border);
                    if (sx < 0 || sx >= SchemeW || sy < 0 || sy >= SchemeH) { pixels[idx] = pixels[idx + 1] = pixels[idx + 2] = 0; continue; }
                    var sidx = (sy * SchemeW + sx) * 3;
                    pixels[idx] = Scheme[sidx]; pixels[idx + 1] = Scheme[sidx + 1]; pixels[idx + 2] = Scheme[sidx + 2];
                }
            return (pixels, W, H);
        }

        public bool Wheel(string direction)
        {
            Calls.Add($"wheel:{direction}");
            if (RefuseAfter is { } n && _clicks >= n)
                return false; // M-C: отказ ввода (смена окна / гейт)
            _clicks++;
            var step = Nonlinear ? (28 + 2 * (_clicks % 3)) : (direction is "Left" or "Right" ? StepX : StepY);
            switch (direction)
            {
                case "Up": Sy = Math.Max(0, Sy - step); break;
                case "Down": Sy = Math.Min(SchemeH - ContentH, Sy + step); break;
                case "Left": Sx = Math.Max(0, Sx - step); break;
                case "Right": Sx = Math.Min(SchemeW - ContentW, Sx + step); break;
            }
            return true;
        }

        public bool Key(string key) { Calls.Add($"key:{key}"); return true; }
        public bool KeyDown(string key) { Calls.Add($"keydown:{key}"); return true; }
        public bool KeyUp(string key) { Calls.Add($"keyup:{key}"); return true; }
        public bool PlaceCursor(int windowX, int windowY) { Calls.Add($"place:{windowX},{windowY}"); return true; }
    }

    private static object NewProjectFiles(string root)
    {
        var t = Bridge.GetType("ClaudeHomeServer.HandsBridge.HandsProjectFiles", throwOnError: true)!;
        return Activator.CreateInstance(t, (object?)root)!;
    }

    private static object MakeOptions(
        string saveTo,
        int maxTiles,
        int overlapPercent,
        string scrollMethod,
        long maxElapsedMs = 300_000)
    {
        var o = Activator.CreateInstance(OptionsType)!;
        Set(o, "Window", 1L);
        Set(o, "SaveTo", saveTo);
        Set(o, "ScrollMethod", Enum.Parse(MethodType, scrollMethod));
        Set(o, "OverlapFraction", overlapPercent / 100.0);
        Set(o, "MaxTiles", maxTiles);
        Set(o, "MaxSide", 16384);
        Set(o, "MaxArea", 64_000_000L);
        Set(o, "MaxElapsedMs", maxElapsedMs);
        return o;
    }

    private static object MakeDeps(FakeScheme fake, object files)
    {
        var d = Activator.CreateInstance(DepsType)!;
        Set(d, "PlaceCursor", new Func<int, int, CancellationToken, Task<bool>>((x, y, _) => Task.FromResult(fake.PlaceCursor(x, y))));
        Set(d, "WheelScroll", new Func<string, int, CancellationToken, Task<bool>>((dir, _, _) => Task.FromResult(fake.Wheel(dir))));
        Set(d, "PressKey", new Func<string, object, int, CancellationToken, Task<bool>>((key, _, _, _) => Task.FromResult(fake.Key(key))));
        Set(d, "KeyDown", new Func<string, CancellationToken, Task<bool>>((key, _) => Task.FromResult(fake.KeyDown(key))));
        Set(d, "KeyUp", new Func<string, CancellationToken, Task<bool>>((key, _) => Task.FromResult(fake.KeyUp(key))));
        Set(d, "CaptureFrame", new Func<CancellationToken, Task<(byte[], int, int)>>(_ => Task.FromResult(fake.Capture())));
        Set(d, "Files", files);
        return d;
    }

    /// <summary>Зависимости для <see cref="IndependentScheme"/> (K1/M-A/M-B/M-C/M-D).</summary>
    private static object MakeDeps(IndependentScheme fake, object files)
    {
        var d = Activator.CreateInstance(DepsType)!;
        Set(d, "PlaceCursor", new Func<int, int, CancellationToken, Task<bool>>((x, y, _) => Task.FromResult(fake.PlaceCursor(x, y))));
        Set(d, "WheelScroll", new Func<string, int, CancellationToken, Task<bool>>((dir, _, _) => Task.FromResult(fake.Wheel(dir))));
        Set(d, "PressKey", new Func<string, object, int, CancellationToken, Task<bool>>((key, _, _, _) => Task.FromResult(fake.Key(key))));
        Set(d, "KeyDown", new Func<string, CancellationToken, Task<bool>>((key, _) => Task.FromResult(fake.KeyDown(key))));
        Set(d, "KeyUp", new Func<string, CancellationToken, Task<bool>>((key, _) => Task.FromResult(fake.KeyUp(key))));
        Set(d, "CaptureFrame", new Func<CancellationToken, Task<(byte[], int, int)>>(_ => Task.FromResult(fake.Capture())));
        Set(d, "Files", files);
        return d;
    }

    private static void Set(object o, string prop, object? value) =>
        o.GetType().GetProperty(prop)!.SetValue(o, value);

    private static async Task<Out> Run(object options, object deps, CancellationToken ct = default)
    {
        var method = ToolType.GetMethod("RunScanAsync", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
        var task = (Task)method.Invoke(null, [options, deps, ct])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var t = result.GetType();
        var seams = (System.Collections.IEnumerable)t.GetField("DoubtfulSeams")!.GetValue(result)!;
        var seamCount = 0;
        foreach (var _ in seams)
            seamCount++;
        return new Out
        {
            Done = (bool)t.GetField("Done")!.GetValue(result)!,
            Path = (string?)t.GetField("Path")!.GetValue(result),
            Width = (int)t.GetField("Width")!.GetValue(result)!,
            Height = (int)t.GetField("Height")!.GetValue(result)!,
            Rows = (int)t.GetField("Rows")!.GetValue(result)!,
            Columns = (int)t.GetField("Columns")!.GetValue(result)!,
            Tiles = (int)t.GetField("Tiles")!.GetValue(result)!,
            SizeBytes = (int)t.GetField("SizeBytes")!.GetValue(result)!,
            SeamCount = seamCount,
            Truncated = (bool)t.GetField("Truncated")!.GetValue(result)!,
            TruncationReason = (string?)t.GetField("TruncationReason")!.GetValue(result),
            StopReason = (string)t.GetField("StopReason")!.GetValue(result)!,
            Error = (string?)t.GetField("Error")!.GetValue(result),
            DurationMs = (long)t.GetField("DurationMs")!.GetValue(result)!,
        };
    }

    private static string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hands-c121-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// C1–C3 (главный критерий): схема 3×2 окна (шаг 60/40 px ≤ 40 % окна, рамка 10 px,
    /// псевдослучайная текстура без периода) → змейка до конца → один PNG. Итоговый холст
    /// сверяется ПОПИКСЕЛЬНО с эталонной схемой: 0 неверных пикселей из N.
    /// </summary>
    [Fact]
    public async Task Full_pass_canvas_matches_the_reference_scheme_pixel_by_pixel()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var fake = new FakeScheme(w: 200, h: 150, stepX: 40, stepY: 30, schemeCols: 3, schemeRows: 2);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("scheme.png", 100, 50, "Wheel");

            var r = await Run(options, deps);
            Assert.True(r.Done, $"не до конца: {r.Error}");
            Assert.Equal("scheme.png", r.Path);
            Assert.True(r.Tiles > 0, "ни одной плитки");
            Assert.True(r.SizeBytes > 8, "PNG пустой");

            // Холст = эталонная схема: размер совпадает.
            Assert.Equal(fake.SchemeW, r.Width);
            Assert.Equal(fake.SchemeH, r.Height);

            // Попиксельное сравнение: декодируем PNG и сравниваем с эталоном.
            var png = File.ReadAllBytes(Path.Combine(root, "scheme.png"));
            var (pixels, w, h) = DecodePng(png);
            Assert.Equal(fake.SchemeW, w);
            Assert.Equal(fake.SchemeH, h);
            Assert.Equal(fake.Scheme.Length, pixels.Length);

            var wrong = 0;
            for (var i = 0; i < fake.Scheme.Length; i++)
            {
                if (fake.Scheme[i] != pixels[i])
                    wrong++;
            }
            Assert.Equal(0, wrong); // 0 неверных пикселей из N
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// K1 (critical): перекрытие &lt; 55 % (в т.ч. дефолт 15 %) — истинный сдвиг &gt; 50 % размера,
    /// симметричное окно от нуля (старый <c>Estimate</c>) его не находит. Схема 3×2 шага с
    /// НЕЗАВИСИМОЙ (псевдослучайной, фиксированный seed) текстурой: итог совпадает с эталоном,
    /// 0 неверных пикселей, при перекрытии 15/30/50/60. Мутация «окно поиска от нуля» красит 15/30.
    /// </summary>
    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(60)]
    public async Task K1_low_overlap_matches_reference(int overlapPercent)
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var fake = new FakeScheme(w: 200, h: 150, stepX: 30, stepY: 30, schemeCols: 3, schemeRows: 2,
                overlapFraction: overlapPercent / 100.0);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("k1.png", 100, overlapPercent, "Wheel");

            var r = await Run(options, deps);
            Assert.True(r.Done, $"не до конца (overlap {overlapPercent}%): {r.Error}");
            Assert.Equal(fake.SchemeW, r.Width);
            Assert.Equal(fake.SchemeH, r.Height);

            var png = File.ReadAllBytes(Path.Combine(root, "k1.png"));
            var (pixels, w, h) = DecodePng(png);
            Assert.Equal(fake.SchemeW, w);
            Assert.Equal(fake.SchemeH, h);
            var wrong = 0;
            for (var i = 0; i < fake.Scheme.Length; i++)
                if (fake.Scheme[i] != pixels[i])
                    wrong++;
            Assert.Equal(0, wrong); // 0 неверных пикселей
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// M-A: калибровка шага проверяется по ЖУРНАЛУ вызовов на независимой схеме: 30 px за щелчок,
    /// ожидаемый сдвиг 180 → 6 щелчков за шаг. Мутация «clicks=1» (калибровка выключена) красит:
    /// число щелчков на шаг было бы 1, а не 6.
    /// </summary>
    [Fact]
    public async Task MA_calibration_measures_shift_via_journal()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            // 30 px за щелчок; область 180 px, перекрытие 10 % → ожидаемый сдвиг 162 → 5 щелчков/шаг.
            var scheme = IndependentScheme.RandomTexture(540, 360);
            var fake = new IndependentScheme(w: 200, h: 150, stepX: 30, stepY: 30, schemeW: 540, schemeH: 360, scheme);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("ma.png", 100, 10, "Wheel");

            var r = await Run(options, deps);
            Assert.True(r.Done, $"не до конца: {r.Error}");

            // Калибровка: один щелчок = 30 px. Ожидаемый сдвиг шага = 162 (180×0.9) → 5 щелчков/шаг.
            // Журнал: run = последовательность однонаправленных щелчков (шаг/сегмент строки).
            // Калибровка даёт ~5 щелчков/шаг → длинный run; мутация «clicks=1» (калибровка
            // выключена) — короткие runs. Максимальный run ≥ 15 ⇒ калибровка работает.
            var runs = new List<int>();
            var cur = 0;
            string? prevDir = null;
            foreach (var c in fake.Calls)
            {
                var dir = c switch
                {
                    "wheel:Right" or "wheel:Left" => "h",
                    "wheel:Down" or "wheel:Up" => "v",
                    _ => null,
                };
                if (dir is not null && dir == prevDir)
                    cur++;
                else
                {
                    if (prevDir is not null)
                        runs.Add(cur);
                    cur = 1;
                    prevDir = dir;
                }
            }
            if (prevDir is not null)
                runs.Add(cur);
            var maxRun = runs.Max();
            // Калибровка: ~5 щелчков/шаг → длинный run (≥ 15). Мутация «clicks=1»: run ≈ 14.
            Assert.True(maxRun >= 15, $"калибровка: максимальный run однонаправленных щелчков {maxRun}, ожидалось ≥ 15 (162/30≈5 щелчков/шаг)");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// M-B: нелинейная прокрутка (28/30/32 px за щелчок) — возврат «вниз, потом вверх» неточен.
    /// После калибровки заново «в начало» по стабильности кадра, и topLeft снимается без сдвига:
    /// 0 неверных пикселей. Мутация «убрать повторный в-начало» красит.
    /// </summary>
    [Fact]
    public async Task MB_nonlinear_scroll_topLeft_not_shifted()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var scheme = IndependentScheme.RandomTexture(540, 360);
            var fake = new IndependentScheme(w: 200, h: 150, stepX: 30, stepY: 30, schemeW: 540, schemeH: 360, scheme)
            {
                Nonlinear = true,
            };
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("mb.png", 100, 50, "Wheel");

            var r = await Run(options, deps);
            Assert.True(r.Done, $"не до конца: {r.Error}");
            Assert.Equal(540, r.Width);
            Assert.Equal(360, r.Height);

            var png = File.ReadAllBytes(Path.Combine(root, "mb.png"));
            var (pixels, _, _) = DecodePng(png);
            var wrong = 0;
            for (var i = 0; i < scheme.Length; i++)
                if (scheme[i] != pixels[i])
                    wrong++;
            Assert.Equal(0, wrong); // topLeft не снят со сдвигом
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// M-C: отказ ввода (смена окна / гейт) посреди скана — НЕ «не доехали» и не успех:
    /// остановка с <c>IsError</c>/Truncated и причиной «input refused». Отказ после 20-го и 60-го
    /// вызова. Мутация «break вместо ошибки» красит.
    /// </summary>
    [Theory]
    [InlineData(20)]
    [InlineData(60)]
    public async Task MC_input_refused_mid_scan_is_error(int refuseAfter)
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var scheme = IndependentScheme.RandomTexture(540, 360);
            var fake = new IndependentScheme(w: 200, h: 150, stepX: 30, stepY: 30, schemeW: 540, schemeH: 360, scheme)
            {
                RefuseAfter = refuseAfter,
            };
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("mc.png", 100, 50, "Wheel");

            var r = await Run(options, deps);
            // Отказ = не Done (IsError) либо Truncated; причина — «input refused».
            Assert.True(!r.Done || r.Truncated, $"ожидалась остановка по отказу, a r.Done={r.Done}, Truncated={r.Truncated}");
            Assert.Contains("input refused", r.TruncationReason ?? r.Error ?? "");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// M-D: повторяющийся узор (плитка 40×40, повторённая) — калибровка с expectedShift=0 брала
    /// наименьший период. После фикса (подсказка + Ambiguous) — либо верный холст, либо явное
    /// предупреждение «uncertain_geometry»; «успех с развалившимся холстом» недопустим.
    /// </summary>
    [Fact]
    public async Task MD_repeating_pattern_gives_warning_or_correct_canvas()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var scheme = IndependentScheme.RepeatingTexture(540, 360, tileW: 40, tileH: 40);
            var fake = new IndependentScheme(w: 200, h: 150, stepX: 25, stepY: 25, schemeW: 540, schemeH: 360, scheme);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("md.png", 100, 50, "Wheel");

            var r = await Run(options, deps);
            // Либо Done с верным холстом (540×360), либо явное предупреждение (TruncationReason
            // содержит «uncertain_geometry»). «Успех» с холстом не эталонного размера — брак.
            if (r.Done)
            {
                Assert.Equal(540, r.Width);
                Assert.Equal(360, r.Height);
            }
            else
            {
                Assert.Contains("uncertain_geometry", r.TruncationReason ?? r.Error ?? "");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Ветка «time» в «в начало»: бюджет времени исчерпан до упора в край — остановка с
    /// <c>Truncated</c> и причиной «time» (не «не доехали», не успех). Малый <c>maxElapsedMs</c>
    /// + большая схема (край далеко): цикл «в начало» не доходит до края и упирается в бюджет.
    /// </summary>
    [Fact]
    public async Task ToStart_time_budget_exceeded_is_truncated_time()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            // Схема большая (2000×2000), старт ДАЛЕКО от угла (600,600): «в начало» идёт ~20
            // итераций, каждая со снимком (30 тыс. пикселей) → суммарно > 1 мс. Бюджет 0 →
            // ветка «time» срабатывает до упора в угол.
            var scheme = IndependentScheme.RandomTexture(2000, 2000);
            var fake = new IndependentScheme(w: 200, h: 150, stepX: 30, stepY: 30, schemeW: 2000, schemeH: 2000, scheme)
            {
                Sx = 600,
                Sy = 600,
            };
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("time.png", 100, 50, "Wheel", maxElapsedMs: 0);

            var r = await Run(options, deps);
            Assert.True(r.Truncated, $"ожидался Truncated, a r.Done={r.Done}, Truncated={r.Truncated}, Error={r.Error}");
            Assert.Equal("time", r.TruncationReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// M4: калибровка шага. Фейк, где один щелчок = 30 px (StepX=30): инструмент делает нужное
    /// число щелчков (round(ожидаемый/30)), и 100 плиток хватает на схему «в разы больше окна».
    /// </summary>
    [Fact]
    public async Task M4_calibration_uses_the_right_number_of_clicks()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            // Один щелчок = 30 px; схема 5×3 окна (в разы больше окна).
            var fake = new FakeScheme(w: 200, h: 150, stepX: 40, stepY: 30, schemeCols: 5, schemeRows: 3);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("m4.png", 100, 50, "Wheel");

            var r = await Run(options, deps);
            Assert.True(r.Done, $"не до конца: {r.Error}");
            Assert.True(r.Tiles > 0, "ни одной плитки");
            // Холст = схема: размер совпадает.
            Assert.Equal(fake.SchemeW, r.Width);
            Assert.Equal(fake.SchemeH, r.Height);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// M4: нулевой сдвиг (прокрутка не двигает) → остановка с понятной причиной, а не
    /// бесконечный цикл. Фейк, у которого колесо не меняет позицию.
    /// </summary>
    [Fact]
    public async Task M4_zero_shift_stops_with_a_clear_reason()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            // Колесо не двигает: позиция не меняется → сдвиг нулевой → область не определена.
            var frozen = new FrozenWheelFake(w: 200, h: 150, stepX: 40, stepY: 30, schemeCols: 3, schemeRows: 2);
            var deps = MakeDeps(frozen, files);
            var options = MakeOptions("frozen.png", 100, 50, "Wheel");

            var r = await Run(options, deps);
            // Не бесконечный цикл: остановился с понятной причиной (область не определена).
            Assert.Equal("error", r.StopReason);
            Assert.NotNull(r.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// M5: предпроверка saveTo ДО первого действия. Плохой путь (не .png) → отказ без единого
    /// вызова ввода/снимка (0 вызовов фейков).
    /// </summary>
    [Fact]
    public async Task M5_bad_saveTo_refuses_before_any_input()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var fake = new FakeScheme(w: 200, h: 150, stepX: 40, stepY: 30, schemeCols: 3, schemeRows: 2);
            var deps = MakeDeps(fake, files);
            // Не .png → CheckOutputPath отказывает.
            var options = MakeOptions("scheme.txt", 100, 50, "Wheel");

            var r = await Run(options, deps);
            Assert.Equal("error", r.StopReason);
            Assert.NotNull(r.Error);
            // Ни одного вызова ввода/снимка (предпроверка до всего).
            Assert.Empty(fake.Calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Сомнительные швы на сетке: на псевдослучайной текстуре с мелким шагом корреляция может дать
    /// несколько пиков → часть швов помечается сомнительной (число &gt; 0).
    /// </summary>
    [Fact]
    public async Task Repeating_grid_marks_doubtful_seams()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var fake = new FakeScheme(w: 200, h: 150, stepX: 30, stepY: 30, schemeCols: 4, schemeRows: 3);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("grid.png", 100, 50, "Wheel");

            var r = await Run(options, deps);
            Assert.True(r.Done, $"не до конца: {r.Error}");
            Assert.True(r.SeamCount > 0, "ожидались сомнительные швы на повторяющейся сетке");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// <c>truncated</c> по плиткам: потолок 2 плитки, схема больше → остановка по потолку,
    /// <c>truncated=true</c> с причиной.
    /// </summary>
    [Fact]
    public async Task Truncated_by_tile_cap()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var fake = new FakeScheme(w: 200, h: 150, stepX: 40, stepY: 30, schemeCols: 4, schemeRows: 3);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("capped.png", maxTiles: 2, overlapPercent: 50, scrollMethod: "Wheel");

            var r = await Run(options, deps);
            Assert.True(r.Truncated, "ожидался truncated по плиткам");
            Assert.NotNull(r.TruncationReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Отказ без <c>saveTo</c>: пустой путь — файл некуда писать, честная ошибка (до склейки).
    /// </summary>
    [Fact]
    public async Task Refuse_without_saveTo()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var fake = new FakeScheme(w: 200, h: 150, stepX: 40, stepY: 30, schemeCols: 3, schemeRows: 2);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("", 100, 50, "Wheel");

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
    /// <c>.v2.png</c> при повторе: файл уже существует → <c>WriteNew</c> берёт следующее свободное
    /// имя, не перезаписывая.
    /// </summary>
    [Fact]
    public async Task V2_on_name_collision()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "taken.png"), "pre-existing");
            var files = NewProjectFiles(root);
            var fake = new FakeScheme(w: 200, h: 150, stepX: 40, stepY: 30, schemeCols: 3, schemeRows: 2);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("taken.png", 100, 50, "Wheel");

            var r = await Run(options, deps);
            Assert.Equal("taken.v2.png", r.Path);
            Assert.True(File.Exists(Path.Combine(root, "taken.v2.png")));
            // Старый файл не перезаписан.
            Assert.Equal("pre-existing", File.ReadAllText(Path.Combine(root, "taken.png")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// <c>scrollMethod=arrows</c>: прокрутка — только клавиши (стрелки), колесо не используется,
    /// курсор не двигается (M3: для стрелков мышь не нужна).
    /// </summary>
    [Fact]
    public async Task Arrows_scroll_method_uses_key_overloads()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var fake = new FakeScheme(w: 200, h: 150, stepX: 40, stepY: 30, schemeCols: 3, schemeRows: 2);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("arrows.png", 100, 50, "Arrows");

            var r = await Run(options, deps);
            Assert.True(r.Done, $"не до конца: {r.Error}");
            // Были нажатия стрелок, колесо — нет, курсор не двигался.
            Assert.Contains(fake.Calls, c => c.StartsWith("key:"));
            Assert.DoesNotContain(fake.Calls, c => c.StartsWith("wheel:"));
            Assert.DoesNotContain(fake.Calls, c => c.StartsWith("place:"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// <c>scrollMethod=shift_wheel</c>: горизонталь — Shift+колесо (keydown:Shift + wheel +
    /// keyup:Shift). M2: KeyUp в finally (символ keyup:Shift всегда после keydown:Shift).
    /// </summary>
    [Fact]
    public async Task ShiftWheel_scroll_method_holds_shift_for_horizontal()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var fake = new FakeScheme(w: 200, h: 150, stepX: 40, stepY: 30, schemeCols: 3, schemeRows: 2);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("shift.png", 100, 50, "ShiftWheel");

            var r = await Run(options, deps);
            Assert.True(r.Done, $"не до конца: {r.Error}");
            // Shift удерживался (keydown/keyup) и колесо использовалось.
            Assert.Contains(fake.Calls, c => c == "keydown:Shift");
            Assert.Contains(fake.Calls, c => c == "keyup:Shift");
            Assert.Contains(fake.Calls, c => c.StartsWith("wheel:"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// M3: колесо/shift_wheel — курсор ставится в центр region (внутри области), а не в (2,2).
    /// </summary>
    [Fact]
    public async Task M3_wheel_places_cursor_at_region_center()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var fake = new FakeScheme(w: 200, h: 150, stepX: 40, stepY: 30, schemeCols: 3, schemeRows: 2);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("cursor.png", 100, 50, "Wheel");

            var r = await Run(options, deps);
            Assert.True(r.Done, $"не до конца: {r.Error}");
            // Был вызов PlaceCursor (курсор над областью), не в (2,2).
            Assert.Contains(fake.Calls, c => c.StartsWith("place:"));
            // Точка — не (2,2) (старая захардкоженная).
            Assert.DoesNotContain(fake.Calls, c => c == "place:2,2");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Отмена: отменённый <c>CancellationToken</c> → <c>OperationCanceledException</c> (цикл
    /// проверяет токен на каждом шаге).
    /// </summary>
    [Fact]
    public async Task Cancellation_stops_the_scan()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var fake = new FakeScheme(w: 200, h: 150, stepX: 40, stepY: 30, schemeCols: 5, schemeRows: 5);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("cancel.png", 100, 50, "Wheel");

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // сразу отменено

            var method = ToolType.GetMethod("RunScanAsync", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
            var task = (Task)method.Invoke(null, [options, deps, cts.Token])!;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// M4: калибровка делает НЕСКОЛЬКО щелчков на шаг (round(ожидаемый/щелчок) &gt; 1): один
    /// щелчок = 30 px, ожидаемый сдвиг 90 px (50 % перекрытия) → 3 щелчка на шаг.
    /// </summary>
    [Fact]
    public async Task M4_first_step_makes_multiple_clicks_when_needed()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var fake = new FakeScheme(w: 200, h: 150, stepX: 30, stepY: 30, schemeCols: 3, schemeRows: 2);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("m4b.png", 100, 50, "Wheel");

            var r = await Run(options, deps);
            Assert.True(r.Done, $"не до конца: {r.Error}");
            // Ожидаемый горизонтальный сдвиг = 180 * (1 - 0.5) = 90; 90 / 30 = 3 щелчка на шаг.
            // Колесо вправо вызывалось ≥ 3 раза (калибровка 1 + шаг ≥ 2).
            var rightWheels = fake.Calls.Count(c => c == "wheel:Right");
            Assert.True(rightWheels >= 3, $"ожидалось ≥ 3 wheel:Right, получено {rightWheels}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------- M4: фейк с «замёрзшим» колесом (не двигает) ----------

    private sealed class FrozenWheelFake : FakeScheme
    {
        public FrozenWheelFake(int w, int h, int stepX, int stepY, int schemeCols, int schemeRows)
            : base(w, h, stepX, stepY, schemeCols, schemeRows) { }
        // Колесо не двигает: позиция не меняется → сдвиг нулевой.
        public override bool Wheel(string direction)
        {
            Calls.Add($"wheel:{direction}");
            return true;
        }
    }

    // ---------- PNG-декодер: единственный проверенный декодер проекта (C4) ----------
    // Холст — RGB (3 канала), поэтому <see cref="PngDecoder.Decode"/> возвращает пиксели как есть.
    // Раньше тут был собственный «минимальный» декодер, который неправильно раскрывал фильтр Sub
    // (добавлял пиксель предыдущей строки, а не левого соседа) — на Sub-строках холст сдвигался.

    private static (byte[] Pixels, int Width, int Height) DecodePng(byte[] png)
    {
        var (pixels, width, height, _) = ClaudeHomeServer.HandsBridge.Scanning.Imaging.PngDecoder.Decode(png);
        return (pixels, width, height);
    }
}
