using System.Diagnostics;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Поиск исполняемого файла claude CLI. На Windows ищем claude.exe напрямую —
// cmd.exe /c не проксирует stdin корректно. Используется ClaudeSession,
// ModelCatalogService (опрос списка моделей) и TaskAiService.
public static class ClaudeCliLocator
{
    public static string FindClaudeExecutable()
    {
        if (!OperatingSystem.IsWindows()) return "claude";
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var exePath = Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        if (File.Exists(exePath)) return exePath;
        // Новый путь standalone-установки: %USERPROFILE%\.local\bin\claude.exe
        var localBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(localBin)) return localBin;
        try
        {
            using var where = Process.Start(new ProcessStartInfo("where.exe", "claude.exe")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
            if (where is not null)
            {
                var line = where.StandardOutput.ReadLine();
                where.WaitForExit(3000);
                if (!string.IsNullOrEmpty(line) && File.Exists(line)) return line.Trim();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ClaudeCliLocator] Поиск claude.exe через where.exe не удался: {ex.Message}");
        }
        return "claude.exe";
    }
}
