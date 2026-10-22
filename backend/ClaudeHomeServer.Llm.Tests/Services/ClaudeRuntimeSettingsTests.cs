using System.Diagnostics;
using System.Text.Json;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Файл --settings хода: выключение хуков и гейт плагина браузера. Проверяем содержимое
// файла, а не запуск CLI: неверный ключ настроек CLI молча проглотит, и плагин поедет
// в контекст всем персонам подряд (ровно то, ради чего гейт и делался).
public class ClaudeRuntimeSettingsTests
{
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
}
