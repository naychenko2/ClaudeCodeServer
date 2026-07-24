using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Продуктовая история: сводит git-коммиты СО ВСЕХ проектов в человеческую
/// сводку по дням — что нового и чем это полезно пользователю (без кода/diff).
/// Суммаризация — батчем по дню (1 вызов Claude на день), результат кешируется
/// в data/changelog/product.json на уровне продукта (ключ дня = хеш sha-набора
/// всех проектов) — сводка одна для всех и перегенерируется только при новых
/// коммитах дня.
/// </summary>
public class ChangelogService(FileService files, IConfiguration config, ILogger<ChangelogService> logger,
    Llm.ICheapTextRunner cheap)
{
    // Длинному JSON-ответу сводки дня (до 12 пунктов) профильного лимита вывода мало —
    // задаём свой большой maxTokens (на claude-путь не влияет: там лимит не ограничиваем)
    private const int ChangelogMaxTokens = 8192;

    private readonly string _cacheDir = Path.Combine(
        Path.GetDirectoryName(config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data"),
        "changelog");

    private readonly string _model = config["Changelog:Model"] ?? "haiku";
    // Запас на самый жирный день. Старый замер (59 коммитов за ~141 с) больше не актуален:
    // 18.07.2026 день из 35 коммитов занял 194 с, а 58 коммитов 19.07 не уложились и в 480 с —
    // на коммит стало примерно вдвое медленнее. Отсюда 900 с; причина замедления не выяснена,
    // так что это запас, а не расчет. Не уложились — день уходит в fallback (сырые subject'ы).
    private readonly int _claudeTimeoutMs = int.TryParse(config["Changelog:TimeoutMs"], out var t) ? t : 900_000;

    // Источник changelog. Если задан SourceRepoPath — «Что нового» строится ТОЛЬКО из этой
    // репы (changelog самого продукта, одинаковый для всех, независимо от проектов юзера).
    // Пусто — прежнее поведение: агрегация по всем проектам инстанса. Путь машинно-специфичный
    // (у каждого своя папка) — задаётся в appsettings.Local.json.
    private readonly string? _sourceRepoPath = config["Changelog:SourceRepoPath"];
    private readonly string? _sourceProjectName = config["Changelog:SourceProjectName"];

    // Алиасы авторов: git-email → отображаемое имя (иначе светится git-ник вроде depeche81)
    private readonly IReadOnlyDictionary<string, string> _authorAliases =
        config.GetSection("Changelog:AuthorAliases").GetChildren()
            .Where(c => c.Value is not null)
            .ToDictionary(c => c.Key, c => c.Value!, StringComparer.OrdinalIgnoreCase);

    // Генерация одного дня может идти долго — не даём параллельным запросам дублировать вызов Claude
    private readonly SemaphoreSlim _generateLock = new(1, 1);
    // Файловый I/O кеша под отдельным локом (НЕ под _generateLock: тот держится минутами)
    private readonly object _cacheFileLock = new();

    // Короткий кеш собранных коммитов всех проектов (сбор дергается на каждый день окна)
    private (List<GitCommitRaw> Commits, long ExpiresAt)? _rawCache;
    private readonly object _rawCacheLock = new();

    private const string CacheFile = "product";

    // Degraded/DegradedReason/Generation — опциональные: старые записи кеша (без этих
    // полей) читаются как «сводка нормальная, расход неизвестен», формат обратно совместим
    private sealed record CachedDay(string ShasHash, List<ChangelogItem> Items,
        bool Degraded = false, string? DegradedReason = null,
        ChangelogGeneration? Generation = null);

    // ===== Публичное API =====

    /// <summary>Список дней с коммитами за окно по всем проектам (мгновенно, без LLM).</summary>
    public List<DaySummaryStub> GetDays(int sinceDays)
    {
        var commits = GetCommitsInWindow(sinceDays);
        var cache = LoadCache();
        return commits
            .GroupBy(c => DayKey(c.Date))
            .OrderByDescending(g => g.Key)
            .Select(g => new DaySummaryStub(
                g.Key,
                g.Count(),
                cache.TryGetValue(g.Key, out var cached) && cached.ShasHash == ShasHash(g)))
            .ToList();
    }

    /// <summary>
    /// Кандидаты на фоновый прогрев за окно (для ChangelogWarmupService, без LLM).
    /// Cached — как в GetDays: запись в кеше с совпадающим хешем sha-набора
    /// (degraded-дни с совпадающим хешем тоже cached — их не перегенерируем).
    /// </summary>
    public List<WarmupCandidate> GetWarmupCandidates(int sinceDays)
    {
        var commits = GetCommitsInWindow(sinceDays);
        var cache = LoadCache();
        return commits
            .GroupBy(c => DayKey(c.Date))
            .OrderByDescending(g => g.Key)
            .Select(g => new WarmupCandidate(
                g.Key,
                cache.TryGetValue(g.Key, out var cached) && cached.ShasHash == ShasHash(g),
                g.Max(c => c.Date)))
            .ToList();
    }

    /// <summary>Продуктовая сводка одного дня: из кеша либо генерация через Claude.</summary>
    public async Task<ChangelogDay> GetDay(string date)
    {
        var dayCommits = GetAllCommits()
            .Where(c => DayKey(c.Date) == date)
            .OrderByDescending(c => c.Date)
            .ToList();
        if (dayCommits.Count == 0) return new ChangelogDay(date, []);

        var hash = ShasHash(dayCommits);
        var cache = LoadCache();
        if (cache.TryGetValue(date, out var cached) && cached.ShasHash == hash)
            return new ChangelogDay(date, cached.Items, cached.Degraded, cached.DegradedReason, cached.Generation);

        await _generateLock.WaitAsync();
        try
        {
            // Перепроверка под замком: пока ждали, другой запрос мог сгенерировать
            cache = LoadCache();
            if (cache.TryGetValue(date, out cached) && cached.ShasHash == hash)
                return new ChangelogDay(date, cached.Items, cached.Degraded, cached.DegradedReason, cached.Generation);

            var (items, error, generation) = await SummarizeDay(dayCommits);
            // Сводки нет — показываем сырые коммиты, но ЧЕСТНО помечаем это, а не выдаём
            // их за настоящую сводку (иначе поломка выглядит как «плохо сгенерилось»)
            var degraded = items is null;
            var reason = degraded ? DescribeFailure(error) : null;
            items ??= FallbackItems(dayCommits);

            cache[date] = new CachedDay(hash, items, degraded, reason, generation);
            SaveCache(cache);
            return new ChangelogDay(date, items, degraded, reason, generation);
        }
        finally { _generateLock.Release(); }
    }

    /// <summary>Сколько коммитов во всех проектах новее since (для бейджа).</summary>
    public int GetNewCommitCount(DateTimeOffset since) =>
        GetAllCommits().Count(c => c.Date > since);

    /// <summary>Настроен ли источник changelog — чтобы фронт показал подсказку донастроить.</summary>
    public ChangelogStatus GetStatus()
    {
        if (string.IsNullOrWhiteSpace(_sourceRepoPath))
            return new ChangelogStatus(false, "repo",
                "Источник не настроен. Укажите git-репозиторий продукта в Changelog:SourceRepoPath в appsettings.");

        // Путь должен существовать и быть git-репой
        var gitDir = Path.Combine(_sourceRepoPath, ".git");
        var valid = Directory.Exists(_sourceRepoPath) && (Directory.Exists(gitDir) || File.Exists(gitDir));
        return new ChangelogStatus(valid, "repo", valid
            ? null
            : $"Источник задан, но путь не найден или это не git-репозиторий: {_sourceRepoPath}. Проверьте Changelog:SourceRepoPath в appsettings.");
    }

    /// <summary>Сбросить кеш одного дня — при следующем GetDay он сгенерится заново.</summary>
    public void InvalidateDay(string date)
    {
        var cache = LoadCache();
        if (cache.Remove(date)) SaveCache(cache);
        InvalidateRawCache();
    }

    /// <summary>Полностью очистить кеш продуктовой истории (все дни).</summary>
    public void ClearAll()
    {
        lock (_cacheFileLock)
        {
            try { var p = CachePath(); if (File.Exists(p)) File.Delete(p); } catch { /* нет файла — уже пусто */ }
        }
        InvalidateRawCache();
    }

    // Сбросить короткий кеш собранных коммитов — чтобы регенерация взяла свежие данные
    private void InvalidateRawCache()
    {
        lock (_rawCacheLock) { _rawCache = null; }
    }

    // ===== Сбор коммитов со всех проектов =====

    private List<GitCommitRaw> GetCommitsInWindow(int sinceDays)
    {
        var cutoff = DateTimeOffset.Now.Date.AddDays(-sinceDays + 1);
        return GetAllCommits().Where(c => c.Date.LocalDateTime >= cutoff).ToList();
    }

    // Все коммиты всех проектов (с коротким кешем — сбор дергается на каждый день)
    private List<GitCommitRaw> GetAllCommits()
    {
        var now = Environment.TickCount64;
        lock (_rawCacheLock)
        {
            if (_rawCache is { } c && c.ExpiresAt > now) return c.Commits;
        }
        var all = new List<GitCommitRaw>();
        if (!string.IsNullOrWhiteSpace(_sourceRepoPath))
        {
            // Единственный источник — репа продукта из конфига (Changelog:SourceRepoPath)
            var name = string.IsNullOrWhiteSpace(_sourceProjectName)
                ? Path.GetFileName(_sourceRepoPath.TrimEnd('/', '\\'))
                : _sourceProjectName;
            all.AddRange(files.GetCommitsRaw(_sourceRepoPath, name, limit: 2000, _authorAliases));
        }
        lock (_rawCacheLock)
        {
            _rawCache = (all, now + 15_000); // TTL 15 секунд
        }
        return all;
    }

    private static string DayKey(DateTimeOffset date) => date.LocalDateTime.ToString("yyyy-MM-dd");

    private static string ShasHash(IEnumerable<GitCommitRaw> commits)
    {
        var joined = string.Join(",", commits.Select(c => c.Sha).OrderBy(s => s, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16];
    }

    // ===== Суммаризация через Claude =====

    /// <summary>
    /// Сводка дня — один вызов claude на все коммиты дня. Дробить день на параллельные
    /// чанки пробовали: выходит МЕДЛЕННЕЕ (старт CLI ~15 с платится за каждый вызов) и хуже
    /// группируется (чанки не видят друг друга и дробят смысл). Замер на 59 коммитах:
    /// один вызов — 141 с / 13 пунктов, три чанка — 182 с / 29 пунктов.
    /// От переполнения таймаута страхует Changelog:TimeoutMs, а не батчинг.
    /// </summary>
    /// <returns>Пункты сводки, либо null + причина сбоя (для честной пометки дня), плюс расход вызова.</returns>
    private async Task<(List<ChangelogItem>? Items, string? Error, ChangelogGeneration? Generation)> SummarizeDay(
        List<GitCommitRaw> commits, CancellationToken ct = default)
    {
        var knownAreas = KnownAreas();
        var (json, error, generation) = await TryRunClaude(BuildPrompt(commits, knownAreas), ct);
        // Расход отдаём и при неудачном разборе: токены на неудачный вызов всё равно потрачены
        if (json is null) return (null, error, generation);

        var items = ParseAndClean(json);
        return items is null
            ? (null, "модель вернула ответ, который не удалось разобрать", generation)
            : (NormalizeAreas(items), null, generation);
    }

    // Техническую ошибку переводим в человеческое «что случилось и что делать».
    // Самый частый и самый коварный случай — истёкший логин CLI: он выглядит как
    // «сводка плохо сгенерилась», хотя claude вообще не отвечал.
    internal static string DescribeFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "Не удалось собрать сводку: AI не ответил.";

        if (error.Contains("Not logged in", StringComparison.OrdinalIgnoreCase)
            || error.Contains("/login", StringComparison.OrdinalIgnoreCase))
            return "AI CLI не залогинен, поэтому сводка не собрана — показаны сырые коммиты. "
                 + "Выполните «claude auth login» либо задайте переменную CLAUDE_CODE_OAUTH_TOKEN "
                 + "(claude setup-token) в окружении сервера.";

        if (error.Contains("не ответил за отведённое время", StringComparison.OrdinalIgnoreCase))
            return "AI не уложился в отведённое время — показаны сырые коммиты. "
                 + "Попробуйте обновить сводку или увеличьте Changelog:TimeoutMs.";

        return $"Не удалось собрать сводку — показаны сырые коммиты. Причина: {error}";
    }

    // Частые области из уже собранных дней — подсказка модели, чтобы области не разъезжались
    // между днями («Заметки» vs «Раздел Заметки», «Интерфейс» vs «Интерфейс чата»)
    private List<string> KnownAreas() =>
        [.. LoadCache().Values
            .SelectMany(d => d.Items)
            .Select(i => i.Area)
            .Where(a => !string.IsNullOrWhiteSpace(a) && a != "Прочее")
            .GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(15)
            .Select(g => g.First())];

    // Канонизация областей: совпадающие без учёта регистра/пробелов схлопываются в первое
    // встреченное написание. Вход — уже склеенный в порядке чанков список, поэтому детерминировано.
    internal static List<ChangelogItem> NormalizeAreas(List<ChangelogItem> items)
    {
        var canon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return [.. items.Select(i =>
        {
            var key = (i.Area ?? "").Trim();
            if (key.Length == 0) return i with { Area = "Прочее" };
            if (!canon.TryGetValue(key, out var c)) canon[key] = c = key;
            return i with { Area = c };
        })];
    }

    private static string BuildPrompt(IEnumerable<GitCommitRaw> commits, IReadOnlyCollection<string> knownAreas)
    {
        var input = new StringBuilder();
        foreach (var c in commits)
        {
            input.AppendLine($"- проект: {c.Project}");
            input.AppendLine($"  автор: {c.Author}");
            input.AppendLine($"  изменение: {c.Subject}");
            if (!string.IsNullOrWhiteSpace(c.Body))
            {
                // Обрезаем тело — на днях с 50+ коммитами и длинными телами промпт
                // раздувается, и claude не укладывается в таймаут (падал в fallback).
                // Пометка [сокращено] вместо голого «…»: обрыв на полуслове модель
                // принимала за битый ввод и просила «полный список» вместо JSON
                // (воспроизводилось на дне с единственным коммитом)
                var body = c.Body.ReplaceLineEndings(" ");
                if (body.Length > 200) body = body[..200] + "… [сокращено]";
                input.AppendLine($"  подробности: {body}");
            }
        }

        // Подсказка уже используемыми областями: выравнивает area между днями
        // («Заметки» vs «Раздел Заметки»). Пустой кеш → строки просто нет.
        var areasHint = knownAreas.Count > 0
            ? $"\n            - ПРЕДПОЧИТАЙ уже используемые названия областей, если пункт подходит\n              под одну из них (не выдумывай новую): {string.Join(", ", knownAreas)};"
            : "";

        return $$"""
            Ты ведешь продуктовый дневник изменений для пользователей продукта. Ниже —
            git-коммиты за один день по разным проектам. Твоя задача — рассказать
            ЧЕЛОВЕЧЕСКИМ языком по-русски, ЧТО нового появилось и ЧЕМ это полезно
            пользователю, как будто пишешь заметку «что улучшилось».

            ВАЖНО:
            - данные полные: длинные подробности коммитов НАМЕРЕННО сокращены при
              подготовке (пометка [сокращено]) — это не обрыв ввода. НИКОГДА не проси
              дополнить данные и не задавай вопросов: при любом количестве коммитов
              (даже одном) отвечай ТОЛЬКО JSON-массивом по имеющемуся;
            - пиши с точки зрения ПОЛЬЗЫ для пользователя, а не с точки зрения кода;
            - НЕ упоминай файлы, классы, функции, технический жаргон, рефакторинг;
            - НИЧЕГО не выбрасывай: каждый коммит должен войти РОВНО в один пункт.
              Даже чисто техническое изменение (сборка, конфиг, рефакторинг) включи —
              опиши его коротко и по-человечески (например "улучшения под капотом");
            - группируй БЛИЗКО родственные коммиты в один пункт, но не сваливай все в кучу —
              разные по смыслу изменения должны быть разными пунктами;
            - ВСЕГО не больше 12 пунктов за день. Коммитов много — группируй агрессивнее
              (несколько правок одной фичи = один пункт), а не плоди мелкие пункты;
            - для каждого пункта определи ОБЛАСТЬ (area) — раздел/часть продукта, которого
              касается изменение, короткое человеческое название (например «Артефакты сессии»,
              «Календарь», «Чат», «История проекта», «Файлы», «Уведомления», «Настройки»).
              Пункты про одно и то же должны иметь ОДИНАКОВУЮ область (дословно), чтобы
              сгруппироваться. Не выдумывай новую область, если подходит уже названная;{{areasHint}}
            - подбери каждому пункту подходящую по СМЫСЛУ эмодзи (например 🔔 для уведомлений,
              📊 для отчетов/экспорта, 🎨 для оформления, 🔍 для поиска, ⚙️ для настроек,
              🚀 для ускорения, 🔒 для безопасности, 💬 для чата) — по ней должно быть
              понятно, о чем пункт;
            - оцени ЗНАЧИМОСТЬ изменения для пользователя по шкале 1-5 (score):
              5 — крупная важная фича или критичный фикс, заметно улучшает продукт;
              4 — полезное заметное улучшение; 3 — обычное изменение;
              2 — мелочь или внутреннее улучшение «под капотом»; 1 — совсем незначительное.
              Обоснование (scoreReason) — твоя живая реплика от первого лица, ОЧЕНЬ КОРОТКО:
              3-6 слов, со строчной буквы, без точки в конце, можно с юмором
              (примеры: «ну наконец-то, заждался», «мелочь, а приятно»,
              «вот это по-взрослому», «не вау, но пусть будет»);

            Верни ТОЛЬКО JSON-массив без другого текста, формат элемента:
            {"type": "feature|improvement|fix|other", "area": "Раздел продукта", "emoji": "🔔", "title": "что нового (кратко)", "benefit": "чем полезно (1 короткое предложение)", "score": 4, "scoreReason": "ну наконец-то, заждался", "authors": ["имя"], "projects": ["проект"]}

            Правила:
            - area — короткое название раздела продукта с заглавной буквы (1-3 слова);
            - emoji — ровно один смысловой эмодзи под содержание пункта;
            - title — короткий заголовок с заглавной буквы, без точки в конце;
            - benefit — понятная польза для пользователя, живым языком, КОРОТКО: одно
              предложение до ~90 символов, без перечислений и деталей реализации;
            - score — целое 1-5; scoreReason — 3-6 слов, со строчной буквы, без точки в конце;
            - authors — уникальные авторы; projects — затронутые проекты;
            - в текстах не используй букву "ё" — пиши "е".

            Коммиты:
            {{input}}
            """;
    }

    // Разбор ответа модели → готовые пункты. Применяется и к одиночному вызову, и к каждому чанку.
    // null — модель не ответила / вернула мусор (наверху сработает fallback).
    private static List<ChangelogItem>? ParseAndClean(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var items = JsonSerializer.Deserialize<List<ChangelogItem>>(ExtractJsonArray(json),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (items is null || items.Count == 0) return null;
            var cleaned = items
                .Select(i => i with
                {
                    Area = string.IsNullOrWhiteSpace(i.Area) ? "Прочее" : Deyo(i.Area.Trim()),
                    Emoji = string.IsNullOrWhiteSpace(i.Emoji) ? DefaultEmoji(i.Type) : i.Emoji.Trim(),
                    Title = Deyo(i.Title),
                    Benefit = Deyo(i.Benefit ?? ""),
                    Score = Math.Clamp(i.Score == 0 ? 3 : i.Score, 1, 5),
                    ScoreReason = Deyo(i.ScoreReason ?? ""),
                    Authors = i.Authors ?? [],
                    Projects = i.Projects ?? [],
                })
                .Where(i => !string.IsNullOrWhiteSpace(i.Title))
                .ToList();
            return cleaned.Count > 0 ? cleaned : null;
        }
        catch { return null; }
    }

    // «ё» → «е»: модель не всегда слушается промпта
    private static string Deyo(string s) => s.Replace('ё', 'е').Replace('Ё', 'Е');

    // Запасная эмодзи по категории, если модель ее не дала
    private static string DefaultEmoji(string type) => type switch
    {
        "feature" => "✨",
        "improvement" => "⚡",
        "fix" => "🐛",
        _ => "🔹",
    };

    /// <summary>Вызов claude через общий OneShotClaudeRunner. Раннер кидает исключение
    /// (таймаут / ненулевой exit code со stderr) — гасим его в null, на котором стоит фолбэк.
    /// NormalizeModel обязателен: на выключенном провайдере BuildCliEnv внутри раннера кидает,
    /// а так модель мягко откатывается на дефолтный Claude.</summary>
    private async Task<(string? Json, string? Error, ChangelogGeneration? Generation)> TryRunClaude(
        string prompt, CancellationToken ct)
    {
        try
        {
            // Detailed-режим: вместе с ответом получаем расход вызова — он показывается
            // пользователю внизу дня, чтобы цена сводки не была невидимой. Идёт через маршрут
            // действия (локаль/бесплатное облако/claude — выбор админа); _model — модель
            // claude-пути по умолчанию. На бесплатной модели usage=null → стоимость не рисуется.
            var run = await cheap.RunDetailedAsync(Llm.LocalActionCatalog.Changelog, prompt,
                fallbackModel: _model, timeout: TimeSpan.FromMilliseconds(_claudeTimeoutMs),
                maxTokens: ChangelogMaxTokens, ct: ct);
            var generation = run.Usage is { } u
                ? new ChangelogGeneration(run.DurationMs, u.InputTokens, u.CacheCreationTokens,
                    u.CacheReadTokens, u.OutputTokens, u.CostUsd, u.Model, DateTimeOffset.Now)
                : null;
            return string.IsNullOrWhiteSpace(run.Text)
                ? (null, "claude вернул пустой ответ", generation)
                : (run.Text, null, generation);
        }
        catch (Exception ex)
        {
            // ex.Message различает «не ответил за отведённое время» и «завершился с кодом N: <детали>»
            logger.LogWarning("Генерация changelog: {Error}", ex.Message);
            return (null, ex.Message, null);
        }
    }

    // Модель может обернуть JSON в ```-блок или добавить преамбулу — вырезаем сам массив
    private static string ExtractJsonArray(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    // Честная реплика вместо пустого пузыря — видно, что пункт сырой, а не оценённый
    private const string FallbackReason = "сводку собрать не вышло, показываю коммит как есть";

    // Область для fallback берём из типа коммита, а не из scope: scope дал бы английские
    // технические имена («Chat extract tasks») рядом с русскими продуктовыми областями.
    private static string FallbackArea(string type) => type switch
    {
        "feature" => "Новое",
        "fix" => "Исправления",
        "improvement" => "Улучшения",
        _ => "Прочее",
    };

    // Fallback без LLM: причесанные subject'ы коммитов (убираем conventional-префикс).
    // Benefit оставляем пустым: тела коммитов технические, а раздел — продуктовый, без жаргона.
    internal static List<ChangelogItem> FallbackItems(List<GitCommitRaw> commits) =>
        commits.Select(c =>
        {
            var subject = c.Subject;
            var type = "other";
            var colon = subject.IndexOf(':');
            if (colon is > 0 and < 30)
            {
                var prefix = subject[..colon].ToLowerInvariant();
                type = prefix switch
                {
                    _ when prefix.StartsWith("feat") => "feature",
                    _ when prefix.StartsWith("fix") => "fix",
                    _ when prefix.StartsWith("perf") || prefix.StartsWith("refactor") => "improvement",
                    _ => "other",
                };
                if (type != "other") subject = subject[(colon + 1)..].Trim();
            }
            if (subject.Length > 0)
                subject = char.ToUpper(subject[0]) + subject[1..];
            return new ChangelogItem(type, FallbackArea(type), DefaultEmoji(type), Deyo(subject), "",
                3, FallbackReason, [c.Author], string.IsNullOrEmpty(c.Project) ? [] : [c.Project]);
        }).ToList();

    // ===== Кеш data/changelog/product.json =====

    private string CachePath() => Path.Combine(_cacheDir, $"{CacheFile}.json");

    private Dictionary<string, CachedDay> LoadCache()
    {
        try
        {
            var path = CachePath();
            lock (_cacheFileLock)
            {
                if (!File.Exists(path)) return [];
                return JsonSerializer.Deserialize<Dictionary<string, CachedDay>>(File.ReadAllText(path)) ?? [];
            }
        }
        catch { return []; }
    }

    private void SaveCache(Dictionary<string, CachedDay> cache)
    {
        try
        {
            lock (_cacheFileLock)
            {
                Directory.CreateDirectory(_cacheDir);
                // Атомарная запись: temp + move, чтобы параллельный LoadCache
                // никогда не увидел полузаписанный JSON
                var path = CachePath();
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch { }
    }
}
