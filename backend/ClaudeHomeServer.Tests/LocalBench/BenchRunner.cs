using System.Diagnostics;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Подмена <see cref="ICheapTextRunner"/>, которая ходит в модель ПО-НАСТОЯЩЕМУ и ведёт
/// трассу вызовов. Встаёт на место продуктового раннера в тех же конструкторах, что и
/// стабы тестов, но вместо заготовленной строки возвращает ответ живой модели.
///
/// Куда ходить — дело наследника: <see cref="LiveLocalRunner"/> идёт в локальный движок,
/// <see cref="LiveCloudRunner"/> — в облако. Всё остальное (замер времени, трасса, шесть
/// членов интерфейса, поведение на отказе) общее и живёт ЗДЕСЬ: разойдись эти две ветки
/// в замере или в деградации — пара «локаль против облака» сравнивала бы не модели, а
/// два разных теста.
///
/// Отличие от продуктового <c>CheapTextRunner</c> ровно одно и намеренное: НЕТ фолбэка на
/// соседнюю модель. Фолбэк скрыл бы отказ — то самое, ради измерения чего замер и затеян;
/// вместо него отказ фиксируется в трассе и отдаётся вызывающему пустой строкой, как её
/// видит продуктовый парсер при пустом ответе модели.
///
/// Параметры вызова берутся из профиля места (<see cref="LocalActionCatalog.ProfileDefaults"/>) —
/// того же источника, которым их берёт продуктовый маршрут: потолок вывода NumPredict,
/// окно NumCtx и таймаут профиля. Задавать их отдельно нельзя: замер должен мерить место
/// с его боевыми ограничителями, а не удобные для отчёта.
/// </summary>
public abstract class BenchRunner : ICheapTextRunner
{
    private readonly List<LocalBenchShot> _shots = [];
    private readonly List<LocalBenchShot> _all = [];
    private readonly Lock _sync = new();

    /// <summary>Кто исполняет ходы — печатается в шапке замера рядом с таблицей.</summary>
    public abstract string Describe { get; }

    /// <summary>Локальный ли исполнитель: место вправе спросить и повести себя иначе.</summary>
    protected abstract bool Local { get; }

    /// <summary>Прогрев: первый вызов на холодной модели меряет загрузку, а не место.</summary>
    public abstract Task WarmUpAsync(CancellationToken ct = default);

    /// <summary>Вызовы ТЕКУЩЕГО кейса по порядку (многоходовое место делает их несколько).</summary>
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

    public bool UsesLocal(string actionKey) => Local;

    public bool HasFreeRoute(string actionKey) => Local;

    public string DescribeRoute(string actionKey, string? fallbackModel) => Describe;

    public async Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
        string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
        // Пустая строка вместо отказа: ровно это видит продуктовый парсер места, когда
        // модель промолчала, — а значит замер проходит через настоящую ветку деградации.
        await CallAsync(actionKey, prompt, jsonFormat, ct) ?? "";

    public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default) =>
        CallAsync(actionKey, prompt, jsonFormat: null, ct);

    public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
        CancellationToken ct = default) =>
        CallAsync(actionKey, prompt, jsonFormat, ct);

    public async Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
        string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
        int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
    {
        var text = await CallAsync(actionKey, prompt, jsonFormat, ct, timeout, maxTokens) ?? "";
        var shot = LastShot;
        var usage = shot is null ? null : new OneShotUsage(
            shot.PromptTokens, 0, 0, shot.CompletionTokens, CostUsd: null, Model: Describe);
        return new OneShotResult(text, usage, shot?.DurationMs ?? 0);
    }

    /// <summary>
    /// Сходить в модель. Отказ отдавать исключением или пустым ответом — база разберётся
    /// с обоими; свои замеры времени и записи в трассу наследник не ведёт.
    /// </summary>
    protected abstract Task<BenchAnswer> CallModelAsync(
        string actionKey, string prompt, object? jsonFormat, CheapProfileSpec profile,
        int numPredict, int timeoutMs, CancellationToken ct);

    // Единственная точка вызова модели на обе ветки: замер, трасса, обработка отказа.
    private async Task<string?> CallAsync(string actionKey, string prompt, object? jsonFormat,
        CancellationToken ct, TimeSpan? timeoutOverride = null, int? maxTokensOverride = null)
    {
        var spec = ProfileOf(actionKey);
        var numPredict = maxTokensOverride ?? spec.NumPredict;
        var timeoutMs = (int)(timeoutOverride?.TotalMilliseconds ?? spec.TimeoutMs);

        // Метки монотонных часов процесса: по ним видно, перекрылись ли соседние вызовы.
        var startedAt = Stopwatch.GetTimestamp();
        var sw = Stopwatch.StartNew();
        BenchAnswer answer;
        try
        {
            answer = await CallModelAsync(actionKey, prompt, jsonFormat, spec, numPredict, timeoutMs, ct);
        }
        catch (Exception ex)
        {
            answer = BenchAnswer.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
        sw.Stop();

        var shot = new LocalBenchShot(
            ActionKey: actionKey,
            Prompt: prompt,
            RawAnswer: answer.Text,
            FinishReason: answer.FinishReason,
            StatusCode: answer.StatusCode,
            PromptTokens: answer.PromptTokens,
            CompletionTokens: answer.CompletionTokens,
            NumPredict: numPredict,
            DurationMs: (long)sw.Elapsed.TotalMilliseconds,
            Error: answer.Error,
            StartedTicks: startedAt,
            FinishedTicks: Stopwatch.GetTimestamp());
        lock (_sync) { _shots.Add(shot); _all.Add(shot); }
        return answer.Text;
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
/// Что модель ответила на один вызов. <paramref name="FinishReason"/> знает не всякий
/// транспорт: у локального движка он приходит из тела ответа, у облака через claude CLI
/// его нет вовсе — там null, и обрыв по потолку вывода такой прогон не различает.
/// </summary>
public sealed record BenchAnswer(
    string? Text, string? FinishReason = null, int StatusCode = 0,
    int PromptTokens = 0, int CompletionTokens = 0, string? Error = null)
{
    public static BenchAnswer Failed(string error) => new(null, Error: error);
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
