using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Исполнитель замера, ходящий в ЛОКАЛЬНУЮ модель (Батарея II, ось A).
///
/// Зачем так, а не копией промпта в тест: промпт остаётся продуктовым — его строит
/// настоящий сервис места (TaskAiService, ProjectIconGlyphService, NotesAiService…),
/// и замер не протухает при правке промпта в продукте.
///
/// Общее с облачной веткой (замер, трасса, поведение на отказе) живёт в базе
/// <see cref="BenchRunner"/>; здесь — только сам поход в движок продуктовым клиентом
/// <see cref="LlamaServerClient"/> и наблюдение за причиной остановки.
/// </summary>
public sealed class LiveLocalRunner(LocalBenchStand stand) : BenchRunner
{
    private readonly LlamaServerClient _client = stand.BuildClient();

    public LocalBenchStand Stand { get; } = stand;

    public override string Describe => $"локаль {Stand.Model} ({Stand.BaseUrl})";

    protected override bool Local => true;

    public override Task WarmUpAsync(CancellationToken ct = default) =>
        _client.WarmUpAsync(Stand.Model, ct);

    // Повторяет локальный шаг CheapTextRunner.RunLocalAsync: свободный текст либо
    // structured output. Клиент глушит свои сбои сам (возвращает null) — пустой ответ
    // здесь и есть отказ локали, ровно как его видит место.
    protected override async Task<BenchAnswer> CallModelAsync(string actionKey, string prompt,
        object? jsonFormat, CheapProfileSpec profile, int numPredict, int timeoutMs, CancellationToken ct)
    {
        Stand.ResetObserved();
        var text = jsonFormat is null
            ? await _client.GenerateTextAsync(prompt, model: null,
                timeout: TimeSpan.FromMilliseconds(timeoutMs), numPredict: numPredict,
                numCtx: profile.NumCtx, ownerId: null, label: actionKey, ct)
            : await _client.ChatJsonAsync(systemPrompt: "", userPrompt: prompt, jsonFormat, ct,
                model: Stand.Model, timeoutMs: timeoutMs, numPredict: numPredict,
                numCtx: profile.NumCtx, ownerId: null, label: actionKey);

        // Наблюдаемости, которой у клиента нет наружу (finish_reason и usage уходят внутрь
        // RecordSpend), стенд добирается перехватом HTTP-ответа.
        var observed = Stand.LastCall;
        return new BenchAnswer(text, observed?.FinishReason, observed?.StatusCode ?? 0,
            observed?.PromptTokens ?? 0, observed?.CompletionTokens ?? 0);
    }
}
