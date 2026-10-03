using System.Diagnostics;

namespace ClaudeHomeServer.Tests.Services.TestRuns;

/// <summary>
/// Ссылки, которые агент мог подложить в рабочее дерево. Симлинк на Windows требует режима
/// разработчика, поэтому каталог там — junction (`mklink /J` прав не требует). false — ссылку
/// создать нельзя: тест выходит без проверки, она живёт в CI на Linux.
/// </summary>
internal static class TreeLinks
{
    public static bool TryLink(string link, string target, bool directory)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(link, target);
            else File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return directory && OperatingSystem.IsWindows() && TryJunction(link, target);
        }
    }

    private static bool TryJunction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        mklink.WaitForExit();
        return mklink.ExitCode == 0 && new DirectoryInfo(link).LinkTarget is not null;
    }

    // Каталог вне дерева под жертву; удаляет вызывающий
    public static string Outside(string prefix)
    {
        var outside = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        return outside;
    }
}
