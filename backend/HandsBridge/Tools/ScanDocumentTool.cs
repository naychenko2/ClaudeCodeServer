using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.HandsBridge;
using ClaudeHomeServer.HandsBridge.Policy;
using ClaudeHomeServer.HandsBridge.Scanning.Pixels;
using ClaudeHomeServer.HandsBridge.Scanning.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sbroenne.WindowsMcp.Capture;
using Sbroenne.WindowsMcp.Models;
using Sbroenne.WindowsMcp.Native;

namespace Sbroenne.WindowsMcp.Tools;

/// <summary>
/// Чтение многостраничного документа из окна без UIA (Citrix, Vidi, Visio) — шаг A5 «руки без UIA».
/// Документ (Word, 50+ страниц, таблицы) открыт в окне, где нет ни буфера, ни дерева UIA: доступны
/// только клавиши, мышь и снимки. Инструмент листает документ <c>pageKey</c> (PageDown), на каждой
/// странице снимает окно в память, распознаёт текст (<c>ocr</c>), склеивает соседние страницы
/// (<see cref="DocumentStitcher"/>, учитывая колонтитулы и перекрытие) и дописывает результат в файл
/// проекта (<c>.md</c>/<c>.txt</c>) через <see cref="HandsProjectFiles"/>. Конец — по повтору кадра
/// (два одинаковых кадра подряд после <c>pageKey</c>) или по <c>maxPages</c>.
/// <para>
/// Один вызов обрабатывает не больше <c>PagesPerCall</c> страниц (~5 минут); дальше отвечает
/// <c>{done:false, next:{...}}</c> с курсором, и следующий вызов с <c>continueFrom</c> дописывает
/// ТОТ ЖЕ файл. Состояние привязано к ходу: если процесс моста перезапущен, продолжение отказывает
/// «начните заново» (файл не создан этим экземпляром), а <c>.v2</c> не перезаписывает.
/// </para>
/// </summary>
[McpServerToolType]
[SupportedOSPlatform("windows")]
public static partial class ScanDocumentTool
{
    /// <summary>Потолок <c>maxPages</c>: 300 страниц за документ.</summary>
    private const int MaxPagesCap = 300;

    /// <summary>Страниц за один вызов: больше — нарезка <c>{done:false, next}</c>.</summary>
    private const int DefaultPagesPerCall = 10;

    /// <summary>Бюджет времени одного вызова: ~5 минут.</summary>
    private const int DefaultMaxCallMs = 300_000;

    /// <summary>Потолок текста в ответе: 4000 символов (остальное — в файле).</summary>
    private const int MaxResponseText = 4000;

    /// <summary>Повтор страницы при ошибке OCR (503 busy / page_timeout): до 2 повторов с паузой.</summary>
    private const int MaxOcrRetries = 2;

    /// <summary>Пауза между повторами OCR, мс.</summary>
    private const int OcrRetryDelayMs = 500;

    /// <summary>
    /// Снимки для скана: в бою — <c>WindowsToolsBase.ScreenshotService</c> (тот же снимок окна, что
    /// <c>screenshot_control</c> и <c>ocr</c>: PNG в памяти, на диск не пишется); в тестах — фейк.
    /// </summary>
    internal static ScreenshotService? CaptureService { get; set; }

    private static ScreenshotService Capture => CaptureService ?? WindowsToolsBase.ScreenshotService;

    /// <summary>
    /// Reads a multi-page document from a window that exposes no UIA tree (Citrix, Vidi, Visio):
    /// the document is scrolled with <c>pageKey</c>, each page is captured, recognized (OCR) and
    /// stitched into a <c>.md</c>/<c>.txt</c> file in the project. Use it when the clipboard is
    /// unavailable (remote desktops) and the document is longer than one screen.
    /// <para>
    /// One call reads at most ~10 pages (~5 minutes); when more remain it answers
    /// <c>{done:false, next:{...}}</c> — call again with that <c>continueFrom</c> cursor to append
    /// to the same file. The end is detected by a repeated frame (two identical frames in a row
    /// after <c>pageKey</c>) or by <c>maxPages</c>. The reply carries at most 4000 characters of the
    /// most recent pages plus metadata (path, pages, characters, doubtful stitches, stop reason).
    /// </para>
    /// </summary>
    /// <param name="windowHandle">Window handle (decimal string from window_management). Required: the document window to read; it must accept input and be the foreground window (activate it with window_management first).</param>
    /// <param name="startKey">Key pressed once before reading to return to the top of the document (default <c>Ctrl+Home</c>; pass an empty value to skip it).</param>
    /// <param name="pageKey">The key that advances one page (default <c>PageDown</c>).</param>
    /// <param name="maxPages">Maximum number of pages to read (default 300, cap 300).</param>
    /// <param name="saveTo">Where to save the document: a path relative to the project root ending in .md or .txt (required — without it the document cannot be saved).</param>
    /// <param name="keepPages">When true, each page screenshot is also written to .cc-attachments/ (default false: screenshots are not kept).</param>
    /// <param name="continueFrom">The continuation cursor from a previous <c>{done:false, next}</c> reply; when set, the same file is appended to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A call result: the read text (capped at 4000 characters), the file path, the page count, the character count, the number of doubtful stitches, and the continuation cursor when the document is not finished.</returns>
    [McpServerTool(Name = "scan_document", Title = "Scan Document", Destructive = true, OpenWorld = false)]
    public static async partial Task<CallToolResult> ExecuteAsync(
        [DefaultValue(null)] string? windowHandle,
        [DefaultValue(null)] string? startKey,
        [DefaultValue(null)] string? pageKey,
        [DefaultValue(null)] int? maxPages,
        string saveTo,
        [DefaultValue(false)] bool keepPages,
        [DefaultValue(null)] string? continueFrom,
        CancellationToken cancellationToken)
    {
        // windowHandle обязателен (как у keyboard_control): без него клавиши улетят в чужое окно.
        if (string.IsNullOrWhiteSpace(windowHandle))
            return Fail("scan_document requires windowHandle: the document window to read. " +
                        "Call window_management(action='list') to get it.");

        if (!WindowHandleParser.TryParse(windowHandle, out var parsed) || parsed == IntPtr.Zero)
            return Fail($"Invalid windowHandle: '{windowHandle}'. Expected a decimal string from window_management.");

        var window = parsed.ToInt64();

        // Гейт: окно обязано принимать ввод и быть передним планом (запрещённое приложение — отказ
        // ДО первого снимка). «press» — действие ввода, поэтому гейт проверяет окно как у клавиатуры.
        var gate = HandsGate.Policy.CheckKeyboard(window, "press");
        if (!gate.Allowed)
            return HandsGate.Deny(HandsTools.ScanDocument, gate);

        // Машина занята другим ходом — ответ этого вызова: действие не идёт, ход продолжается.
        var action = "scan";
        if (HandsGate.Acted(HandsTools.ScanDocument, action) is { } busy)
            return busy;

        // OCR-эндпоинт не передан: агент старый.
        if (OcrClient.Endpoint is null)
            return Fail("ocr unavailable: update the agent (it does not pass --ocr-endpoint to the bridge)");

        var options = new ScanOptions
        {
            Window = window,
            SaveTo = saveTo,
            StartKey = string.IsNullOrWhiteSpace(startKey) ? "Ctrl+Home" : startKey,
            PageKey = string.IsNullOrWhiteSpace(pageKey) ? "PageDown" : pageKey,
            MaxPages = maxPages is null ? 300 : Math.Clamp(maxPages.Value, 1, MaxPagesCap),
            KeepPages = keepPages,
            ContinueFrom = continueFrom,
            PagesPerCall = DefaultPagesPerCall,
            MaxCallMs = DefaultMaxCallMs,
        };

        var deps = new ScanDeps
        {
            CapturePage = ct => CaptureWindowAsync(windowHandle, ct),
            Recognize = async (png, ct) => (object)await OcrClient.RecognizeAsync(png, ct),
            HashFrame = HashPng,
            PressKey = (combo, ct) => PressComboAsync(combo, window, ct),
            WaitStable = ct => WaitStableAsync(windowHandle, ct),
            Files = HandsGate.ProjectFiles,
        };

        try
        {
            var result = await RunScanAsync(options, deps, cancellationToken);
            return ToCallToolResult(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(WindowsToolsBase.SerializeToolError("scan_document", ex));
        }
    }

    // ---------- ядро цикла (internal: тесты зовут его напрямую на Linux) ----------

    /// <summary>
    /// Цикл страниц: <c>startKey</c> → [стабилизация → снимок → OCR → склейка → запись →
    /// <c>pageKey</c> → стабилизация] до повтора кадра (конец) или до бюджета/<c>maxPages</c>.
    /// Чистая логика без WinAPI: все обращения к системе — через <paramref name="deps"/>.
    /// </summary>
    internal static async Task<ScanResult> RunScanAsync(ScanOptions o, ScanDeps d, CancellationToken ct)
    {
        var result = new ScanResult { StopReason = "end_of_document" };

        // Курсор продолжения: файл, число страниц, хэш последнего кадра.
        string? contFile = null;
        int contPages = 0;
        ulong? contLastHash = null;
        if (o.ContinueFrom is not null)
        {
            var cursor = TryParseCursor(o.ContinueFrom);
            if (cursor is null)
                return FailResult("continueFrom is not a continuation cursor from a previous scan_document reply.");
            (contFile, contPages, contLastHash) = cursor.Value;
        }

        var existing = "";              // весь текст документа в памяти (для склейки)
        var prevExistingLen = 0;        // длина existing до текущей страницы (для дельты AppendOwn)
        var fileWritten = contFile is not null;
        var pagesThisCall = 0;
        var totalPages = contPages;
        var totalChars = 0;
        var prevHash = contLastHash;    // хэш последнего кадра до этого вызова
        var started = Stopwatch.StartNew();

        // Первый вызов: в начало документа.
        if (contFile is null && !string.IsNullOrWhiteSpace(o.StartKey))
        {
            if (!await d.PressKey(o.StartKey, ct))
                return FailResult($"start key '{o.StartKey}' before the first page.");
            await d.WaitStable(ct);
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // 1. Стабилизация кадра после предыдущей pageKey.
            await d.WaitStable(ct);

            // 2. Снимок окна в память.
            var frame = await d.CapturePage(ct);
            var hash = d.HashFrame(frame);

            // 3. Конец: два кадра подряд с одинаковым хэшем после pageKey — контент не двигается.
            if (prevHash is { } ph && ph == hash)
            {
                result.StopReason = "end_of_document";
                break;
            }

            // 4. OCR страницы (повтор при 503 busy / page_timeout).
            var (text, ocrOk, ocrErr) = await RecognizeWithRetryAsync(d, frame, ct);
            if (!ocrOk)
            {
                result.StopReason = "error";
                result.Error = ocrErr;
                break;  // частичный файл: предыдущие страницы уже записаны
            }

            // 5. Склейка в накопленный текст (колонтитулы, перекрытие, швы).
            existing = string.IsNullOrEmpty(existing) ? text : DocumentStitcher.Append(existing, text).Text;

            // 6. Запись в файл проекта: WriteNew первый раз, AppendOwn дальше (только свой файл).
            if (!fileWritten)
            {
                var (path, reason) = d.Files.WriteNew(HandsTools.ScanDocument, o.SaveTo, Encoding.UTF8.GetBytes(existing));
                if (path is null)
                    return FailResult($"first page recognized, but the file could not be created: {reason}");
                result.Path = path;
                fileWritten = true;
            }
            else
            {
                var delta = existing.Length > prevExistingLen ? existing[prevExistingLen..] : string.Empty;
                if (delta.Length > 0)
                {
                    var (path, reason) = d.Files.AppendOwn(HandsTools.ScanDocument, o.SaveTo, Encoding.UTF8.GetBytes(delta));
                    if (path is null)
                        return FailResult($"the file could not be appended to: {reason}");
                    result.Path = path;
                }
            }

            prevExistingLen = existing.Length;
            totalChars = existing.Length;
            pagesThisCall++;
            totalPages++;

            // keepPages: снимок страницы в .cc-attachments/ (служебный путь — WriteNew).
            if (o.KeepPages)
                _ = d.Files.WriteNew(HandsTools.ScanDocument, $".cc-attachments/scan-{totalPages:000}.png", frame);

            prevHash = hash;

            // 7. Бюджет: не больше PagesPerCall страниц или MaxCallMs за вызов.
            if (pagesThisCall >= o.PagesPerCall || started.ElapsedMilliseconds >= o.MaxCallMs)
            {
                if (totalPages < o.MaxPages)
                {
                    result.StopReason = "budget";
                    break;
                }
            }

            // 8. maxPages.
            if (totalPages >= o.MaxPages)
            {
                result.StopReason = "max_pages";
                break;
            }

            // 9. Лист
            if (!await d.PressKey(o.PageKey, ct))
                return FailResult($"page key '{o.PageKey}' failed after page {totalPages}.");
        }

        result.Pages = pagesThisCall;
        result.TotalPages = totalPages;
        result.TotalChars = totalChars;
        result.Existing = existing;
        result.DoubtfulStitches = CountStitches(existing);
        result.Done = result.StopReason is "end_of_document" or "max_pages";
        if (result.StopReason == "budget")
            result.Next = BuildCursor(result.Path, totalPages, prevHash);

        return result;
    }

    /// <summary>
    /// OCR страницы с повтором: при 503 busy / page_timeout страница повторяется до
    /// <see cref="MaxOcrRetries"/> раз с паузой; прочие отказы (no_endpoint, too_large, …) — сразу.
    /// </summary>
    private static async Task<(string Text, bool Ok, string? Error)> RecognizeWithRetryAsync(
        ScanDeps d, byte[] frame, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var outcome = (OcrOutcome)await d.Recognize(frame, ct);
            if (outcome.Ok)
                return (outcome.Text ?? "", true, null);

            var retryable = outcome.Reason is "busy" or "busy_timeout" or "page_timeout" or "timeout";
            if (!retryable || attempt >= MaxOcrRetries)
                return ("", false, outcome.Message);

            await Task.Delay(OcrRetryDelayMs, ct);
        }
    }

    /// <summary>Число сомнительных швов (<c>&lt;!-- шов: проверить --&gt;</c>) в накопленном тексте.</summary>
    private static int CountStitches(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var count = 0;
        var from = 0;
        while ((from = text.IndexOf(DocumentStitcher.DoubtfulMarker, from, StringComparison.Ordinal)) >= 0)
        {
            count++;
            from += DocumentStitcher.DoubtfulMarker.Length;
        }
        return count;
    }

    /// <summary>Курсор продолжения: путь, число страниц, хэш последнего кадра (JSON).</summary>
    private static string? BuildCursor(string? file, int pages, ulong? lastHash)
    {
        if (file is null)
            return null;
        return JsonSerializer.Serialize(new ScanCursor
        {
            File = file,
            Pages = pages,
            LastHash = lastHash?.ToString("x16"),
        });
    }

    private static (string? File, int Pages, ulong? LastHash)? TryParseCursor(string json)
    {
        try
        {
            var c = JsonSerializer.Deserialize<ScanCursor>(json);
            if (c is null || string.IsNullOrWhiteSpace(c.File))
                return null;
            ulong? hash = c.LastHash is null ? null : ulong.Parse(c.LastHash, System.Globalization.NumberStyles.HexNumber);
            return (c.File, c.Pages, hash);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------- реальные швы (Windows) ----------

    /// <summary>Снимок окна: PNG в памяти (на диск не пишется).</summary>
    private static async Task<byte[]> CaptureWindowAsync(string windowHandle, CancellationToken ct)
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
        return Convert.FromBase64String(capture.ImageData);
    }

    /// <summary>Нажатие комбинации (например <c>Ctrl+Home</c>) в целевом окне.</summary>
    private static async Task<bool> PressComboAsync(string combo, long window, CancellationToken ct)
    {
        var (key, mods) = ParseKeyCombo(combo);
        if (key is null)
            return false;
        var res = await WindowsToolsBase.KeyboardInputService.PressKeyAsync(key, mods, 1, (nint)window, ct);
        return res.Success;
    }

    /// <summary>Разбор комбинации: модификаторы (Ctrl/Shift/Alt/Win) + главная клавиша.</summary>
    private static (string? Key, ModifierKey Mods) ParseKeyCombo(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mods = ModifierKey.None;
        string? key = null;
        foreach (var p in parts)
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKey.Ctrl; break;
                case "shift": mods |= ModifierKey.Shift; break;
                case "alt": mods |= ModifierKey.Alt; break;
                case "win" or "windows" or "meta": mods |= ModifierKey.Win; break;
                default: key = p; break;
            }
        }
        return (key ?? parts[^1], mods);
    }

    /// <summary>
    /// Стабилизация кадра: снимаем, ждём, снимаем снова; пока два кадра стабильны
    /// (<see cref="FrameStability.AreStable"/>) — кадр «перестал меняться».
    /// </summary>
    private static async Task WaitStableAsync(string windowHandle, CancellationToken ct)
    {
        byte[]? prev = null;
        for (var i = 0; i < 5; i++)
        {
            var frame = await CaptureWindowAsync(windowHandle, ct);
            if (prev is not null && FrameStability.AreStable(prev, frame, 1.0))
                return;
            prev = frame;
            await Task.Delay(150, ct);
        }
    }

    /// <summary>
    /// Хэш кадра: FNV-1a 64 по байтам PNG. Детерминированный: одинаковые кадры дают одинаковый
    /// хэш (конец по повтору), разные — разные.
    /// </summary>
    private static ulong HashPng(byte[] data)
    {
        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211;
        var hash = FnvOffset;
        foreach (var b in data)
            hash = (hash ^ b) * FnvPrime;
        return hash;
    }

    // ---------- ответ ----------

    /// <summary>
    /// Ответ модели: текст ≤ 4000 (последние страницы) + метаданные (путь, страниц, символов,
    /// швов «проверить», причина остановки, курсор продолжения).
    /// </summary>
    private static CallToolResult ToCallToolResult(ScanResult r)
    {
        var text = string.IsNullOrEmpty(r.Existing)
            ? ""
            : r.Existing.Length > MaxResponseText ? r.Existing[^MaxResponseText..] : r.Existing;

        var payload = new
        {
            success = true,
            done = r.Done,
            path = r.Path,
            pages = r.TotalPages,
            characters = r.TotalChars,
            doubtfulStitches = r.DoubtfulStitches,
            stopReason = r.StopReason,
            error = r.Error,
            text,
            next = r.Next,
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

    private static ScanResult FailResult(string message) => new()
    {
        Done = false,
        StopReason = "error",
        Error = message,
    };

    // ---------- структуры ----------

    /// <summary>Параметры скана (внутренние: тесты задают маленький бюджет).</summary>
    internal sealed class ScanOptions
    {
        public required long Window { get; init; }
        public required string SaveTo { get; init; }
        public required string StartKey { get; init; }
        public required string PageKey { get; init; }
        public int MaxPages { get; init; } = 300;
        public bool KeepPages { get; init; }
        public string? ContinueFrom { get; init; }
        public int PagesPerCall { get; init; } = DefaultPagesPerCall;
        public int MaxCallMs { get; init; } = DefaultMaxCallMs;
    }

    /// <summary>
    /// Швы цикла: снимок, OCR, хэш, клавиша, стабилизация, запись. В бою — Windows-сервисы,
    /// в тестах — фейки.
    /// </summary>
    internal sealed class ScanDeps
    {
        public required Func<CancellationToken, Task<byte[]>> CapturePage { get; init; }
        public required Func<byte[], CancellationToken, Task<object>> Recognize { get; init; }
        public required Func<byte[], ulong> HashFrame { get; init; }
        public required Func<string, CancellationToken, Task<bool>> PressKey { get; init; }
        public required Func<CancellationToken, Task> WaitStable { get; init; }
        public required HandsProjectFiles Files { get; init; }
    }

    /// <summary>Итог прогона: метаданные + курсор продолжения (when <c>done:false</c>).</summary>
    internal sealed class ScanResult
    {
        public bool Done;
        public string? Path;
        public int Pages;
        public int TotalPages;
        public int TotalChars;
        public int DoubtfulStitches;
        public string StopReason = "";
        public string? Error;
        public string? Next;
        public string Existing = "";
    }

    /// <summary>Курсор продолжения (JSON в <c>continueFrom</c>).</summary>
    private sealed class ScanCursor
    {
        [JsonPropertyName("file")] public string? File { get; set; }
        [JsonPropertyName("pages")] public int Pages { get; set; }
        [JsonPropertyName("lastHash")] public string? LastHash { get; set; }
    }
}
