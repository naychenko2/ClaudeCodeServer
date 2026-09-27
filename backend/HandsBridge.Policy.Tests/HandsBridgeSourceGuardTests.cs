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
        { HandsTools.WindowManagement, "Tools/WindowManagementTool.cs", "HandsGate.Policy.CheckWindowAction(" },
        { HandsTools.ScreenshotControl, "Tools/ScreenshotControlTool.cs", "HandsGate.Policy.CheckScreenshot(" },
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

    [Fact]
    public void List_find_and_foreground_filter_own_windows()
    {
        var source = Read("Tools/WindowManagementTool.cs");

        Assert.Equal(2, Regex.Matches(source, @"HandsGate\.Policy\.FilterOwn\(").Count);
        Assert.Contains("HandsGate.Policy.CheckForegroundResult(", source);
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
