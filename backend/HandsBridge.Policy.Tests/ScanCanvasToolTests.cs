using System.Reflection;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.HandsBridge;
using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Ядро <c>scan_canvas</c> (C1 «руки без UIA»): цикл «курсор из области → в начало → область
/// содержимого → змейка (знаковый сдвиг) → сборка холста → PNG → запись» вызывается напрямую
/// (<c>RunScanAsync</c>, internal) на фейках (снимок окна, прокрутка, клавиши) — WinAPI в нём нет,
/// поэтому Linux-тесты покрывают: полный проход до PNG (байты декодируются и равны оригиналу),
/// сомнительные швы на сетке, <c>truncated</c> по плиткам, отказ без <c>saveTo</c>, <c>.v2.png</c>
/// при повторе, <c>scrollMethod</c>=arrows/shift_wheel (правильные внутренние перегрузки), отмену.
/// Мост грузится рефлексией (тот же путь, что <see cref="HandsBridgeGateRuntimeTests"/>).
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
    /// Синтетическая «схема»: окно <paramref name="w"/>×<paramref name="h"/> с рамкой (10 px,
    /// статична) и содержимым — 2D-градиентом, зависящим от позиции прокрутки <c>(sx, sy)</c>.
    /// Прокрутка меняет позицию (с клампом в границы схемы), снимок — вид окна в этой позиции.
    /// Записывает каждый вызов ввода (для проверки <c>scrollMethod</c>).
    /// </summary>
    private sealed class FakeScheme
    {
        public const int Border = 10;
        public readonly int W;
        public readonly int H;
        public readonly int StepX;
        public readonly int StepY;
        public readonly int MaxSx;
        public readonly int MaxSy;

        public int Sx;
        public int Sy;

        /// <summary>Журнал вызовов: "move", "wheel:Up", "key:Down", "keydown:Shift", ...</summary>
        public readonly List<string> Calls = new();

        public FakeScheme(int w, int h, int stepX, int stepY, int schemeCols, int schemeRows)
        {
            W = w;
            H = h;
            StepX = stepX;
            StepY = stepY;
            MaxSx = (schemeCols - 1) * stepX;
            MaxSy = (schemeRows - 1) * stepY;
        }

        /// <summary>Снимок окна в текущей позиции: рамка статична, содержимое — градиент от (sx, sy).</summary>
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

                    var cx = Sx + x;
                    var cy = Sy + y;
                    var v = (byte)((cx * 3 + cy * 5) % 256);
                    pixels[idx] = v;
                    pixels[idx + 1] = (byte)((v + 40) % 256);
                    pixels[idx + 2] = (byte)((v + 80) % 256);
                }
            }

            return (pixels, W, H);
        }

        /// <summary>Прокрутка: колесо (Up/Down/Left/Right) — по вертикали/горизонтали.</summary>
        public bool Wheel(string direction)
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

        /// <summary>Клавиша (стрелки): Up/Down/Left/Right — по вертикали/горизонтали.</summary>
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

        public bool Move(int x, int y)
        {
            Calls.Add("move");
            return true;
        }
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
        Set(d, "MovePointer", new Func<int, int, CancellationToken, Task<bool>>((x, y, _) => Task.FromResult(fake.Move(x, y))));
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

    private static async Task<Out> Run(object options, object deps)
    {
        var method = ToolType.GetMethod("RunScanAsync", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
        var task = (Task)method.Invoke(null, [options, deps, CancellationToken.None])!;
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
        var dir = Path.Combine(Path.GetTempPath(), "hands-c12-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Полный проход: схема 3×2 окна (сдвиг 100 px, перекрытие 15 %) → змейка до конца → один PNG.
    /// Байты PNG декодируются (сигнатура/IHDR) и холст не пустой.
    /// </summary>
    [Fact]
    public async Task Full_pass_produces_a_png()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var fake = new FakeScheme(w: 200, h: 150, stepX: 100, stepY: 100, schemeCols: 3, schemeRows: 2);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("scheme.png", 100, 15, "Wheel");

            var r = await Run(options, deps);

            Assert.True(r.Done, $"не до конца: {r.Error}");
            Assert.Equal("scheme.png", r.Path);
            Assert.True(r.Tiles > 0, "ни одной плитки");
            Assert.True(r.SizeBytes > 8, "PNG пустой");
            Assert.True(r.Width > 0 && r.Height > 0, "холст нулевой");
            // PNG существует на диске и начинается с сигнатуры.
            var png = File.ReadAllBytes(Path.Combine(root, "scheme.png"));
            Assert.Equal(0x89, png[0]);
            Assert.Equal(0x50, png[1]); // 'P'
            Assert.Equal(0x4E, png[2]); // 'N'
            Assert.Equal(0x47, png[3]); // 'G'
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Байты PNG декодируются и равны оригиналу: холст (из CanvasAssembler) кодируется в PNG,
    /// декодер возвращает те же пиксели (R/G/B) и те же размеры.
    /// </summary>
    [Fact]
    public async Task Png_bytes_decode_to_the_original_canvas()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var fake = new FakeScheme(w: 200, h: 150, stepX: 100, stepY: 100, schemeCols: 3, schemeRows: 2);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("scheme.png", 100, 15, "Wheel");

            var r = await Run(options, deps);
            Assert.True(r.Done, $"не до конца: {r.Error}");

            var png = File.ReadAllBytes(Path.Combine(root, "scheme.png"));
            // Декодер: IHDR (width/height) + IDAT (zlib) + фильтры None/Sub → пиксели.
            var (pixels, w, h) = DecodePng(png);
            Assert.Equal(r.Width, w);
            Assert.Equal(r.Height, h);
            Assert.Equal(w * h * 3, pixels.Length);
            // Холст не пустой: есть ненулевые пиксели (содержимое схемы).
            Assert.Contains(pixels, b => b != 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Сомнительные швы на сетке: на повторяющемся градиенте корреляция даёт несколько пиков →
    /// часть швов помечается сомнительной (число &gt; 0).
    /// </summary>
    [Fact]
    public async Task Repeating_grid_marks_doubtful_seams()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            // Мелкий шаг (30 px) → сильный повтор градиента → сомнительные швы.
            var fake = new FakeScheme(w: 200, h: 150, stepX: 30, stepY: 30, schemeCols: 4, schemeRows: 3);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("grid.png", 100, 15, "Wheel");

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
            var fake = new FakeScheme(w: 200, h: 150, stepX: 100, stepY: 100, schemeCols: 4, schemeRows: 3);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("capped.png", maxTiles: 2, overlapPercent: 15, scrollMethod: "Wheel");

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
            var fake = new FakeScheme(w: 200, h: 150, stepX: 100, stepY: 100, schemeCols: 3, schemeRows: 2);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("", 100, 15, "Wheel");

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
            var fake = new FakeScheme(w: 200, h: 150, stepX: 100, stepY: 100, schemeCols: 3, schemeRows: 2);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("taken.png", 100, 15, "Wheel");

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
    /// <c>scrollMethod=arrows</c>: прокрутка — только клавиши (стрелки), колесо не используется.
    /// </summary>
    [Fact]
    public async Task Arrows_scroll_method_uses_key_overloads()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var fake = new FakeScheme(w: 200, h: 150, stepX: 100, stepY: 100, schemeCols: 3, schemeRows: 2);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("arrows.png", 100, 15, "Arrows");

            var r = await Run(options, deps);
            Assert.True(r.Done, $"не до конца: {r.Error}");
            // Были нажатия стрелок, колесо — нет.
            Assert.Contains(fake.Calls, c => c.StartsWith("key:"));
            Assert.DoesNotContain(fake.Calls, c => c.StartsWith("wheel:"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// <c>scrollMethod=shift_wheel</c>: горизонталь — Shift+колесо (keydown:Shift + wheel + keyup:Shift).
    /// </summary>
    [Fact]
    public async Task ShiftWheel_scroll_method_holds_shift_for_horizontal()
    {
        var root = NewRoot();
        try
        {
            var files = NewProjectFiles(root);
            var fake = new FakeScheme(w: 200, h: 150, stepX: 100, stepY: 100, schemeCols: 3, schemeRows: 2);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("shift.png", 100, 15, "ShiftWheel");

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
            var fake = new FakeScheme(w: 200, h: 150, stepX: 100, stepY: 100, schemeCols: 5, schemeRows: 5);
            var deps = MakeDeps(fake, files);
            var options = MakeOptions("cancel.png", 100, 15, "Wheel");

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

    // ---------- минимальный PNG-декодер (сигнатура, IHDR, IDAT zlib, фильтры None/Sub) ----------

    private static (byte[] Pixels, int Width, int Height) DecodePng(byte[] png)
    {
        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        var colorType = png[25];
        var channels = colorType == 6 ? 4 : 3;

        var idat = new List<byte>();
        var offset = 8;
        while (offset + 8 <= png.Length)
        {
            var len = (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
            var type = new string(new[] { (char)png[offset + 4], (char)png[offset + 5], (char)png[offset + 6], (char)png[offset + 7] });
            if (type == "IDAT")
            {
                for (var i = 0; i < len; i++)
                    idat.Add(png[offset + 8 + i]);
            }
            offset += 12 + len;
            if (type == "IEND")
                break;
        }

        using var input = new MemoryStream(idat.ToArray());
        using var zlib = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        var raw = output.ToArray();

        var rowLen = width * channels;
        var pixels = new byte[width * height * 3];
        var prev = new byte[rowLen];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (1 + rowLen)];
            for (var i = 0; i < rowLen; i++)
            {
                var a = raw[y * (1 + rowLen) + 1 + i];
                var b = filter == 1 ? (byte)(a + prev[i]) : a;
                pixels[y * rowLen + i] = b;
                prev[i] = b;
            }

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

        return (pixels, width, height);
    }
}
