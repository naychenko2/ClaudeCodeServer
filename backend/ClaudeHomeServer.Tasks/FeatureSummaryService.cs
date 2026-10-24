using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Services.Tasks;

// W4-ANB (разрез §4): авторезюме фичи моделью по кнопке. Вход модели — детерминированная
// выжимка FeatureRun (FeatureSummaryDigest), исполнитель — админский выбор в «Поставщиках
// моделей» для места «feature-summary» (по умолчанию локаль). Кэш — память процесса:
// LRU с потолком записей, ключ (owner, featureId, отпечаток выжимки). Тот же отпечаток
// отдаёт кэш без вызова модели (cached: true); ?force=true пересчитывает принудительно.
// Параллельные запросы по одному ключу — single-flight: второй ждёт первый, без второго
// вызова модели. После перезапуска резюме считается заново по кнопке: диск не заводим —
// цена ошибки один дешёвый вызов, а файл в data/ тянет за собой бэкап и схему.
// Отказ модели (таймаут LlmTimeoutException, пустой ответ) — исключение; ручка гасит его
// в честный 502 с причиной.
public sealed class FeatureSummaryService(ICheapTextRunner cheap, IConfiguration config)
{
    private const int CacheCap = 64;

    private readonly Lock _sync = new();
    // key → (отпечаток, результат). _lru — порядок обращения, последний — самый свежий.
    private readonly Dictionary<(string Owner, string FeatureId), (string Fingerprint, FeatureSummaryResult Result)> _cache =
        new();
    private readonly List<(string, string)> _lru = [];
    // Идущий расчёт по ключу (owner, featureId, отпечаток): ожидающие получают тот же результат.
    private readonly Dictionary<(string, string, string), Task<FeatureSummaryResult>> _inFlight = new();

    public async Task<FeatureSummaryResult> SummarizeAsync(
        string ownerId, string featureId, FeatureRun run, bool force, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var (digest, fingerprint) = FeatureSummaryDigest.Build(run);
        var key = (ownerId, featureId);
        var flightKey = (ownerId, featureId, fingerprint);

        Task<FeatureSummaryResult>? flight = null;
        bool started = false;
        lock (_sync)
        {
            if (!force && _cache.TryGetValue(key, out var hit) && hit.Fingerprint == fingerprint)
            {
                Touch(key);
                return hit.Result with { Cached = true };
            }
            if (!_inFlight.TryGetValue(flightKey, out flight))
            {
                // Счётчик событий, на которых построено резюме: хронология run — те же события,
                // что лента страницы фичи (BuildEvents; число не зависит от снимка сессий).
                var eventCount = FeatureProjector.BuildEvents(run, []).Count;
                flight = GenerateAsync(ownerId, featureId, digest, eventCount, ct);
                _inFlight[flightKey] = flight;
                started = true;
            }
        }

        FeatureSummaryResult result;
        try
        {
            result = await flight;
        }
        finally
        {
            // Сбой не мемоизируется: запись снимаем, следующий запрос попробует снова.
            if (started)
                lock (_sync) _inFlight.Remove(flightKey);
        }

        lock (_sync)
        {
            _cache[key] = (fingerprint, result);
            Touch(key);
            while (_lru.Count > CacheCap)
            {
                var oldest = _lru[0];
                _lru.RemoveAt(0);
                _cache.Remove(oldest);
            }
        }
        // Первый вызов — свежий расчёт (cached: false); ожидающий тот же расчёт — не повторный
        // вызов модели (cached: true).
        return started ? result : result with { Cached = true };
    }

    private async Task<FeatureSummaryResult> GenerateAsync(
        string ownerId, string featureId, string digest, int eventCount, CancellationToken ct)
    {
        // Хвост W4-ANB (отчёт Майи): промпт явно требует три раздела — фронт делит ответ по
        // заголовкам «Сделано», «Где застряло», «Дальше» (parseSummary в FeatureAnalysisSummary).
        var prompt =
            "Сделай короткое резюме хода фичи строго по фактам ниже. Ответь тремя разделами " +
            "с заголовками строго «Сделано», «Где застряло», «Дальше» (каждый с новой строки), " +
            "под каждым — пункты markdown. Если в разделе нечего сказать — пункт «—». Без " +
            "выдумывания и без оценок. Ответь ТОЛЬКО этими тремя разделами.\n\n" + digest;
        var res = await cheap.RunDetailedAsync(LocalActionCatalog.FeatureSummary, prompt,
            config["Tasks:AiModel"] ?? "haiku", ownerId, ct: ct);
        if (string.IsNullOrWhiteSpace(res.Text))
            throw new InvalidOperationException("Модель вернула пустой ответ");
        return new FeatureSummaryResult(res.Text, DateTime.UtcNow, res.Usage?.Model, false, res.Usage, eventCount);
    }

    private void Touch((string, string) key)
    {
        _lru.Remove(key);
        _lru.Add(key);
    }
}

// Ответ ручки POST features/{id}/summary: { text, generatedAt, model, cached, usage, eventCount }.
// Model — модель фактического вызова (на локальном пути usage null → model null:
// стоимости нет, модели в смысле тарификации тоже). Cached — результат из кэша или
// того же идущего расчёта, без нового вызова модели. EventCount — число событий
// хронологии, на которых построено резюме (лента событий страницы фичи, BuildEvents):
// для шапки «по N событиям» и тега «устарело» (хвост W4-ANB, отчёт Майи).
public sealed record FeatureSummaryResult(
    string Text, DateTime GeneratedAt, string? Model, bool Cached, OneShotUsage? Usage, int EventCount);
