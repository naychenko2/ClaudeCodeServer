using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// saveTo (D2d.1): <c>ScreenshotControlTool.SaveToProject</c>. До доработки annotate без
/// <c>includeImage</c> молча терял saveTo (<c>ImageData == null</c> → ранний выход без пути и без
/// ошибки). Теперь — явный отказ; с картинкой (<c>includeImage=true</c>) — сохраняет и возвращает
/// фактический путь. Мост — net10.0-windows, поэтому <c>SaveToProject</c> (private static) вызывается
/// рефлексией, как и в <see cref="HandsProjectFilesTests"/>.
/// </summary>
[Collection(HandsBridgeGateRuntimeTests.Collection)]
public class ScreenshotSaveToTests
{
    private static readonly byte[] FakeImage = [1, 2, 3, 4];

    private static Assembly Bridge => HandsBridgeGateRuntimeTests.Bridge.Value;

    /// <summary>Создаёт <c>ScreenshotControlResult</c> annotate-снимка: <c>includeImage</c> управляет ImageData.</summary>
    private static object Annotated(bool includeImage)
    {
        var resultType = Bridge.GetType("Sbroenne.WindowsMcp.Models.ScreenshotControlResult", throwOnError: true)!;
        var elementType = Bridge.GetType("Sbroenne.WindowsMcp.Models.AnnotatedElement", throwOnError: true)!;

        var element = RuntimeHelpers.GetUninitializedObject(elementType);
        Set(element, "Index", 1);
        Set(element, "Type", "Button");
        Set(element, "Id", "e1");
        Set(element, "Click", new[] { 10, 10, 0 });

        var elements = Array.CreateInstance(elementType, 1);
        elements.SetValue(element, 0);

        var imageData = includeImage ? Convert.ToBase64String(FakeImage) : null;
        var invoked = resultType.GetMethod("AnnotatedSuccess")!
            .Invoke(null, [imageData, 100, 100, "jpeg", elements, null, null, null]);
        return (object)invoked!;
    }

    private static void Set(object target, string name, object? value)
    {
        var setter = target.GetType().GetProperty(name)!.GetSetMethod(nonPublic: true)!;
        setter.Invoke(target, [value]);
    }

    /// <summary>Вызывает private static <c>SaveToProject(result, saveTo)</c>.</summary>
    private static (bool Success, string? Message, string? FilePath) SaveTo(object result, string saveTo)
    {
        var method = Bridge.GetType("Sbroenne.WindowsMcp.Tools.ScreenshotControlTool", throwOnError: true)!
            .GetMethod("SaveToProject", BindingFlags.NonPublic | BindingFlags.Static)!;
        var saved = (object)method.Invoke(null, [result, saveTo])!;
        return (
            (bool)GetProp(saved, "Success")!,
            (string?)GetProp(saved, "Message"),
            (string?)GetProp(saved, "FilePath"));
    }

    private static object? GetProp(object target, string name) =>
        target.GetType().GetProperty(name)!.GetValue(target);

    /// <summary>Ставит <c>HandsProjectFiles</c> с корнем в <c>HandsGate.s_projectFiles</c> (рефлексия).</summary>
    private static void SetProjectFiles(string? root)
    {
        var handsProjectFilesType = Bridge.GetType("ClaudeHomeServer.HandsBridge.HandsProjectFiles", throwOnError: true)!;
        var instance = Activator.CreateInstance(handsProjectFilesType, (object?)root)!;
        var handsGateType = Bridge.GetType("ClaudeHomeServer.HandsBridge.HandsGate", throwOnError: true)!;
        var field = handsGateType.GetField("s_projectFiles", BindingFlags.NonPublic | BindingFlags.Static)!;
        field.SetValue(null, instance);
    }

    /// <summary>
    /// annotate без includeImage: <c>ImageData == null</c> → явный отказ, а не молчаливая потеря saveTo.
    /// </summary>
    [Fact]
    public void Annotate_without_includeImage_refuses_saveTo()
    {
        var result = Annotated(includeImage: false);
        var (success, message, filePath) = SaveTo(result, "shot.png");

        Assert.False(success);
        Assert.Null(filePath);
        Assert.Contains("includeImage", message!);
    }

    /// <summary>
    /// annotate с includeImage: картинка в памяти → saveTo сохраняет файл и возвращает фактический путь.
    /// </summary>
    [Fact]
    public void Annotate_with_includeImage_saves_and_returns_the_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "hands-d2d1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SetProjectFiles(root);
            var result = Annotated(includeImage: true);
            var (success, _, filePath) = SaveTo(result, "shot.png");

            Assert.True(success);
            Assert.Equal("shot.png", filePath);
            Assert.True(File.Exists(Path.Combine(root, "shot.png")));
            Assert.Equal(FakeImage, File.ReadAllBytes(Path.Combine(root, "shot.png")));
        }
        finally
        {
            SetProjectFiles(null);
            Directory.Delete(root, recursive: true);
        }
    }
}
