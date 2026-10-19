using System.Text.Json;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Файл настроек для claude --settings: гасит хуки плагинов только на Windows-хосте
// (disableAllHooks). На остальных платформах (Linux/macOS) хуки нужны: PostToolUse
// срабатывает на каждый вызов и помогает локальной модели держать правила CLAUDE-local.md,
// которые к сороковому шагу забываются. Причина первоначального глушения — на Windows-хосте
// хуки плагинов (oh-my-claudecode: SessionStart/SessionEnd/UserPromptSubmit/PreToolUse)
// на каждый ход порождают дочерние git/cmd-процессы, каждый из которых открывает
// мелькающее окно консоли. Сервер переехал на Linux — причина отпала, привязку к
// платформе хоста делаем здесь. Остальные плагины при этом остаются загружены —
// скиллы /oh-my-claudecode:* работают, а эффект keyword-detector воспроизводит
// OmcKeywordRouting на стороне сервера.
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
// Третья обязанность файла — ретенция транскриптов: cleanupPeriodDays продлевает
// плановую уборку CLI (дефолт ~30 дней, вычищает {csid}.jsonl вместе с контекстом
// --resume). Ключ выпал из актуальной документации, но действует — проверено
// экспериментально 2026-08-24: транскрипт с внутренними timestamp −45 дней и mtime
// −40 дней удаляется при cleanupPeriodDays=1 и выживает при 9999, в том числе когда
// ключ приходит только из этого --settings-файла (чистка идёт на старте CLI, не чаще
// раза в сутки — метка .last-cleanup в корне профиля; срабатывает лишь при старых
// ОБОИХ признаках: и timestamp внутри jsonl, и mtime файла). Гигантское значение —
// осознанный выбор: серверные транскрипты чистит сам продукт точечно (DeleteEverywhere
// при удалении чата, копия при архивации — ArchivedTranscriptStore), беззаботная
// ретенция CLI срезала бы контент из-под архива. На чужие интерактивные сессии не
// влияет: файл подкладывается только нашим запускам.
//
// Четвёртая обязанность файла — выключить синк скиллов и плагинов аккаунта claude.ai
// (CLI ≥ 2.1.2xx тянет их в каждую сессию с подписочным логином: docx/pptx/canvas-design
// и др. входят в системный блок каждого хода и съедают контекст). Ключи syncClaudeAiSkills
// и syncClaudeAiPlugins стоят false всегда. Для container-владельцев, где --settings не
// передаётся (HooksOffArgs), те же ключи выключает LlmProviderRegistry.EnsureClaudeAiSyncOff
// в settings.json профиля CLI.
//
// Пятая обязанность файла — хук PreToolUse на Bash, режущий убийство процессов по имени
// (pkill/killall, xargs kill, kill $(...)). Боевой сервер и дев-стенд из worktree — один и
// тот же `dotnet ClaudeHomeServer.dll`, а у CLI-агентов в командной строке лежит весь
// системный промпт, поэтому любой шаблон задевает прод или чужие ходы (инциденты 28.09,
// 12.10, 16.10, 17.10: последний оборвал все ходы). Запрет в тексте задач не держится —
// держит только отказ на уровне инструмента. Скрипт пишется рядом с файлом настроек.
//
// Только для local-среды: в песочнице (Linux) окон нет, а путь к хостовому файлу
// внутри контейнера недоступен — там --settings не добавляем (и гейт браузера
// в песочнице не работает; браузера в образе всё равно нет).
public static class ClaudeRuntimeSettings
{
    // Плагин официального маркетплейса, дающий mcp__plugin_playwright_playwright__browser_*
    private const string BrowserPluginKey = "playwright@claude-plugins-official";

    // 10 лет: практическое «не чистить» без рискованной магии нуля (его семантика
    // не документирована и не проверялась)
    private const int TranscriptRetentionDays = 3650;

    private const string BlockKillScriptName = "block-process-kill.py";

    // Читает tool_input.command из stdin; код 2 + stderr — отказ, текст уходит модели.
    // Совпадение ищется только там, где слово стоит в позиции команды (начало, после
    // ; & | ( или перевода строки, sudo, xargs): grep/echo/commit-сообщение с этим словом
    // внутри аргументов и в теле heredoc проходят. kill <PID> разрешён намеренно.
    private const string BlockKillScript = """
        import json, re, sys

        try:
            cmd = (json.load(sys.stdin).get("tool_input") or {}).get("command") or ""
        except Exception:
            sys.exit(0)

        # Тексты, а не команды: тело heredoc и аргумент -m/--message (сообщение коммита)
        cmd = re.sub(r"<<-?\s*(['\"]?)(\w+)\1[^\n]*\n.*?\n\s*\2\b", "<<", cmd, flags=re.S)
        cmd = re.sub(r"(?:-m|--message|--body)\s*(?:\"(?:\\.|[^\"\\])*\"|'[^']*')", "-m X", cmd)

        pos = r"(?:^|[;&|(\n])\s*(?:sudo\s+)?"
        patterns = [
            pos + r"(?:pkill|killall)\b",
            r"\bxargs\s+(?:-\S+\s+)*(?:sudo\s+)?kill\b",
            pos + r"kill\b[^;&|\n]*(?:\$\(|`)",
        ]
        if any(re.search(p, cmd) for p in patterns):
            sys.stderr.write(
                "Заблокировано: убийство процессов по имени или шаблону (pkill, killall, "
                "xargs kill, kill $(...)) запрещено. Боевой сервер /opt/ccs/app/ClaudeHomeServer.dll "
                "и дев-стенд — один бинарь, шаблон заденет прод и оборвёт все ходы. "
                "Гаси только свой процесс по PID: запомни $! при запуске или найди через "
                "`ss -ltnp` по порту стенда, затем `kill <PID>`; стенд лучше поднимать и "
                "гасить инструментами start_stand/stop_stand.\n")
            sys.exit(2)
        """;

    // На Windows хуки плагинов порождают мелькающие окна консоли — гасим.
    // На остальных платформах хуки нужны как канал влияния на локальную модель.
    private static bool HostRequiresHooksOff => OperatingSystem.IsWindows();

    // Ключ кэша — (temp-каталог среды, режим, гасить ли хуки): temp у драйверов запуска
    // разный, одного флага мало — первый владелец «застолбил» бы путь в своей папке для
    // всех остальных; добавляем измерение платформы хоста, иначе при смене режима хуков
    // (Windows-процесс vs Linux-процесс с одним TempDir) первый запуск отдаст файл
    // неправильного состава.
    private static readonly Dictionary<(string TempDir, bool Browser, bool DisableHooks), string> _cachedPaths = [];
    private static readonly Lock _lock = new();

    // Аргументы --settings для запуска claude; пусто для песочницы.
    // browserEnabled: false — вдобавок к хукам гасит плагин браузера.
    // Путь файла входит в сигнатуру прогона, поэтому решение обязано быть
    // детерминировано по сессии (оно и есть — считается по персоне и платформе,
    // не по ходу), иначе живой процесс перезапускался бы между ходами.
    public static IEnumerable<string> HooksOffArgs(IProcessLauncher launcher, bool browserEnabled = true) =>
        launcher.IsSandboxed ? [] : ["--settings", EnsureFile(launcher.HostTempDir, browserEnabled)];

    // Ленивая запись файла настроек; путь кэшируется по режиму. Файл эфемерный
    // (служебный конфиг, не стор данных) — пересоздаётся при отсутствии.
    private static string EnsureFile(string hostTempDir, bool browserEnabled)
    {
        lock (_lock)
        {
            var disableHooks = HostRequiresHooksOff;
            var cacheKey = (hostTempDir, browserEnabled, disableHooks);
            if (_cachedPaths.TryGetValue(cacheKey, out var cached) && File.Exists(cached)) return cached;
            var dir = Path.Combine(hostTempDir, "claude-runtime");
            Directory.CreateDirectory(dir);
            // Имя повторяет состав файла: по нему (в cliArgs снимков промпта) читают, включены
            // ли хуки, — содержимое там не видно. И путь различается по тем же измерениям,
            // что ключ кэша, иначе два ключа делили бы один файл и перезаписывали друг друга.
            var path = Path.Combine(dir,
                $"hooks-{(disableHooks ? "off" : "on")}{(browserEnabled ? "" : "-no-browser")}.settings.json");
            // На хосте с погашенными хуками (Windows) ни хук, ни python3 не нужны
            var hooksJson = "";
            if (!disableHooks)
            {
                var scriptPath = Path.Combine(dir, BlockKillScriptName);
                File.WriteAllText(scriptPath, BlockKillScript);
                var command = JsonSerializer.Serialize($"python3 -I \"{scriptPath}\"");
                hooksJson = $",\"hooks\":{{\"PreToolUse\":[{{\"matcher\":\"Bash\",\"hooks\":[{{\"type\":\"command\",\"command\":{command}}}]}}]}}";
            }
            File.WriteAllText(path,
                $"{{\"disableAllHooks\":{(disableHooks ? "true" : "false")},\"enabledPlugins\":{{\"{BrowserPluginKey}\":{(browserEnabled ? "true" : "false")}}},\"cleanupPeriodDays\":{TranscriptRetentionDays},\"syncClaudeAiSkills\":false,\"syncClaudeAiPlugins\":false{hooksJson}}}");
            _cachedPaths[cacheKey] = path;
            return path;
        }
    }
}
