using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Что делает автообновление с самим CLI хоста: установка конкретной версии, дымовой прогон,
// откат. Вынесено из сторожа ради подмены в тестах (CI на Linux не должен трогать реальный
// ~/.local/bin/claude). Всё — системный one-shot, всегда local.
internal sealed record SmokeResult(bool Ok, string? Reason);

internal sealed record CliAutoUpdateOps(
    Func<string, CancellationToken, Task<bool>> Install,
    Func<CancellationToken, Task<SmokeResult>> Smoke,
    Func<string, CancellationToken, Task<bool>> Rollback)
{
    public static CliAutoUpdateOps Real => new(
        ClaudeCliAutoUpdateOps.InstallAsync, ClaudeCliAutoUpdateOps.SmokeAsync, ClaudeCliAutoUpdateOps.RollbackAsync);
}

internal static class ClaudeCliAutoUpdateOps
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SmokeTimeout = TimeSpan.FromMinutes(2);
    private const string SmokeModel = "haiku";

    // `claude install X.Y.Z` — конкретная версия, не `claude update` (тот ставит то, что решит CLI)
    public static async Task<bool> InstallAsync(string version, CancellationToken ct)
    {
        var (code, _, err) = await RunAsync(["install", version], null, null, InstallTimeout, ct);
        if (code != 0) Console.Error.WriteLine($"[ClaudeCliAutoUpdate] claude install {version}: exit {code} {Tail(err)}");
        return code == 0;
    }

    // Откат: старые версии лежат в ~/.local/share/claude/versions, поэтому быстрее и надёжнее
    // перекинуть симлинк, чем снова качать; нет симлинка/файла версии — `claude install <прежняя>`.
    public static async Task<bool> RollbackAsync(string previous, CancellationToken ct)
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
            Console.Error.WriteLine($"[ClaudeCliAutoUpdate] откат симлинком не удался: {ex.Message}");
        }
        var (code, _, _) = await RunAsync(["install", previous, "--force"], null, null, InstallTimeout, ct);
        return code == 0;
    }

    // Дым: короткий ход haiku с тем же очищенным env, что у штатного запуска (ANTHROPIC_* и
    // CLAUDE_CONFIG_DIR вычищены, CLAUDE_CODE_OAUTH_TOKEN не трогаем) и CLAUDE_CODE_DISABLE_CLAUDE_MDS=1.
    // Проверка переменной BareMode: в рабочей папке лежит CLAUDE.md с одноразовым маркером, модель просят
    // его назвать; если переменная перестала работать, CLAUDE.md подгрузится и маркер утечёт в ответ.
    // Признак односторонний: утечка = переменная сломана точно, отсутствие утечки — «скорее всего работает».
    public static async Task<SmokeResult> SmokeAsync(CancellationToken ct)
    {
        var nonce = "MARKER" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var dir = Path.Combine(Path.GetTempPath(), "claude-cli-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "CLAUDE.md"),
                $"# Проект\nСекретный код проекта: {nonce}. Если спросят код — назови его.\n");
            var args = new List<string> { "--print", "--output-format", "stream-json", "--verbose" };
            args.AddRange(ClaudeRuntimeSettings.HooksOffArgs(LocalProcessRunner.Instance));
            args.AddRange(["--model", SmokeModel]);
            var env = new Dictionary<string, string?> { ["CLAUDE_CODE_DISABLE_CLAUDE_MDS"] = "1" };
            var (code, stdout, stderr) = await RunAsync(args, dir, env, SmokeTimeout, ct,
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
        if (exitCode != 0) return new SmokeResult(false, $"процесс завершился с кодом {exitCode} {Tail(stderr)}".Trim());
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
                        return new SmokeResult(false, "result с is_error=true");
                    gotResult = true;
                }
            }
            catch (JsonException) { }
        }
        if (!gotResult) return new SmokeResult(false, "в выводе нет result");
        if (stdout.Contains(nonce, StringComparison.Ordinal))
            return new SmokeResult(false, "CLAUDE_CODE_DISABLE_CLAUDE_MDS больше не отключает CLAUDE.md проекта");
        return new SmokeResult(true, null);
    }

    private static string Tail(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : (s.Length > 300 ? s[^300..] : s).Trim();

    private static async Task<(int Code, string Out, string Err)> RunAsync(
        IEnumerable<string> args, string? workDir, IDictionary<string, string?>? env,
        TimeSpan timeout, CancellationToken ct, string? stdin = null)
    {
        var psi = new ProcessStartInfo(ClaudeCliLocator.FindClaudeExecutable())
        {
            UseShellExecute = false,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (workDir is not null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Маршрут CLI задаёт только сервер: унаследованные ANTHROPIC_* и профиль не пускаем
        foreach (var k in LlmProviderRegistry.ProviderEnvKeys) psi.Environment.Remove(k);
        if (env is not null)
            foreach (var (k, v) in env)
            {
                if (v is null) psi.Environment.Remove(k); else psi.Environment[k] = v;
            }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("не удалось запустить claude");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            if (stdin is not null)
            {
                await p.StandardInput.WriteAsync(stdin.AsMemory(), cts.Token);
                p.StandardInput.Close();
            }
            var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            var errTask = p.StandardError.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return (p.ExitCode, await outTask, await errTask);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "таймаут");
        }
    }
}
