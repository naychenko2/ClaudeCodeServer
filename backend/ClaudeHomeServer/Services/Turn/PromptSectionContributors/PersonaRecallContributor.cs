using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Turn;

// Auto-recall долгой памяти персоны (F3): релевантные записи памяти по тексту хода +
// (опционально, при включённом change-dossiers-recall и проектном чате) паспорта
// изменений как ОТДЕЛЬНАЯ секция dossier-recall. Гейт один и тот же (MemoryMcp != null),
// поэтому обе секции выезжают из одного контрибьютора: раздельные промптеры плодили
// бы дубль флагов и риск потери одного из них.
//
// Гейт IsEnabled повторяет прежний ClaudeSession.cs:2798 «_personaRecallProvider is
// not null && _memoryMcp is not null». Без memory-сервера искать по id записи
// бессмысленно (memory_* не подключены). golden-фикстура 6 ловит именно потерю гейта.
//
// Якоря паспортов (файлы предыдущего хода) и EffectiveRootOf — это per-session знание,
// но доступное через ChatHistoryService и ProjectManager: поднимаем в singleton через
// DI и читаем на каждый ход, как прежний BuildPersonaRecallProvider.
public sealed class PersonaRecallContributor : IPromptSectionContributor
{
    private readonly PersonaMemoryService _personaMemory;
    private readonly Dossiers.DossierRecallService? _dossierRecall;
    private readonly FeatureFlagService _flags;
    private readonly ChatHistoryService _history;
    private readonly ProjectManager _projects;
    private readonly IConfiguration _config;
    private readonly ILogger<PersonaRecallContributor> _log;

    // Кеш «файлы прошлого хода» на чат: BuildRecallAsync бежит каждый ход, перечитывать
    // историю на нём дороже самих recall'ов. Прежний SessionManager держал такой же.
    private readonly Dictionary<string, (DateTime? Stamp, IReadOnlyList<string> Files)> _anchorCache = new();
    private readonly object _anchorLock = new();

    public PersonaRecallContributor(
        PersonaMemoryService personaMemory,
        Dossiers.DossierRecallService? dossierRecall,
        FeatureFlagService flags,
        ChatHistoryService history,
        ProjectManager projects,
        IConfiguration config,
        ILogger<PersonaRecallContributor> log)
    {
        _personaMemory = personaMemory;
        _dossierRecall = dossierRecall;
        _flags = flags;
        _history = history;
        _projects = projects;
        _config = config;
        _log = log;
    }

    public string Key => "recall-memory";
    public string Title => "Что персона помнит по теме";
    // После recall-notes (200), до prompt-sections (400) — порядок проводки.
    public int Order => 300;
    public string Group => "persona";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.HasMemoryMcp && sessionContext.OwnerId is not null && sessionContext.Persona is not null;

    public async Task<PromptSectionContribution?> BuildAsync(
        PromptSessionContext sessionContext, string? turnText)
    {
        if (sessionContext.OwnerId is null || sessionContext.Persona is null) return null;
        if (turnText is null) return null;

        var topK = int.TryParse(_config["Persona:RecallTopK"], out var k) ? k : 5;
        var minScore = double.TryParse(_config["Persona:RecallMinScore"],
            System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0.30;
        var timeoutMs = int.TryParse(_config["Persona:RecallTimeoutMs"], out var t) ? t : 2500;

        var query = KnowledgeService.TrimQuery(turnText);
        if (query.Length == 0) return null;

        try
        {
            var session = sessionContext.Session;

            // Контекст паспортов (ADR-004 §5): проект чата + якоря + текст хода. null —
            // вне проектного контекста (нет ProjectId у сессии) или выключен флаг.
            Dossiers.DossierRecallRequest? dossier = null;
            if (_dossierRecall is not null && session.ProjectId is { } dossierProjectId
                && _flags.IsEnabled(sessionContext.OwnerId, FeatureFlagKeys.ChangeDossiersRecall))
            {
                var prevTurnFiles = await LastTurnChangedFilesAsync(session);
                dossier = new Dossiers.DossierRecallRequest(
                    dossierProjectId,
                    EffectiveRootOf(session),
                    session.TaskId,
                    [.. Dossiers.DossierRecallService.ExtractPathsFromText(turnText), .. prevTurnFiles],
                    turnText);
            }

            // Выделение досье в свою секцию (план «Секции промптов» этап 3) — под тем же
            // флагом, что и prompt-sections (единый dark launch). Выключен — досье остаётся
            // внутри recall-memory, как до фичи.
            var splitDossier = _flags.IsEnabled(sessionContext.OwnerId, FeatureFlagKeys.SpecialtyPromptSections);
            var recallTask = _personaMemory.BuildRecallAsync(
                sessionContext.OwnerId, sessionContext.Persona.Id, query,
                topK, minScore, dossier, splitDossier);
            var completed = await Task.WhenAny(recallTask, Task.Delay(timeoutMs));
            if (completed != recallTask) return null;
            var recall = await recallTask;
            if (recall?.Text is null && recall?.DossierText is null) return null;

            var items = (recall!.Hits.Select(h => new RecallItem("memory", h.Id, h.Text, null)))
                .Concat(recall.TeamHits.Select(e => new RecallItem("team", e.Id, e.Text, null)))
                .Concat(recall.DossierHits.Select(d => new RecallItem("dossier", d.Id,
                    $"Паспорт {d.CommitSha[..Math.Min(7, d.CommitSha.Length)]}: {d.CommitSubject}", null)))
                .ToList();

            // Селекция секций: основной блок всегда идёт; dossier-блок — только когда
            // выделен (splitDossier=true) и непустой, иначе секция не добавляется.
            var sections = new List<PromptSection>
            {
                new(Key, recall.Text ?? string.Empty),
            };
            if (splitDossier && !string.IsNullOrWhiteSpace(recall.DossierText))
            {
                sections.Add(new PromptSection("dossier-recall", recall.DossierText!));
            }
            return new PromptSectionContribution(sections, items);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Persona memory recall для {Persona}",
                sessionContext.Persona.Id);
            return null;
        }
    }

    // Рабочее дерево сессии (ADR-003): у чата с worktree своё дерево — паспорта и их
    // статусы считаются по нему. Прежний SessionManager.EffectiveRootOf.
    private string? EffectiveRootOf(Session session)
    {
        if (session.WorktreePath is { } wt) return wt;
        return session.ProjectId is { } pid ? _projects.GetById(pid)?.RootPath : null;
    }

    // Якоря «файлы предыдущего хода» (ADR-004 §5) с кешем по LastWriteUtc истории.
    private async Task<IReadOnlyList<string>> LastTurnChangedFilesAsync(Session session)
    {
        try
        {
            var stamp = session.ClaudeSessionId is null ? null : _history.LastWriteUtc(session.ClaudeSessionId);
            lock (_anchorLock)
            {
                if (_anchorCache.TryGetValue(session.Id, out var cached) && cached.Stamp == stamp)
                    return cached.Files;
            }
            if (session.ClaudeSessionId is null) return [];

            var history = await _history.LoadAsync(session.ClaudeSessionId);

            // Хвост от предпоследнего сообщения пользователя: последнее — текущий ход
            // (уже дописан к моменту сборки промпта) либо прошлый; в обоих случаях
            // последний ЗАВЕРШЁННЫЙ ход попадает в диапазон.
            var userIdx = new List<int>();
            for (var i = 0; i < history.Count; i++)
                if (history[i] is StoredUserMessage) userIdx.Add(i);
            var start = userIdx.Count >= 2 ? userIdx[^2] : 0;
            var root = EffectiveRootOf(session) ?? "";
            List<string> files = root.Length == 0
                ? []
                : [.. SessionChangedPaths.Extract(history.Skip(start).ToList(), root).Keys];

            lock (_anchorLock) _anchorCache[session.Id] = (stamp, files);
            return files;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "dossiers: якоря прошлого хода {Session}", session.Id);
            return [];
        }
    }
}