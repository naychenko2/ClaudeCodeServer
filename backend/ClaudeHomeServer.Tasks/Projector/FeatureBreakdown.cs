using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Spend;
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
    // Токены группы (вход+выход записей расхода); null — записей расхода для группы нет.
    long? Tokens,
    // Деньги (USD): сумма надёжных цен (free → 0); null — есть запись без надёжной цены или
    // записей расхода для группы нет.
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
    // Токены задачи (вход+выход записей расхода); null — записей расхода для задачи нет.
    long? Tokens,
    // Доля токенов задачи в суммарных токенах среза (0..1); null — записей расхода нет.
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
        DateTime now,
        // Записи расхода фичи (шов ISpendFeatureReader, Б9b): токены/стоимость групп и
        // LongestNow. null/пусто — расхода нет (подсистема Spend выключена) → токены/стоимость
        // групп и LongestNow = null (нет данных, не 0).
        IReadOnlyList<FeatureSpendRecord>? records = null)
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

        // Work-интервал задачи: [ClaudeStartedAt, min(CompletedAt, ExecutorStoppedAt, now));
        // null — не стартовала. Конец — FeatureActivityBuilder.WorkEnd (единый модуль активности, шаг 3).
        (DateTime Start, DateTime End)? WorkInterval(TaskItem t)
        {
            if (t.ClaudeStartedAt is null) return null;
            var start = t.ClaudeStartedAt.Value;
            var end = FeatureActivityBuilder.WorkEnd(t, now);
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

        // Группировка по ключу: key → (work-интервалы, chatIds) + токены/стоимость из записей
        // расхода того же ключа. key null/пусто → задача/запись в этот разрез не входит.
        // ДОКУМЕНТИРОВАННОЕ ПРАВИЛО (минор Б9b): запись ключевого (оркестраторского) чата без
        // TaskId/PersonaId/Model не резолвится ни в одну ось → входит в ИТОГ расхода (срез по
        // чату), но не в этот разрез. Сумма разрезов может быть < итога — осознанно, не баг.
        // ChatIds — id чата задачи, только если в снимке владельца (fail-closed).
        // Токены = вход+выход записей; стоимость = сумма надёжных цен (free → 0), null — если есть
        // запись без надёжной цены (ненадёжная) или записей расхода для группы нет вовсе (нет данных).
        List<FeatureBreakdownGroup> GroupBy(Func<TaskItem, string?> keyOf,
            Func<FeatureSpendRecord, string?> keyOfRecord)
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

            // Агрегат записей расхода по тому же ключу: токены (вход+выход), стоимость (надёжная),
            // признак ненадёжной записи. Ключ записи null/пусто → запись в этот разрез не входит.
            // Надёжная стоимость — единая точка правды (SpendCostReliability.ReliableCostOf,
            // доработка Б9c): 0 — free, null — ненадёжно (флаг UnreliableCost / нет цены при
            // Source != free / NaN или ∞). На неё же переходит итог расхода (SpendFeatureReader)
            // и сплиттер (SharedSpendSplitter) — своих формул «цена надёжна» больше нет,
            // иначе разрез спорил бы с итогом (deepseek cost=2.0: итог 0, разрез 2.0, ревью Глеба).
            var spend = new Dictionary<string, (long Tokens, double Cost, bool HasUnreliable)>();
            if (records is not null)
            {
                foreach (var r in records)
                {
                    var key = keyOfRecord(r);
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    // a — локальная копия; после обновления ОБЯЗАТЕЛЬНО пишем обратно в словарь
                    // (tuple — value type: TryGetValue отдаёт копию, сам словарь не мутируется).
                    if (!spend.TryGetValue(key, out var a))
                        a = (0, 0, false);
                    // Минор Б9b: кламп отрицательных токенов и в разрезе (как в итоге,
                    // SpendFeatureReader): сбой CLI с отрицательным числом не тянет группу в минус.
                    // Надёжная стоимость: null → 0 в сумму + HasUnreliable (консервативный null группы).
                    var cost = SpendCostReliability.ReliableCostOf(r);
                    a = (a.Tokens + Math.Max(0, r.InputTokens) + Math.Max(0, r.OutputTokens),
                        a.Cost + (double)(cost ?? 0),
                        a.HasUnreliable || cost is null);
                    spend[key] = a;
                }
            }

            var result = new List<FeatureBreakdownGroup>(groups.Count);
            foreach (var (key, (intervals, chatIds)) in groups)
            {
                // Токены/стоимость: null — записей расхода для группы нет (нет данных); иначе
                // токены = сумма, стоимость = null, если есть ненадёжная запись, иначе сумма цен.
                long? tokens = null;
                decimal? cost = null;
                if (spend.TryGetValue(key, out var a))
                {
                    tokens = a.Tokens;
                    cost = a.HasUnreliable ? null : (decimal)a.Cost;
                }
                result.Add(new FeatureBreakdownGroup(
                    Key: key,
                    Minutes: FeatureTimeMath.UnionMinutes(intervals),
                    Tokens: tokens,
                    CostUsd: cost,
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

        // Ключ записи расхода по оси «роли»: TaskId записи → Role задачи (та же ось, что t.Role).
        var roleByTask = tasks.Where(t => t.Id is not null).ToDictionary(t => t.Id!, t => t.Role);
        string? RoleOfRecord(FeatureSpendRecord r) =>
            r.TaskId is not null && roleByTask.TryGetValue(r.TaskId, out var role) ? role : null;

        var breakdown = new FeatureBreakdown(
            Personas: GroupBy(t => t.PersonaId, r => r.PersonaId),
            Models: GroupBy(ModelKey, r => UnifiedModelKey(r.Model)),
            Roles: GroupBy(t => t.Role, RoleOfRecord));

        // «Дольше всех сейчас»: самая долгая сейчас идущая задача (ClaudeStartedAt, без
        // CompletedAt). Minutes = now − ClaudeStartedAt. ChatId — fail-closed (только из снимка).
        // Tokens/Share — из записей расхода (Б9b): токены задачи (вход+выход) и её доля в
        // суммарных токенах среза; null — записей расхода нет (нет данных). Ничего не идёт → null.
        // Минор 5: «идущая» = InProgress БЕЗ waiting/stopped: задача, ждущая человека
        // (AwaitingHumanSince) или остановленная (ExecutorStoppedAt), статусом InProgress, но
        // реально не работает — в «дольше всех сейчас» не попадает.
        var totalTokens = records is null ? 0L : records.Sum(r => r.InputTokens + r.OutputTokens);
        // Токены по TaskId одним предпроходом (минор Б9b: раньше O(задачи×записи) —
        // на каждую идущую задачу скан ВСЕХ записей; теперь словарь, O(записи+задачи)).
        var tokensByTask = new Dictionary<string, (long Tokens, bool Has)>();
        if (records is not null)
            foreach (var r in records)
                if (r.TaskId is not null)
                {
                    var acc = tokensByTask.TryGetValue(r.TaskId, out var a) ? a : ((Tokens: 0L, Has: false));
                    tokensByTask[r.TaskId] = (acc.Tokens + r.InputTokens + r.OutputTokens, true);
                }
        FeatureLongestNow? longest = null;
        DateTime? longestStart = null;
        foreach (var t in tasks)
        {
            if (t.ClaudeStartedAt is null || t.CompletedAt is not null) continue;
            if (t.AwaitingHumanSince is not null || t.ExecutorStoppedAt is not null) continue;
            if (longestStart is null || t.ClaudeStartedAt.Value < longestStart.Value)
            {
                longestStart = t.ClaudeStartedAt;
                // Токены задачи: вход+выход записей, чей TaskId == id задачи (из словаря).
                long taskTokens = 0;
                var hasTaskRecords = false;
                if (t.Id is not null && tokensByTask.TryGetValue(t.Id, out var acc))
                {
                    taskTokens = acc.Tokens;
                    hasTaskRecords = acc.Has;
                }
                longest = new FeatureLongestNow(
                    TaskId: t.Id,
                    ChatId: LiveSession(t.LinkedSessionId)?.Id,
                    PersonaId: t.PersonaId,
                    StepRef: t.StepRef,
                    Minutes: FeatureTimeMath.Minutes(t.ClaudeStartedAt.Value, now),
                    Tokens: hasTaskRecords ? taskTokens : null,
                    Share: totalTokens > 0 && hasTaskRecords ? (double)taskTokens / totalTokens : null);
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
