using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Spheres;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Линт вики (S3-3, ADR-035, решение 2 «Обнаружение дрейфа» и «Последствия»: базовая линия
// метрик): ночное окно + запуск по кнопке (REST POST /api/wiki/lint). По каждому разделу
// заведённой вики владельца: снимок графа (S3-1) → механические находки (S3-2) → _health.md
// раздела через Хранителя (единственный писатель pages/, ADR-035, решение 2) → строка
// метрик в keeper/metrics.jsonl под той же блокировкой раздела.
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
// Служба работает по заведённой вики (признак _schema.md, ADR-035, решение 1), а не по
// фич-флагу: флаг llm-wiki прячет UI и инструменты, гейт заведённой вики работает всегда.
// Влияние флага на граф — внутри NotesService: при выключенном флаге снимок не несёт
// страниц вики, и лентер по разделам молчит — та же тишина, что по пустому разделу.
public sealed class WikiLintService : BackgroundService
{
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly ISphereDirectory _spheres;
    private readonly IConfiguration _config;
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
        IConfiguration config, ILogger<WikiLintService> log)
    {
        _notes = notes;
        _keeper = keeper;
        _spheres = spheres;
        _config = config;
        _log = log;
        // Каталог данных — как у всех сервисов (DataPath указывает на projects.json в dataDir).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    // ─── Публичный контракт (его же зовёт REST-кнопка) ────────────────────────

    /// <summary>Прогон по владельцу: по всем его разделам на диске. Started=false —
    /// прогон этого владельца уже идёт: вызывающий получает «уже идёт» (409), а не
    /// двойную запись _health.md и metrics.jsonl. SectionsFailed — разделы, где прогон
    /// не завершился: их _health.md содержит строку об отказе.</summary>
    public async Task<WikiLintRunResult> TryRunOwnerAsync(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) return new(false, 0, 0);
        var slot = _ownerSlots.GetOrAdd(ownerId, _ => new SemaphoreSlim(1));
        if (!slot.Wait(0)) return new(false, 0, 0);
        try { return await RunOwnerAsync(ownerId); }
        finally { slot.Release(); }
    }

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
        if (!WikiPaths.IsInitialized(_dataDir, ownerId)) return new(true, 0, 0); // нечего линтовать

        var root = WikiPaths.Root(_dataDir, ownerId);
        var pagesRoot = WikiPaths.Pages(_dataDir, ownerId);
        var sections = EnumerateSectionKeys(pagesRoot);
        if (sections.Count == 0) return new(true, 0, 0);

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
                var report = WikiLint.Run(
                    snapshot, section, ReadManifestQuietly(root, section), now,
                    new WikiLintContext(
                        // Свой раздел лентер добавляет сам (WikiLint.cs:205-206) — здесь только цепочка вверх.
                        WikiSections.UpChain(section, ownerId, _spheres),
                        // Лентер сам дописывает свой раздел и дедуплицирует (WikiLint.cs:399).
                        SphereProjectSections(ownerId, section),
                        annotationIds,
                        allFacts));
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
        return new(true, ok, failed);
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

    // Имена полей — продолжение имён базовой линии S0b-2 (llm-wiki-baseline-script.cs.txt):
    // назначение файла — сравнение с базовой линией, а не внутренние имена лентера.
    // Доли — доли (0..1), как их считает лентер; перевод в проценты — за читающим.
    private sealed record MetricsLine(
        DateTimeOffset Ts, string Section, string CodeVersion,
        int NoteCount, int LinkCount, int DanglingLinks, double GhostsShare, int DanglingTargets,
        int Orphans, double OrphansShare, int OrphansNoJournal, double OrphansShareNoJournal,
        int WikiPages, int WikiPagesWithoutSources, int UnlinkedMentionPairs);

    private static string MetricsLineJson(WikiLintReport report, DateTimeOffset now)
    {
        var m = report.Metrics;
        return JsonSerializer.Serialize(new MetricsLine(
            now, report.Section, CodeVersion,
            m.NoteCount, m.LinkCount, m.DanglingLinkCount, m.DanglingLinkShare, m.DanglingTargetCount,
            m.OrphanCount, m.OrphanShare, m.OrphanCountExcludingJournal, m.OrphanShareExcludingJournal,
            m.WikiPageCount, m.WikiPagesWithoutSources, m.UnlinkedMentionPairs), JsonOpts);
    }
}

/// <summary>Исход прогона владельца. Started=false — прогон уже идёт («уже идёт» для
/// вызывающего, не двойная запись файлов). SectionsOk / SectionsFailed — разделы,
/// обработанные этим прогоном, и разделы, где прогон не завершился (их _health.md
/// содержит строку об отказе).</summary>
public readonly record struct WikiLintRunResult(bool Started, int SectionsOk, int SectionsFailed);
