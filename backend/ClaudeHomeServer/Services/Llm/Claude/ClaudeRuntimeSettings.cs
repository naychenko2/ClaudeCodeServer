using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Файл настроек для claude --settings: отключает ВСЕ хуки (disableAllHooks) в
// серверных сессиях. Причина — на Windows-хосте хуки плагинов (oh-my-claudecode:
// SessionStart/SessionEnd/UserPromptSubmit/PreToolUse) на каждый ход порождают
// дочерние git/cmd-процессы, каждый из которых открывает мелькающее окно консоли.
// Остальные плагины при этом остаются загружены — скиллы /oh-my-claudecode:*
// работают, а эффект keyword-detector воспроизводит OmcKeywordRouting на стороне
// сервера.
//
// Второй режим файла — гейт браузера: enabledPlugins явно включает или выключает
// плагин playwright (24 browser_*-инструмента) независимо от того, что лежит в
// settings.json профиля. Раньше при browserEnabled=true ключ не писался вовсе —
// плагин доставался тем случайно: LlmProviderRegistry.SyncUserProfile синкает
// settings.json профиля ЦЕЛИКОМ с хостового (обычно {}), затирая enabledPlugins,
// которое кто-то мог включить вручную. Явная запись true/false на каждый ход
// делает доступность браузера зависимой ТОЛЬКО от роли персоны, а не от состояния
// профильного файла. Кому он нужен по роли — решает Tool-ключ «browser» персоны
// (PersonaBindingsService, дефолт по пресету: тестировщику включён).
//
// Только для local-среды: в песочнице (Linux) окон нет, а путь к хостовому файлу
// внутри контейнера недоступен — там --settings не добавляем (и гейт браузера
// в песочнице не работает; браузера в образе всё равно нет).
public static class ClaudeRuntimeSettings
{
    // Плагин официального маркетплейса, дающий mcp__plugin_playwright_playwright__browser_*
    private const string BrowserPluginKey = "playwright@claude-plugins-official";

    // Ключ кэша — (temp-каталог среды, режим): temp у драйверов запуска разный, и одного
    // флага мало — первый владелец «застолбил» бы путь в своей папке для всех остальных
    private static readonly Dictionary<(string TempDir, bool Browser), string> _cachedPaths = [];
    private static readonly Lock _lock = new();

    // Аргументы --settings для запуска claude; пусто для песочницы.
    // browserEnabled: false — вдобавок к хукам гасит плагин браузера.
    // Путь файла входит в сигнатуру прогона, поэтому решение обязано быть
    // детерминировано по сессии (оно и есть — считается по персоне, не по ходу),
    // иначе живой процесс перезапускался бы между ходами.
    public static IEnumerable<string> HooksOffArgs(IProcessLauncher launcher, bool browserEnabled = true) =>
        launcher.IsSandboxed ? [] : ["--settings", EnsureFile(launcher.HostTempDir, browserEnabled)];

    // Ленивая запись файла настроек; путь кэшируется по режиму. Файл эфемерный
    // (служебный конфиг, не стор данных) — пересоздаётся при отсутствии.
    private static string EnsureFile(string hostTempDir, bool browserEnabled)
    {
        lock (_lock)
        {
            var cacheKey = (hostTempDir, browserEnabled);
            if (_cachedPaths.TryGetValue(cacheKey, out var cached) && File.Exists(cached)) return cached;
            var dir = Path.Combine(hostTempDir, "claude-runtime");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir,
                browserEnabled ? "hooks-off.settings.json" : "hooks-off-no-browser.settings.json");
            File.WriteAllText(path,
                $"{{\"disableAllHooks\":true,\"enabledPlugins\":{{\"{BrowserPluginKey}\":{(browserEnabled ? "true" : "false")}}}}}");
            _cachedPaths[cacheKey] = path;
            return path;
        }
    }
}
