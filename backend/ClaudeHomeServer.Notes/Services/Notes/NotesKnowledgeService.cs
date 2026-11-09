using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;

namespace ClaudeHomeServer.Services.Notes;

// Семантический индекс заметок в Dify: отдельный dataset per-owner
// («{username}:notes», permission only_me). Синхронизация — полный дифф по хешам
// содержимого (устойчив к переименованиям/массовым правкам), с дебаунсом на мутации.
// Без настроенного Dify всё тихо выключено (graceful degradation).
// Страницы вики — в отдельный датасет «{username}:wiki» с метаданными wiki_section
// (ADR-035, решение 8; ADR-036, решение 6): в :notes вики не попадает, проекции _*
// не индексируются нигде (предикат WikiPaths.IsProjection + пропуск Scan).
public sealed class NotesKnowledgeService : Knowledge.IKnowledgeSyncParticipant, INoteSemanticIndex
{
    // userId → { datasetId, noteId → { difyDocId, contentHash } }
    private sealed class Entry
    {
        public string? DatasetId { get; set; }
        public Dictionary<string, DocRef> Docs { get; set; } = new();
        // Датасет вики и его документы (S2c): ключ записи — тот же noteId (id страницы
        // графа). Аддитивно к прежней форме: в старых файлах полей нет → null/пусто.
        public string? WikiDatasetId { get; set; }
        public Dictionary<string, DocRef> WikiDocs { get; set; } = new();
    }
    private sealed class DocRef
    {
        public string DocId { get; set; } = "";
        public string Hash { get; set; } = "";
    }

    private static readonly TimeSpan SyncDebounce = TimeSpan.FromSeconds(15);

    private readonly IKnowledgeIndex _knowledge;
    private readonly NotesService _notes;
    private readonly IUserStore _users;
    private readonly ILogger<NotesKnowledgeService> _logger;
    private readonly string _storePath;
    private readonly string _dataDir;
    private readonly Dictionary<string, Entry> _store;
    private readonly Lock _saveLock = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Timer> _debounce = new();
    // Горизонтали видимости (S6g-1): нужны только scoped-поиску модели — набор разделов
    // считает WikiVisibility.SectionsFor (ADR-036, решение 2), здесь он лишь запрашивается.
    // Опциональны, как у NotesToolset: тестовая обвязка без горизонталей обязана собираться.
    private readonly ISphereDirectory? _spheres;
    private readonly IProjectManager? _projects;

    public NotesKnowledgeService(IKnowledgeIndex knowledge, NotesService notes, IUserStore users,
        IConfiguration config, ILogger<NotesKnowledgeService> logger,
        ISphereDirectory? spheres = null, IProjectManager? projects = null)
    {
        _knowledge = knowledge;
        _notes = notes;
        _users = users;
        _logger = logger;
        _spheres = spheres;
        _projects = projects;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _storePath = Path.Combine(dataDir, "notes-knowledge.json");
        _dataDir = dataDir; // каскад удаления владельца трогает data/wiki/{userId}/ (ADR-035, реш. 13)
        _store = JsonFileStore.Load<Dictionary<string, Entry>>(_storePath) ?? new();
    }

    public bool Available => _knowledge.IsConfigured;

    // Есть ли у пользователя хоть один проиндексированный документ (без похода в Dify —
    // дешёвая отсечка для auto-recall на пустом индексе)
    public bool HasIndex(string userId)
    {
        var entry = GetEntry(userId);
        return !string.IsNullOrEmpty(entry.DatasetId) && entry.Docs.Count > 0;
    }

    // Id Dify-датасета заметок владельца («{username}:notes»); null — индекс ещё не создавался.
    // Нужен привязкам персон (PersonaBindingsService): датасет заметок — валидная цель Knowledge.
    public string? GetDatasetId(string userId) => GetEntry(userId).DatasetId;

    // Markdown-блок с релевантными заметками для системного промпта хода (auto-recall).
    // Пустой список / все ниже порога → null (нечего подмешивать).
    // `public`, а не `internal`: метод зовёт спина — `NotesRecallContributor` в слое
    // промпта хода. Это чистый форматтер (hits → строка), прятать нечего, а `internal`
    // заставлял вертикаль раздавать `InternalsVisibleTo` на Main, то есть держать часть
    // своего публичного контракта скрытой (находка ревью 2026-09-08).
    public static string? BuildRecallBlock(IReadOnlyList<NoteSemanticHit> hits, double minScore, int topK)
    {
        var top = hits.Where(h => h.Score >= minScore).Take(topK).ToList();
        if (top.Count == 0) return null;
        var sb = new StringBuilder();
        sb.AppendLine("Возможно релевантные заметки из базы знаний пользователя " +
                      "(авто-подбор по смыслу, может быть мимо — используй только если помогает):");
        foreach (var h in top)
            sb.AppendLine($"- [[{h.Title}]] ({h.SourceLabel}, id: {h.Id}) — {h.Snippet}");
        sb.AppendLine("Полный текст нужной заметки — через mcp__notes__notes_read по её id.");
        return sb.ToString();
    }

    // Отложенная синхронизация после мутации заметок (дебаунс — частые правки не спамят Dify)
    public void QueueSync(string userId)
    {
        if (!Available) return;
        _debounce.AddOrUpdate(userId,
            _ => new Timer(_ => RunSyncSafe(userId), null, SyncDebounce, Timeout.InfiniteTimeSpan),
            (_, timer) => { timer.Change(SyncDebounce, Timeout.InfiniteTimeSpan); return timer; });
    }

    private void RunSyncSafe(string userId) =>
        _ = Task.Run(async () =>
        {
            try { await SyncAllAsync(userId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Синхронизация заметок в Dify для {User}", userId); }
        });

    // Полная дифф-синхронизация заметок пользователя с его dataset
    public async Task<int> SyncAllAsync(string userId)
    {
        if (!Available) return 0;
        await _syncLock.WaitAsync();
        try
        {
            var entry = GetEntry(userId);
            if (string.IsNullOrEmpty(entry.DatasetId))
            {
                var username = _users.GetById(userId)?.Username ?? userId;
                entry.DatasetId = await _knowledge.CreateDatasetAsync($"{username}:notes");
                Save();
            }

            // Комментарии к документам в семантический индекс не попадают (шум: цитаты
            // дублируют сами документы); исчезнувшие из выборки чистятся штатной веткой ниже.
            // Вики — не в :notes (ADR-035, решение 8): страницы и карточки источников
            // идут в отдельный датасет :wiki ниже. Проекции _* исключены ещё Scan'ом
            // графа (тот же предикат WikiPaths.IsProjection); двойной guard ниже — страховка
            // контракта, а не второй путь фильтрации.
            var all = _notes.GetSummaries(userId, null, null)
                .Where(s => s.Annotation is null).ToList();
            var summaries = all.Where(s => s.Source != NotesService.WikiKey).ToList();
            var wikiSummaries = all.Where(s => s.Source == NotesService.WikiKey).ToList();
            var alive = new HashSet<string>(summaries.Select(s => s.Id));
            var changed = 0;

            foreach (var s in summaries)
            {
                var detail = _notes.GetDetail(userId, s.Id);
                if (detail is null) continue;
                var hash = Hash($"{detail.Title}\n{detail.Content}\n{string.Join(',', detail.Tags)}");
                if (entry.Docs.TryGetValue(s.Id, out var doc) && doc.Hash == hash) continue;

                // Пересоздание документа (update_by_text у Dify капризен к типам — проще delete+create)
                if (doc is not null)
                    try { await _knowledge.DeleteDocumentAsync(entry.DatasetId!, doc.DocId); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Удаление старого документа {Note}", s.Id); }

                var info = await _knowledge.IndexFileByTextAsync(
                    entry.DatasetId!, detail.Title, detail.Content, detail.Tags.ToList());
                entry.Docs[s.Id] = new DocRef { DocId = info.Id, Hash = hash };
                changed++;
            }

            // Удалённые заметки — из индекса
            foreach (var stale in entry.Docs.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                try { await _knowledge.DeleteDocumentAsync(entry.DatasetId!, entry.Docs[stale].DocId); }
                catch (Exception ex) { _logger.LogDebug(ex, "Удаление документа исчезнувшей заметки"); }
                entry.Docs.Remove(stale);
                changed++;
            }

            // ─── Вики: отдельный датасет «{username}:wiki» (ADR-035, решение 8; ADR-036, решение 6) ───
            // Датасет заводится лениво — по первой индексированной странице: пустой :wiki
            // на каждого владельца не нужен; чистка исчезнувших страниц идёт и когда
            // страниц уже нет (раздел удалён Хранителем) — иначе их документы осиротели бы
            // в индексе. Ключ и значение wiki_section — из WikiSections.Parse: единственный
            // разбор разделов; путь, не разбираемый в раздел, не индексируется (fail-closed,
            // та же семантика, что у видимости ADR-036). Проекции _* — не страницы: не уходят
            // ни в один датасет (Scan графа их уже исключил, этот guard — страхование контракта).
            if (wikiSummaries.Count > 0 || !string.IsNullOrEmpty(entry.WikiDatasetId))
            {
                if (string.IsNullOrEmpty(entry.WikiDatasetId))
                {
                    var username = _users.GetById(userId)?.Username ?? userId;
                    entry.WikiDatasetId = await _knowledge.CreateDatasetAsync($"{username}:wiki");
                    Save();
                }

                var aliveWiki = new HashSet<string>();
                foreach (var s in wikiSummaries)
                {
                    if (WikiPaths.IsProjection(s.Path)) continue;
                    var section = WikiSections.Parse(s.Path);
                    if (section is null) continue;
                    var detail = _notes.GetDetail(userId, s.Id);
                    if (detail is null) continue;
                    aliveWiki.Add(s.Id);

                    var hash = Hash($"{detail.Title}\n{detail.Content}\n{string.Join(',', detail.Tags)}");
                    if (entry.WikiDocs.TryGetValue(s.Id, out var wdoc) && wdoc.Hash == hash) continue;

                    if (wdoc is not null)
                        try { await _knowledge.DeleteDocumentAsync(entry.WikiDatasetId!, wdoc.DocId); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Удаление старого документа вики {Page}", s.Id); }

                    var info = await _knowledge.IndexFileByTextAsync(
                        entry.WikiDatasetId!, detail.Title, detail.Content, detail.Tags.ToList(),
                        metadata: new Dictionary<string, string> { ["wiki_section"] = section });
                    entry.WikiDocs[s.Id] = new DocRef { DocId = info.Id, Hash = hash };
                    changed++;
                }

                foreach (var stale in entry.WikiDocs.Keys.Where(k => !aliveWiki.Contains(k)).ToList())
                {
                    try { await _knowledge.DeleteDocumentAsync(entry.WikiDatasetId!, entry.WikiDocs[stale].DocId); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Удаление документа исчезнувшей страницы вики"); }
                    entry.WikiDocs.Remove(stale);
                    changed++;
                }
            }

            if (changed > 0) Save();
            return changed;
        }
        finally { _syncLock.Release(); }
    }

    // Семантический поиск: чанки Dify → заметки пользователя (по маппингу docId → noteId).
    // Без видимости разделов: это путь человека (REST UI — «человек в UI видит всю вики»,
    // ADR-036, решение 2) и не затронутые S6g-1 потребители. Путь модели — scoped-перегрузка
    // ниже: она обязана фильтровать, и отсутствие контекста здесь — не «забыли», а другой путь.
    public async Task<IReadOnlyList<NoteSemanticHit>> SearchAsync(string userId, string query, int topK = 8)
    {
        if (!Available) return [];
        var entry = GetEntry(userId);
        if (string.IsNullOrEmpty(entry.DatasetId)) return [];

        var chunks = await _knowledge.RetrieveAsync(entry.DatasetId!, query, topK);
        return MapHits(chunks, entry.Docs, _notes.GetSummaries(userId, null, null));
    }

    // Семантический поиск для пути модели (auto-recall и notes_semantic_search — один и тот
    // же путь, ADR-036, решение 6): с набором видимых разделов хода.
    //   - запрос к :wiki — фильтр «wiki_section ∈ набор» (OR по ключам) и top-k с запасом ×3:
    //     фильтр снижает шум в запросе Dify, но гарантией не является;
    //   - запрос к :notes — без метаданных (дом заметки уже в хите, второй копии в датасете
    //     не нужно);
    //   - гарантия — пост-фильтр WikiVisibility.Filter по каждому хиту обоих датасетов:
    //     дом хита :notes — источник (личный vault → personal, проект Q → projects/Q —
    //     PersonalKey и ключ личного раздела — одна строка, см. WikiSourceCapture.CaptureNote),
    //     дом хита :wiki — раздел пути (единственный разбор — WikiSections.Parse).
    // Чистая функция над хитами: общего состояния не трогает, кроме чтения реестра и графа.
    public async Task<IReadOnlyList<NoteSemanticHit>> SearchAsync(
        string userId, string query, int topK, WikiVisibility.CallContext context)
    {
        if (!Available) return [];
        // Горизонталей нет — набора нет: fail-closed, ничего не добавляем (тот же смысл,
        // что у отказа NotesToolset «Горизонталь вики не собрана»).
        if (_spheres is null || _projects is null) return [];
        var entry = GetEntry(userId);
        var visible = WikiVisibility.SectionsFor(userId, context, _spheres, _projects);
        var summaries = _notes.GetSummaries(userId, null, null);

        var hits = new List<NoteSemanticHit>();
        if (!string.IsNullOrEmpty(entry.DatasetId))
        {
            // Запрос к :notes — тоже с запасом ×3: пост-фильтр так же режет :notes-хиты
            // (их дом — источник), и без запаса топ-k, целиком состоящий из отбрасываемых
            // домов, даёт пустую выдачу при релевантных заметках P ниже по выдаче
            // (ADR-036, решение 6 — запас у обоих фильтруемых датасетов; усечение
            // выдачи до topK — ниже, после пост-фильтра).
            var chunks = await _knowledge.RetrieveAsync(entry.DatasetId!, query, topK * 3);
            hits.AddRange(MapHits(chunks, entry.Docs, summaries));
        }
        if (!string.IsNullOrEmpty(entry.WikiDatasetId))
        {
            var filters = visible
                .Select(k => new KnowledgeMetadataFilter("wiki_section", "is", k))
                .ToList();
            var chunks = await _knowledge.RetrieveAsync(
                entry.WikiDatasetId!, query, topK * 3, filters, logic: "or");
            hits.AddRange(MapHits(chunks, entry.WikiDocs, summaries));
        }

        // Сниппет видимой страницы может содержать ссылку на раздел вне набора (чанк Dify
        // берётся из содержимого): её заголовок — тоже текст, и он заменяется [скрыто] тем же
        // правилом, что в выдаче инструментов (ADR-036, решение 2). Ссылка, рассечённая
        // усечением чанка, — предел метода: не ловится.
        var kept = WikiVisibility.Filter(hits, visible, h => SectionKeyOf(h, summaries))
            .Select(h => h with { Snippet = WikiVisibility.RedactLinks(h.Snippet, visible) });
        return kept
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Id, StringComparer.Ordinal)   // равный score — детерминированный порядок
            .Take(topK)
            .ToList();
    }

    // Чанки Dify → хиты заметок: маппинг docId → id записи реестра → summary; одна заметка —
    // лучший чанк. Общий для обоих путей: второй копии маппинга в файле быть не должно.
    private static List<NoteSemanticHit> MapHits(
        IReadOnlyList<DifyRetrieveChunk> chunks, Dictionary<string, DocRef> docs,
        IReadOnlyList<NoteSummary> allSummaries)
    {
        // Не ToDictionary по DocId: на общем DocId он падает (см. MatchDocIds)
        var byDocId = Knowledge.KnowledgeSyncTarget.MatchDocIds(docs, d => d.DocId,
                chunks.Select(c => c.DocumentId).ToList())
            .ToLookup(p => p.DocId, p => p.EntryKey);
        var summaries = allSummaries.ToDictionary(s => s.Id);
        var hits = new List<NoteSemanticHit>();
        var seen = new HashSet<string>();
        foreach (var ch in chunks)
        {
            var snippet = ch.Content.Length > 220 ? ch.Content[..217] + "…" : ch.Content;
            foreach (var noteId in byDocId[ch.DocumentId])
            {
                if (!summaries.TryGetValue(noteId, out var note)) continue;
                if (!seen.Add(noteId)) continue;   // одна заметка — лучший чанк
                hits.Add(new NoteSemanticHit(noteId, note.Title, note.Source, note.SourceLabel,
                    Math.Round(ch.Score, 3), snippet.Replace('\n', ' ')));
            }
        }
        return hits;
    }

    // Дом хита для пост-фильтра: :notes — по источнику (личный vault «personal» — он же ключ
    // личного раздела; проект Q → projects/Q, невалидный id источника — null, fail-closed);
    // :wiki — по пути (WikiSections.Parse: неразобранный путь не видим нигде).
    private static string? SectionKeyOf(NoteSemanticHit hit, IReadOnlyList<NoteSummary> allSummaries)
    {
        var s = allSummaries.FirstOrDefault(n => n.Id == hit.Id);
        if (s is null) return null;
        return s.Source switch
        {
            // NotesService.PersonalKey == WikiSections.Personal == "personal"
            // (сквозной инвариант, зафиксирован в комментарии WikiSourceCapture.CaptureNote).
            WikiSections.Personal => WikiSections.Personal,
            NotesService.WikiKey => WikiSections.Parse(s.Path),
            _ => WikiSections.TryProjectId(WikiSections.ProjectsPrefix + s.Source, out _)
                ? WikiSections.ProjectsPrefix + s.Source
                : null,
        };
    }

    // Id обычных заметок, которые цитирует хит вики (для правила «при странице и цитируемой
    // ею заметке остаётся страница», ADR-035, решение 8):
    //   - карточка источника — ссылка в шапке: origin «note» + ref «note:{id}» (форма —
    //     WikiSourceCapture.CaptureNote);
    //   - страница — сносками на карточки (цель сноска — заголовок карточки,
    //     WikiPlanValidator.RenderApply); карточка того же раздела, что и страница, ищется
    //     по заголовку среди её страниц, её ref даёт id заметки (одноимённая карточка
    //     чужого раздела — другая карточка: цель сноски — голая ссылка, она
    //     внутрираздельная (ADR-035, решение 1).
    // Чтение — по графу NotesService (GetDetail/GetSummaries), без нового состояния.
    public IReadOnlyList<string> CitedNoteIds(string userId, NoteSemanticHit wikiHit)
    {
        var detail = _notes.GetDetail(userId, wikiHit.Id);
        if (detail is null) return [];
        var fm = WikiFrontmatter.Parse(detail.Content).Model;
        if (fm is null) return [];
        if (fm.Type == WikiFrontmatter.TypeSource)
            return fm.Origin == WikiGate.OriginNote && fm.Ref is { } r
                && r.StartsWith("note:", StringComparison.Ordinal)
                ? [r["note:".Length..]]
                : [];

        var page = WikiPage.Parse(detail.Content).Model;
        if (page is null) return [];
        var cardTitles = page.Footnotes
            .Where(f => f.Target.IsLink)
            .Select(f => f.Target.Text)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (cardTitles.Count == 0) return [];

        var ids = new List<string>();
        // Карточка ищется в том же разделе, что и страница: цель сноски — голая
        // внутрираздельная ссылка (ADR-035, решение 1), а одноимённая карточка чужого
        // раздела — другая карточка; выбор по заголовку среди всех разделов подставил бы
        // id чужого раздела и в cited попала бы заметка вне набора (MINOR 5, ревью R-S6g-1).
        var pageSection = WikiSections.Parse(detail.Path);
        if (pageSection is null) return [];   // страница вне раздела: «того же раздела» нет — цитаты нет
        var cards = _notes.GetSummaries(userId, NotesService.WikiKey, null);
        foreach (var title in cardTitles)
        {
            var card = cards.FirstOrDefault(c => c.Title == title
                && WikiSections.Parse(c.Path) == pageSection);
            if (card is null) continue;   // карточки этого раздела в графе нет — цитаты нет
            var cardFm = _notes.GetDetail(userId, card.Id) is { } cd
                ? WikiFrontmatter.Parse(cd.Content).Model : null;
            if (cardFm is null || cardFm.Type != WikiFrontmatter.TypeSource
                || cardFm.Origin != WikiGate.OriginNote || cardFm.Ref is not { } cr
                || !cr.StartsWith("note:", StringComparison.Ordinal))
                continue;
            ids.Add(cr["note:".Length..]);
        }
        return ids;
    }

    // --- Участник реконсайлера error-документов (Knowledge.IKnowledgeSyncParticipant) ---
    // Цель на каждого пользователя с созданным датасетом заметок; ключ записи — noteId.
    // Мутации entry.Docs в SyncAllAsync идут под _syncLock — берём его же, внутри _saveLock.
    public IReadOnlyList<Knowledge.KnowledgeSyncTarget> ListTargets()
    {
        List<(string UserId, string DatasetId)> snapshot;
        lock (_saveLock)
            snapshot = _store
                .Where(kv => !string.IsNullOrEmpty(kv.Value.DatasetId))
                .Select(kv => (kv.Key, kv.Value.DatasetId!))
                .ToList();

        return snapshot
            .Select(t => new Knowledge.KnowledgeSyncTarget(
                t.DatasetId, [t.UserId], $"notes:{t.UserId}",
                docIds => ResolveDocsAsync(t.UserId, docIds),
                keys => InvalidateDocsAsync(t.UserId, keys),
                () => QueueSync(t.UserId)))
            .ToList();
    }

    private async Task<IReadOnlyList<(string DocId, string EntryKey)>> ResolveDocsAsync(
        string userId, IReadOnlyCollection<string> docIds)
    {
        await _syncLock.WaitAsync();
        try
        {
            lock (_saveLock)
            {
                if (!_store.TryGetValue(userId, out var entry)) return [];
                return Knowledge.KnowledgeSyncTarget.MatchDocIds(entry.Docs, d => d.DocId, docIds);
            }
        }
        finally { _syncLock.Release(); }
    }

    private async Task InvalidateDocsAsync(string userId, IReadOnlyCollection<string> entryKeys)
    {
        await _syncLock.WaitAsync();
        try
        {
            lock (_saveLock)
            {
                if (!_store.TryGetValue(userId, out var entry)) return;
                var changed = false;
                foreach (var key in entryKeys)
                    if (entry.Docs.TryGetValue(key, out var doc) && doc.Hash.Length > 0)
                    {
                        // Замена объекта, не правка поля — на случай общих снапшотов
                        entry.Docs[key] = new DocRef { DocId = doc.DocId, Hash = "" };
                        changed = true;
                    }
                if (changed) JsonFileStore.Save(_storePath, _store);
            }
        }
        finally { _syncLock.Release(); }
    }

    // Уборка локальной записи индекса заметок и вики пользователя — каскад удаления
    // пользователя. Dify-датасеты (:notes и :wiki) удаляет вызывающий (общий проход по
    // префиксу имени «{username}:» в UserKnowledgeCascade): :wiki назван тем же шаблоном,
    // поэтому попадает в тот же проход без отдельного шага (ADR-035, решение 13).
    // Каталог data/wiki/{userId}/ — здесь: он принадлежит вертикали Notes, и другой
    // участник каскада про него не знает. Best-effort: сбой диска не роняет удаление
    // учётной записи (тот же приём, что у шагов самого каскада).
    public void DeleteUser(string userId)
    {
        lock (_saveLock)
        {
            _store.Remove(userId);
            JsonFileStore.Save(_storePath, _store);
        }
        try
        {
            var wikiRoot = WikiPaths.Root(_dataDir, userId);
            if (Directory.Exists(wikiRoot)) Directory.Delete(wikiRoot, recursive: true);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Каскад юзера {User}: каталог вики", userId); }
    }

    // Каскадное удаление знаний владельца через участник синка — обёртка над DeleteUser.
    // Dify не трогаем.
    public Task DeleteAllAsync(string userId)
    {
        DeleteUser(userId);
        return Task.CompletedTask;
    }

    private Entry GetEntry(string userId)
    {
        lock (_saveLock)
        {
            if (!_store.TryGetValue(userId, out var e)) _store[userId] = e = new Entry();
            return e;
        }
    }

    private void Save()
    {
        lock (_saveLock) JsonFileStore.Save(_storePath, _store);
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
