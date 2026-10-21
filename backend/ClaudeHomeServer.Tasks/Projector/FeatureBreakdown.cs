using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Journal;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// ─── Записи разрезов (JSON camelCase) ─────────────────────────────────────────────────────────

// Группа разреза: ключ (personaId | modelFull | taskRole), минуты работы (объединение интервалов
// задач группы), токены/деньги (до Б9 = null), id чатов группы (только из снимка владельца).
public sealed record FeatureBreakdownGroup(
    // Ключ группы: personaId | modelFull | taskRole (сырой).
    string Key,
    // Работа (минуты): объединение work-интервалов [ClaudeStartedAt, CompletedAt ?? now]
    // задач группы — параллельные задачи одной группы не складываются (как в Б2/Б5a).
    int Minutes,
    // Токены группы; null до Б9 (учёт расхода).
    long? Tokens,
    // Деньги (USD); null до Б9.
    decimal? CostUsd,
    // Id чатов группы: только из снимка сессий владельца (fail-closed: чужой/удалённый чат
    // не попадает).
    List<string> ChatIds);

// «Дольше всех сейчас» (GET features/{id}, stats.longestNow): самая долгая сейчас идущая задача;
// null — ничего не идёт. Tokens/Share = null до Б9 (учёт расхода).
public sealed record FeatureLongestNow(
    // Id задачи.
    string TaskId,
    // Чат задачи (fail-closed: только если в снимке владельца, иначе null).
    string? ChatId,
    // Исполнитель (PersonaId задачи).
    string? PersonaId,
    // Код шага (у step/rework).
    string? StepRef,
    // Сколько идёт (минуты): now − ClaudeStartedAt.
    int Minutes,
    // Токены; null до Б9.
    long? Tokens,
    // Доля в фиче (0..1); null до Б9.
    double? Share);

// ─── Разрезы времени фичи ─────────────────────────────────────────────────────────────────────

// Разрезы времени фичи (волна 3 «Обзор фич», вкладка «Статистика», Б6): чистая проекция
// FeatureRun → (FeatureBreakdown, FeatureLongestNow).
//
// Время по группе — работа (минуты): объединение work-интервалов [ClaudeStartedAt,
// CompletedAt ?? now] задач группы (параллельные задачи одной группы не складываются, как в
// Б2/Б5a).
//   • персоны — по PersonaId задач;
//   • роли — по TaskItem.Role (сырой);
//   • модели — модель чата задачи: из журнала (turn.model), если журнал полный
//     (StartsWithCreated && !Truncated), иначе из снимка сессий (FeatureIndexSession.Model +
//     резолв «Авто» через modelResolver — готовая функция Б4, не своя склейка слота).
// ChatIds — id чатов группы только из снимка сессий владельца (fail-closed).
// LongestNow — самая долгая сейчас идущая задача (ClaudeStartedAt, без CompletedAt); null —
// ничего не идёт.
public sealed record FeatureBreakdown(
    // По персонам: PersonaId задач.
    List<FeatureBreakdownGroup> Personas,
    // По моделям: модель чата задачи (из журнала, если полный, иначе из снимка + резолв «Авто»).
    List<FeatureBreakdownGroup> Models,
    // По ролям: TaskItem.Role (сырой).
    List<FeatureBreakdownGroup> Roles)
{
    // Чистая проекция: FeatureRun → (разрезы, «дольше всех сейчас»). now — «сейчас» (тесты
    // фиксируют). journal — журнал фичи (null — без журнала, модели из снимка). sessions — снимок
    // сессий владельца (fail-closed для ChatIds/LongestNow.ChatId). modelResolver — готовая
    // функция Б4 (резолв «Авто» → слот); null — модели из снимка как есть (тесты).
    public static (FeatureBreakdown Breakdown, FeatureLongestNow? LongestNow) Build(
        FeatureRun run,
        FeatureJournalReadResult? journal,
        IReadOnlyCollection<FeatureIndexSession> sessions,
        Func<FeatureIndexSession, (string? Label, string? Full)>? modelResolver,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(sessions);

        // Снимок сессий владельца → lookup по id (fail-closed: чужой/удалённый чат → null).
        var sessionById = sessions.ToDictionary(s => s.Id);
        FeatureIndexSession? LiveSession(string? chatId) =>
            chatId is not null && sessionById.TryGetValue(chatId, out var s) ? s : null;

        // Журнал полный: начинается с created и не обрезан → модели из журнала (turn.model).
        var journalComplete = journal is { StartsWithCreated: true, Truncated: false };
        // turn.model по chatId: первый непустой model среди ходов чата (у чата одна модель).
        Dictionary<string, string>? journalModelByChat = null;
        if (journalComplete && journal is not null)
        {
            journalModelByChat = new Dictionary<string, string>();
            foreach (var e in journal.Entries)
            {
                if (e.Kind != "turn" || e.SessionId is null || string.IsNullOrWhiteSpace(e.Model))
                    continue;
                journalModelByChat.TryAdd(e.SessionId, e.Model);
            }
        }

        // Все задачи фичи (эпик + дочерние) — та же выборка, что FeatureTimeline.AllTasks.
        var tasks = AllTasks(run).ToList();

        // Work-интервал задачи: [ClaudeStartedAt, CompletedAt ?? now]; null — не стартовала.
        (DateTime Start, DateTime End)? WorkInterval(TaskItem t)
        {
            if (t.ClaudeStartedAt is null) return null;
            var start = t.ClaudeStartedAt.Value;
            var end = t.CompletedAt ?? now;
            if (end < start) end = start;
            return (start, end);
        }

        // Ключ модели задачи: из журнала (полный) или из снимка (резолв «Авто» через
        // modelResolver — готовая функция Б4). Чат вне снимка и без журнала → null (модель не
        // известна — задача в разрез моделей не входит).
        // Минор 6: ЕДИНЫЙ ключ (modelFull) — сырой id журнала резолвится в то же имя, что снимок
        // (через modelResolver), иначе одна модель попадала бы под два ключа (сырой id vs подпись).
        // Чаты без ходов в журнале — осознанный откат на снимок (модель из журнала неизвестна).
        string? ModelKey(TaskItem t)
        {
            var chatId = t.LinkedSessionId;
            if (journalComplete && chatId is not null && journalModelByChat is not null
                && journalModelByChat.TryGetValue(chatId, out var jm))
                return UnifiedModelKey(jm);
            var chat = LiveSession(chatId);
            if (chat is null) return null;
            if (modelResolver is not null)
                return modelResolver(chat).Full;
            return chat.Model; // без резолвера (тесты) — модель снимка как есть
        }

        // Сырой id модели → единый ключ (то же имя, что даёт снимок через modelResolver):
        // синтетическая сессия с этой моделью → modelResolver разворачивает как явную модель.
        // Без резолвера (тесты) — id как есть (тогда и снимок отдаёт id как есть — ключи совпадают).
        string? UnifiedModelKey(string? rawModel)
        {
            if (modelResolver is null) return rawModel;
            var synthetic = new FeatureIndexSession("", null, null, false, rawModel);
            return modelResolver(synthetic).Full;
        }

        // Группировка по ключу: key → (work-интервалы, chatIds). key null/пусто → задача в этот
        // разрез не входит. ChatIds — id чата задачи, только если в снимке владельца (fail-closed).
        List<FeatureBreakdownGroup> GroupBy(Func<TaskItem, string?> keyOf)
        {
            var groups = new Dictionary<string, (List<(DateTime Start, DateTime End)> Intervals, List<string> ChatIds)>();
            foreach (var t in tasks)
            {
                var key = keyOf(t);
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (!groups.TryGetValue(key, out var g))
                    groups[key] = g = (new List<(DateTime, DateTime)>(), new List<string>());
                var wi = WorkInterval(t);
                if (wi is not null) g.Intervals.Add((wi.Value.Start, wi.Value.End));
                if (t.LinkedSessionId is not null && LiveSession(t.LinkedSessionId) is not null)
                    g.ChatIds.Add(t.LinkedSessionId);
            }

            var result = new List<FeatureBreakdownGroup>(groups.Count);
            foreach (var (key, (intervals, chatIds)) in groups)
            {
                result.Add(new FeatureBreakdownGroup(
                    Key: key,
                    Minutes: FeatureTimeMath.UnionMinutes(intervals),
                    Tokens: null, // до Б9
                    CostUsd: null, // до Б9
                    ChatIds: chatIds.Distinct().OrderBy(c => c).ToList()));
            }
            // Сортировка по Minutes убыв; при равенстве — по Key (детерминизм).
            result.Sort((a, b) =>
            {
                var c = b.Minutes.CompareTo(a.Minutes);
                return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
            });
            return result;
        }

        var breakdown = new FeatureBreakdown(
            Personas: GroupBy(t => t.PersonaId),
            Models: GroupBy(ModelKey),
            Roles: GroupBy(t => t.Role));

        // «Дольше всех сейчас»: самая долгая сейчас идущая задача (ClaudeStartedAt, без
        // CompletedAt). Minutes = now − ClaudeStartedAt. ChatId — fail-closed (только из снимка).
        // Tokens/Share = null до Б9. Ничего не идёт → null.
        // Минор 5: «идущая» = InProgress БЕЗ waiting/stopped: задача, ждущая человека
        // (AwaitingHumanSince) или остановленная (ExecutorStoppedAt), статусом InProgress, но
        // реально не работает — в «дольше всех сейчас» не попадает.
        FeatureLongestNow? longest = null;
        DateTime? longestStart = null;
        foreach (var t in tasks)
        {
            if (t.ClaudeStartedAt is null || t.CompletedAt is not null) continue;
            if (t.AwaitingHumanSince is not null || t.ExecutorStoppedAt is not null) continue;
            if (longestStart is null || t.ClaudeStartedAt.Value < longestStart.Value)
            {
                longestStart = t.ClaudeStartedAt;
                longest = new FeatureLongestNow(
                    TaskId: t.Id,
                    ChatId: LiveSession(t.LinkedSessionId)?.Id,
                    PersonaId: t.PersonaId,
                    StepRef: t.StepRef,
                    Minutes: FeatureTimeMath.Minutes(t.ClaudeStartedAt.Value, now),
                    Tokens: null, // до Б9
                    Share: null); // до Б9
            }
        }

        return (breakdown, longest);
    }

    // ─── Вспомогательные ──────────────────────────────────────────────────────────────────────

    // Все задачи фичи: эпик + дочерние (та же выборка, что FeatureTimeline.AllTasks).
    private static IEnumerable<TaskItem> AllTasks(FeatureRun run)
    {
        yield return run.Epic;
        foreach (var t in run.Steps) yield return t;
        foreach (var t in run.Reviews) yield return t;
        foreach (var t in run.Qa) yield return t;
        if (run.Final is not null) yield return run.Final;
        if (run.Merge is not null) yield return run.Merge;
        foreach (var t in run.Cut) yield return t;
        foreach (var t in run.Plan) yield return t;
    }

    // (Объединение/минуты/обрезка — в общем FeatureTimeMath, дедуп с FeatureTimeline, минор 8.)
}
