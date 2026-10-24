using System.Text;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// W4-ANB (разрез §4): детерминированная выжимка FeatureRun для one-shot вызова «feature-summary».
// Одна чистая функция: из SAME входа строит и выжимку (вход модели), и отпечаток (ключ кэша).
// Транскрипты чатов не читаем: дорого и чужие данные для one-shot.
// Порядок разделов фиксирован: состояние и тревога → итог → стадии → шаги. «Тревога» и «Итог»
// идут ДО «Шагов», а «Шаги» ограничены последними MaxStepChainsShown цепочками: при длинной
// истории шагов модель видит тревогу и итог, а не только голову истории (вердикт W4-R-ANB, MAJOR-1).
// Отпечаток считается по полному тексту (все цепочки, без обрезки): правка состояния в хвосте
// истории инвалидирует кэш, даже если в выжимку этот хвост не попал (MAJOR-2). Обрезка — по
// секциям: голова выжимки сохраняется целиком, «Шаги» обрезаются до остатка бюджета.
// Потолки: причина витка 200 символов, выжимка целиком MaxDigestChars — вызов должен быть коротким
// (≤ ~4k токенов). Один и тот же run даёт один и тот же текст и один и тот же отпечаток.
public static class FeatureSummaryDigest
{
    public const int MaxDigestChars = 4000;
    private const int MaxReasonChars = 200;
    // «Шаги» в выжимку: только последние N цепочек, чтобы хвост истории не резался слепым [..MaxDigestChars].
    private const int MaxStepChainsShown = 10;

    // Выжимка (для модели) + SHA-256 полного текста (hex, ключ кэша). Один и тот же run → тот же
    // отпечаток; любое изменение состояния фичи, вплоть до хвоста истории шагов, даёт другой.
    public static (string Digest, string Fingerprint) Build(FeatureRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var taskCount = 1 + run.Steps.Count + run.Reviews.Count + run.Qa.Count
            + (run.Final is not null ? 1 : 0) + (run.Merge is not null ? 1 : 0)
            + run.Cut.Count + run.Design.Count + run.Plan.Count + run.Unroled.Count;

        // Голова: текущее состояние, тревога и итог — всегда в выжимке, до «Шагов».
        var head = new StringBuilder();
        head.AppendLine($"Фича: {run.Epic.Title} (id {run.FeatureId})");
        head.AppendLine($"Состояние: {run.State}; задач: {taskCount}; прогресс по шагам: {run.StepProgress.Accepted} из {run.StepProgress.Total}");
        head.AppendLine($"Вердикы: принято {run.Verdicts.Accepted}, с замечаниями {run.Verdicts.AcceptedWithRemarks}, " +
                       $"возвращено {run.Verdicts.Returned}, снято {run.Verdicts.Withdrawn}");
        head.AppendLine($"Последняя активность: {run.LastActivityAt?.ToString("O") ?? "не было"}");
        if (run.Hold is not null)
            head.AppendLine($"Удержание: {run.Hold.Reason} с {run.Hold.Since.ToString("O")}");

        if (run.Attention is not null)
            head.AppendLine($"Тревога: {run.Attention.Kind} с {run.Attention.Since?.ToString("O") ?? "?"}" +
                (run.Attention.Reason is not null ? $": {Clip(run.Attention.Reason)}" : ""));

        if (run.Summary is not null)
            head.AppendLine($"Итог: длительность {run.Summary.DurationMinutes} мин, " +
                $"шагов не принято {run.Summary.StepsOpen}, возвратов {run.Summary.Loops}");

        head.AppendLine("Стадии:");
        foreach (var stage in run.Stages)
            head.AppendLine($"- {stage.Key}: {stage.State}" +
                (stage.Tone is not null ? $", тон {stage.Tone}" : "") +
                $", проходов {stage.Passes}, ~{stage.ApproxMinutes} мин");

        // Отпечаток — по полному тексту: все цепочки, без обрезки и без лимита «Шагов».
        var full = new StringBuilder(head.ToString());
        AppendChains(full, run.StepChains);
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(full.ToString())));

        // Выжимка для модели: голова + «Шаги» последними N цепочками, до остатка бюджета.
        var steps = new StringBuilder("Шаги:");
        if (run.StepChains.Count > MaxStepChainsShown)
        {
            steps.AppendLine($"- … {run.StepChains.Count - MaxStepChainsShown} ранних цепочек опущено");
            AppendChains(steps, run.StepChains.Slice(run.StepChains.Count - MaxStepChainsShown, MaxStepChainsShown));
        }
        else
        {
            AppendChains(steps, run.StepChains);
        }

        var headText = head.ToString();
        var stepsText = steps.ToString();
        var digest = headText + stepsText;
        if (digest.Length > MaxDigestChars)
            digest = headText.Length < MaxDigestChars
                ? headText + stepsText.Substring(0, MaxDigestChars - headText.Length - 1) + "…"
                : headText.Substring(0, MaxDigestChars);
        return (digest, fingerprint);
    }

    private static void AppendChains(StringBuilder sb, List<FeatureStepChain> chains)
    {
        foreach (var chain in chains)
        {
            sb.AppendLine($"- {chain.StepRef} ({chain.Title}): {chain.Outcome}, возвратов {chain.Loops}");
            foreach (var round in chain.Rounds)
                sb.AppendLine($"  виток {round.N}: {round.Verdict}" +
                    (round.Reason is not null ? $": {Clip(round.Reason)}" : ""));
        }
    }

    private static string Clip(string text) =>
        text.Length <= MaxReasonChars ? text : text[..MaxReasonChars] + "…";
}
