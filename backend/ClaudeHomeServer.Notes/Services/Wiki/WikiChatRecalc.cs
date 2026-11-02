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
public sealed class WikiChatRecalc
{
    private readonly ISessionDirectory _sessions;
    private readonly string _dataDir;

    public WikiChatRecalc(ISessionDirectory sessions, IConfiguration config)
    {
        _sessions = sessions;
        // Каталог данных — как у всех сервисов вертикали (тот же корень, что у Хранителя).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    /// <summary>Итог пересчёта. Отказ: Ok=false, Code — машинный код стора (wiki_*), Reason —
    /// строка для человека; Counted=0, Counts пустые (на отказе их никто не показывает), файл
    /// стора не изменён. Успех: число пересчитанных чатов и counts по зонам ступени 1 (их
    /// посчитал скорер; список показывает их с приоритетом исхода разбора — ShownZone).</summary>
    public sealed record RecalcResult(
        bool Ok, string? Code, string? Reason,
        int Counted, IReadOnlyDictionary<string, int> Counts);

    /// <summary>Пересчёт по владельцу: ступень 0 и балл ступени 1 по всем его чатам, включая
    /// архивные (ADR-035 §12в считает бэклог «по всем чатам владельца»; архивность — способ
    /// хранения, не признак бессмысленности), результат — в стор по каждому чату. Отказ стора
    /// (нечитаемый файл или сбой записи посреди цикла) — RecalcResult с кодом и причиной:
    /// уже записанные чаты на диске остаются (запись шла по одному чату под блоком корня),
    /// цикл на отказе останавливается. Исключения наружу нет.</summary>
    public async Task<RecalcResult> RecalculateAsync(string ownerId)
    {
        var root = WikiPaths.Root(_dataDir, ownerId);
        var read = WikiChatTriageStore.Get(root);
        if (read.State == WikiTriageFileState.Invalid)
            return new RecalcResult(false, WikiChatTriageStore.CodeInvalid, "файл разбора чатов не читается", 0, new Dictionary<string, int>());

        var streaks = new Dictionary<string, int>();
        foreach (var e in read.Entries)
            streaks[e.SessionId] = e.NothingNewStreak;

        var counts = new Dictionary<string, int>();
        int counted = 0;
        foreach (var session in _sessions.GetAll().Where(s => _sessions.ResolveOwnerId(s) == ownerId))
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
                res.Zone, res.Reason);
            if (!up.Ok)
                return new RecalcResult(false, up.Code, up.Reason, 0, counts);
            counts[res.Zone] = counts.TryGetValue(res.Zone, out var c) ? c + 1 : 1;
            counted++;
        }
        return new RecalcResult(true, null, null, counted, counts);
    }
}
