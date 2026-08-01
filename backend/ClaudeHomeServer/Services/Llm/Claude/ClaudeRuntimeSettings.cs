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
// Второй режим файла — «без браузера»: тем же enabledPlugins выключается плагин
// playwright (24 browser_*-инструмента). Плагин установлен пользователем в ~/.claude
// и оттуда разъехался по всем профилям CLI (LlmProviderRegistry.SyncUserProfile
// копирует каталог plugins целиком), так что браузер получал каждый чат каждого
// профиля. Кому он нужен по роли — решает Tool-ключ «browser» персоны
// (PersonaBindingsService, дефолт по пресету: тестировщику включён).
//
// Только для local-среды: в песочнице (Linux) окон нет, а путь к хостовому файлу
// внутри контейнера недоступен — там --settings не добавляем (и гейт браузера
// в песочнице не работает; браузера в образе всё равно нет).
public static class ClaudeRuntimeSettings
{
    // Плагин официального маркетплейса, дающий mcp__plugin_playwright_playwright__browser_*
    private const string BrowserPluginKey = "playwright@claude-plugins-official";

    private static readonly Dictionary<bool, string> _cachedPaths = [];
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
            if (_cachedPaths.TryGetValue(browserEnabled, out var cached) && File.Exists(cached)) return cached;
            var dir = Path.Combine(hostTempDir, "claude-runtime");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir,
                browserEnabled ? "hooks-off.settings.json" : "hooks-off-no-browser.settings.json");
            File.WriteAllText(path, browserEnabled
                ? "{\"disableAllHooks\":true}"
                : $"{{\"disableAllHooks\":true,\"enabledPlugins\":{{\"{BrowserPluginKey}\":false}}}}");
            _cachedPaths[browserEnabled] = path;
            return path;
        }
    }
}
