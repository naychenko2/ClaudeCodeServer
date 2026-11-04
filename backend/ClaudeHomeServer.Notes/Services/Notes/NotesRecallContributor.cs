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
            // «Если в выдаче есть и страница вики, и заметка, которую она цитирует, остаётся
            // страница» (ADR-035, решение 8): заметка уступает место странице, освободившийся
            // слот topK добирается следующей заметкой.
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