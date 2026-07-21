namespace ClaudeHomeServer.Services.Llm;

// Единая точка «дешёвого» текстового one-shot вызова для фоновых действий. Скрывает от
// потребителя выбор исполнителя: если действие сконфигурировано на локаль (LocalActionRouter)
// и Ollama доступна — идёт бесплатный локальный вызов; при недоступности/ошибке/пустом ответе
// откатывается на существующий путь claude (OneShotClaudeRunner). Действие не на локали —
// сразу claude. Контракт RunAsync намеренно совпадает с прежним прямым вызовом раннера
// (единый prompt → строка ответа), чтобы потребители разбирали ответ теми же парсерами.
public interface ICheapTextRunner
{
    bool UsesLocal(string actionKey);

    // actionKey — ключ из LocalActionCatalog (определяет маршрут и профиль вызова).
    // fallbackModel — модель claude для существующего пути (как раньше читалась из конфига).
    // ownerId — владелец для среды исполнения claude-пути (Ollama ходит по HTTP независимо).
    // jsonFormat — формат ответа для действий со СТРОГИМ JSON-контрактом: строка "json"
    // (просто «отвечай валидным JSON») либо полноценная JSON-схема. Локальный путь тогда идёт
    // в ChatJsonAsync (structured output Ollama) вместо свободной генерации: без этого мелкая
    // модель регулярно оборачивает JSON в прозу, парсер падает и действие всё равно уходит в
    // фолбэк на claude — экономии не возникает. Обычно достаточно "json": форму ответа задаёт
    // текст промпта, а схему пришлось бы держать в синхроне с моделями руками. На claude-путь
    // не влияет (там контракт задаётся промптом, как и раньше).
    Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
        string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default);

    // Только локаль, БЕЗ фолбэка на платный claude. null — локаль выключена/недоступна/пусто.
    // Для необязательных «украшений» (суть уведомления), где платный вызов нежелателен.
    Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default);
}

public sealed class CheapTextRunner(
    LocalActionRouter router, OllamaClient ollama, IOneShotRunner claude,
    ILogger<CheapTextRunner> log) : ICheapTextRunner
{
    public bool UsesLocal(string actionKey) => router.UsesLocal(actionKey);

    // Цепочка одинакова для всех действий: выбранный исполнитель → локальная модель →
    // claude. Последний шаг умышленно без страховки: если упал и он, исключение уходит
    // наверх, и потребитель обрабатывает отказ как раньше (у каждого своя деградация).
    public async Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
        string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
    {
        var route = router.Resolve(actionKey);

        // Шаг 1 — выбранная админом модель конкретного провайдера.
        if (route.Kind == RouteKind.Model && !string.IsNullOrWhiteSpace(route.Model))
        {
            var picked = await TryModelAsync(actionKey, route.Model!, prompt, ownerId, ct);
            if (picked is not null) return picked;
        }

        // Шаг 2 — локальная модель. Идёт и как основной путь (Kind=Local), и как страховка
        // выбранной модели; при Kind=Claude локаль пропускаем, иначе выбор «claude» ничем
        // не отличался бы от «локаль».
        if ((route.Kind is RouteKind.Local or RouteKind.Model) && ollama.Enabled)
        {
            var local = await RunLocalAsync(actionKey, prompt, jsonFormat, ct);
            if (!string.IsNullOrWhiteSpace(local)) return local;
            log.LogDebug("cheap-runner: действие {Action} — фолбэк с Ollama на claude", actionKey);
        }

        // Шаг 3 — claude с моделью действия по умолчанию.
        return await claude.RunAsync(prompt, claude.NormalizeModel(fallbackModel), ct: ct, ownerId: ownerId);
    }

    // Вызов выбранной модели. null — шаг не удался (провайдер не настроен, ошибка CLI,
    // таймаут или пустой ответ), вызывающий идёт дальше по цепочке. Отмену ct наверх НЕ
    // проглатываем: это не сбой модели, а осознанный обрыв — фолбэчить по нему бессмысленно.
    private async Task<string?> TryModelAsync(string actionKey, string model, string prompt,
        string? ownerId, CancellationToken ct)
    {
        try
        {
            var text = await claude.RunAsync(prompt, model, ct: ct, ownerId: ownerId);
            if (!string.IsNullOrWhiteSpace(text)) return text;
            log.LogDebug("cheap-runner: действие {Action} — модель {Model} вернула пустой ответ", actionKey, model);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "cheap-runner: действие {Action} — модель {Model} недоступна, иду дальше по цепочке",
                actionKey, model);
        }
        return null;
    }

    private Task<string?> RunLocalAsync(string actionKey, string prompt, object? jsonFormat, CancellationToken ct)
    {
        var spec = router.ProfileFor(actionKey);
        return jsonFormat is null
            ? ollama.GenerateTextAsync(
                prompt, model: null, timeout: TimeSpan.FromMilliseconds(spec.TimeoutMs),
                numPredict: spec.NumPredict, numCtx: spec.NumCtx, ct)
            : ollama.ChatJsonAsync(
                systemPrompt: "", userPrompt: prompt, formatSchema: jsonFormat, ct,
                model: ollama.TextModel, timeoutMs: spec.TimeoutMs,
                numPredict: spec.NumPredict, numCtx: spec.NumCtx);
    }

    public async Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default)
    {
        if (!router.UsesLocal(actionKey)) return null;
        var spec = router.ProfileFor(actionKey);
        var local = await ollama.GenerateTextAsync(
            prompt, model: null, timeout: TimeSpan.FromMilliseconds(spec.TimeoutMs),
            numPredict: spec.NumPredict, numCtx: spec.NumCtx, ct);
        return string.IsNullOrWhiteSpace(local) ? null : local;
    }
}
