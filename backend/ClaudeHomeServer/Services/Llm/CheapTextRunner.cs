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
    Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
        string? ownerId = null, CancellationToken ct = default);

    // Только локаль, БЕЗ фолбэка на платный claude. null — локаль выключена/недоступна/пусто.
    // Для необязательных «украшений» (суть уведомления), где платный вызов нежелателен.
    Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default);
}

public sealed class CheapTextRunner(
    LocalActionRouter router, OllamaClient ollama, IOneShotRunner claude,
    ILogger<CheapTextRunner> log) : ICheapTextRunner
{
    public bool UsesLocal(string actionKey) => router.UsesLocal(actionKey);

    public async Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
        string? ownerId = null, CancellationToken ct = default)
    {
        if (router.UsesLocal(actionKey))
        {
            var spec = router.ProfileFor(actionKey);
            var local = await ollama.GenerateTextAsync(
                prompt, model: null, timeout: TimeSpan.FromMilliseconds(spec.TimeoutMs),
                numPredict: spec.NumPredict, numCtx: spec.NumCtx, ct);
            if (!string.IsNullOrWhiteSpace(local)) return local;
            // null = Ollama недоступна/ошибка/пусто → не роняем фичу, идём на claude
            log.LogDebug("cheap-runner: действие {Action} — фолбэк с Ollama на claude", actionKey);
        }

        return await claude.RunAsync(prompt, claude.NormalizeModel(fallbackModel), ct: ct, ownerId: ownerId);
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
