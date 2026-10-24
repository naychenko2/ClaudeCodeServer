using System.Text;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// W4-ANB (разрез §4): детерминированная выжимка FeatureRun для one-shot вызова «feature-summary».
// Одна чистая функция: из SAME входа строит и выжимку (вход модели), и отпечаток (ключ кэша) —
// риск «в отпечаток не вошло поле, меняющее смысл» закрыт: любое изменение входа меняет выжимку,
// а выжимка — отпечаток. Транскрипты чатов не читаем: дорого и чужие данные для one-shot.
// Потолки: причина витка 200 символов, выжимка целиком MaxDigestChars — вызов должен быть коротким
// (≤ ~4k токенов). Порядок разделов фиксирован: один и тот же run даёт один и тот же текст.
public static class FeatureSummaryDigest
{
    public const int MaxDigestChars = 4000;
    private const int MaxReasonChars = 200;

    // Выжимка + её SHA-256 (hex). Один и тот же run → одна и та же выжимка → один и тот же
    // отпечаток; любое изменение состояния фичи даёт другой отпечаток.
    public static (string Digest, string Fingerprint) Build(FeatureRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var taskCount = 1 + run.Steps.Count + run.Reviews.Count + run.Qa.Count
            + (run.Final is not null ? 1 : 0) + (run.Merge is not null ? 1 : 0)
            + run.Cut.Count + run.Design.Count + run.Plan.Count + run.Unroled.Count;

        var sb = new StringBuilder();
        sb.AppendLine($"Фича: {run.Epic.Title} (id {run.FeatureId})");
        sb.AppendLine($"Состояние: {run.State}; задач: {taskCount}; прогресс по шагам: {run.StepProgress.Accepted} из {run.StepProgress.Total}");
        sb.AppendLine($"Вердикы: принято {run.Verdicts.Accepted}, с замечаниями {run.Verdicts.AcceptedWithRemarks}, " +
                     $"возвращено {run.Verdicts.Returned}, снято {run.Verdicts.Withdrawn}");
        sb.AppendLine($"Последняя активность: {run.LastActivityAt?.ToString("O") ?? "не было"}");
        if (run.Hold is not null)
            sb.AppendLine($"Удержание: {run.Hold.Reason} с {run.Hold.Since.ToString("O")}");

        sb.AppendLine("Стадии:");
        foreach (var stage in run.Stages)
            sb.AppendLine($"- {stage.Key}: {stage.State}" +
                (stage.Tone is not null ? $", тон {stage.Tone}" : "") +
                $", проходов {stage.Passes}, ~{stage.ApproxMinutes} мин");

        sb.AppendLine("Шаги:");
        foreach (var chain in run.StepChains)
        {
            sb.AppendLine($"- {chain.StepRef} ({chain.Title}): {chain.Outcome}, возвратов {chain.Loops}");
            foreach (var round in chain.Rounds)
                sb.AppendLine($"  виток {round.N}: {round.Verdict}" +
                    (round.Reason is not null ? $": {Clip(round.Reason)}" : ""));
        }

        if (run.Attention is not null)
            sb.AppendLine($"Тревога: {run.Attention.Kind} с {run.Attention.Since?.ToString("O") ?? "?"}" +
                (run.Attention.Reason is not null ? $": {Clip(run.Attention.Reason)}" : ""));

        if (run.Summary is not null)
            sb.AppendLine($"Итог: длительность {run.Summary.DurationMinutes} мин, " +
                $"шагов не принято {run.Summary.StepsOpen}, возвратов {run.Summary.Loops}");

        var digest = sb.ToString();
        if (digest.Length > MaxDigestChars)
            digest = digest[..MaxDigestChars];
        return (digest, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(digest))));
    }

    private static string Clip(string text) =>
        text.Length <= MaxReasonChars ? text : text[..MaxReasonChars] + "…";
}
