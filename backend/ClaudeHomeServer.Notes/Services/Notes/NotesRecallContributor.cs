using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.Notes;

// Auto-recall заметок по тексту хода (F3, golden-фикстура 1): блок релевантных заметок
// для системного промпта + айтемы манифеста «использовано сейчас». Реестр Dify
// владельца проверяется внутри (Available/HasIndex) — гейт на каждый ход.
//
// Гейт IsEnabled повторяет прежний ClaudeSession.cs:2632 «_recallProvider is not null
// && _notesMcp is not null»: без notes-сервера искать по id заметки бессмысленно
// (notes_read не подключён). При выключенном MCP-контексте контрибьютор НЕ дёргается —
// golden-фикстура 5 ловит именно потерю этого гейтинга.
//
// TopK/MinScore/TimeoutMs — из конфига, как в прежнем BuildRecallProvider; читаются
// на каждый ход, чтобы правка конфига применялась без рестарта (как у других Func<…>
// в LlmSessionContext).
//
// Этап 5, шаг 6 (инверсия контрибьюторов промпта): контрибьютор переехал в вертикаль
// Notes из Services/Turn — Turn больше не знает про этот кусок. Регистрируется в
// `NotesSubsystem.Register` через DI как `IPromptSectionContributor`; Turn собирает
// `IEnumerable<IPromptSectionContributor>` и натравливает на шину, порядок секций
// задаётся `Order` (см. `TurnEventBus.ApplyAsync`).
public sealed class NotesRecallContributor : IPromptSectionContributor
{
    private readonly NotesKnowledgeService _notes;
    private readonly IConfiguration _config;
    private readonly ILogger<NotesRecallContributor> _log;

    public NotesRecallContributor(NotesKnowledgeService notes, IConfiguration config,
        ILogger<NotesRecallContributor> log)
    {
        _notes = notes;
        _config = config;
        _log = log;
    }

    // Шов для теста сбоя архива: скан раздела на живых файлах не бросает не-IO исключений.
    internal Func<string, string, WikiReadsStore.Snapshot, DateTimeOffset, IReadOnlySet<string>> DormantScan { get; init; }
        = WikiDormancy.DormantPages;

    public string Key => "recall-notes";
    public string Title => "Заметки, подходящие к вопросу";
    // После dossier-trailer (100), до recall-memory (300) — порядок проводки.
    public int Order => 200;
    public string Group => "recall";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.HasNotesMcp && sessionContext.OwnerId is not null
        // S2a-4 (разрез И.3, решение 29.10): в узкой рамке общего проекта личный vault в подборку
        // не идёт вовсе. Recall ищет по единому индексу заметок владельца (SearchAsync ключуется
        // userId, personal/project разнятся только хитом) — разделить их на этом контрибьюторе
        // нельзя, поэтому при Narrow он выключается целиком.
        && sessionContext.Frame == ClaudeHomeServer.Services.Access.TurnFrameKind.Wide;

    public async Task<PromptSectionContribution?> BuildAsync(
        PromptSessionContext sessionContext, string? turnText)
    {
        if (sessionContext.OwnerId is null) return null;
        if (turnText is null) return null;

        var topK = int.TryParse(_config["Notes:AutoRecallTopK"], out var k) ? k : 4;
        var minScore = double.TryParse(_config["Notes:AutoRecallMinScore"],
            System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0.35;
        var timeoutMs = int.TryParse(_config["Notes:AutoRecallTimeoutMs"], out var t) ? t : 2500;

        if (!_notes.Available || !_notes.HasIndex(sessionContext.OwnerId)) return null;

        var query = KnowledgeQueryUtilities.TrimQuery(turnText);
        if (query.Length == 0) return null;

        try
        {
            // Контекст вызова — из хвоста маршрута (ADR-012): сессия → проект → персона.
            // Набор видимых разделов считает WikiVisibility внутри scoped-SearchAsync
            // (ADR-036, решение 2): здесь только сам контекст.
            var callContext = new WikiVisibility.CallContext(
                sessionContext.Session?.ProjectId, sessionContext.Persona);
            var searchTask = _notes.SearchAsync(sessionContext.OwnerId, query, Math.Max(topK, 8), callContext);
            var completed = await Task.WhenAny(searchTask, Task.Delay(timeoutMs));
            if (completed != searchTask) return null;   // таймаут — ход без recall
            var hits = (await searchTask).Where(h => h.Score >= minScore).ToList();

            // R6 (ADR-039, реш. 7): спящая страница в блок не попадает — освободившийся слот
            // добирается следующим хитом. Отбрасывание — до Take(topK), порядок по score
            // сохраняется (фильтр порядка не меняет). Снимок стора учёта — один на ход.
            // Подготовка (корень, ключи страниц) — вне try архива: сбой скана не отменяет
            // учёт этого хода (минор 3, ревью 759c478e) — инвариант «каждое чтение страницы
            // вики, попавшей в блок, учитывается» не зависит от успеха архива.
            // Свой try скана: сбой архива не отменяет блок — ход получает recall как без него.
            // dataDir — та же формула, что у всех сервисов вертикали (DataPath указывает на projects.json).
            var dataDir = Path.GetDirectoryName(Path.GetFullPath(
                _config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))!;
            var wikiRoot = WikiPaths.Root(dataDir, sessionContext.OwnerId);
            var pageKeys = _notes.WikiPageKeysOf(sessionContext.OwnerId, hits);
            try
            {
                if (pageKeys.Count > 0)
                {
                    var snapshot = WikiReadsStore.Read(wikiRoot);
                    var dormant = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var section in pageKeys.Values.Select(WikiSections.Parse)
                                 .Where(k => k is not null).Distinct(StringComparer.Ordinal))
                        dormant.UnionWith(DormantScan(
                            wikiRoot, section!, snapshot, DateTimeOffset.UtcNow));
                    if (dormant.Count > 0)
                        hits = hits.Where(h => !pageKeys.TryGetValue(h.Id, out var key) || !dormant.Contains(key)).ToList();
                }
            }
            catch (Exception ex)
            {
                // сбой архива — ни исключения, ни отмены учёта: блок тот же, что без архива
                _log.LogWarning(ex, "Исключение спящих страниц вики для {Owner}", sessionContext.OwnerId);
            }

            // «Если в выдаче есть и страница вики, и заметка, которую она цитирует, остаётся
            // страница» (ADR-035, решение 8): заметка уступает место странице, освободившийся
            // слот topK добирается следующей заметкой. cited — ПОСЛЕ отбрасывания спящих
            // (минор 1, ревью 759c478e): страница, исключённая архивом, не гасит живую
            // заметку, которую цитировала, — иначе из блока уходили бы обе.
            var cited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in hits.Where(h => h.Source == NotesService.WikiKey))
                foreach (var id in _notes.CitedNoteIds(sessionContext.OwnerId, h))
                    cited.Add(id);
            if (cited.Count > 0)
                hits = hits.Where(h => h.Source == NotesService.WikiKey || !cited.Contains(h.Id)).ToList();

            hits = hits.Take(topK).ToList();
            if (hits.Count == 0) return null;
            var blockText = NotesKnowledgeService.BuildRecallBlock(hits, minScore, topK);
            if (string.IsNullOrWhiteSpace(blockText)) return null;
            var items = hits
                .Select(h => new RecallItem("note", h.Id, h.Title, h.Snippet))
                .ToList();

            // R6: учёт чтений, попавших в блок: вид Recall (слабый), ключ — путь страницы под
            // pages/ (стор R1). Учёт — после отбора, синхронно, вне бюджета поиска. Свой try:
            // сбой учёта не отменяет блок. Учёт не зависит от успеха архива: wikiRoot и
            // pageKeys готовы до рискового скана и catch их не обнуляет (минор 3, ревью 759c478e).
            try
            {
                var readItems = hits
                    .Where(h => pageKeys.TryGetValue(h.Id, out _))
                    .Select(h => (pageKeys[h.Id], WikiReadKind.Recall))
                    .ToList();
                if (readItems.Count > 0)
                    WikiReadsStore.RecordMany(wikiRoot, readItems, DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Учёт чтений страниц вики для {Owner}", sessionContext.OwnerId);
            }

            return new PromptSectionContribution(
                Sections: [new PromptSection(Key, blockText)],
                ManifestItems: items);
        }
        catch (Exception ex)
        {
            // recall заметок не должен ронять ход (прежнее поведение)
            _log.LogWarning(ex, "Auto-recall заметок для {Owner}", sessionContext.OwnerId);
            return null;
        }
    }
}