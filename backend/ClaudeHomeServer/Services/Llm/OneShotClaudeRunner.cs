using System.Diagnostics;
using System.Text;

namespace ClaudeHomeServer.Services.Llm;

// Абстракция one-shot вызова LLM — для мокирования в тестах.
// В DI интерфейс указывает на тот же singleton OneShotClaudeRunner.
public interface IOneShotRunner
{
    // Модель ненастроенного провайдера тихо заменяется дефолтом claude
    string? NormalizeModel(string? model);

    // effort — усилие рассуждения (--effort), для моделей с его поддержкой
    Task<string> RunAsync(string prompt, string? model = null,
        TimeSpan? timeout = null, CancellationToken ct = default, string? effort = null);
}

// Общий раннер одноразовых вызовов claude --print (без сессии): промпт через stdin,
// ответ — stdout целиком. Модель стороннего провайдера подключается env-оверрайдами
// (LlmProviderRegistry.BuildCliEnv). Рабочая папка — пустая temp (claude не получает
// доступ к файлам). Используется генерациями задач и заметок; ChangelogService
// исторически держит свою копию.
public sealed class OneShotClaudeRunner(LlmProviderRegistry llmProviders) : IOneShotRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    // Модель ненастроенного провайдера тихо заменяется дефолтом claude —
    // генерация не должна падать из-за отсутствующего ключа
    public string? NormalizeModel(string? model) =>
        llmProviders.ResolveByModel(model) is { Enabled: false } ? null : model;

    public async Task<string> RunAsync(string prompt, string? model = null,
        TimeSpan? timeout = null, CancellationToken ct = default, string? effort = null)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "claude-oneshot");
        Directory.CreateDirectory(workDir);

        var utf8NoBom = new UTF8Encoding(false);
        var psi = new ProcessStartInfo
        {
            FileName = Claude.ClaudeCliLocator.FindClaudeExecutable(),
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = utf8NoBom,
            StandardErrorEncoding = utf8NoBom,
            StandardInputEncoding = utf8NoBom,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("text");
        if (!string.IsNullOrWhiteSpace(model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(LlmProviderRegistry.StripClaudeWindowAlias(model)!);
        }
        if (!string.IsNullOrWhiteSpace(effort))
        {
            psi.ArgumentList.Add("--effort");
            psi.ArgumentList.Add(effort);
        }

        if (llmProviders.BuildCliEnv(model) is { } env)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Не удалось запустить claude");

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
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("Claude не ответил за отведённое время");
        }
    }
}
