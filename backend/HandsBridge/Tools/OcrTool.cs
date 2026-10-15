using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.HandsBridge;
using ClaudeHomeServer.HandsBridge.Policy;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sbroenne.WindowsMcp.Capture;
using Sbroenne.WindowsMcp.Models;

namespace Sbroenne.WindowsMcp.Tools;

/// <summary>
/// OCR of a page (step A4 "hands without UIA"): a screenshot of a window, region, or screen
/// goes to the server (<c>--ocr-endpoint</c>), and the server returns the recognized text.
/// For windows without a UIA tree (Citrix, Vidi, Visio): the model reads the screen, not the tree.
/// The reply is text (capped at 4000 characters), model, ms, and token counts; <c>saveTo</c>
/// writes the full text to a <c>.txt</c>/<c>.md</c> file in the project. The window must not be
/// a forbidden app (terminal, interpreter, Explorer) — the gate checks it before any capture.
/// </summary>
public static partial class OcrTool
{
    /// <summary>
    /// Reply text cap: the rest of the text is only in the file (when <c>saveTo</c> is set).
    /// Longer text without <c>saveTo</c> is truncated with a note.
    /// </summary>
    private const int MaxResponseText = 4000;

    /// <summary>Truncation note: the text is longer than the cap and <c>saveTo</c> is not set.</summary>
    private const string TruncatedSuffix =
        "\n\n[truncated at 4000 characters; set saveTo to get the full text in a file]";

    /// <summary>
    /// Снимки для OCR: в бою — <c>WindowsToolsBase.ScreenshotService</c> (тот же снимок, что
    /// <c>screenshot_control</c>: PNG в памяти, на диск не пишется); в тестах — фейк.
    /// </summary>
    internal static ScreenshotService? CaptureService { get; set; }

    private static ScreenshotService Capture => CaptureService ?? WindowsToolsBase.ScreenshotService;

    /// <summary>
    /// Обрезка ответа: потолок 4000 символов. Текст длиннее и полный не записан в файл
    /// (saveTo не задан) — пометка «обрезано, задайте saveTo».
    /// </summary>
    internal static (string Text, bool Truncated) TruncateForReply(string text, bool fullTextSaved)
    {
        if (text.Length <= MaxResponseText)
            return (text, false);

        var truncated = text[..MaxResponseText];
        if (!fullTextSaved)
            truncated += TruncatedSuffix;

        return (truncated, true);
    }

    /// <summary>
    /// Recognizes the text on a screen, monitor, window, or region: the screenshot goes to the
    /// server's OCR endpoint and the reply is the recognized text (Markdown), the model, the
    /// duration in ms, and the token counts. Use it for windows without a UIA tree (Citrix,
    /// Vidi, Visio): the model reads the screen, not the tree.
    /// <para>
    /// The window must not be a forbidden app (terminal, interpreter, Explorer): reading its
    /// screen is reading its commands. The reply text is capped at 4000 characters; set
    /// <c>saveTo</c> to write the full text to a <c>.txt</c> or <c>.md</c> file in the project.
    /// </para>
    /// </summary>
    /// <param name="target">What to read. Valid values: 'primary_screen' (main display, default), 'secondary_screen' (other monitor, only for 2-monitor setups), 'monitor' (by index for 3+ monitors), 'window' (by handle), 'region' (by coordinates), 'all_monitors' (composite of all displays). Default: 'primary_screen'.</param>
    /// <param name="monitorIndex">Monitor index for 'monitor' target (0-based).</param>
    /// <param name="windowHandle">Window handle (HWND) as a decimal string for 'window' target. Get it from window_management and pass it through verbatim.</param>
    /// <param name="regionX">X coordinate (left) for 'region' target. Can be negative for multi-monitor setups.</param>
    /// <param name="regionY">Y coordinate (top) for 'region' target. Can be negative for multi-monitor setups.</param>
    /// <param name="regionWidth">Width in pixels for 'region' target. Must be positive.</param>
    /// <param name="regionHeight">Height in pixels for 'region' target. Must be positive.</param>
    /// <param name="saveTo">
    /// Save the full recognized text into the project: a path relative to the project root,
    /// ending in .txt or .md. When set, the full text is written to the project (a free name is
    /// chosen if the path is taken) and the reply carries the actual path. Requires the bridge to
    /// be started with --project-root; without it the save is refused.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A call result with the recognized text (capped at 4000 characters), the model, the duration in ms, the token counts, and the saved path when saveTo is set.</returns>
    [McpServerTool(Name = "ocr", Title = "OCR Text Recognition", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    public static async partial Task<CallToolResult> ExecuteAsync(
        [DefaultValue(null)] string? target,
        [DefaultValue(null)] int? monitorIndex,
        [DefaultValue(null)] string? windowHandle,
        [DefaultValue(null)] int? regionX,
        [DefaultValue(null)] int? regionY,
        [DefaultValue(null)] int? regionWidth,
        [DefaultValue(null)] int? regionHeight,
        [DefaultValue(null)] string? saveTo,
        CancellationToken cancellationToken)
    {
        // Gate before the first action: the target is the same as screenshot_control, and the
        // window must not be a forbidden app (reading a terminal is reading its commands).
        var gate = HandsGate.Policy.CheckOcr(target, windowHandle, regionWidth, regionHeight);
        if (gate is { Allowed: false })
            return HandsGate.Deny(HandsTools.Ocr, gate);

        // No --ocr-endpoint: the agent is old and does not pass the OCR endpoint.
        if (OcrClient.Endpoint is null)
            return Fail("ocr unavailable: update the agent (it does not pass --ocr-endpoint to the bridge)");

        var parsedTarget = ScreenshotControlTool.ParseTarget(target);
        if (parsedTarget is null)
            return Fail(
                $"Unknown target '{target}'. Valid values: 'primary_screen', 'secondary_screen', 'monitor', 'window', 'region', 'all_monitors'.");

        var request = new ScreenshotControlRequest
        {
            Action = ScreenshotAction.Capture,
            Target = parsedTarget.Value,
            MonitorIndex = monitorIndex,
            WindowHandle = windowHandle,
            Region = parsedTarget.Value == CaptureTarget.Region
                ? new CaptureRegion(regionX ?? 0, regionY ?? 0, regionWidth ?? 0, regionHeight ?? 0)
                : null,
            ImageFormat = ImageFormat.Png,
            OutputMode = OutputMode.Inline,
        };

        // Screenshot to memory (PNG, no disk): the same service as screenshot_control.
        var capture = await Capture.ExecuteAsync(request, cancellationToken);
        if (!capture.Success || capture.ImageData is null)
            return Fail($"OCR capture failed: {capture.Message}");

        var png = Convert.FromBase64String(capture.ImageData);

        // Screenshot → server → text.
        var ocr = await OcrClient.RecognizeAsync(png, cancellationToken);
        if (!ocr.Ok)
            return Fail(ocr.Message!);

        var text = ocr.Text!;

        // saveTo: the full text goes to a file in the project (only .txt/.md, via HandsProjectFiles).
        string? savedPath = null;
        if (!string.IsNullOrWhiteSpace(saveTo))
        {
            var (path, reason) = HandsGate.ProjectFiles.WriteNew(
                HandsTools.Ocr, saveTo, Encoding.UTF8.GetBytes(text));
            if (path is null)
                return Fail($"OCR succeeded, but the text could not be saved: {reason}");
            savedPath = path;
        }

        // Reply cap: 4000 characters; the rest is only in the file (when saveTo is set).
        var (replyText, truncated) = TruncateForReply(text, savedPath is not null);

        return new CallToolResult
        {
            Content = [new TextContentBlock
            {
                Text = JsonSerializer.Serialize(new
                {
                    success = true,
                    text = replyText,
                    truncated,
                    model = ocr.Model,
                    ms = ocr.Ms,
                    promptTokens = ocr.PromptTokens,
                    completionTokens = ocr.CompletionTokens,
                    savedPath,
                }, JsonOptions),
            }],
            IsError = false,
        };
    }

    /// <summary>Ответ-отказ в форме MCP-результата (тот же формат, что <c>WindowsToolsBase.Fail</c>).</summary>
    private static CallToolResult Fail(string message) => new()
    {
        Content = [new TextContentBlock
        {
            Text = JsonSerializer.Serialize(new { success = false, error = message, isError = true }, JsonOptions),
        }],
        IsError = true,
    };

    /// <summary>Null-поля не сериализуются (как в <c>WindowsToolsBase.JsonOptions</c>).</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
