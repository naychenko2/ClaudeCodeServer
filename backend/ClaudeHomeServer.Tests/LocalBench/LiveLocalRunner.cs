using System.Diagnostics;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Подмена <see cref="ICheapTextRunner"/>, которая ходит в локальную модель по-настоящему.
/// Встаёт на место продуктового раннера в тех же конструкторах, что и стабы тестов
/// (StubCheapTextRunner, SequencedCheap, CountingCheap), но вместо заготовленной строки
/// возвращает ответ живого движка.
///
/// Зачем так, а не копией промпта в тест: промпт остаётся продуктовым — его строит
/// настоящий сервис места (TaskAiService, ProjectIconGlyphService, NotesAiService…),
/// и замер не протухает при правке промпта в продукте.
///
/// Отличие от продуктового <c>CheapTextRunner</c> ровно одно и намеренное: НЕТ фолбэка на
/// платный claude. Фолбэк скрыл бы отказ локали — то самое, ради измерения чего замер и
/// затеян; вместо него отказ фиксируется в трассе и отдаётся вызывающему пустой строкой,
/// как её видит продуктовый парсер при пустом ответе модели.
///
/// Параметры вызова берутся из профиля места (<see cref="LocalActionCatalog.ProfileDefaults"/>) —
/// того же источника, которым их берёт продуктовый маршрут: потолок вывода NumPredict,
/// окно NumCtx и таймаут профиля. Задавать их отдельно нельзя: замер должен мерить место
/// с его боевыми ограничителями, а не удобные для отчёта.
/// </summary>
public sealed class LiveLocalRunner(LocalBenchStand stand) : ICheapTextRunner
{
    private readonly LlamaServerClient _client = stand.BuildClient();
    private readonly List<LocalBenchShot> _shots = [];
    private readonly List<LocalBenchShot> _all = [];
    private readonly Lock _sync = new();

    public LocalBenchStand Stand { get; } = stand;

    /// <summary>Все вызовы прогона по порядку.</summary>
    public IReadOnlyList<LocalBenchShot> Shots { get { lock (_sync) return _shots.ToArray(); } }

    /// <summary>Последний вызов. null — раннера ещё не звали.</summary>
    public LocalBenchShot? LastShot { get { lock (_sync) return _shots.Count == 0 ? null : _shots[^1]; } }

    /// <summary>Забыть накопленное — между кейсами, чтобы трасса кейса не тянула хвост.</summary>
    public void ResetShots() { lock (_sync) _shots.Clear(); }

    /// <summary>
    /// ВСЕ вызовы прогона, включая прогревочные: <see cref="ResetShots"/> их не трогает.
    /// По ним проверяется, что замер шёл последовательно — перекрывшиеся во времени
    /// вызовы означают очередь в движке, а значит недостоверное время.
    /// </summary>
    public IReadOnlyList<LocalBenchShot> AllShots { get { lock (_sync) return _all.ToArray(); } }

    // Прогрев модели: первый вызов на холодных весах меряет загрузку, а не место.
    public Task WarmUpAsync(CancellationToken ct = default) => _client.WarmUpAsync(Stand.Model, ct);

    public bool UsesLocal(string actionKey) => true;

    public bool HasFreeRoute(string actionKey) => true;

    public string DescribeRoute(string actionKey, string? fallbackModel) =>
        $"kind=local/bench model={Stand.Model}";

    public async Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
        string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
        // Пустая строка вместо отказа: ровно это видит продуктовый парсер места, когда
        // локаль промолчала, — а значит замер проходит через настоящую ветку деградации.
        await RunLocalAsync(actionKey, prompt, jsonFormat, ct) ?? "";

    public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default) =>
        RunLocalAsync(actionKey, prompt, jsonFormat: null, ct);

    public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
        CancellationToken ct = default) =>
        RunLocalAsync(actionKey, prompt, jsonFormat, ct);

    public async Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
        string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
        int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
    {
        var text = await RunLocalAsync(actionKey, prompt, jsonFormat, ct, timeout, maxTokens) ?? "";
        var shot = LastShot;
        // Стоимость локального вызова — ноль, как и в продукте: usage несёт только токены.
        var usage = shot is null ? null : new OneShotUsage(
            shot.PromptTokens, 0, 0, shot.CompletionTokens, CostUsd: 0, Model: Stand.Model);
        return new OneShotResult(text, usage, shot?.DurationMs ?? 0);
    }

    // Единственная точка вызова движка: повторяет локальный шаг CheapTextRunner.RunLocalAsync
    // (свободный текст либо structured output) и записывает трассу.
    private async Task<string?> RunLocalAsync(string actionKey, string prompt, object? jsonFormat,
        CancellationToken ct, TimeSpan? timeoutOverride = null, int? maxTokensOverride = null)
    {
        var spec = ProfileOf(actionKey);
        var numPredict = maxTokensOverride ?? spec.NumPredict;
        var timeoutMs = (int)(timeoutOverride?.TotalMilliseconds ?? spec.TimeoutMs);

        Stand.ResetObserved();
        // Метки монотонных часов процесса: по ним видно, перекрылись ли соседние вызовы.
        var startedAt = Stopwatch.GetTimestamp();
        var sw = Stopwatch.StartNew();
        string? answer = null;
        string? error = null;
        try
        {
            answer = jsonFormat is null
                ? await _client.GenerateTextAsync(prompt, model: null,
                    timeout: TimeSpan.FromMilliseconds(timeoutMs), numPredict: numPredict,
                    numCtx: spec.NumCtx, ownerId: null, label: actionKey, ct)
                : await _client.ChatJsonAsync(systemPrompt: "", userPrompt: prompt, jsonFormat, ct,
                    model: Stand.Model, timeoutMs: timeoutMs, numPredict: numPredict,
                    numCtx: spec.NumCtx, ownerId: null, label: actionKey);
        }
        catch (Exception ex)
        {
            // Клиент глушит свои сбои сам (возвращает null); сюда долетает разве что отмена.
            error = $"{ex.GetType().Name}: {ex.Message}";
        }
        sw.Stop();

        var observed = Stand.LastCall;
        var shot = new LocalBenchShot(
            ActionKey: actionKey,
            Prompt: prompt,
            RawAnswer: answer,
            FinishReason: observed?.FinishReason,
            StatusCode: observed?.StatusCode ?? 0,
            PromptTokens: observed?.PromptTokens ?? 0,
            CompletionTokens: observed?.CompletionTokens ?? 0,
            NumPredict: numPredict,
            DurationMs: (long)sw.Elapsed.TotalMilliseconds,
            Error: error,
            StartedTicks: startedAt,
            FinishedTicks: Stopwatch.GetTimestamp());
        lock (_sync) { _shots.Add(shot); _all.Add(shot); }
        return answer;
    }

    // Профиль места — из каталога, единого источника правды (продуктовый LocalActionRouter
    // берёт оттуда же, добавляя лишь переопределения конфига Ollama:Profiles: в замере их
    // быть не должно, меряется место с дефолтными ограничителями).
    private static CheapProfileSpec ProfileOf(string actionKey)
    {
        var profile = LocalActionCatalog.Find(actionKey)?.Profile ?? CheapProfile.Text;
        return LocalActionCatalog.ProfileDefaults[profile];
    }
}

/// <summary>
/// Трасса одного вызова модели: что спросили, что ответили и чем ответ кончился.
/// <see cref="Truncated"/> — вывод обрезан потолком NumPredict; считается ОТДЕЛЬНО от
/// невалидности: оборванный JSON — молчаливая деградация места, самостоятельный риск.
/// </summary>
public sealed record LocalBenchShot(
    string ActionKey, string Prompt, string? RawAnswer, string? FinishReason, int StatusCode,
    int PromptTokens, int CompletionTokens, int NumPredict, long DurationMs, string? Error,
    long StartedTicks = 0, long FinishedTicks = 0)
{
    public bool Answered => !string.IsNullOrWhiteSpace(RawAnswer);

    public bool Truncated => string.Equals(FinishReason, "length", StringComparison.OrdinalIgnoreCase);
}
