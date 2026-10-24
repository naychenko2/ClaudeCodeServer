using System.Text;
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
// в честный 502 с причиной. Формат ответа гарантируется здесь: без трёх заголовков ответ
// приводится к структуре (хвост W4-ANB, вердикт W4-R-ANB3).
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
                // Хвост W4-ANB (вердикт W4-R-ANB3): счётчик пересчитывается по живому run.
                // Кэш хранит текст, а не число: хронология может вырасти без смены отпечатка
                // (DroppedByHumanAt/ExecutorStoppedAt в выжимку не входят) — шапка страницы
                // «по N событиям» считается живым BuildEvents, и ответ ручки обязан совпадать.
                return hit.Result with { Cached = true, EventCount = FeatureProjector.BuildEvents(run, []).Count };
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
        // вызов модели (cached: true). Счётчик событий ожидающий пересчитывает по своему run:
        // число может вырасти без смены отпечатка (вердикт W4-R-ANB5, хвост-4), как при кэш-хите.
        return started ? result
            : result with { Cached = true, EventCount = FeatureProjector.BuildEvents(run, []).Count };
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
        // Хвост W4-ANB (вердикт W4-R-ANB3): формат ответа не проверялся на бэкенде — ответ без
        // заголовков («Сделано (3 пункта)») фронтный parseSummary схлопывает в одну колонку.
        // Страховка здесь: три заголовка гарантируются до ответа фронту.
        return new FeatureSummaryResult(EnsureSections(res.Text), DateTime.UtcNow, res.Usage?.Model, false, res.Usage, eventCount);
    }

    // Заголовки разделов — те же, что у фронтного parseSummary (FeatureAnalysisSummary.tsx):
    // строка считается заголовком, если после снятия «#», «*», «:» она равна ключу (без регистра).
    private static readonly string[] SectionKeys = ["сделано", "где застряло", "дальше"];
    private static readonly string[] SectionTitles = ["Сделано", "Где застряло", "Дальше"];

    private static bool IsHeading(string line, string key)
    {
        var s = line.Trim();
        if (s.Length == 0) return false;
        var stripped = new StringBuilder(s.Length);
        foreach (var c in s)
            if (c != '#' && c != '*' && c != ':') stripped.Append(c);
        return stripped.ToString().Trim().ToLowerInvariant() == key;
    }

    // Хвост W4-ANB (вердикты W4-R-ANB3, W4-R-ANB5): страховка формата. Ответ с тремя
    // заголовками — как есть; иначе он приводится к структуре: найденные заголовки
    // разбираются по позициям — содержимое раздела строится от его заголовка до следующего
    // найденного; недостающие разделы вставляются по порядку ключей целиком (заголовок +
    // «—»), никогда между заголовком и его пунктами. Текст до первого заголовка становится
    // содержимым «Сделано»: фронт любой текст до первого заголовка теряет (parseSummary
    // в FeatureAnalysisSummary), поэтому его надо поставить под заголовок.
    private static string EnsureSections(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var found = new int[SectionKeys.Length];
        for (int i = 0; i < found.Length; i++) found[i] = -1;
        for (int i = 0; i < lines.Length; i++)
            for (int k = 0; k < SectionKeys.Length; k++)
                if (found[k] < 0 && IsHeading(lines[i], SectionKeys[k])) found[k] = i;
        if (Array.TrueForAll(found, f => f >= 0)) return text;

        // Позиция первого найденного заголовка; заголовков нет — конец текста.
        int first = lines.Length;
        for (int k = 0; k < SectionKeys.Length; k++)
            if (found[k] >= 0 && found[k] < first) first = found[k];

        var sb = new StringBuilder();
        for (int k = 0; k < SectionKeys.Length; k++)
        {
            sb.AppendLine(SectionTitles[k]);
            bool hasContent = false;
            if (k == 0)
            {
                // Преамбула до первого заголовка — содержимое «Сделано».
                for (int i = 0; i < first; i++) sb.AppendLine(lines[i]);
                hasContent = first > 0;
            }
            if (found[k] >= 0)
            {
                // Содержимое раздела: строки от его заголовка до следующего найденного.
                int end = lines.Length;
                for (int j = found[k] + 1; j < lines.Length; j++)
                    for (int m = 0; m < SectionKeys.Length; m++)
                        if (found[m] == j) { end = j; break; }
                for (int i = found[k] + 1; i < end; i++) sb.AppendLine(lines[i]);
                hasContent = true;
            }
            if (!hasContent) sb.AppendLine("—");
        }
        return sb.ToString();
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
// для шапки «по N событиям» и тега «устарело» (хвост W4-ANB, отчёт Майи). В кэше хранится
// текст, не счётчик: при кэш-хите число пересчитывается по живому run (хвост W4-ANB).
public sealed record FeatureSummaryResult(
    string Text, DateTime GeneratedAt, string? Model, bool Cached, OneShotUsage? Usage, int EventCount);
