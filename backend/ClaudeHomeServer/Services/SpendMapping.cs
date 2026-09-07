using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spend;

namespace ClaudeHomeServer.Services;

// Чистые функции сборки SpendRecord из потока сообщений/событий приёма хода. Вынесены из
// SessionManager (этап 4, волна 1 «приём хода», 2026-09-07) — это код спины, использующий
// обе стороны (ISpendCollector подсистемы Spend и LlmProviderRegistry слоя Llm), и держать
// его внутри SessionManager было лишним весом ядра. Никакого состояния, только зависимости
// в параметрах — шов здесь не нужен.
internal static class SpendMapping
{
    // Извлекает request_id из результата вызова, если это генерация fal.ai. Признак fal —
    // наличие request_id И fal-домена где-либо в ответе. Покрывает обе формы результата:
    //  • run_model/submit_job: fal.run в *_url (status_url/response_url/cancel_url);
    //  • get_job_result (видео/аудио): *_url нет, но fal.media в URL медиа.
    public static string? TryExtractFalRequestId(string content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        if (!content.Contains("fal.run") && !content.Contains("fal.ai") && !content.Contains("fal.media")) return null;
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("request_id", out var rid) && rid.ValueKind == JsonValueKind.String)
                return rid.GetString();
            return null;
        }
        catch { return null; } // не JSON / не наш формат — это не fal-результат
    }

    // Запись расхода штатного хода в аналитику (Spend Analytics): все разрезы из Session,
    // модель — фактическая из modelUsage result'а (субагенты могли считать другой моделью),
    // фолбэк — модель сессии. Ошибка записи ход не роняет.
    public static void RecordTurnSpend(
        ISpendCollector? spend,
        LlmProviderRegistry llmProviders,
        Func<Session, string?> resolveOwnerId,
        ILogger? log,
        Session? s,
        ResultMessage m)
    {
        if (spend is null || s is null || m.Usage is null) return;
        try
        {
            var provider = SpendSources.NormalizeProvider(s.Provider);
            // Фактическая модель хода из modelUsage (субагенты могли считать другой), фолбэк —
            // модель сессии; пустой результат резолвится в дефолт подписки, чтобы SpendRecord
            // никогда не оставался без модели (иначе в аналитике копилась «Модель по умолчанию»).
            var model = llmProviders.ResolveModelOrDefault(m.UsageModel ?? s.Model, provider);
            spend.Record(new SpendRecord
            {
                OwnerId = resolveOwnerId(s) ?? "",
                ProjectId = s.ProjectId,
                SessionId = s.Id,
                TaskId = s.TaskId,
                PersonaId = s.PersonaId,
                Provider = provider,
                Model = model,
                Source = SpendSources.IsFree(provider, model) ? SpendSources.Free : SpendSources.ChatTurn,
                InputTokens = m.Usage.InputTokens,
                OutputTokens = m.Usage.OutputTokens,
                CacheReadTokens = m.Usage.CacheReadTokens,
                CacheCreationTokens = m.Usage.CacheCreationTokens,
                CostUsd = m.TotalCostUsd,
                DurationMs = m.DurationMs,
            });
        }
        catch (Exception ex) { log?.LogWarning(ex, "spend: запись хода не удалась"); }
    }

    // Аналитика расхода: генерация fal.ai — счётчик операций (токенов у fal нет),
    // фактическая стоимость про запас. Дедуп выше гарантирует одну запись на request_id.
    // Вызывается уже после снятия _falPersistLock — внешнего состояния не держит.
    public static void RecordFalGeneration(
        ISpendCollector? spend,
        Func<Session, string?> resolveOwnerId,
        ILogger? log,
        Session s,
        FalCostMessage msg)
    {
        if (spend is null) return;
        try
        {
            spend.Record(new SpendRecord
            {
                OwnerId = resolveOwnerId(s) ?? "",
                ProjectId = s.ProjectId,
                SessionId = s.Id,
                TaskId = s.TaskId,
                PersonaId = s.PersonaId,
                Provider = "fal",
                Model = msg.EndpointId,
                Source = SpendSources.Fal,
                CostUsd = msg.CostUsd,
                Generations = 1,
                Label = msg.EndpointId,
            });
        }
        catch (Exception ex) { log?.LogWarning(ex, "spend: запись генерации fal не удалась"); }
    }

    // Аналитика: генерация glif — счётчик операций, кредиты про запас, стоимость USD неизвестна.
    public static void RecordGlifGeneration(
        ISpendCollector? spend,
        Func<Session, string?> resolveOwnerId,
        ILogger? log,
        Session s,
        GlifCostMessage msg)
    {
        if (spend is null) return;
        try
        {
            spend.Record(new SpendRecord
            {
                OwnerId = resolveOwnerId(s) ?? "",
                ProjectId = s.ProjectId,
                SessionId = s.Id,
                TaskId = s.TaskId,
                PersonaId = s.PersonaId,
                Provider = "glif",
                Model = msg.Model ?? msg.OutputType,
                Source = SpendSources.Glif,
                CostUsd = null,
                Generations = 1,
                Label = msg.OutputType,
            });
        }
        catch (Exception ex) { log?.LogWarning(ex, "spend: запись генерации glif не удалась"); }
    }
}