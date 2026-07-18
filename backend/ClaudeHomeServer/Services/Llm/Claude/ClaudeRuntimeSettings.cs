using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Файл настроек для claude --settings: отключает ВСЕ хуки (disableAllHooks) в
// серверных сессиях. Причина — на Windows-хосте хуки плагинов (oh-my-claudecode:
// SessionStart/SessionEnd/UserPromptSubmit/PreToolUse) на каждый ход порождают
// дочерние git/cmd-процессы, каждый из которых открывает мелькающее окно консоли.
// Плагин при этом остаётся загружен (enabledPlugins не трогаем) — скиллы
// /oh-my-claudecode:* работают, а эффект keyword-detector воспроизводит
// OmcKeywordRouting на стороне сервера.
//
// Только для local-среды: в песочнице (Linux) окон нет, а путь к хостовому файлу
// внутри контейнера недоступен — там --settings не добавляем.
public static class ClaudeRuntimeSettings
{
    private static string? _cachedPath;
    private static readonly Lock _lock = new();

    // Аргументы --settings для запуска claude; пусто для песочницы.
    public static IEnumerable<string> HooksOffArgs(IProcessLauncher launcher) =>
        launcher.IsSandboxed ? [] : ["--settings", EnsureFile(launcher.HostTempDir)];

    // Ленивая запись файла настроек; путь кэшируется. Файл эфемерный (служебный
    // конфиг, не стор данных) — пересоздаётся при отсутствии.
    private static string EnsureFile(string hostTempDir)
    {
        lock (_lock)
        {
            if (_cachedPath is not null && File.Exists(_cachedPath)) return _cachedPath;
            var dir = Path.Combine(hostTempDir, "claude-runtime");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "hooks-off.settings.json");
            File.WriteAllText(path, "{\"disableAllHooks\":true}");
            _cachedPath = path;
            return path;
        }
    }
}
