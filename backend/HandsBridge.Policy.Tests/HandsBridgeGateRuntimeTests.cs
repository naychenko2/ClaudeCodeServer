using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Рантайм-сторож гейта: инструменты моста вызываются по-настоящему. Мост — net10.0-windows
/// (WinForms), ссылкой в тесты под net10.0 его не подключить, поэтому сборка грузится
/// рефлексией в свой контекст из <c>hands-bridge/</c> вывода тестов. Отказ гейта отдаётся до
/// первого обращения к WinAPI, так что на Linux путь отказа исполняется целиком. Гейт без
/// <c>Configure</c> не знает ни одного процесса, поэтому ввод получает отказ. Если отказ не
/// вернуть (убрать <c>return</c>), выполнение пойдёт дальше к действию, и ответ будет уже не
/// отказом гейта. Обратное тоже проверяется: снимок экрана и чтение чужого окна проходят гейт —
/// на Windows ответ не <c>hands_policy</c>, на Linux исполнение доходит до сервисов моста и падает
/// на инициализации WinAPI/COM, куда отказ гейта не пускает.
/// </summary>
[Collection(Collection)]
public class HandsBridgeGateRuntimeTests
{
    /// <summary>Гейт моста — статика процесса: тесты, настраивающие его, не идут параллельно.</summary>
    public const string Collection = "hands-bridge-runtime";

    internal static readonly Lazy<Assembly> Bridge = new(LoadBridge);

    [Fact]
    public async Task App_denied_by_gate_does_not_launch()
    {
        var result = await Call("Sbroenne.WindowsMcp.Tools.AppTool", new()
        {
            ["programPath"] = @"C:\Windows\System32\cmd.exe",
        });

        AssertGateDenied(result, "app", "never started");
    }

    [Fact]
    public async Task Ui_click_into_window_of_unknown_program_is_denied_by_gate()
    {
        var result = await Call("Sbroenne.WindowsMcp.Automation.Tools.UIClickTool", new()
        {
            ["windowHandle"] = "65552",
            ["name"] = "OK",
        });

        AssertGateDenied(result, "ui_click", "never click or type");
    }

    [Theory]
    [InlineData("primary_screen")]
    [InlineData("all_monitors")]
    [InlineData("region")]
    public async Task Screen_capture_passes_the_gate(string target)
    {
        await AssertPassesGate("Sbroenne.WindowsMcp.Tools.ScreenshotControlTool", new()
        {
            ["target"] = target,
            ["regionX"] = 0,
            ["regionY"] = 0,
            ["regionWidth"] = 10,
            ["regionHeight"] = 10,
        });
    }

    [Fact]
    public async Task Foreign_window_capture_and_read_pass_the_gate()
    {
        await AssertPassesGate("Sbroenne.WindowsMcp.Tools.ScreenshotControlTool", new()
        {
            ["target"] = "window",
            ["windowHandle"] = "65552",
        });
        await AssertPassesGate("Sbroenne.WindowsMcp.Automation.Tools.UIReadTool", new()
        {
            ["windowHandle"] = "65552",
        });
    }

    [Fact]
    public async Task Annotated_screenshot_with_output_path_is_denied_by_gate()
    {
        var result = await Call("Sbroenne.WindowsMcp.Tools.ScreenshotControlTool", new()
        {
            ["target"] = "window",
            ["windowHandle"] = "65552",
            ["annotate"] = true,
            ["outputMode"] = "inline",
            ["outputPath"] = Path.Combine(Path.GetTempPath(), "hands-gate-" + Guid.NewGuid().ToString("N"), "shot.jpg"),
        });

        AssertGateDenied(result, "screenshot_control", "outputPath");
    }

    private static void AssertGateDenied((bool IsError, string Text) result, string tool, string reason)
    {
        Assert.True(result.IsError, result.Text);

        using var json = JsonDocument.Parse(result.Text);
        Assert.Equal("hands_policy", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(tool, json.RootElement.GetProperty("tool").GetString());
        Assert.Contains(reason, json.RootElement.GetProperty("message").GetString());
    }

    private static async Task AssertPassesGate(string typeName, Dictionary<string, object?> args)
    {
        try
        {
            var result = await Call(typeName, args);
            Assert.DoesNotContain("hands_policy", result.Text);
        }
        catch (TypeInitializationException ex) when (!OperatingSystem.IsWindows() && ex.TypeName == "Sbroenne.WindowsMcp.Tools.WindowsToolsBase")
        {
            // Linux: гейт пропустил, дальше нет WinAPI — это и есть «дошло до действия»
        }
    }

    /// <summary>Вызов метода инструмента: неуказанные параметры — умолчания из схемы.</summary>
    internal static async Task<(bool IsError, string Text)> Call(string typeName, Dictionary<string, object?> args)
    {
        var type = Bridge.Value.GetType(typeName, throwOnError: true)!;
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.GetCustomAttributes().Any(a => a.GetType().Name == "McpServerToolAttribute"));

        var values = method.GetParameters()
            .Select(p => p.ParameterType == typeof(CancellationToken) ? CancellationToken.None
                : args.TryGetValue(p.Name!, out var value) ? value
                : p.GetCustomAttribute<System.ComponentModel.DefaultValueAttribute>()?.Value)
            .ToArray();
        Assert.All(args.Keys, name => Assert.Contains(method.GetParameters(), p => p.Name == name));

        var task = (Task)method.Invoke(null, values)!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;

        var isError = (bool?)result.GetType().GetProperty("IsError")!.GetValue(result) ?? false;
        var content = (System.Collections.IEnumerable)result.GetType().GetProperty("Content")!.GetValue(result)!;
        var first = content.Cast<object>().First();
        var text = (string)first.GetType().GetProperty("Text")!.GetValue(first)!;
        return (isError, text);
    }

    private static Assembly LoadBridge()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "hands-bridge");
        return new BridgeLoadContext(dir).LoadFromAssemblyPath(Path.Combine(dir, "HandsBridge.dll"));
    }

    /// <summary>Зависимости моста — из его же каталога, рантайм — общий.</summary>
    private sealed class BridgeLoadContext(string dir) : AssemblyLoadContext("hands-bridge")
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var path = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
