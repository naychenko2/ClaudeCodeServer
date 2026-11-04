using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Пересчёт ступени 1 отбора чатов (S6f, ADR-035, решение 12а; NA-3 — тело прежнего
// WikiChatsController.Recalculate, перенесён в службу без изменения логики): ступень 0 и балл
// ступени 1 по всем чатам владельца, результат — в стор. Ничего не запускает: ни модели, ни
// разбора (теневой режим). Курсор, зона и причина исхода разбора, nothingNewStreak
// существующих записей сохраняются: пересчёт пишет только свои поля (score/signals/
// stage1Zone/stage1Reason) через UpdateScoring под блокировкой корня — конкурентность стора
// не здесь, она в сторе. Нечитаемый стор — отказ с его кодом и причиной: пересчёт его не
// перезапишет молча (тот же fail-closed, что у стора), файл на диске не трогается.
//
// Вызывающий решает, как показать отказ: REST (кнопка «пересчитать») отображает код в 409,
// ночной разбор (Ф2) — не начинает ночь. Причина — строка для человека, прежний message
// ответа контроллера. Зоны и балл считает WikiChatScorer (чистая функция); показанная зона
// списка — забота читающего (WikiChatsController.List / ShownZone), здесь её нет.
//
// Приватные чаты (PrivateToUserId, FIN3-3) в пересчёт не входят: тот же инвариант, что у
// очереди ночи (BuildQueue) и разбора (AnalyzeAsync) — машинный потребитель не читает их
// вовсе. Их балл/зона/причина не пишутся в стор и не попадают в counts ответа: данные о
// чужих чатах не оседают в сторе разбора владельца.
public sealed class WikiChatRecalc
{
    private readonly ISessionDirectory _sessions;
    private readonly string _dataDir;
    private readonly ILogger<WikiChatRecalc>? _log;

    // Логгер необязательный: прямые вызовы new WikiChatRecalc(...) в тестах без журнала
    // остаются компилируемыми; DI (AddSingleton в NotesSubsystem) подставляет настоящий
    // ILogger<T> — через него отказ стора («нечитаемый файл») попадает в журнал, а не молчит.
    public WikiChatRecalc(ISessionDirectory sessions, IConfiguration config, ILogger<WikiChatRecalc>? log = null)
    {
        _sessions = sessions;
        _log = log;
        // Каталог данных — как у всех сервисов вертикали (тот же корень, что у Хранителя).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    /// <summary>Итог пересчёта. Отказ: Ok=false, Counted=0, Code — машинный код стора (wiki_*),
    /// Reason — строка для человека. Counts — по месту отказа: до цикла (файл нечитаем) — пустые,
    /// файл на диске не тронут; посреди цикла (отказ записи) — счётчики чатов, уже записанных
    /// до отказа: каждый писался по одному чату под блоком корня и остался на диске, цикл на
    /// отказе останавливается. Успех: число пересчитанных чатов и counts по зонам ступени 1 (их
    /// посчитал скорер; список показывает их с приоритетом исхода разбора — ShownZone).</summary>
    public sealed record RecalcResult(
        bool Ok, string? Code, string? Reason,
        int Counted, IReadOnlyDictionary<string, int> Counts);

    /// <summary>Пересчёт по владельцу: ступень 0 и балл ступени 1 по всем его чатам, включая
    /// архивные (ADR-035 §12в считает бэклог «по всем чатам владельца»; архивность — способ
    /// хранения, не признак бессмысленности), кроме приватных (PrivateToUserId, FIN3-3 —
    /// машинный потребитель их не читает). Результат — в стор по каждому чату. Отказ стора
    /// (нечитаемый файл или сбой записи посреди цикла) — RecalcResult с кодом и причиной:
    /// уже записанные чаты на диске остаются (запись шла по одному чату под блоком корня),
    /// цикл на отказе останавливается. Исключения наружу нет.</summary>
    public async Task<RecalcResult> RecalculateAsync(string ownerId)
    {
        var root = WikiPaths.Root(_dataDir, ownerId);
        var read = WikiChatTriageStore.Get(root, _log);
        if (read.State == WikiTriageFileState.Invalid)
            return new RecalcResult(false, WikiChatTriageStore.CodeInvalid, "файл разбора чатов не читается", 0, new Dictionary<string, int>());

        var streaks = new Dictionary<string, int>();
        foreach (var e in read.Entries)
            streaks[e.SessionId] = e.NothingNewStreak;

        var counts = new Dictionary<string, int>();
        int counted = 0;
        // Приватный чат (FIN3-3) отсекается здесь — до чтения истории и до записи в стор:
        // тот же фильтр, что у BuildQueue (WikiNightlyService) и AnalyzeAsync (WikiChatIngestService).
        foreach (var session in _sessions.GetAll().Where(s =>
                   _sessions.ResolveOwnerId(s) == ownerId && s.PrivateToUserId is null))
        {
            var history = await _sessions.GetHistoryAsync(session.Id);
            var res = WikiChatScorer.Score(new WikiChatScorer.WikiChatInput(
                session.TaskExecution, session.WikiMark, session.IsPinned,
                session.SummaryNoteId is not null,
                session.ContinuedFromSessionId, session.BranchedFromSessionId,
                session.CreatedAt, session.UpdatedAt,
                streaks.TryGetValue(session.Id, out var st) ? st : 0,
                history));
            var up = WikiChatTriageStore.UpdateScoring(
                root, session.Id, res.Score,
                [.. res.Signals.Select(s => new WikiTriageSignal(s.Name, s.Weight))],
                res.Zone, res.Reason, _log);
            if (!up.Ok)
                return new RecalcResult(false, up.Code, up.Reason, 0, counts);
            counts[res.Zone] = counts.TryGetValue(res.Zone, out var c) ? c + 1 : 1;
            counted++;
        }
        return new RecalcResult(true, null, null, counted, counts);
    }
}
