using System.Text;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Llm;

// Абстракция one-shot вызова LLM — для мокирования в тестах.
// В DI интерфейс указывает на тот же singleton OneShotClaudeRunner.
public interface IOneShotRunner
{
    // Модель ненастроенного провайдера тихо заменяется дефолтом claude
    string? NormalizeModel(string? model);

    // ownerId — владелец вызова: его среда исполнения определяет, где запустится claude
    // (локально или в песочнице). null — системный вызов, всегда локально.
    // effort — усилие рассуждения (--effort), для моделей с его поддержкой.
    Task<string> RunAsync(string prompt, string? model = null,
        TimeSpan? timeout = null, CancellationToken ct = default,
        string? ownerId = null, string? effort = null);
}

// Общий раннер одноразовых вызовов claude --print (без сессии): промпт через stdin,
// ответ — stdout целиком. Модель стороннего провайдера подключается env-оверрайдами
// (LlmProviderRegistry.BuildCliEnv). Рабочая папка — пустая temp (claude не получает
// доступ к файлам). Используется генерациями задач и заметок; ChangelogService
// исторически держит свою копию.
public sealed class OneShotClaudeRunner(LlmProviderRegistry llmProviders, ILauncherFactory launchers) : IOneShotRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    // Модель ненастроенного провайдера тихо заменяется дефолтом claude —
    // генерация не должна падать из-за отсутствующего ключа
    public string? NormalizeModel(string? model) =>
        llmProviders.ResolveByModel(model) is { Enabled: false } ? null : model;

    public async Task<string> RunAsync(string prompt, string? model = null,
        TimeSpan? timeout = null, CancellationToken ct = default,
        string? ownerId = null, string? effort = null)
    {
        var launcher = launchers.ForOwner(ownerId);
        var workDir = Path.Combine(launcher.HostTempDir, "claude-oneshot");
        Directory.CreateDirectory(workDir);

        var args = new List<string> { "--print", "--output-format", "text" };
        // One-shot — чистая генерация текста: инструменты жёстко выключены. Это контракт
        // (пустая temp-cwd и раньше подразумевала «без файлов», но не мешала Read по
        // абсолютному пути) и защита от инъекции в промпт — в т.ч. когда вызов сделан
        // от имени изолированного пользователя, а процесс работает на хосте.
        args.Add("--disallowedTools");
        args.Add("Bash,Read,Write,Edit,MultiEdit,NotebookEdit,Glob,Grep,WebFetch,WebSearch,Task,Agent,KillShell,BashOutput");
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(model);
        }
        if (!string.IsNullOrWhiteSpace(effort))
        {
            args.Add("--effort");
            args.Add(effort);
        }

        var env = llmProviders.BuildCliEnv(model);

        var turnId = Guid.NewGuid().ToString("N")[..12];
        using var process = launcher.Start(new ProcessSpec
        {
            FileName = launcher.ClaudeCliCommand,
            Args = args,
            WorkingDirectory = workDir,
            Env = env,
            StdioEncoding = new UTF8Encoding(false),
            TurnId = turnId,
        });

        // Чтение вывода запускаем ДО записи промпта — иначе на большом промпте
        // возможен deadlock на заполненных пайпах
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.StandardInput.WriteAsync(prompt.AsMemory(), cts.Token);
            process.StandardInput.Close();

            await process.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                // Причину CLI пишет не только в stderr: «Not logged in · Please run /login»
                // уходит в stdout при пустом stderr. Раньше её тут теряли, и в логах всех
                // сервисов оставалось «завершился с кодом 1:» без объяснения.
                var detail = stderr.Trim();
                if (detail.Length == 0) detail = stdout.Trim();
                if (detail.Length > 500) detail = detail[..500] + "…";
                throw new InvalidOperationException(
                    $"claude завершился с кодом {process.ExitCode}: {detail}");
            }
            return stdout.Trim();
        }
        catch (OperationCanceledException)
        {
            launcher.Kill(process, turnId);
            throw new InvalidOperationException("Claude не ответил за отведённое время");
        }
    }
}
