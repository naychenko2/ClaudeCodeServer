using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Генерация контента задач одноразовым вызовом LLM (без сессии).
// Провайдер — по Tasks:AiModel: deepseek-* → DeepSeek API, иначе claude --print.
// Контекст проекта передаётся в промпте (имя + выдержка из CLAUDE.md) — инструменты
// не нужны, поэтому у claude cwd — пустая temp-папка, ответ приходит одним текстом.
public class TaskAiService(ProjectManager projects, IConfiguration config,
    Llm.DeepSeek.DeepSeekClient deepSeek, Microsoft.Extensions.Options.IOptions<DeepSeekOptions> deepSeekOptions)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    public async Task<string> GenerateDescriptionAsync(string title, string? projectId, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Составь краткое описание задачи в Markdown: заголовок «### Цель», 1-2 предложения сути, " +
                      "маркированный список ключевых пунктов (3-5), при необходимости — критерии готовности. " +
                      "Не больше 120 слов. Ответь ТОЛЬКО markdown-описанием, без вступлений и пояснений.");
        sb.AppendLine();
        sb.AppendLine($"Задача: «{title}»");
        AppendProjectContext(sb, projectId);
        return CleanupText(await RunAsync(sb.ToString(), ct));
    }

    public async Task<IReadOnlyList<string>> GenerateSubtasksAsync(
        string title, string description, string? projectId, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Разбей задачу на 3-7 конкретных выполнимых подзадач (коротких, в повелительном наклонении). " +
                      "Ответь ТОЛЬКО JSON-массивом строк без пояснений и без markdown-обёртки. " +
                      "Пример формата: [\"Первая подзадача\", \"Вторая подзадача\"]");
        sb.AppendLine();
        sb.AppendLine($"Задача: «{title}»");
        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.AppendLine("Описание задачи:");
            sb.AppendLine(description);
        }
        AppendProjectContext(sb, projectId);

        var raw = await RunAsync(sb.ToString(), ct);
        return ParseSubtasks(raw);
    }

    private void AppendProjectContext(StringBuilder sb, string? projectId)
    {
        if (projectId is null) return;
        var project = projects.GetById(projectId);
        if (project is null) return;

        sb.AppendLine();
        sb.AppendLine($"Контекст: задача относится к проекту «{project.Name}».");
        try
        {
            var claudeMd = Path.Combine(project.RootPath, "CLAUDE.md");
            if (File.Exists(claudeMd))
            {
                var text = File.ReadAllText(claudeMd);
                if (text.Length > 4000) text = text[..4000] + "\n…";
                sb.AppendLine("Описание проекта (CLAUDE.md):");
                sb.AppendLine(text);
            }
        }
        catch { /* контекст опционален */ }
    }

    // Выбор провайдера: Tasks:AiModel = deepseek-модель → DeepSeek API, иначе claude CLI
    private async Task<string> RunAsync(string prompt, CancellationToken ct)
    {
        var aiModel = config["Tasks:AiModel"];
        if (Llm.LlmProviderResolver.Resolve(aiModel) == Llm.LlmProvider.DeepSeek
            && deepSeekOptions.Value.Enabled)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            return await deepSeek.CompleteAsync(aiModel!, prompt, cts.Token);
        }
        return await RunClaudeAsync(prompt, ct);
    }

    // Запуск claude --print: промпт через stdin, ответ — stdout целиком
    private static async Task<string> RunClaudeAsync(string prompt, CancellationToken ct)
    {
        // Пустая рабочая папка: генерации не нужны файлы, а claude не получает лишний доступ
        var workDir = Path.Combine(Path.GetTempPath(), "claude-task-ai");
        Directory.CreateDirectory(workDir);

        var utf8NoBom = new UTF8Encoding(false);
        var psi = new ProcessStartInfo
        {
            FileName = Llm.Claude.ClaudeCliLocator.FindClaudeExecutable(),
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

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Не удалось запустить claude");

        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
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

    // Снимаем возможную ```-обёртку вокруг ответа
    private static string CleanupText(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && lastFence > firstNewline)
                text = text[(firstNewline + 1)..lastFence].Trim();
        }
        return text;
    }

    private static IReadOnlyList<string> ParseSubtasks(string raw)
    {
        var text = CleanupText(raw);
        // Ищем JSON-массив в ответе (модель могла добавить текст вокруг)
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start >= 0 && end > start)
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(text[start..(end + 1)]);
                if (list is { Count: > 0 })
                    return list.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            }
            catch (JsonException) { /* фолбэк ниже */ }
        }
        // Фолбэк: строки-список («- …» / «1. …»)
        return text.Split('\n')
            .Select(l => l.Trim().TrimStart('-', '*', '•', ' ').Trim())
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^\d+[.)]\s*", ""))
            .Where(l => l.Length > 2)
            .Take(10)
            .ToList();
    }
}
