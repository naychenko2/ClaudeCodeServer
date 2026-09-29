using System.Text.RegularExpressions;
using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Сторожа по исходникам моста. Сам мост — net10.0-windows, на Linux CI его код не исполнить,
/// поэтому то, что гейт вообще вызывается, проверяется по тексту: каждый инструмент зовёт свою
/// проверку и возвращает отказ ДО первого <c>await</c>, а лишних инструментов в сборке нет.
/// </summary>
public class HandsBridgeSourceGuardTests
{
    private static readonly string BridgeDir = FindBridgeDir();

    /// <summary>Инструмент → файл и вызов гейта, который обязан стоять до первого действия.</summary>
    public static readonly TheoryData<string, string, string> Gates = new()
    {
        { HandsTools.App, "Tools/AppTool.cs", "HandsGate.Policy.CheckLaunch(" },
        { HandsTools.UiSnapshot, "Automation/Tools/UISnapshotTool.cs", "HandsGate.Policy.CheckUi(HandsTools.UiSnapshot," },
        { HandsTools.UiFind, "Automation/Tools/UIFindTool.cs", "HandsGate.Policy.CheckUi(HandsTools.UiFind," },
        { HandsTools.UiClick, "Automation/Tools/UIClickTool.cs", "HandsGate.Policy.CheckUi(HandsTools.UiClick," },
        { HandsTools.UiType, "Automation/Tools/UITypeTool.cs", "HandsGate.Policy.CheckUi(HandsTools.UiType," },
        { HandsTools.UiRead, "Automation/Tools/UIReadTool.cs", "HandsGate.Policy.CheckUi(HandsTools.UiRead," },
        { HandsTools.WindowManagement, "Tools/WindowManagementTool.cs", "HandsPolicy.CheckWindowAction(" },
        { HandsTools.ScreenshotControl, "Tools/ScreenshotControlTool.cs", "HandsPolicy.CheckScreenshot(" },
        // Браузерная рука (ADR-016 §7.1): инструменты появляются в Ш6, до него эти строки красные
        { HandsTools.BrowserNavigate, "Tools/BrowserNavigateTool.cs", "HandsPolicy.CheckBrowserUrl(" },
        { HandsTools.BrowserSnapshot, "Tools/BrowserSnapshotTool.cs", "HandsPolicy.CheckBrowserSnapshot(" },
        { HandsTools.BrowserClick, "Tools/BrowserClickTool.cs", "HandsPolicy.CheckBrowserRef(HandsTools.BrowserClick," },
        { HandsTools.BrowserType, "Tools/BrowserTypeTool.cs", "HandsPolicy.CheckBrowserRef(HandsTools.BrowserType," },
        { HandsTools.BrowserTabs, "Tools/BrowserTabsTool.cs", "HandsPolicy.CheckBrowserTabs(" },
        { HandsTools.BrowserWait, "Tools/BrowserWaitTool.cs", "HandsPolicy.CheckBrowserWait(" },
        { HandsTools.BrowserScreenshot, "Tools/BrowserScreenshotTool.cs", "HandsPolicy.CheckBrowserScreenshot(" },
        { HandsTools.BrowserQuery, "Tools/BrowserQueryTool.cs", "HandsPolicy.CheckBrowserQuery(" },
    };

    [Fact]
    public void Bridge_exposes_exactly_the_hands_tools()
    {
        var names = Directory.EnumerateFiles(BridgeDir, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\[McpServerTool\(Name = ""([^""]+)""").Select(m => m.Groups[1].Value))
            .Order()
            .ToList();

        Assert.Equal(HandsTools.All.Order(), names);
    }

    [Theory]
    [MemberData(nameof(Gates))]
    public void Tool_calls_its_gate_before_any_action(string tool, string file, string gateCall)
    {
        var body = ToolBody(file, tool);

        var gateAt = body.IndexOf(gateCall, StringComparison.Ordinal);
        var denyAt = body.IndexOf($"return HandsGate.Deny(HandsTools.", StringComparison.Ordinal);
        var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);

        Assert.True(gateAt >= 0, $"{tool}: нет вызова гейта «{gateCall}»");
        Assert.True(denyAt > gateAt, $"{tool}: отказ гейта не возвращается");
        Assert.True(firstAwait < 0 || denyAt < firstAwait, $"{tool}: действие раньше гейта");
    }

    /// <summary>
    /// Сигнал «ход действует руками» (<see cref="HandsActivity"/>): каждый инструмент, который может
    /// тронуть машину, сообщает о действии сразу после гейта — после возврата отказа, чтобы отказ
    /// сигнал не поднимал, и до первого действия. Чтение дерева окна о действии не сообщает.
    /// </summary>
    [Theory]
    [MemberData(nameof(Gates))]
    public void Tool_reports_its_action_after_the_gate(string tool, string file, string gateCall)
    {
        var body = ToolBody(file, tool);
        var actedAt = body.IndexOf("HandsGate.Acted(HandsTools.", StringComparison.Ordinal);

        if (tool is HandsTools.UiSnapshot or HandsTools.UiFind or HandsTools.UiRead)
        {
            Assert.True(actedAt < 0, $"{tool}: чтение не должно зажигать плашку");
            return;
        }

        var gateAt = body.IndexOf(gateCall, StringComparison.Ordinal);
        var denyAt = body.IndexOf("return HandsGate.Deny(HandsTools.", StringComparison.Ordinal);
        var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);

        Assert.True(actedAt >= 0, $"{tool}: нет HandsGate.Acted — первое действие не зажжёт плашку");
        Assert.Contains($"HandsGate.Acted(HandsTools.{ToolConstant(tool)}", body);
        Assert.True(gateAt >= 0 && actedAt > denyAt, $"{tool}: сигнал раньше отказа гейта");
        Assert.True(firstAwait < 0 || actedAt < firstAwait, $"{tool}: сигнал после действия");
    }

    [Fact]
    public void Window_and_screenshot_report_their_action_name()
    {
        var windows = Read("Tools/WindowManagementTool.cs");
        Assert.Contains("HandsPolicy.CheckWindowAction(windowAction);", windows);
        Assert.Contains("HandsGate.Acted(HandsTools.WindowManagement, windowAction);", windows);
        Assert.Contains("HandsGate.Acted(HandsTools.ScreenshotControl, action);", Read("Tools/ScreenshotControlTool.cs"));
    }

    [Fact]
    public void Bridge_passes_the_activity_event_to_the_gate() =>
        Assert.Contains("HandsGate.Configure(browserProfile, GetOption(args, HandsBridgeArgs.ActivityEvent));", Read("Program.cs"));

    private static string ToolConstant(string tool) =>
        typeof(HandsTools).GetFields().Single(f => f.IsLiteral && (string?)f.GetRawConstantValue() == tool).Name;

    /// <summary>
    /// Граница «только свои окна» снята (ADR-016 §7): список, поиск (в том числе по заголовку) и
    /// окно переднего плана отдают все окна, а не фильтруются, и поиск окна после запуска — тоже.
    /// </summary>
    [Fact]
    public void List_find_and_foreground_return_every_window()
    {
        var windows = Read("Tools/WindowManagementTool.cs");
        var app = Read("Tools/AppTool.cs");

        foreach (var source in new[] { windows, app })
        {
            Assert.DoesNotContain("FilterOwn", source);
            Assert.DoesNotContain("IsOwnWindow", source);
            Assert.DoesNotContain("CheckForegroundResult", source);
        }
        Assert.Contains("HandleWaitForAsync(title,", windows);
    }

    [Fact]
    public void App_launches_without_shell_into_the_job_and_never_by_title()
    {
        var source = Read("Tools/AppTool.cs");

        Assert.Contains("UseShellExecute = false", source);
        Assert.DoesNotContain("UseShellExecute = true", source);
        Assert.Contains("AssignToAppsJob(process)", source);
        Assert.Contains("FileName = programPath", source);
        Assert.Contains("HandleLaunchAsync(gate.ProgramPath,", source);
        Assert.DoesNotContain("Title?.Contains", source);
        Assert.DoesNotContain("w.ProcessName, process.ProcessName", source);
    }

    [Fact]
    public void Keyboard_checks_system_combinations_on_every_press()
    {
        var source = Read("Input/KeyboardInputService.cs");

        Assert.Contains("HandsPolicy.CheckKeys(", source);
        Assert.Contains("HandsPolicy.CheckKeyDown(", source);
    }

    [Fact]
    public void Screen_pixels_are_never_copied_for_a_window()
    {
        Assert.DoesNotContain("CopyFromScreen", Read("Automation/Tools/UIReadTool.cs"));
        Assert.DoesNotContain(
            "var region = new CaptureRegion(windowRect.Left, windowRect.Top, width, height);",
            Read("Capture/ScreenshotService.cs"));
    }

    [Fact]
    public void Removed_tool_classes_stay_removed()
    {
        string[] removed =
        [
            "Tools/KeyboardControlTool.cs", "Tools/MouseControlTool.cs", "Automation/Tools/UIBatchTool.cs",
            "Automation/Tools/UIFileTool.cs", "Automation/Tools/UIOpenFileTool.cs", "Automation/Tools/UIReadTableTool.cs",
            "Automation/Tools/UISelectTool.cs", "Automation/Tools/UIWaitTool.cs", "Clipboard/Tools", "Macros/Tools", "Processes/Tools",
        ];

        foreach (var path in removed)
        {
            var full = Path.Combine(BridgeDir, path);
            Assert.False(File.Exists(full) || Directory.Exists(full), path);
        }
    }

    /// <summary>Тело метода с атрибутом <c>[McpServerTool(Name = tool)]</c> — до следующего члена класса.</summary>
    private static string ToolBody(string file, string tool)
    {
        var source = Read(file);
        var start = source.IndexOf($"[McpServerTool(Name = \"{tool}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{file}: нет инструмента {tool}");

        var open = source.IndexOf('{', source.IndexOf("CancellationToken cancellationToken)", start, StringComparison.Ordinal));
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }

        throw new InvalidOperationException($"{file}: тело {tool} не закрыто");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(BridgeDir, relative));

    private static string FindBridgeDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "HandsBridge");
            if (File.Exists(Path.Combine(candidate, "HandsBridge.csproj")))
                return candidate;
            if (File.Exists(Path.Combine(dir.FullName, "HandsBridge", "HandsBridge.csproj")))
                return Path.Combine(dir.FullName, "HandsBridge");
        }

        throw new InvalidOperationException("Не найден каталог backend/HandsBridge выше " + AppContext.BaseDirectory);
    }
}
