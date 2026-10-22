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
// Шестая обязанность файла — хук PreToolUse на Edit|Write: при ПЕРВОЙ правке файла в сессии CLI
// спрашивает граф кода (GET .../code-graph/neighbors?file=…&direction=in) и кладёт в ход
// additionalContext со списком потребителей и покрывающих тестов (≤ 20 строк, дальше «ещё N»).
// Только контекст: правку хук не блокирует никогда — нет переменных окружения, нет графа,
// файл вне графа, ответ дольше 3 с, ошибка авторизации — молчание и код 0. Адрес, проект и
// токен приезжают в окружении процесса CLI (CCS_HOOK_API / CCS_HOOK_PROJECT / CCS_HOOK_TOKEN,
// ставит ClaudeSession; токен — тот же сервисный JWT, что в заголовках MCP-конфига хода).
// Признак «файл уже правили» — файл <префикс проекта>_<session_id> в каталоге hook-state рядом со скриптом (0700, файлы 0600); пометка — только когда контекст реально отдан (200 с непустым списком; файл вне графа не помечается).
// В песочнице и на Windows-хосте хука нет по построению (см. ниже) — для них отдельный шаг.
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

    internal const string ConsumersHookScriptName = "edit-consumers-hook.py";

    // Единые имена каталогов: хук пишет состояние, Program.cs его убирает — оба через эти константы.
    internal const string RuntimeDirName = "claude-runtime";
    internal const string HookStateDirName = "hook-state";

    private static readonly string ConsumersHookScript = ConsumersHookScriptTemplate.Replace("__HOOK_STATE__", HookStateDirName);

    // Читает session_id, cwd и tool_input.file_path из stdin. Весь скрипт обёрнут так, чтобы любой
    // сбой заканчивался молчанием и кодом 0: хук не должен ни блокировать правку, ни ронять ход.
    // Общий потолок — 3 с на всё (setitimer), плюс таймаут сокета; прокси отключены явно.
    private const string ConsumersHookScriptTemplate = """
        import hashlib, json, os, re, signal, sys, time
        import urllib.error, urllib.parse, urllib.request

        LIMIT = 20
        TIMEOUT = 3
        STATE_TTL = 86400
        COOLDOWN = 30
        DOWN = {"path": None}
        BUILDING_NOTE = "Граф этого дерева ещё строится; ниже — по основному дереву проекта (ветка может отличаться)."


        def quiet(*_):
            touch_down()
            os._exit(0)


        def is_test(path):
            parts = path.split("/")
            if any(c.lower() in ("__tests__", "tests", "test") or c.endswith((".Tests", ".Test")) for c in parts[:-1]):
                return True
            return re.search(r"(Tests?\.cs|\.(test|spec)\.[jt]sx?)$", parts[-1]) is not None


        def state_dir():
            path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "__HOOK_STATE__")
            os.makedirs(path, mode=0o700, exist_ok=True)
            try:
                os.chmod(path, 0o700)
            except OSError:
                pass
            return path


        def cleanup(path):
            now = time.time()
            for name in os.listdir(path):
                full = os.path.join(path, name)
                try:
                    if now - os.path.getmtime(full) > STATE_TTL:
                        os.remove(full)
                except OSError:
                    pass


        def already_seen(path, rel):
            if not os.path.exists(path):
                return False
            with open(path, encoding="utf-8") as f:
                return rel in {line.rstrip("\n") for line in f}


        def mark_seen(path, rel):
            fd = os.open(path, os.O_WRONLY | os.O_APPEND | os.O_CREAT, 0o600)
            with os.fdopen(fd, "a", encoding="utf-8") as f:
                f.write(rel + "\n")


        def cooling_down(path):
            try:
                return time.time() - os.path.getmtime(path) < COOLDOWN
            except OSError:
                return False


        def touch_down():
            if not DOWN["path"]:
                return
            try:
                fd = os.open(DOWN["path"], os.O_WRONLY | os.O_CREAT, 0o600)
                os.close(fd)
                os.utime(DOWN["path"])
            except OSError:
                pass


        def fetch(api, project, token, rel, root_path):
            query = {"file": rel, "direction": "in", "limit": "100"}
            if root_path:
                query["rootPath"] = root_path
            url = "%s/api/projects/%s/code-graph/neighbors?%s" % (
                api.rstrip("/"), urllib.parse.quote(project, safe=""), urllib.parse.urlencode(query))
            request = urllib.request.Request(url, headers={"Authorization": "Bearer " + token})
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            try:
                with opener.open(request, timeout=TIMEOUT) as response:
                    return response.status, json.load(response)
            except urllib.error.HTTPError as e:
                try:
                    return e.code, json.load(e)
                except Exception:
                    return e.code, None


        def render(rel, result, note):
            neighbors = result.get("neighbors") or []
            total = int(result.get("total") or len(neighbors))
            if total <= 0:
                return None
            tests = [n for n in neighbors if is_test(n.get("location") or n.get("fqn") or "")]
            others = [n for n in neighbors if not is_test(n.get("location") or n.get("fqn") or "")]
            ordered = tests[:4] + others + tests[4:]
            lines = [note] if note else []
            lines.append("Потребители %s по графу кода (файлов: %d, тестов среди них: %d):" % (rel, total, len(tests)))
            room = LIMIT - len(lines)
            if total <= room:
                show = ordered[:room]
            else:
                show = ordered[:room - 1]
            for n in show:
                path = n.get("location") or n.get("fqn") or "?"
                mark = " [тест]" if is_test(path) else ""
                lines.append("- %s%s (%s)" % (path, mark, n.get("relation") or ""))
            rest = total - len(show)
            if rest > 0:
                lines.append("ещё %d" % rest)
            return "\n".join(lines[:LIMIT])


        def main():
            api = os.environ.get("CCS_HOOK_API")
            token = os.environ.get("CCS_HOOK_TOKEN")
            project = os.environ.get("CCS_HOOK_PROJECT")
            if not (api and token and project):
                return
            data = json.load(sys.stdin.buffer)
            session_id = data.get("session_id") or ""
            if not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", session_id):
                return
            file_path = (data.get("tool_input") or {}).get("file_path")
            if not file_path:
                return
            cwd = data.get("cwd") or os.getcwd()
            if not os.path.isabs(file_path):
                file_path = os.path.join(cwd, file_path)
            rel = os.path.relpath(file_path, cwd).replace(os.sep, "/")
            if rel.startswith(".."):
                return
            state = state_dir()
            cleanup(state)
            prefix = hashlib.sha1(project.encode("utf-8")).hexdigest()[:12]
            seen_path = os.path.join(state, prefix + "_" + session_id)
            DOWN["path"] = os.path.join(state, prefix + ".down")
            if already_seen(seen_path, rel) or cooling_down(DOWN["path"]):
                return
            try:
                status, body = fetch(api, project, token, rel, cwd)
                note = None
                if status == 404 and isinstance(body, dict) and body.get("building"):
                    status, body = fetch(api, project, token, rel, None)
                    note = BUILDING_NOTE
            except Exception:
                touch_down()
                return
            if status != 200 or not isinstance(body, dict):
                if status in (401, 403) or status >= 500:
                    touch_down()
                return
            text = render(rel, body, note)
            if text:
                sys.stdout.write(json.dumps({"hookSpecificOutput": {
                    "hookEventName": "PreToolUse", "additionalContext": text}}))
                sys.stdout.flush()
                mark_seen(seen_path, rel)


        try:
            signal.signal(signal.SIGALRM, quiet)
            signal.setitimer(signal.ITIMER_REAL, TIMEOUT)
            main()
        except BaseException:
            pass
        os._exit(0)
        """;

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

    // Хук потребителей реально запускается: не песочница (--settings там не передаётся) и хост не гасит хуки.
    public static bool ConsumersHookRuns(IProcessLauncher launcher) => !launcher.IsSandboxed && !HostRequiresHooksOff;

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
            var path = WriteSettings(hostTempDir, browserEnabled, disableHooks, BlockKillScript, ConsumersHookScript);
            _cachedPaths[cacheKey] = path;
            return path;
        }
    }

    // Запись набора файлов настроек в каталог, имя которого — отпечаток состава хуков (тексты скриптов,
    // режим, пользователь ОС). Инстансы с разной сборкой на одной машине делят temp, и общее имя по
    // (disableHooks, browserEnabled) давало им перезаписывать файл друг друга: у работающего CLI хук
    // Edit|Write или Bash (гейт безопасности) молча исчезал. Теперь разный состав — разный каталог, одинаковый —
    // тот же файл с тем же содержимым. consumersScript = null — сборка без хука потребителей.
    internal static string WriteSettings(string hostTempDir, bool browserEnabled, bool disableHooks,
        string blockKillScript, string? consumersScript)
    {
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('\u0001',
                disableHooks, Environment.UserName, blockKillScript, consumersScript ?? "-"))))[..12].ToLowerInvariant();
        var dir = Path.Combine(hostTempDir, RuntimeDirName, fingerprint);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Имя повторяет состав файла: по нему (в cliArgs снимков промпта) читают, включены
        // ли хуки, — содержимое там не видно.
        var path = Path.Combine(dir,
            $"hooks-{(disableHooks ? "off" : "on")}{(browserEnabled ? "" : "-no-browser")}.settings.json");
        // На хосте с погашенными хуками (Windows) ни хук, ни python3 не нужны
        var hooks = new List<string>();
        if (!disableHooks)
        {
            var scriptPath = Path.Combine(dir, BlockKillScriptName);
            File.WriteAllText(scriptPath, blockKillScript);
            var command = JsonSerializer.Serialize($"python3 -I \"{scriptPath}\"");
            hooks.Add($"{{\"matcher\":\"Bash\",\"hooks\":[{{\"type\":\"command\",\"command\":{command}}}]}}");
            if (consumersScript is not null)
            {
                var consumersPath = Path.Combine(dir, ConsumersHookScriptName);
                File.WriteAllText(consumersPath, consumersScript);
                var consumersCommand = JsonSerializer.Serialize($"python3 -I \"{consumersPath}\"");
                hooks.Add($"{{\"matcher\":\"Edit|Write\",\"hooks\":[{{\"type\":\"command\",\"command\":{consumersCommand}}}]}}");
            }
        }
        var hooksJson = hooks.Count == 0 ? "" : $",\"hooks\":{{\"PreToolUse\":[{string.Join(',', hooks)}]}}";
        File.WriteAllText(path,
            $"{{\"disableAllHooks\":{(disableHooks ? "true" : "false")},\"enabledPlugins\":{{\"{BrowserPluginKey}\":{(browserEnabled ? "true" : "false")}}},\"cleanupPeriodDays\":{TranscriptRetentionDays},\"syncClaudeAiSkills\":false,\"syncClaudeAiPlugins\":false{hooksJson}}}");
        return path;
    }

    // Уборка состояния хука потребителей (признаки «файл правили» и паузы после сбоев): TTL сутки.
    // Каталог состояния лежит в каталоге отпечатка — обходим все плюс прежний общий hook-state.
    // Сами каталоги отпечатков (скрипты и settings.json прежних сборок) убираем, когда в них
    // ничего не менялось дольше TTL; каталоги, чьи файлы настроек отданы в этом процессе
    // (_cachedPaths), не трогаем никогда.
    public static void CleanupHookState(string hostTempDir, TimeSpan ttl)
    {
        var runtime = Path.Combine(hostTempDir, RuntimeDirName);
        if (!Directory.Exists(runtime)) return;
        var threshold = DateTime.UtcNow - ttl;
        var states = Directory.EnumerateDirectories(runtime).Select(d => Path.Combine(d, HookStateDirName))
            .Append(Path.Combine(runtime, HookStateDirName));
        foreach (var state in states.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(state))
                try { if (File.GetLastWriteTimeUtc(f) < threshold) File.Delete(f); } catch { }

        HashSet<string> live;
        lock (_lock)
            live = _cachedPaths.Values.Select(p => Path.GetFullPath(Path.GetDirectoryName(p)!)).ToHashSet();
        foreach (var dir in Directory.EnumerateDirectories(runtime))
        {
            if (live.Contains(Path.GetFullPath(dir))) continue;
            try
            {
                var newest = Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
                    .Append(dir).Max(e => File.GetLastWriteTimeUtc(e));
                if (newest < threshold) Directory.Delete(dir, true);
            }
            catch { }
        }
    }
}
