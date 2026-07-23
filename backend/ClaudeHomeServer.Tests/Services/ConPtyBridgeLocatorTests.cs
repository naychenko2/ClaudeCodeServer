using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Тесты резолва ConPTY-моста: чистая функция Find(baseDir, osBuild) —
/// решение «мост vs фолбэк» проверяется без реальной ОС и процессов.
/// </summary>
public class ConPtyBridgeLocatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("conpty-locator-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PutExe()
    {
        var path = Path.Combine(_dir, "ConPtyBridge.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A]); // пустышка: локатору важен факт наличия
        return path;
    }

    [Fact]
    public void Возвращает_путь_когда_exe_есть_и_билд_новый()
    {
        var exe = PutExe();
        ConPtyBridgeLocator.Find(_dir, 22631).Should().Be(exe);
    }

    [Fact]
    public void Null_когда_билд_старше_1809()
    {
        PutExe();
        ConPtyBridgeLocator.Find(_dir, 17000).Should().BeNull();
    }

    [Fact]
    public void Null_когда_exe_отсутствует()
    {
        ConPtyBridgeLocator.Find(_dir, 22631).Should().BeNull();
    }

    [Fact]
    public void Граница_минимального_билда_включительна()
    {
        PutExe();
        ConPtyBridgeLocator.Find(_dir, ConPtyBridgeLocator.MinConPtyBuild).Should().NotBeNull();
        ConPtyBridgeLocator.Find(_dir, ConPtyBridgeLocator.MinConPtyBuild - 1).Should().BeNull();
    }
}
