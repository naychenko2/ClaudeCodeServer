using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Spheres;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Линт вики (S3-3, ADR-035, решение 2 «Обнаружение дрейфа» и «Последствия»: базовая линия
// метрик): ночное окно + запуск по кнопке (REST POST /api/wiki/lint). По каждому разделу
// заведённой вики владельца: снимок графа (S3-1) → механические находки (S3-2) → сверка
// манифеста с диском по байтам (ниже) → _health.md раздела через Хранителя (единственный
// писатель pages/, ADR-035, решение 2) → строка метрик в keeper/metrics.jsonl под той же
// блокировкой раздела.
//
// try/catch на каждый раздел: отказ одного раздела не останавливает остальные, а
// невыловленное исключение в BackgroundService по умолчанию остановило бы весь хост
// (та же причина, что тик под catch в PersonaMemoryConsolidationService).
//
// Раздел с нечитаемой страницей НЕ отчитывается чистым: его отчёт — строка об отказе.
// Линтер о странице с неразобранной шапкой молчит по построению (фактов нет — находок
// нет), и «чисто» по такому разделу было бы ложью: цель health-файла — довести сигнал
// до человека, а не скрыть его.
//
// Один прогон на владельца: кнопка и ночное окно проходят через слот владельца
// (неблокирующий Wait(0)): второй получает «уже идёт», а не второй записи _health.md и
// metrics.jsonl подряд.
//
// Флаг llm-wiki гейтит прогон (вердикт ревью S3-3): для владельца с выключенным флагом
// прогон пропускается целиком — ни _health.md, ни строки метрик. Раньше служба знала только
// заведённость вики (признак _schema.md) и при выключенном флаге писала «Находки (0)» с
// нулевыми метриками: флаг прячет страницы вики из графа (NotesService.cs:128), и «чисто»
// по невидимой вики — ложь, а не мера: отчёт говорил бы «чисто» там, где проверка не
// выполнялась. Одна проверка флага, в одной точке: и ночное окно, и кнопка идут через
// TryRunOwnerAsync; шов — тот же IFeatureFlagGate, что у источника графа, не ключ конфига.
//
// Дрейф по байтам (ADR-035 §2: «Манифест раздела сверяется с диском при каждом lint…
// расхождение даёт статус drift, строку в _health.md»): перед линтом служба сверяет каждую
// запись манифеста с файлом на диске — той же мерой, что применение и показ страницы
// (WikiApplyService.IsPageDrifted, второй копии меры нет). Её находки идут в тот же
// _health.md и в счётчик дрейфа метрик: сам линтер — чистая функция по снимку и видит
// только членство страниц в манифесте, байтов он не читает.
public sealed class WikiLintService : BackgroundService
{
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly ISphereDirectory _spheres;
    private readonly IConfiguration _config;
    private readonly Composition.IFeatureFlagGate? _flags;
    private readonly string _dataDir;
    private readonly ILogger<WikiLintService> _log;

    // Слоты прогона на владельца. Wait(0) — неблокирующий: второй вызывающий отвергается
    // сразу; очередь второго прогона дала бы вторую запись health+метрик подряд.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ownerSlots = new();

    // Дата последней ночной проходки (локальная): одна на календарные сутки в окне.
    private DateTime _lastPassDate = DateTime.MinValue;

    // Версия кода в строке метрик: базовая линия S0b-2 штампована ревизией кода
    // (docs/research/llm-wiki-baseline-2026-10.md); сравнение метрик без штампа — не сравнение.
    internal static readonly string CodeVersion =
        typeof(WikiLintService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    public WikiLintService(NotesService notes, WikiKeeper keeper, ISphereDirectory spheres,
        IConfiguration config, ILogger<WikiLintService> log,
        // Шов флага — необязательный параметр, как у NotesService: в приложении его даёт DI
        // (Program.cs:1313), в тесте его даёт вызывающий — состояние флага часть проверки.
        Composition.IFeatureFlagGate? flags = null)
    {
        _notes = notes;
        _keeper = keeper;
        _spheres = spheres;
        _config = config;
        _flags = flags;
        _log = log;
        // Каталог данных — как у всех сервисов (DataPath указывает на projects.json в dataDir).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    // ─── Публичный контракт (его же зовёт REST-кнопка) ────────────────────────

    /// <summary>Прогон по владельцу: по всем его разделам на диске. AlreadyRunning — прогон
    /// этого владельца уже идёт: вызывающий получает «уже идёт» (409), а не двойную запись
    /// _health.md и metrics.jsonl. FeatureOff — функция выключена: прогон пропущен целиком,
    /// ни _health.md, ни метрик не написано. SectionsFailed — разделы, где прогон не
    /// завершился: их _health.md содержит строку об отказе.</summary>
    public async Task<WikiLintRunResult> TryRunOwnerAsync(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) return new(WikiLintRunOutcome.AlreadyRunning, 0, 0);
        // Одна проверка флага, в одной точке: и ночное окно, и кнопка идут через этот метод.
        if (!IsLlmWikiOn(ownerId)) return new(WikiLintRunOutcome.FeatureOff, 0, 0);
        var slot = _ownerSlots.GetOrAdd(ownerId, _ => new SemaphoreSlim(1));
        if (!slot.Wait(0)) return new(WikiLintRunOutcome.AlreadyRunning, 0, 0);
        try { return await RunOwnerAsync(ownerId); }
        finally { slot.Release(); }
    }

    // Флаг — тот же шов и та же форма проверки, что у источника вики в графе
    // (NotesService.cs:128): нет шва или нет ключа — выключено (fail-closed).
    private bool IsLlmWikiOn(string ownerId) =>
        _flags?.IsEnabled(ownerId, FeatureFlagKeys.LlmWiki) == true;

    // ─── Ночное окно ──────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(CheckInterval, ct); }
            catch (OperationCanceledException) { break; }

            // Всё тело тика под catch: отказ за пределами раздела-изоляции не должен
            // гасить службу (тот же образец, что PersonaMemoryConsolidationService).
            try
            {
                var now = DateTimeOffset.Now; // «ночь» владельца — локальное время сервера
                if (!InWindow(now.Hour) || _lastPassDate == now.Date) continue;
                _lastPassDate = now.Date;
                foreach (var ownerId in EnumerateWikiOwners())
                    await RunOwnerGuardedAsync(ownerId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Ночное окно линта вики");
            }
        }
    }

    // Окно полуприкрытое [start, end) по локальному часу; start > end — окно через
    // полночь; start == end — окна нет. Умолчание 03–4.
    private bool InWindow(int hour)
    {
        var start = IntSetting("Wiki:LintWindowStartHour", 3);
        var end = IntSetting("Wiki:LintWindowEndHour", 4);
        if (start == end) return false;
        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    private TimeSpan CheckInterval => TimeSpan.FromMinutes(IntSetting("Wiki:LintCheckIntervalMinutes", 30));

    private int IntSetting(string key, int fallback) =>
        int.TryParse(_config[key], out var v) && v is >= 0 and <= 23 ? v : fallback;

    // Владельцы с заведённой вики: подкаталоги data/wiki/ с pages/_schema.md. Реестр
    // пользователей здесь не нужен: прогон нужен там, где вики есть на диске.
    private IEnumerable<string> EnumerateWikiOwners()
    {
        var wikiRoot = Path.Combine(_dataDir, WikiPaths.DirName);
        if (!Directory.Exists(wikiRoot)) return [];
        try
        {
            return Directory.EnumerateDirectories(wikiRoot)
                .Select(d => Path.GetFileName(d))
                .Where(id => !string.IsNullOrWhiteSpace(id) && WikiPaths.IsInitialized(_dataDir, id))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Ночная проходка одного владельца: исключение не выходит наружу — оно закрывает
    // этого владельца, не остальных и не службу.
    private async Task RunOwnerGuardedAsync(string ownerId)
    {
        try { await TryRunOwnerAsync(ownerId); }
        catch (Exception ex) { _log.LogWarning(ex, "Ночной линт вики {Owner}", ownerId); }
    }

    // ─── Прогон владельца ─────────────────────────────────────────────────────

    private async Task<WikiLintRunResult> RunOwnerAsync(string ownerId)
    {
        if (!WikiPaths.IsInitialized(_dataDir, ownerId)) return new(WikiLintRunOutcome.Ran, 0, 0); // нечего линтовать

        var root = WikiPaths.Root(_dataDir, ownerId);
        var pagesRoot = WikiPaths.Pages(_dataDir, ownerId);
        var sections = EnumerateSectionKeys(pagesRoot);
        if (sections.Count == 0) return new(WikiLintRunOutcome.Ran, 0, 0);

        // ts файлов — UTC, как у строк журнала (AppendLog); окно — локальное.
        var now = DateTimeOffset.UtcNow;
        var snapshot = _notes.GetGraphSnapshot(ownerId);
        // Узлы-аннотации (frontmatter annotates): не заметки базовой линии, не сироты,
        // не участники метрик (решение Софьи M3 по ревью S3-1). Их опознаёт граф с
        // includeAnnotations — тот же признак (kind comment/reply), что у публичного UI.
        var annotationIds = _notes.GetGraph(ownerId, includeAnnotations: true)
            .Nodes.Where(n => n.Kind is "comment" or "reply").Select(n => n.Id).ToList();

        // Шаг 1 — чтение страниц раздела. Строгое: неразобранная шапка — исключение, а
        // не молчание (смысл — в комментарии класса). Фактов такой страницы нет ни у
        // кого: метрики «по страницам вики» остаются без неё — лентер об этом предупреждён.
        var factsBySection = new Dictionary<string, List<WikiPageFacts>>(StringComparer.Ordinal);
        var failedSections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            try { factsBySection[section] = ReadSectionFacts(pagesRoot, section); }
            catch (Exception ex) { failedSections[section] = ex.Message; }
        }
        var allFacts = factsBySection.Values.SelectMany(f => f).ToList();

        int ok = 0, failed = 0;
        foreach (var section in sections)
        {
            if (failedSections.TryGetValue(section, out var reason))
            {
                await WriteHealthQuietlyAsync(ownerId, section, RenderFailureHealth(section, now, reason), null);
                failed++;
                continue;
            }
            try
            {
                var manifest = ReadManifestQuietly(root, section);
                var report = WithServiceFindings(
                    WikiLint.Run(
                        snapshot, section, manifest, now,
                        new WikiLintContext(
                            // Свой раздел лентер добавляет сам (WikiLint.cs:205-206) — здесь только цепочка вверх.
                            WikiSections.UpChain(section, ownerId, _spheres),
                            // Лентер сам дописывает свой раздел и дедуплицирует (WikiLint.cs:399).
                            SphereProjectSections(ownerId, section),
                            annotationIds,
                            allFacts)),
                    // Байтовая сверка — до линта и вне его: в WikiLint.Run идёт тот же
                    // статус манифеста, что раньше (по нему линтер судит только членство).
                    ManifestByteDrift(root, manifest));
                await WriteHealthQuietlyAsync(ownerId, section, RenderHealth(report), MetricsLineJson(report, now));
                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                _log.LogWarning(ex, "Линт раздела {Owner}/{Section}", ownerId, section);
                await WriteHealthQuietlyAsync(ownerId, section, RenderFailureHealth(section, now, ex.Message), null);
            }
        }
        return new(WikiLintRunOutcome.Ran, ok, failed);
    }

    // Ключи разделов на диске: верхние папки pages/ и их подпапки-id — тот же разбор и
    // тот же fail-closed, что у дерева контроллера (WikiController.DiskSectionKeys):
    // неразобранный каталог не становится разделом.
    private static List<string> EnumerateSectionKeys(string pagesRoot)
    {
        var keys = new List<string>();
        if (!Directory.Exists(pagesRoot)) return keys;
        try
        {
            foreach (var top in Directory.EnumerateDirectories(pagesRoot))
            {
                var relTop = Path.GetRelativePath(pagesRoot, top).Replace('\\', '/');
                if (WikiSections.Parse(relTop + "/") is { } key) { keys.Add(key); continue; }
                // «Проекты»/«Сферы» сами — не ключ раздела: разделы — их подпапки по id.
                foreach (var sub in Directory.EnumerateDirectories(top))
                {
                    var relSub = Path.GetRelativePath(pagesRoot, sub).Replace('\\', '/');
                    if (WikiSections.Parse(relSub + "/") is { } subKey) keys.Add(subKey);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Каталог не читается — ничего не линтуем, не падаем.
        }
        keys.Sort((a, b) => string.CompareOrdinal(a, b));
        return keys;
    }

    // Факты страниц одного раздела (тот же ключ RelPath, что у узлов снимка — по нему
    // лентер связывает узел с его шапкой и текстом). Строгость — по документу:
    // нечитаемый файл или неразобранная шапка поднимают исключение вызывающему.
    private static List<WikiPageFacts> ReadSectionFacts(string pagesRoot, string section)
    {
        var folder = WikiSections.Folder(section)!;
        var dir = Path.Combine(pagesRoot, folder);
        var facts = new List<WikiPageFacts>();
        if (!Directory.Exists(dir)) return facts;
        foreach (var full in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(pagesRoot, full).Replace('\\', '/');
            if (WikiPaths.IsProjection(rel)) continue; // _index/_log/_health — не страницы
            var text = File.ReadAllText(full, Encoding.UTF8);
            var parsed = WikiFrontmatter.Parse(text);
            if (parsed.Model is null)
                throw new InvalidDataException($"шапка страницы «{rel}» не разобрана: {parsed.Errors[0]}");
            facts.Add(new WikiPageFacts(
                rel,
                TitleOf(text, parsed.CloseLineIndex, Path.GetFileNameWithoutExtension(full)),
                parsed.Model.Type, parsed.Model.Aliases, parsed.Model.Owns, parsed.Model.Sources,
                ParseUpdated(parsed.Model.Updated), parsed.Model.Hash, text));
        }
        facts.Sort((a, b) => string.CompareOrdinal(a.RelPath, b.RelPath));
        return facts;
    }

    // Заголовок страницы: первый «# …» после шапки, иначе — имя файла (тот же порядок,
    // что у каталога _index.md: имени нет — страница остаётся под именем файла).
    private static string TitleOf(string text, int closeLineIndex, string fileName)
    {
        var lines = WikiFrontmatter.SplitLines(text);
        for (var i = closeLineIndex + 1; i < lines.Length; i++)
            if (lines[i].StartsWith("# ", StringComparison.Ordinal)) return lines[i][2..].Trim();
        return fileName;
    }

    // updated из шапки: дата или null (строгий парсер проверяет форму поля, не календарь;
    // «без изменений» считается только по читаемой дате).
    private static DateTimeOffset? ParseUpdated(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v : null;

    private List<string> SphereProjectSections(string ownerId, string section)
    {
        if (!WikiSections.TryProjectId(section, out var projectId)) return [];
        if (_spheres.SphereOf(ownerId, projectId) is not { } sphereId) return [];
        return _spheres.ProjectsOf(ownerId, sphereId)
            .Select(p => WikiSections.ProjectsPrefix + p)
            .ToList();
    }

    private static WikiManifestReadResult ReadManifestQuietly(string root, string sectionKey)
    {
        try
        {
            return WikiManifest.TryRead(root, sectionKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WikiManifestReadResult(WikiManifestState.Invalid, new Dictionary<string, string>());
        }
    }

    // Дрейф по байтам (ADR-035 §2): каждая запись манифеста — файл с этим хэшем. Мера — та же,
    // что шаг 2 применения и показ страницы (WikiApplyService.IsPageDrifted): второй копии меры
    // нет. Сторона «файл на диске без записи в манифесте» — за ней уже линтер (его сверка по
    // членству, WikiLint.cs:341-351); здесь ровно обратная — запись без совпавшего файла,
    // т. е. правка или удаление, сделанные мимо Хранителя. Манифест Missing/Invalid — не дрейф
    // (тот же смысл, что у показа страницы): состоянием раздела говорит строка линтера.
    private static List<WikiFinding> ManifestByteDrift(string root, WikiManifestReadResult manifest)
    {
        var findings = new List<WikiFinding>();
        if (manifest.State != WikiManifestState.Valid) return findings;
        // Порядок ключей словаря не определён, а строки отчёта обязаны быть одними и теми же
        // при одном диске.
        foreach (var page in manifest.Manifest.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            bool drifted;
            try { drifted = WikiApplyService.IsPageDrifted(root, page, manifest.Manifest); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Файл не прочитать — расхождения не подтвердить: не дрейф (тот же приём, что
                // плашка страницы в контроллере); чинит его Хранитель, не метрика.
                continue;
            }
            if (!drifted) continue;
            // Форма строки — за ней уже решившая её мера; недоверенный путь — в PageRelPath,
            // его экранирует FindingLine.
            var full = SafePath.JoinBelowRoot(root, "pages/" + page);
            findings.Add(new WikiFinding(WikiFindingKind.Drift, page, [], null, 0,
                File.Exists(full)
                    ? "файл расходится с записью манифеста (правка мимо Хранителя)"
                    : "файла из записи манифеста нет на диске"));
        }
        return findings;
    }

    // Находки службы входят в тот же отчёт и в том же порядке, что находки линтера
    // (вид → путь → ключ → связанные): один и тот же диск даёт один и тот же _health.md.
    private static WikiLintReport WithServiceFindings(WikiLintReport report, List<WikiFinding> extra) =>
        extra.Count == 0 ? report : report with
        {
            Findings = report.Findings.Concat(extra)
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.PageRelPath ?? "", StringComparer.Ordinal)
                .ThenBy(f => f.Value ?? "", StringComparer.Ordinal)
                .ThenBy(f => string.Join('\0', f.Related), StringComparer.Ordinal)
                .ToList(),
        };

    // Запись health+метрик не роняет прогон следующего раздела: отказ записи — событие
    // журнала службы, раздел остаётся необработанным.
    private async Task WriteHealthQuietlyAsync(string ownerId, string section, string health, string? metricsLine)
    {
        try { await _keeper.WriteSectionHealthAndMetricsAsync(ownerId, section, health, metricsLine); }
        catch (Exception ex) { _log.LogWarning(ex, "Запись health раздела {Owner}/{Section}", ownerId, section); }
    }

    // ─── _health.md ───────────────────────────────────────────────────────────

    // Строки находок рендерит вызывающий (контракт WikiFinding: Message без недоверенных
    // строк, экранирование — за вызывающим). Недоверенное (пути, ключи, связанные) — после
    // Escape, в backtick-ограде: строка health не может стать чужой ссылкой или разорваться.
    internal static string RenderHealth(WikiLintReport report)
    {
        var sb = new StringBuilder();
        sb.Append("# Здоровье раздела «").Append(report.Section).Append("»\n\n");
        sb.Append("Проверено: ").Append(report.GeneratedAt.ToString("O", CultureInfo.InvariantCulture))
            .Append(". Код: ").Append(CodeVersion).Append(".\n");
        sb.Append("\n## Находки (").Append(report.Findings.Count).Append(")\n");
        foreach (var f in report.Findings)
            sb.Append(FindingLine(f)).Append('\n');
        return sb.ToString();
    }

    private static string RenderFailureHealth(string section, DateTimeOffset now, string reason)
    {
        var sb = new StringBuilder();
        sb.Append("# Здоровье раздела «").Append(section).Append("»\n\n");
        sb.Append("Проверено: ").Append(now.ToString("O", CultureInfo.InvariantCulture))
            .Append(". Код: ").Append(CodeVersion).Append(".\n");
        sb.Append("\n## Находки (0)\n\n## Отказ\n- ").Append(Escape(reason)).Append('\n');
        return sb.ToString();
    }

    private static string FindingLine(WikiFinding f)
    {
        var parts = new List<string> { KindLabel(f.Kind) };
        parts.Add(f.PageRelPath is not null ? $"страница `{Escape(f.PageRelPath)}`" : "раздел");
        parts.Add(Escape(f.Message));
        if (f.Related.Count > 0) parts.Add("связанные: " + string.Join(", ", f.Related.Select(r => $"`{Escape(r)}`")));
        if (f.Value is not null) parts.Add($"ключ: `{Escape(f.Value)}`");
        if (f.Count > 0) parts.Add("счёт: " + f.Count);
        return "- " + string.Join(" · ", parts);
    }

    private static string KindLabel(WikiFindingKind kind) => kind switch
    {
        WikiFindingKind.DanglingTarget => "призрак",
        WikiFindingKind.Orphan => "сирота",
        WikiFindingKind.UnlinkedMention => "упоминание без ссылки",
        WikiFindingKind.NearDuplicate => "почти дубль",
        WikiFindingKind.MissingSources => "нет источников",
        WikiFindingKind.OwnsConflict => "один owns у нескольких",
        WikiFindingKind.Drift => "дрейф",
        WikiFindingKind.Stale => "без изменений",
        WikiFindingKind.OutOfReach => "ссылка вне раздела",
        WikiFindingKind.PromoteSuggestion => "предложение повышения",
        _ => kind.ToString(),
    };

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c < ' ') { sb.Append(' '); continue; }
            if (c == '`') { sb.Append("\\`"); continue; }
            sb.Append(c);
        }
        var r = sb.ToString().Trim();
        return r.Length > 200 ? r[..197] + "…" : r;
    }

    // ─── metrics.jsonl ────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Единицы и имена строки — базовой линии S0b-2 (docs/research/llm-wiki-baseline-script.cs.txt):
    // назначение файла — сравнение с базовой линией, а не внутренние имена лентера. Проценты с
    // одним знаком — по её формуле (Math.Round(100.0 * ghosts / links, 1)); имена полей — её
    // имена, соответствие внутреннему полю указано здесь один раз: NoteCount→notes,
    // LinkCount→links, DanglingLinkCount→ghosts, DanglingLinkShare→ghostsShare,
    // DanglingTargetCount→distinctGhostTargets, OrphanCount→orphans, OrphanShare→orphansShareAll,
    // OrphanCountExcludingJournal→orphansNoJournal, OrphanShareExcludingJournal→
    // orphansShareNoJournal, UnlinkedMentionPairs→unlinkedMentions (тот же смысл — упоминание
    // без ссылки; область — страницы вики, их базовая линия по человеку не считала).
    // Полей без аналога в базовой линии три, они остались под своими именами: wikiPages и
    // wikiPagesWithoutSources (по страницам вики — такой половины базы в базовой линии нет) и
    // driftCount (байтовая сверка манифеста — предмета вики). ts/section/codeVersion — конверт
    // строки, не метрика: его формат не меняется.
    private sealed record MetricsLine(
        DateTimeOffset Ts, string Section, string CodeVersion,
        int Notes, int Links, int Ghosts, double GhostsShare, int DistinctGhostTargets,
        int Orphans, double OrphansShareAll, int OrphansNoJournal, double OrphansShareNoJournal,
        int DriftCount,
        int WikiPages, int WikiPagesWithoutSources, int UnlinkedMentions);

    // Доля → процент с одним знаком — ровно формула базовой линии; пустой знаменатель даёт 0
    // (его уже даёт Share() линтера).
    private static double Pct(double share) => Math.Round(100.0 * share, 1);

    private static string MetricsLineJson(WikiLintReport report, DateTimeOffset now)
    {
        var m = report.Metrics;
        return JsonSerializer.Serialize(new MetricsLine(
            now, report.Section, CodeVersion,
            m.NoteCount, m.LinkCount, m.DanglingLinkCount, Pct(m.DanglingLinkShare), m.DanglingTargetCount,
            m.OrphanCount, Pct(m.OrphanShare), m.OrphanCountExcludingJournal, Pct(m.OrphanShareExcludingJournal),
            // Счётчик дрейфа раздела — все его находки дрейфа: и членство (линт), и байты (служба).
            report.Findings.Count(f => f.Kind == WikiFindingKind.Drift),
            m.WikiPageCount, m.WikiPagesWithoutSources, m.UnlinkedMentionPairs), JsonOpts);
    }
}

/// <summary>Исход прогона владельца. Ran — разделы пройдены. AlreadyRunning — прогон этого
/// владельца уже идёт: вызывающий отвечает «уже идёт», а не второй записью файлов.
/// FeatureOff — функция выключена: прогон пропущен целиком, ни _health.md, ни строки метрик
/// не написано; вызывающий отвечает отказом с причиной, а не «здорово».</summary>
public enum WikiLintRunOutcome { Ran, AlreadyRunning, FeatureOff }

/// <summary>Исход прогона владельца и число его разделов: SectionsOk — обработанные,
/// SectionsFailed — те, где прогон не завершился (их _health.md содержит строку об
/// отказе).</summary>
public readonly record struct WikiLintRunResult(WikiLintRunOutcome Outcome, int SectionsOk, int SectionsFailed)
{
    /// <summary>Этот вызов ведёт (или уже ведёт) разделы владельца: false — ничего не
    /// проверялось: прогон занят другим вызовом либо функция выключена.</summary>
    public bool Started => Outcome == WikiLintRunOutcome.Ran;
}
