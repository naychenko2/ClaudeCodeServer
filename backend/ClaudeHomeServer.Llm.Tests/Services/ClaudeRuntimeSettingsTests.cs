using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// Файл --settings хода: выключение хуков и гейт плагина браузера. Проверяем содержимое
// файла, а не запуск CLI: неверный ключ настроек CLI молча проглотит, и плагин поедет
// в контекст всем персонам подряд (ровно то, ради чего гейт и делался).
// Коллекция Inotify: тест относительного DataPath поднимает хост (он открывает inotify-
// экземпляры перечитывания конфигурации), а InotifyLeakRegressionTests меряет их число в
// процессе — тот же оговор, что у LlmSubsystemDiCycleTests. Хост — на КЛАСС (храповик,
// тест. «Г3»): поднимается один раз в фикстуре Hosts, живёт до конца класса — вся работа
// с ним остаётся внутри сериализованной коллекции, как и раньше.
[Trait("Category", "Host")]
[Collection(TestCollections.Inotify)]
public class ClaudeRuntimeSettingsTests : IClassFixture<ClaudeRuntimeSettingsTests.Hosts>
{
    // Хост класса нужен одному тесту (относительный DataPath) и требует реального DI-графа.
    // Тестовый каталог создаётся ПОД cwd процесса, поэтому относительный путь резолвится
    // именно в него и весь хост (users.json, projects.json) остаётся в нём, а не в чужой папке.
    public sealed class Hosts : TestWebApplicationFactory
    {
        public string RelDir { get; } = "ccs-relative-data-" + Guid.NewGuid().ToString("N");

        public Hosts()
        {
            Directory.CreateDirectory(RelDir);
            // ExtraConfig применяется после встроенных ключей фабрики — перетирает DataPath
            ExtraConfig["DataPath"] = Path.Combine(RelDir, "projects.json");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { try { Directory.Delete(RelDir, true); } catch { } }
        }
    }

    private readonly Hosts _hosts;

    public ClaudeRuntimeSettingsTests(Hosts hosts) => _hosts = hosts;

    private static (string Path, JsonElement Json) Settings(bool browserEnabled)
    {
        var args = ClaudeRuntimeSettings
            .HooksOffArgs(LocalProcessRunner.Instance, browserEnabled).ToList();
        args[0].Should().Be("--settings");
        var path = args[1];
        return (path, JsonDocument.Parse(File.ReadAllText(path)).RootElement);
    }

    [Fact]
    public void ХукиГасятсяТолькоНаWindows()
    {
        // Сервер переехал на Linux: на нём хуки нужны как канал влияния на локальную
        // модель (PostToolUse), а мелькающих окон консоли нет. На Windows прежнее
        // поведение — дочерние процессы плагинов открывают консоль на каждый ход.
        var expected = OperatingSystem.IsWindows();
        foreach (var browser in new[] { true, false })
            Settings(browser).Json.GetProperty("disableAllHooks").GetBoolean().Should().Be(expected);
    }

    [Fact]
    public void БраузерРазрешён_ПлагинPlaywrightВключенЯвно()
    {
        // Явная запись true нужна, даже когда доступ разрешён по умолчанию: иначе синк
        // settings.json профиля (LlmProviderRegistry.SyncUserProfile) может затереть
        // enabledPlugins хостовым {} и браузер пропадёт молча
        var plugins = Settings(browserEnabled: true).Json.GetProperty("enabledPlugins");
        plugins.GetProperty("playwright@claude-plugins-official").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void БраузерЗапрещён_ПлагинPlaywrightВыключен()
    {
        var plugins = Settings(browserEnabled: false).Json.GetProperty("enabledPlugins");
        plugins.GetProperty("playwright@claude-plugins-official").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void ИмяФайлаСовпадаетССоставомХуков()
    {
        // Состояние хуков читают по cliArgs снимка промпта, где видно только имя файла:
        // «hooks-off» с включёнными хуками внутри однажды уже увёл диагноз не туда.
        foreach (var browser in new[] { true, false })
        {
            var (path, json) = Settings(browser);
            var marker = json.GetProperty("disableAllHooks").GetBoolean() ? "hooks-off" : "hooks-on";
            Path.GetFileName(path).Should().StartWith(marker);
        }
    }

    [Fact]
    public void РежимыЖивутВРазныхФайлах()
    {
        // Иначе один режим переписывал бы файл другого, а сигнатура прогона (в неё входит
        // путь --settings) не отличала бы сессию с браузером от сессии без него
        Settings(browserEnabled: true).Path.Should().NotBe(Settings(browserEnabled: false).Path);
    }

    [Fact]
    public void ДваИнстансаСРазнымСоставомХуков_НеВлияютДругНаДруга()
    {
        if (OperatingSystem.IsWindows()) return;
        var temp = Path.Combine(Path.GetTempPath(), "ccs-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            // «Старая сборка»: без хука потребителей; «новая» — с ним. Один temp-каталог на машине.
            var oldPath = ClaudeRuntimeSettings.WriteSettings(temp, true, false, "kill-v1", null);
            var oldBefore = File.ReadAllText(oldPath);
            var newPath = ClaudeRuntimeSettings.WriteSettings(temp, true, false, "kill-v2", "consumers-v2");
            // повторная запись старым инстансом (его перезапуск) не должна затронуть файл нового
            var newBefore = File.ReadAllText(newPath);
            ClaudeRuntimeSettings.WriteSettings(temp, true, false, "kill-v1", null);

            newPath.Should().NotBe(oldPath);
            File.ReadAllText(oldPath).Should().Be(oldBefore);
            File.ReadAllText(newPath).Should().Be(newBefore);
            JsonDocument.Parse(File.ReadAllText(oldPath)).RootElement.GetProperty("hooks")
                .GetProperty("PreToolUse").GetArrayLength().Should().Be(1, "у старой сборки только Bash");
            JsonDocument.Parse(File.ReadAllText(newPath)).RootElement.GetProperty("hooks")
                .GetProperty("PreToolUse").EnumerateArray().Select(h => h.GetProperty("matcher").GetString())
                .Should().Equal("Bash", "Edit|Write");
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(newPath)!, "block-process-kill.py")).Should().Be("kill-v2");
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    [Fact]
    public void ОчисткаСостоянияХука_ЧиститКаталогиОтпечатковИПрежнийОбщий()
    {
        var temp = Path.Combine(Path.GetTempPath(), "ccs-cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fresh = Path.Combine(temp, ClaudeRuntimeSettings.RuntimeDirName, "abc", ClaudeRuntimeSettings.HookStateDirName);
            var legacy = Path.Combine(temp, ClaudeRuntimeSettings.RuntimeDirName, ClaudeRuntimeSettings.HookStateDirName);
            Directory.CreateDirectory(fresh);
            Directory.CreateDirectory(legacy);
            var old1 = Path.Combine(fresh, "old"); var old2 = Path.Combine(legacy, "old"); var keep = Path.Combine(fresh, "keep");
            foreach (var f in new[] { old1, old2, keep }) File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(old1, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(old2, DateTime.UtcNow.AddDays(-2));

            ClaudeRuntimeSettings.CleanupHookState(temp, TimeSpan.FromDays(1));

            File.Exists(old1).Should().BeFalse();
            File.Exists(old2).Should().BeFalse();
            File.Exists(keep).Should().BeTrue();
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    [Fact]
    public void ОчисткаСостоянияХука_УбираетСтарыеКаталогиОтпечатков_СвежиеОставляет()
    {
        var temp = Path.Combine(Path.GetTempPath(), "ccs-cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runtime = Path.Combine(temp, ClaudeRuntimeSettings.RuntimeDirName);
            var oldDir = Path.Combine(runtime, "old");
            var freshDir = Path.Combine(runtime, "fresh");
            foreach (var d in new[] { oldDir, freshDir }) Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(oldDir, "hook.py"), "x");
            File.WriteAllText(Path.Combine(freshDir, "hook.py"), "x");
            var past = DateTime.UtcNow.AddDays(-3);
            File.SetLastWriteTimeUtc(Path.Combine(oldDir, "hook.py"), past);
            Directory.SetLastWriteTimeUtc(oldDir, past);

            ClaudeRuntimeSettings.CleanupHookState(temp, TimeSpan.FromDays(1));

            Directory.Exists(oldDir).Should().BeFalse("в каталоге прежней сборки ничего не менялось дольше TTL");
            Directory.Exists(freshDir).Should().BeTrue("недавно менявшийся каталог остаётся");
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    [Fact]
    public void ОчисткаСостоянияХука_КаталогТекущегоОтпечатка_НеУдаляетсяДажеСтарый()
    {
        var temp = Path.Combine(Path.GetTempPath(), "ccs-cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = ClaudeRuntimeSettings.HooksOffArgs(new TempLauncher(temp)).ToList()[1];
            var current = Path.GetDirectoryName(settings)!;
            var past = DateTime.UtcNow.AddDays(-3);
            foreach (var f in Directory.EnumerateFiles(current)) File.SetLastWriteTimeUtc(f, past);
            Directory.SetLastWriteTimeUtc(current, past);

            ClaudeRuntimeSettings.CleanupHookState(temp, TimeSpan.FromDays(1));

            File.Exists(settings).Should().BeTrue("файл настроек отдан живому процессу: каталог текущего отпечатка не трогаем");
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    private sealed class TempLauncher(string temp) : IProcessLauncher
    {
        public bool IsSandboxed => false;
        public bool TargetIsWindows => false;
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => temp;
        public string? McpApiUrlOverride => null;
        public Process Start(ProcessSpec spec) => throw new NotSupportedException();
        public void Kill(Process process, string? turnId = null) { }
        public int EstimateCommandLineLength(ProcessSpec spec) => 0;
    }

    [Fact]
    public void ОкружениеХукаПотребителей_НеПопадаетВЛогиИСнимки()
    {
        // Гвард от регрессии: CCS_HOOK_TOKEN — сервисный JWT. Имена окружения хука знает только
        // ClaudeSession (там токен подставляется после сигнатуры) и скрипт хука; ни одна строка
        // с ними не должна быть логирующим вызовом, а env процесса — уезжать в Log*.
        var root = RepoRoot();
        var backend = Path.Combine(root, "backend");
        var files = Directory.EnumerateFiles(backend, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains("Tests"))
            .ToList();
        var hookNames = new[] { "CCS_HOOK_", "ConsumersHookTokenEnv", "ConsumersHookTokenPlaceholder" };
        var allowed = new[] { "ClaudeSession.cs", "ClaudeRuntimeSettings.cs" };
        var logCall = new System.Text.RegularExpressions.Regex(@"\bLog[A-Za-z]*\(|\b_?log(ger)?\.|Console\.Write");
        var violations = new List<string>();
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (hookNames.Any(line.Contains) && !allowed.Contains(name))
                    violations.Add($"{name}:{i + 1}: имя окружения хука вне ClaudeSession/ClaudeRuntimeSettings");
                if (hookNames.Any(line.Contains) && logCall.IsMatch(line))
                    violations.Add($"{name}:{i + 1}: имя окружения хука в логирующем вызове");
                if (name == "ClaudeSession.cs" && logCall.IsMatch(line)
                    && System.Text.RegularExpressions.Regex.IsMatch(line, @"\benvOverrides\b|\bspec\.Env\b|\.Env\b"))
                    violations.Add($"{name}:{i + 1}: env процесса в логирующем вызове");
            }
        }
        violations.Should().BeEmpty();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if ((File.Exists(Path.Combine(dir.FullName, ".git")) || Directory.Exists(Path.Combine(dir.FullName, ".git")))
                && Directory.Exists(Path.Combine(dir.FullName, "backend"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Корень репозитория не найден");
    }

    [Fact]
    public void РетенцияТранскриптовПродленаКлючомCleanupPeriodDays()
    {
        // Плановая уборка CLI (дефолт ~30 дней) вычищает {csid}.jsonl из-под архива чатов;
        // эксперимент 2026-08-24 подтвердил, что ключ действует и из --settings-файла.
        // Без ключа транскрипт переживает дефолтную ретенцию, и --resume возвращённого
        // чата начинает разговор с нуля
        foreach (var browser in new[] { true, false })
            Settings(browser).Json.GetProperty("cleanupPeriodDays").GetInt32().Should().BeGreaterThan(30);
    }

    [Fact]
    public void СинкСкилловИПлагиновClaudeAiВыключен()
    {
        // CLI ≥ 2.1.2xx тянет скиллы/плагины аккаунта claude.ai (docx/pptx/canvas-design)
        // в каждую сессию с подписочным логином — они входят в системный блок каждого хода
        // и съедают контекст. Для хостовых владельцев --settings — единственный канал
        // настройки, поэтому ключи стоят false всегда (container-путь — LlmProviderRegistry).
        foreach (var browser in new[] { true, false })
        {
            var json = Settings(browser).Json;
            json.GetProperty("syncClaudeAiSkills").GetBoolean().Should().BeFalse();
            json.GetProperty("syncClaudeAiPlugins").GetBoolean().Should().BeFalse();
        }
    }

    // ADR-035 §2 (S1c): статический deny {data}/wiki/** в настройках запуска CLI — ОДНО
    // правило Edit (ревью S1c, Н1): CLI 2.1.310 матчит файловые проверки только по Edit(path),
    // правила Write/NotebookEdit инертны и дают securityNotice «never matches». Все три
    // инструмента по-прежнему держит серверный backstop — см. ClaudeSessionWikiDenyTests.
    // deny-правила CLI вычисляет раньше allow и раньше режимов прав: в acceptEdits правку
    // файла он не выносит на карточку вовсе — без этого deny отказ до агента не дошёл бы.
    private static JsonElement SettingsWithWikiDeny(string temp, string dataDir)
    {
        var args = ClaudeRuntimeSettings.HooksOffArgs(new TempLauncher(temp), true, dataDir).ToList();
        return JsonDocument.Parse(File.ReadAllText(args[1])).RootElement;
    }

    // Ожидание — ЛИТЕРАЛ документированной формы, а не пересчёт формулы прода: пересчёт той
    // же склейкой прошёл бы и на лишнем слеше («///…»), и на лишнем правиле (ревью S1c, Н3).
    // Синтетический корень: dataDir ничьих файлов не трогает — он входит в отпечаток и в текст
    // правила.
    private const string DataRoot = "/srv/ccs/data";
    private const string ExpectedDenyRule = "Edit(//srv/ccs/data/wiki/**)";

    [Fact]
    public void DenyВики_ОдноПравилоEdit_ЛитералАбсолютнойФормы()
    {
        var temp = Path.Combine(Path.GetTempPath(), "ccs-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var deny = SettingsWithWikiDeny(temp, DataRoot).GetProperty("permissions").GetProperty("deny");
            deny.GetArrayLength().Should().Be(1, "одно Edit-правило: Write/NotebookEdit в CLI инертны");
            deny.EnumerateArray().Select(e => e.GetString()).Should().Equal(ExpectedDenyRule);
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    [Fact]
    public void DenyВики_СоставНеЗависитОтХода_СтрокаНастроекПобайтноРавна()
    {
        // Два вызова — «два хода одной сессии»: путь --settings входит в сигнатуру прогона,
        // любое расхождение состава между ходами перезапускало бы живой CLI со всеми MCP.
        var temp = Path.Combine(Path.GetTempPath(), "ccs-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var p1 = ClaudeRuntimeSettings.WriteSettings(temp, true, false, "kill-v1", null, DataRoot);
            var text1 = File.ReadAllText(p1);
            var p2 = ClaudeRuntimeSettings.WriteSettings(temp, true, false, "kill-v1", null, DataRoot);
            var text2 = File.ReadAllText(p2);

            p2.Should().Be(p1, "тот же состав — тот же файл, иначе сигнатура прогона мерцает");
            text2.Should().Be(text1, "содержимое побайтно то же");
            JsonDocument.Parse(text2).RootElement.GetProperty("permissions").GetProperty("deny")
                .EnumerateArray().Select(e => e.GetString()).Should().Equal(ExpectedDenyRule);
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    [Fact]
    public void DenyВики_ИнстансыСРазнымиDataDir_НеПерезаписываютФайлДругДруга()
    {
        if (OperatingSystem.IsWindows()) return; // тот же оговор, что у теста состава хуков: файл пишется на POSIX-манере
        var temp = Path.Combine(Path.GetTempPath(), "ccs-settings-" + Guid.NewGuid().ToString("N"));
        const string dataA = "/srv/ccs/data-A", dataB = "/srv/ccs/data-B";
        try
        {
            var pathA = ClaudeRuntimeSettings.WriteSettings(temp, true, false, "kill-v1", null, dataA);
            var textA = File.ReadAllText(pathA);
            var pathB = ClaudeRuntimeSettings.WriteSettings(temp, true, false, "kill-v1", null, dataB);
            // повторная запись инстансом A (его перезапуск) не должна затронуть файл B
            ClaudeRuntimeSettings.WriteSettings(temp, true, false, "kill-v1", null, dataA);

            pathB.Should().NotBe(pathA, "разный data-корень — разные deny-правила, значит разный отпечаток");
            File.ReadAllText(pathA).Should().Be(textA);
            JsonDocument.Parse(File.ReadAllText(pathB)).RootElement.GetProperty("permissions").GetProperty("deny")
                .EnumerateArray().Select(e => e.GetString()).Should().Equal("Edit(//srv/ccs/data-B/wiki/**)");
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    // Н5: data-корень SessionManager берёт резолвным GetFullPath (тот же приём, что Program.cs
    // и NotesService) — иначе относительный DataPath отдал бы в правило относительный корень,
    // а «//…» в синтаксисе правил CLI значит абсолютный путь. Идём через реальный DI-граф
    // класса-фикстуры Hosts (тот же приём, что LlmSubsystemDiCycleTests): относительный
    // DataPath задан в её конструкторе, каталог создан под cwd процесса — резолвится в него.
    [Fact]
    public void ОтносительныйDataPath_ДаётАбсолютныйКореньВDenyПравиле()
    {
        var sessions = _hosts.Services.GetRequiredService<SessionManager>();

        var dataDir = (string)typeof(SessionManager)
            .GetField("_dataDir", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(sessions)!;
        Path.IsPathRooted(dataDir).Should().BeTrue(
            "корень deny-правила резолвится из относительного DataPath: GetFullPath");

        var abs = Path.GetFullPath(_hosts.RelDir).Replace('\\', '/');
        ClaudeRuntimeSettings.WikiDenyRules(dataDir).Should().Equal($"Edit(//{abs.TrimStart('/')}/wiki/**)");
    }

    [Fact]
    public void БезDataDir_КлючPermissionsНеПоявляется()
    {
        // One-shot прогоны и пробы (без правок вики) остаются на прежнем составе файла
        foreach (var browser in new[] { true, false })
            Settings(browser).Json.TryGetProperty("permissions", out _).Should().BeFalse();
    }

    // Хук режет убийство процессов по имени: прод и дев-стенд — один бинарь, а в командной
    // строке CLI-агентов лежит весь системный промпт (инциденты 28.09, 12.10, 16.10, 17.10)
    private static (int Exit, string Err) RunKillHook(string command)
    {
        var json = Settings(browserEnabled: true).Json;
        var hookCommand = json.GetProperty("hooks").GetProperty("PreToolUse")[0]
            .GetProperty("hooks")[0].GetProperty("command").GetString()!;
        var parts = hookCommand.Split(' ', 3);
        var psi = new ProcessStartInfo(parts[0])
        {
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add(parts[1]);
        psi.ArgumentList.Add(parts[2].Trim('"'));
        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write(JsonSerializer.Serialize(new { tool_input = new { command } }));
        proc.StandardInput.Close();
        var err = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, err);
    }

    [Fact]
    public void ХукБлокируетУбийствоПроцессовПоИмени_НаЛинуксе()
    {
        if (OperatingSystem.IsWindows()) return; // на Windows хуки погашены целиком

        var json = Settings(browserEnabled: true).Json;
        json.GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("matcher").GetString().Should().Be("Bash");

        foreach (var cmd in new[]
        {
            "pkill -f ClaudeHomeServer",
            "echo ok; pkill -f \"ClaudeHomeServer.dll\" 2>/dev/null",
            "sudo killall dotnet",
            "ps aux | grep ClaudeHomeServer | awk '{print $2}' | xargs kill -9",
            "kill -9 $(pgrep -f ClaudeHomeServer)",
            "kill `pgrep dotnet`",
        })
        {
            var (exit, err) = RunKillHook(cmd);
            exit.Should().Be(2, $"команда должна блокироваться: {cmd}");
            err.Should().Contain("PID");
        }
    }

    [Fact]
    public void ХукПропускаетОбычныеКоманды_НаЛинуксе()
    {
        if (OperatingSystem.IsWindows()) return;

        foreach (var cmd in new[]
        {
            "kill 12345",
            "kill -TERM $STAND_PID",
            "grep -rn pkill docs/",
            "git commit -m \"fix: запретить pkill по имени\"",
            "ss -ltnp | grep :5000",
            "git commit -F - <<'EOF'\nfix: запретить\nkill $(pgrep x) и pkill\nEOF",
            "git commit -m \"первая строка\nkill $(pgrep x)\"",
        })
            RunKillHook(cmd).Exit.Should().Be(0, $"команда должна проходить: {cmd}");
    }

    // Хук «сборка и тесты — только инструментами»: действует лишь при CCS_TASK_EXECUTOR=1
    private static (int Exit, string Err) RunToolsHook(string command, bool executor = true)
    {
        var json = Settings(browserEnabled: true).Json;
        var hookCommand = json.GetProperty("hooks").GetProperty("PreToolUse").EnumerateArray()
            .Select(h => h.GetProperty("hooks")[0].GetProperty("command").GetString()!)
            .Single(c => c.Contains("block-build-in-bash.py"));
        var parts = hookCommand.Split(' ', 3);
        var psi = new ProcessStartInfo(parts[0])
        {
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add(parts[1]);
        psi.ArgumentList.Add(parts[2].Trim('"'));
        psi.Environment.Remove(ClaudeRuntimeSettings.TaskExecutorEnv);
        if (executor) psi.Environment[ClaudeRuntimeSettings.TaskExecutorEnv] = "1";
        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write(JsonSerializer.Serialize(new { tool_input = new { command } }));
        proc.StandardInput.Close();
        var err = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, err);
    }

    [Fact]
    public void ХукСборки_БлокируетDotnetИVitest_ВЧатеИсполнителя()
    {
        if (OperatingSystem.IsWindows()) return;

        foreach (var cmd in new[]
        {
            "dotnet test",
            "dotnet build backend/ClaudeHomeServer.slnx -v q",
            "cd ~/.cache/ccs-mutation/c1 && dotnet test backend/X.Tests --filter A",
            "DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet build",
            "FOO=1 BAR=2 npx vitest run src/a.test.ts",
            "cd frontend && npx --no-install vitest run",
            "echo start; sudo dotnet test",
            "timeout 60 true || dotnet build",
            "time dotnet test",
            "if dotnet build; then echo ok; fi",
            "bash -c 'dotnet test backend/X'",
            "echo $(dotnet build)",
            "/usr/bin/dotnet build",
        })
        {
            var (exit, err) = RunToolsHook(cmd);
            exit.Should().Be(2, $"команда должна блокироваться: {cmd}");
            err.Should().Contain("run_tests").And.Contain("tree");
        }
    }

    [Fact]
    public void ХукСборки_ПропускаетСловаВСтрокахИКомментариях_ИДругиеКоманды()
    {
        if (OperatingSystem.IsWindows()) return;

        foreach (var cmd in new[]
        {
            "echo \"dotnet test\"",
            "grep -rn 'dotnet build' docs/",
            "git commit -m \"fix: вынести dotnet test в run_tests\"",
            "git commit -F - <<'EOF'\ndotnet test теперь через run_tests\nEOF",
            "ls # потом dotnet test",
            "dotnet --version",
            "dotnet restore backend/X.slnx",
            "dotnet format --verify-no-changes",
            "npx tsc -b",
            "npm run lint:design",
            "cat notes-about-dotnet-test.md",
            "scripts/mutation-copy.sh create",
        })
            RunToolsHook(cmd).Exit.Should().Be(0, $"команда должна проходить: {cmd}");
    }

    [Fact]
    public void ХукСборки_НеДействуетВОбычномЧате()
    {
        if (OperatingSystem.IsWindows()) return;

        // Чат человека и оркестратор: переменной нет — Bash со сборкой им бывает нужен
        RunToolsHook("dotnet test", executor: false).Exit.Should().Be(0);
        RunToolsHook("npx vitest run", executor: false).Exit.Should().Be(0);
    }

    [Fact]
    public void ХукСборки_ВТойЖеЗаписиФайлаДляВсехЧатов_ОдинФайлНастроек()
    {
        if (OperatingSystem.IsWindows()) return;

        // Признак ходит в окружении, а не в файле: путь --settings не зависит от роли чата
        Settings(browserEnabled: true).Path.Should().Be(Settings(browserEnabled: true).Path);
        Settings(browserEnabled: true).Json.GetProperty("hooks").GetProperty("PreToolUse").EnumerateArray()
            .Select(h => h.GetProperty("matcher").GetString()).Should().Equal("Bash", "Bash", "Edit|Write");
    }
}
