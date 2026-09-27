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
/// <c>Configure</c> закрыт (своих окон нет), значит каждый вызов ниже обязан получить отказ.
/// Если отказ не вернуть (убрать <c>return</c>), выполнение пойдёт дальше к действию, и ответ
/// будет уже не отказом гейта.
/// </summary>
public class HandsBridgeGateRuntimeTests
{
    private static readonly Lazy<Assembly> Bridge = new(LoadBridge);

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
    public async Task Ui_click_on_foreign_window_is_denied_by_gate()
    {
        var result = await Call("Sbroenne.WindowsMcp.Automation.Tools.UIClickTool", new()
        {
            ["windowHandle"] = "65552",
            ["name"] = "OK",
        });

        AssertGateDenied(result, "ui_click", "is not yours");
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

    /// <summary>Вызов метода инструмента: неуказанные параметры — умолчания из схемы.</summary>
    private static async Task<(bool IsError, string Text)> Call(string typeName, Dictionary<string, object?> args)
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
