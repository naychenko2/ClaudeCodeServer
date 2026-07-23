namespace ClaudeHomeServer.Services.Execution;

/// <summary>
/// Поиск ConPtyBridge.exe — ConPTY-моста Windows-терминала (аналог /app/pty-bridge
/// для Linux). Мост лежит рядом с сервером: в dev его кладёт Copy-таргет
/// ClaudeHomeServer.csproj, на бою — publish-шаг deploy80.ps1.
/// null = ConPTY недоступен, терминал деградирует в голое перенаправление.
/// </summary>
public static class ConPtyBridgeLocator
{
    /// <summary>Минимальный билд Windows с ConPTY API — Win10 1809.</summary>
    internal const int MinConPtyBuild = 17763;

    public static string? Find()
        => !OperatingSystem.IsWindows()
            ? null
            : Find(AppContext.BaseDirectory, Environment.OSVersion.Version.Build);

    /// <summary>Тестируемая перегрузка: чистая функция от папки и билда ОС.</summary>
    internal static string? Find(string baseDir, int osBuild)
    {
        if (osBuild < MinConPtyBuild) return null;
        var path = Path.Combine(baseDir, "ConPtyBridge.exe");
        return File.Exists(path) ? path : null;
    }
}
