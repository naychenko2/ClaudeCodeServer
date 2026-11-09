using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Что делает автообновление с самим CLI хоста: установка конкретной версии, дымовой прогон,
// откат. Вынесено из сторожа ради подмены в тестах (CI на Linux не должен трогать реальный
// ~/.local/bin/claude). Всё — системный one-shot, всегда local.
// AuthFailed — дым упал из-за авторизации (протухший вход), а не из-за версии CLI.
internal sealed record SmokeResult(bool Ok, string? Reason, bool AuthFailed = false);

internal sealed record CliAutoUpdateOps(
    Func<string, CancellationToken, Task<bool>> Install,
    Func<CancellationToken, Task<SmokeResult>> Smoke,
    Func<string, CancellationToken, Task<bool>> Rollback)
{
    // Автообновление выключено: операции не вызываются, а случайный вызов должен быть громким
    public static CliAutoUpdateOps Disabled { get; } = new(
        (_, _) => throw new InvalidOperationException("автообновление claude CLI выключено"),
        _ => throw new InvalidOperationException("автообновление claude CLI выключено"),
        (_, _) => throw new InvalidOperationException("автообновление claude CLI выключено"));

    public static CliAutoUpdateOps Real(ClaudeCliAutoUpdateOps ops) => new(ops.InstallAsync, ops.SmokeAsync, ops.RollbackAsync);
}

internal sealed class ClaudeCliAutoUpdateOps(OneShotClaudeRunner runner, ILauncherFactory launchers,
    LlmProviderRegistry llmProviders, ILogger log)
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SmokeTimeout = TimeSpan.FromMinutes(2);
    private const string SmokeModel = "haiku";

    // Протухший вход/ключ — не поломка версии CLI: такой дым версию «битой» не делает
    private static readonly Regex AuthMarkers = new(
        @"not logged in|oauth|authenticat|unauthori[sz]ed|/login|\b401\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // `claude install X.Y.Z` — конкретная версия, не `claude update` (тот ставит то, что решит CLI)
    public async Task<bool> InstallAsync(string version, CancellationToken ct)
    {
        var (code, _, err) = await RunAsync(["install", version], InstallTimeout, ct);
        if (code != 0) log.LogWarning("claude install {Version}: exit {Code} {Tail}", version, code, Tail(err));
        return code == 0;
    }

    // Откат: старые версии лежат в ~/.local/share/claude/versions, поэтому быстрее и надёжнее
    // перекинуть симлинк, чем снова качать; нет симлинка/файла версии — `claude install <прежняя>`.
    public async Task<bool> RollbackAsync(string previous, CancellationToken ct)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var target = Path.Combine(home, ".local", "share", "claude", "versions", previous);
                var link = Path.Combine(home, ".local", "bin", "claude");
                if (File.Exists(target) && new FileInfo(link).LinkTarget is not null)
                {
                    var tmp = link + ".rollback." + Guid.NewGuid().ToString("N");
                    File.CreateSymbolicLink(tmp, target);
                    File.Move(tmp, link, overwrite: true);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Откат claude CLI симлинком не удался");
        }
        var (code, _, _) = await RunAsync(["install", previous, "--force"], InstallTimeout, ct);
        return code == 0;
    }

    // Дым: короткий ход haiku ровно как штатный one-shot: launcher ForOwner(null) (local), env из
    // OneShotClaudeRunner.ResolveEnv (тот же путь авторизации — подписка/пул), ClearEnv реестра
    // провайдеров, плюс CLAUDE_CODE_DISABLE_CLAUDE_MDS=1.
    // Проверка переменной BareMode: в рабочей папке лежит CLAUDE.md с одноразовым маркером, модель просят
    // его назвать; если переменная перестала работать, CLAUDE.md подгрузится и маркер утечёт в ответ.
    // Признак односторонний: утечка = переменная сломана точно, отсутствие утечки — «скорее всего работает».
    public async Task<SmokeResult> SmokeAsync(CancellationToken ct)
    {
        var nonce = "MARKER" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var dir = Path.Combine(Path.GetTempPath(), "claude-cli-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "CLAUDE.md"),
                $"# Проект\nСекретный код проекта: {nonce}. Если спросят код — назови его.\n");
            var launcher = launchers.ForOwner(null);
            var args = new List<string> { "--print", "--output-format", "stream-json", "--verbose" };
            args.AddRange(ClaudeRuntimeSettings.HooksOffArgs(launcher));
            args.AddRange(["--model", SmokeModel]);
            var env = new Dictionary<string, string>(runner.ResolveEnv(SmokeModel).Env ?? new Dictionary<string, string>())
            {
                ["CLAUDE_CODE_DISABLE_CLAUDE_MDS"] = "1",
            };
            var (code, stdout, stderr) = await RunAsync(args, SmokeTimeout, ct, dir, env,
                stdin: "Назови секретный код проекта, если он тебе известен. Если неизвестен — ответь одним словом UNKNOWN.");
            return Evaluate(code, stdout, nonce, stderr);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new SmokeResult(false, $"дым не запустился: {ex.Message}"); }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // Чистая оценка вывода дыма — тестируется без процесса
    internal static SmokeResult Evaluate(int exitCode, string stdout, string nonce, string? stderr = null)
    {
        if (exitCode != 0)
            return new SmokeResult(false, $"процесс завершился с кодом {exitCode} {Tail(stderr)}".Trim(),
                LooksUnauthorized(stdout, stderr));
        var gotResult = false;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var t)
                    && t.GetString() == "result")
                {
                    if (root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True)
                        return new SmokeResult(false, "result с is_error=true", LooksUnauthorized(stdout, stderr));
                    gotResult = true;
                }
            }
            catch (JsonException) { }
        }
        if (!gotResult) return new SmokeResult(false, "в выводе нет result", LooksUnauthorized(stdout, stderr));
        if (stdout.Contains(nonce, StringComparison.Ordinal))
            return new SmokeResult(false, "CLAUDE_CODE_DISABLE_CLAUDE_MDS больше не отключает CLAUDE.md проекта");
        return new SmokeResult(true, null);
    }

    // Признак «не авторизован»: stderr, не-JSON строки stdout («Not logged in · Please run /login»
    // CLI пишет туда) и текст result. JSON-строки init не смотрим — в них случайные идентификаторы
    internal static bool LooksUnauthorized(string stdout, string? stderr)
    {
        if (!string.IsNullOrEmpty(stderr) && AuthMarkers.IsMatch(stderr)) return true;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var t)
                    && t.GetString() == "result" && root.TryGetProperty("result", out var r)
                    && r.ValueKind == JsonValueKind.String && AuthMarkers.IsMatch(r.GetString()!))
                    return true;
            }
            catch (JsonException)
            {
                if (AuthMarkers.IsMatch(line)) return true;
            }
        }
        return false;
    }

    private static string Tail(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : (s.Length > 300 ? s[^300..] : s).Trim();

    // Через launcher ForOwner(null) (local): тот же запуск, что у штатных one-shot. install/rollback
    // идут без env (профиль/авторизация им не нужны), с очисткой маршрутных переменных провайдеров.
    private async Task<(int Code, string Out, string Err)> RunAsync(
        IEnumerable<string> args, TimeSpan timeout, CancellationToken ct,
        string? workDir = null, IReadOnlyDictionary<string, string>? env = null, string? stdin = null)
    {
        var launcher = launchers.ForOwner(null);
        var turnId = Guid.NewGuid().ToString("N")[..12];
        using var p = launcher.Start(new ProcessSpec
        {
            FileName = launcher.ClaudeCliCommand,
            Args = args.ToList(),
            WorkingDirectory = workDir,
            Env = env,
            ClearEnv = llmProviders.EnvKeysToClear,
            RedirectStdin = stdin is not null,
            StdioEncoding = new UTF8Encoding(false),
            TurnId = turnId,
        });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            var errTask = p.StandardError.ReadToEndAsync(cts.Token);
            if (stdin is not null)
            {
                await p.StandardInput.WriteAsync(stdin.AsMemory(), cts.Token);
                p.StandardInput.Close();
            }
            await p.WaitForExitAsync(cts.Token);
            return (p.ExitCode, await outTask, await errTask);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            launcher.Kill(p, turnId);
            return (-1, "", "таймаут");
        }
    }
}
