using System.Diagnostics;
using System.Text;

namespace ClaudeHomeServer.Services.Llm;

// Общий раннер одноразовых вызовов claude --print (без сессии): промпт через stdin,
// ответ — stdout целиком. Модель стороннего провайдера подключается env-оверрайдами
// (LlmProviderRegistry.BuildCliEnv). Рабочая папка — пустая temp (claude не получает
// доступ к файлам). Используется генерациями задач и заметок; ChangelogService
// исторически держит свою копию.
public sealed class OneShotClaudeRunner(LlmProviderRegistry llmProviders)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    // Модель ненастроенного провайдера тихо заменяется дефолтом claude —
    // генерация не должна падать из-за отсутствующего ключа
    public string? NormalizeModel(string? model) =>
        llmProviders.ResolveByModel(model) is { Enabled: false } ? null : model;

    public async Task<string> RunAsync(string prompt, string? model = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
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
            psi.ArgumentList.Add(model);
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
                throw new InvalidOperationException(
                    $"claude завершился с кодом {process.ExitCode}: {stderr.Trim()}");
            return stdout.Trim();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("Claude не ответил за отведённое время");
        }
    }
}
