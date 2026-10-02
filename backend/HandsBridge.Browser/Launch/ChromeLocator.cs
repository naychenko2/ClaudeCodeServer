namespace ClaudeHomeServer.HandsBridge.Browser.Launch;

/// <summary>
/// Где на машине лежит <c>chrome.exe</c>. Чистая функция: реестр и окружение читает мост на
/// Windows, сюда приходят готовые значения — так порядок поиска проверяется тестами на Linux.
/// </summary>
public static class ChromeLocator
{
    /// <summary>Явный путь к Chrome; заданная переменная отменяет поиск целиком, даже если файла нет.</summary>
    public const string OverrideVariable = "CHROME_PATH";

    /// <summary>Отказ инструмента: остальные руки при этом работают.</summary>
    public const string NotFoundRefusal =
        "Google Chrome was not found on this machine: install Google Chrome to use the browser_* tools " +
        "(the other hands tools keep working).";

    /// <summary>Переменные окружения каталогов установки — в порядке поиска после App Paths.</summary>
    public static readonly IReadOnlyList<string> InstallRootVariables = ["ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA"];

    /// <summary>
    /// Кандидаты по порядку: значения App Paths (HKCU, затем HKLM), затем
    /// <c>Google\Chrome\Application\chrome.exe</c> под каждым каталогом установки.
    /// </summary>
    public static IReadOnlyList<string> Candidates(IEnumerable<string?> appPaths, Func<string, string?> environment)
    {
        var result = new List<string>();
        foreach (var value in appPaths)
        {
            // Значение App Paths бывает в кавычках
            var path = value?.Trim().Trim('"').Trim();
            if (!string.IsNullOrEmpty(path))
                Add(path);
        }

        foreach (var variable in InstallRootVariables)
        {
            var root = environment(variable);
            if (!string.IsNullOrWhiteSpace(root))
                Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
        }

        return result;

        void Add(string path)
        {
            if (!result.Contains(path, StringComparer.OrdinalIgnoreCase))
                result.Add(path);
        }
    }

    /// <summary>Первый существующий кандидат; null — Chrome не найден.</summary>
    public static string? Find(IEnumerable<string?> appPaths, Func<string, string?> environment, Func<string, bool> fileExists)
    {
        var explicitPath = environment(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var path = explicitPath.Trim().Trim('"');
            return fileExists(path) ? path : null;
        }

        return Candidates(appPaths, environment).FirstOrDefault(fileExists);
    }
}
