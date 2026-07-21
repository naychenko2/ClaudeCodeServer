using System.Diagnostics;
using System.Text;

namespace ClaudeHomeServer.Services;

// Конвертация бинарных документов (pdf/docx/xlsx/pptx и др.) в Markdown внешней утилитой
// markitdown (pip-пакет, ставится на хост). Детерминированно, без модели. Запуск на ХОСТЕ
// бэкенда: файлы проекта доступны по хостовому пути (в т.ч. у container-юзеров — их корень
// смонтирован на хост). Возвращает Markdown либо null при любой ошибке/таймауте/отсутствии
// markitdown — потребитель это переживает (фича деградирует, не падает).
public sealed class MarkitdownService(IConfiguration config, ILogger<MarkitdownService> log)
{
    // Команда markitdown в PATH; переопределяется Markitdown:Command (напр. "python" + аргумент -m).
    private string Command => config["Markitdown:Command"] is { Length: > 0 } c ? c : "markitdown";
    private int TimeoutMs => int.TryParse(config["Markitdown:TimeoutMs"], out var t) ? t : 60_000;

    // absolutePath — уже провалидированный (SafeJoin) абсолютный путь к файлу на хосте.
    public async Task<string?> ConvertAsync(string absolutePath, CancellationToken ct = default)
    {
        if (!File.Exists(absolutePath)) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            // Markitdown:Command может нести префикс-аргументы (напр. "python -m markitdown"):
            // первое слово — исполняемый файл, остальное — аргументы перед путём.
            var parts = Command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            psi.FileName = parts[0];
            for (var i = 1; i < parts.Length; i++) psi.ArgumentList.Add(parts[i]);
            psi.ArgumentList.Add(absolutePath);

            using var p = Process.Start(psi);
            if (p is null) return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeoutMs);
            var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                log.LogDebug("markitdown таймаут на {Path}", absolutePath);
                return null;
            }

            var stdout = await stdoutTask;
            if (p.ExitCode != 0)
            {
                var err = (await stderrTask).Trim();
                log.LogDebug("markitdown код {Code}: {Err}", p.ExitCode, err.Length > 300 ? err[..300] : err);
                return null;
            }
            return string.IsNullOrWhiteSpace(stdout) ? null : stdout;
        }
        catch (Exception ex)
        {
            // Нет markitdown в PATH / ошибка запуска — деградируем молча
            log.LogDebug(ex, "markitdown недоступен ({Command})", Command);
            return null;
        }
    }
}
