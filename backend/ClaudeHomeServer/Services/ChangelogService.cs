using System.Diagnostics;
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
public class ChangelogService(FileService files, ProjectManager projects, IConfiguration config, ILogger<ChangelogService> logger)
{
    private readonly string _cacheDir = Path.Combine(
        Path.GetDirectoryName(config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data"),
        "changelog");

    private readonly string _model = config["Changelog:Model"] ?? "haiku";
    private readonly int _claudeTimeoutMs = int.TryParse(config["Changelog:TimeoutMs"], out var t) ? t : 240_000;

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

    private sealed record CachedDay(string ShasHash, List<ChangelogItem> Items);

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
            return new ChangelogDay(date, cached.Items);

        await _generateLock.WaitAsync();
        try
        {
            // Перепроверка под замком: пока ждали, другой запрос мог сгенерировать
            cache = LoadCache();
            if (cache.TryGetValue(date, out cached) && cached.ShasHash == hash)
                return new ChangelogDay(date, cached.Items);

            var items = await SummarizeDay(dayCommits) ?? FallbackItems(dayCommits);
            cache[date] = new CachedDay(hash, items);
            SaveCache(cache);
            return new ChangelogDay(date, items);
        }
        finally { _generateLock.Release(); }
    }

    /// <summary>Сколько коммитов во всех проектах новее since (для бейджа).</summary>
    public int GetNewCommitCount(DateTimeOffset since) =>
        GetAllCommits().Count(c => c.Date > since);

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
        foreach (var p in projects.GetAll())
        {
            if (string.IsNullOrWhiteSpace(p.RootPath)) continue;
            all.AddRange(files.GetCommitsRaw(p.RootPath, p.Name, limit: 2000, _authorAliases));
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

    private async Task<List<ChangelogItem>?> SummarizeDay(List<GitCommitRaw> commits)
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
                // раздувается, и claude не укладывается в таймаут (падал в fallback)
                var body = c.Body.ReplaceLineEndings(" ");
                if (body.Length > 200) body = body[..200] + "…";
                input.AppendLine($"  подробности: {body}");
            }
        }

        var prompt = $$"""
            Ты ведешь продуктовый дневник изменений для пользователей продукта. Ниже —
            git-коммиты за один день по разным проектам. Твоя задача — рассказать
            ЧЕЛОВЕЧЕСКИМ языком по-русски, ЧТО нового появилось и ЧЕМ это полезно
            пользователю, как будто пишешь заметку «что улучшилось».

            ВАЖНО:
            - пиши с точки зрения ПОЛЬЗЫ для пользователя, а не с точки зрения кода;
            - НЕ упоминай файлы, классы, функции, технический жаргон, рефакторинг;
            - НИЧЕГО не выбрасывай: каждый коммит должен войти РОВНО в один пункт.
              Даже чисто техническое изменение (сборка, конфиг, рефакторинг) включи —
              опиши его коротко и по-человечески (например "улучшения под капотом");
            - группируй БЛИЗКО родственные коммиты в один пункт, но не сваливай все в кучу —
              разные по смыслу изменения должны быть разными пунктами;
            - для каждого пункта определи ОБЛАСТЬ (area) — раздел/часть продукта, которого
              касается изменение, короткое человеческое название (например «Артефакты сессии»,
              «Календарь», «Чат», «История проекта», «Файлы», «Уведомления», «Настройки»).
              Пункты про одно и то же должны иметь ОДИНАКОВУЮ область (дословно), чтобы
              сгруппироваться. Не выдумывай новую область, если подходит уже названная;
            - подбери каждому пункту подходящую по СМЫСЛУ эмодзи (например 🔔 для уведомлений,
              📊 для отчетов/экспорта, 🎨 для оформления, 🔍 для поиска, ⚙️ для настроек,
              🚀 для ускорения, 🔒 для безопасности, 💬 для чата) — по ней должно быть
              понятно, о чем пункт;
            - оцени ЗНАЧИМОСТЬ изменения для пользователя по шкале 1-5 (score):
              5 — крупная важная фича или критичный фикс, заметно улучшает продукт;
              4 — полезное заметное улучшение; 3 — обычное изменение;
              2 — мелочь или внутреннее улучшение «под капотом»; 1 — совсем незначительное.
              Обоснование (scoreReason) пиши ОТ ПЕРВОГО ЛИЦА, как будто ты, Claude, лично
              оцениваешь эту задачу и делишься впечатлением с другом — живая разговорная
              реплика в 1 предложение, С ЮМОРОМ И ПРИКОЛОМ, эмоционально и неформально:
              можно ирония, самоирония, разговорное словечко, легкая шутка или эмодзи в тему.
              ВАЖНО по формату: начинай со СТРОЧНОЙ (маленькой) буквы и БЕЗ точки в конце —
              так менее формально (примеры: «ну наконец-то, а то я уже заждался»,
              «мелочь, а на душе теплее», «вот это по-взрослому, аж горжусь»,
              «честно, не вау, но пусть будет»). Не превращай в занудство — коротко и по-живому.

            Верни ТОЛЬКО JSON-массив без другого текста, формат элемента:
            {"type": "feature|improvement|fix|other", "area": "Раздел продукта", "emoji": "🔔", "title": "что нового (кратко)", "benefit": "чем полезно (1 короткое предложение)", "score": 4, "scoreReason": "ну наконец-то удобно, а то я уже извелся весь", "authors": ["имя"], "projects": ["проект"]}

            Правила:
            - area — короткое название раздела продукта с заглавной буквы (1-3 слова);
            - emoji — ровно один смысловой эмодзи под содержание пункта;
            - title — короткий заголовок с заглавной буквы, без точки в конце;
            - benefit — понятная польза для пользователя, живым языком, КОРОТКО: одно
              предложение до ~90 символов, без перечислений и деталей реализации;
            - score — целое 1-5; scoreReason — твоя личная оценка от первого лица, живая
              разговорная реплика в 1 предложение С ЮМОРОМ/ПРИКОЛОМ, эмоционально и
              неформально (как будто это говоришь ты, Claude, приятелю); со СТРОЧНОЙ буквы
              и БЕЗ точки в конце;
            - authors — уникальные авторы; projects — затронутые проекты;
            - в текстах не используй букву "ё" — пиши "е".

            Коммиты:
            {{input}}
            """;

        var json = await RunClaude(prompt);
        if (json is null) return null;
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

    /// <summary>Запуск claude --print; вернуть текст результата либо null при любой ошибке.</summary>
    private async Task<string?> RunClaude(string prompt)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FindClaudeExecutable(),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "--print", "--output-format", "text", "--model", _model })
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            using var cts = new CancellationTokenSource(_claudeTimeoutMs);
            // Читаем stdout/stderr СРАЗУ, до записи в stdin: на большом промпте (50+ коммитов)
            // claude начнет писать в свои пайпы, буфер ОС переполнится и мы словим deadlock,
            // если сначала целиком писать stdin, а читать вывод только потом.
            var outputTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = proc.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await proc.StandardInput.WriteAsync(prompt.AsMemory(), cts.Token);
                proc.StandardInput.Close();
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                logger.LogWarning("Генерация changelog: claude не уложился в таймаут {TimeoutMs} мс", _claudeTimeoutMs);
                return null;
            }

            var output = await outputTask;
            if (proc.ExitCode != 0)
            {
                var err = "";
                try { err = await errorTask; } catch { /* stderr не дочитался — не критично */ }
                logger.LogWarning("Генерация changelog: claude вышел с кодом {Code}. stderr: {Err}",
                    proc.ExitCode, err.Length > 500 ? err[..500] : err);
                return null;
            }
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Генерация changelog: ошибка запуска claude");
            return null;
        }
    }

    // Модель может обернуть JSON в ```-блок или добавить преамбулу — вырезаем сам массив
    private static string ExtractJsonArray(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    // Fallback без LLM: причесанные subject'ы коммитов (убираем conventional-префикс)
    private static List<ChangelogItem> FallbackItems(List<GitCommitRaw> commits) =>
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
            return new ChangelogItem(type, "Прочее", DefaultEmoji(type), Deyo(subject), "", 3, "", [c.Author], string.IsNullOrEmpty(c.Project) ? [] : [c.Project]);
        }).ToList();

    // На Windows ищем claude.exe напрямую (паттерн из ClaudeSession)
    private static string FindClaudeExecutable()
    {
        if (!OperatingSystem.IsWindows()) return "claude";
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var exePath = Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        if (File.Exists(exePath)) return exePath;
        // Новый путь standalone-установки: %USERPROFILE%\.local\bin\claude.exe
        var localBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(localBin)) return localBin;
        try
        {
            var where = Process.Start(new ProcessStartInfo("where.exe", "claude.exe")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
            var line = where.StandardOutput.ReadLine();
            if (!string.IsNullOrEmpty(line) && File.Exists(line)) return line.Trim();
        }
        catch { }
        return "claude.exe";
    }

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
