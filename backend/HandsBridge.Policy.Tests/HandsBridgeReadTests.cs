using System.Reflection;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// <c>ui_read</c> без OCR (мост без проекции Windows SDK, 2026-09-27): текст, который UI Automation
/// не отдаёт, — честный отказ с подсказкой снять окно, а не пустой успех. Проверяется по-настоящему
/// через сборку моста: разбор результата WinAPI не трогает и на Linux исполняется целиком.
/// </summary>
[Collection(HandsBridgeGateRuntimeTests.Collection)]
public class HandsBridgeReadTests
{
    private static readonly Type ResultType =
        HandsBridgeGateRuntimeTests.Bridge.Value.GetType("Sbroenne.WindowsMcp.Models.UIAutomationResult", throwOnError: true)!;

    private static object Read(string? text)
    {
        var success = ResultType.GetMethod("CreateSuccessWithText", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, ["get_text", text, null])!;
        var tool = HandsBridgeGateRuntimeTests.Bridge.Value.GetType("Sbroenne.WindowsMcp.Automation.Tools.UIReadTool", throwOnError: true)!;
        return tool.GetMethod("WhenNoText", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [success])!;
    }

    private static T? Prop<T>(object result, string name) => (T?)ResultType.GetProperty(name)!.GetValue(result);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Пустой_текст_UI_Automation_честный_отказ_с_подсказкой_снять_окно(string? text)
    {
        var result = Read(text);

        Assert.False(Prop<bool>(result, "Success"));
        Assert.Equal("no_text_found", Prop<string>(result, "ErrorType"));
        Assert.Contains("screenshot_control(target='window'", Prop<string>(result, "RecoverySuggestion"));
    }

    [Fact]
    public void Прочитанный_текст_отдаётся_как_есть()
    {
        var result = Read("Сохранить");

        Assert.True(Prop<bool>(result, "Success"));
        Assert.Equal("Сохранить", Prop<string>(result, "Text"));
    }
}
