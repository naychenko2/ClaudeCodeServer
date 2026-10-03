using System.Text;

namespace ClaudeHomeServer.Services.TestRuns;

// Исход сборки — общий для движков `dotnet build` и `npm run`. Refusal — не начиналась;
// Cancelled — «Стоп» или обрыв вызова CLI; TimedOut — серверный потолок; NeverStarted —
// оборвано в очереди сборок, процесса не было
public sealed record BuildRunResult(string? Refusal, int? ExitCode, bool Cancelled, bool TimedOut,
    TimeSpan Elapsed, IReadOnlyList<string> Tail, string? TurnId = null)
{
    public bool NeverStarted { get; init; }

    // Готовых единиц (проектов у dotnet, этапов у npm) и их общее число; null — неизвестно
    public int Done { get; init; }
    public int? Total { get; init; }

    // Подпись последнего прогресса («12 из 40 проектов», «vite build · этап 2 из 6») — для итога на обрыве
    public string? ProgressLabel { get; init; }

    // Первые ошибки сборки без повторов (только при ненулевом коде)
    public IReadOnlyList<string> BuildErrors { get; init; } = [];

    // Папка артефактов ОТНОСИТЕЛЬНО рабочего дерева (console.log)
    public string? ArtifactsPath { get; init; }

    public static BuildRunResult Refused(string reason) => new(reason, null, false, false, TimeSpan.Zero, []);
}

// Текст итога сборки для модели
public static class BuildRunSummary
{
    public static string Format(BuildRunResult r, int maxBytes)
    {
        if (r.Refusal is { } refusal) return "Сборка не запускалась: " + refusal;
        var text = new StringBuilder();
        var took = TestRunSummaryFormatter.FormatElapsed(r.Elapsed);
        text.Append(r switch
        {
            { NeverStarted: true, Cancelled: true } => "Сборка отменена в очереди сборок — процесс не запускался.",
            { NeverStarted: true } => "Сборка не дождалась очереди сборок до потолка — процесс не запускался.",
            { Cancelled: true } => $"Сборка остановлена ({r.ProgressLabel}, {took}) — процесс погашен.",
            { TimedOut: true } => $"Сборка оборвана по потолку ({r.ProgressLabel}, {took}). Повтори: следующий вызов будет инкрементальным.",
            { ExitCode: 0 } => $"Собрано за {took}: {r.ProgressLabel}, 0 ошибок.",
            _ => $"Сборка упала (код {r.ExitCode?.ToString() ?? "?"}, {took}): {r.ProgressLabel}.",
        });
        if (r.BuildErrors.Count > 0)
        {
            text.Append($"\n\nПервые ошибки сборки ({r.BuildErrors.Count}):");
            foreach (var error in r.BuildErrors) text.Append('\n').Append(error);
        }
        else if (r.ExitCode is not 0 && !r.NeverStarted && r.Tail.Count > 0)
        {
            text.Append("\n\nХвост вывода:\n").Append(string.Join('\n', r.Tail));
        }
        if (r.ArtifactsPath is { } path) text.Append($"\n\nПолный лог: {path}/console.log");
        return Clamp(text.ToString(), maxBytes);
    }

    private static string Clamp(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
        const string cut = "\n…(обрезано, полный лог — в папке артефактов)";
        var budget = maxBytes - Encoding.UTF8.GetByteCount(cut);
        var length = Math.Min(text.Length, Math.Max(budget, 0));
        while (length > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, length)) > budget) length--;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + cut;
    }
}
