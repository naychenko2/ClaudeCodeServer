using System.Collections.Concurrent;
using ClaudeHomeServer.Core.Telemetry;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChatSubjects;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// Темы чатов проекта (docs/features/chat-subjects.md, план «AI-группировка чатов проекта по
// темам»): раскладка верхнеуровневых чатов проекта по реестру тем. Склейка с ядром сессий —
// здесь, рядом с ChatArchiveService; чистая часть (промпт, разбор, потолки, пачка) — в Core
// (Services/ChatSubjects). Образец устройства — ChatArchiveService: отложенный первый тик,
// PeriodicTimer, публичный TickAsync(nowUtc) для тестов, общий _passLock тика и ручного
// запуска.
//
// Модель — ТОЛЬКО локальная (решение Григория): RunLocalOnlyAsync, у которого нет ни шага
// claude, ни прямого облачного адаптера по построению, а маршрут места он сверяет на КАЖДОМ
// вызове (админ мог перевести место на облако посреди тика). RunFreeAsync здесь не зовётся
// никогда. Локаль не настроена, занята ходом или не ответила — чаты остаются без темы до
// следующего прохода, в облако не уходит ничего. Гейта онлайна устройства нет: разбор —
// серверный one-shot по серверной ленте history.json, устройство ему не нужно.
public class ChatSubjectSweeper(
    SessionManager sessions,
    ProjectManager projects,
    UserStore users,
    ICheapTextRunner cheap,
    ChatHistoryService history,
    IConfiguration config,
    ILogger<ChatSubjectSweeper> log,
    LocalActionRouter? router = null) : BackgroundService
{
    private const string ActionKey = LocalActionCatalog.ChatSubjects;

    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FirstTickDelay = TimeSpan.FromMinutes(5);

    // Фон берёт только остывшие чаты, в которых было хотя бы ~2 хода
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(15);
    public const int MinMessages = 4;
    // Пачек на владельца за тик: нагрузка на GPU-очередь ограничена сверху
    public const int MaxBatchesPerOwnerTick = 3;
    // Потолок пачки по времени (см. SubjectBatchPlanner.Plan) — машинная настройка
    public const int DefaultBatchCeiling = 8;

    public const string StateIdle = "idle";
    public const string StateRunning = "running";
    // Ответ запуска, когда замок занят проходом ДРУГОГО проекта (или тиком): у этого проекта
    // ничего не идёт, поэтому «running» был бы ложью. В состояние проекта не пишется.
    public const string StateBusy = "busy";

    // Тик и ручные запуски сериализуются: два прохода дрались бы за одни и те же чаты и
    // дважды заводили бы темы
    private readonly SemaphoreSlim _passLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SubjectRunState> _states = new();

    // Текущий ручной проход — тестам, чтобы дождаться конца без Task.Delay
    internal Task? CurrentManualRun { get; private set; }

    private int BatchCeiling => config.GetValue("ChatSubjects:BatchCeiling", DefaultBatchCeiling);

    // Маршрут места = локаль и движок настроен. HasFreeRoute сюда не годится: он пускает и
    // прямой облачный адаптер агрегатора. Это ранний выход, а не гарантия — гарантию даёт
    // RunLocalOnlyAsync, который сверяет маршрут на каждом вызове.
    public bool LocalRouteReady => cheap.UsesLocal(ActionKey);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(FirstTickDelay, ct); }
        catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await TickAsync(DateTime.UtcNow, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { log.LogError(ex, "Ошибка тика разбора чатов по темам"); }
            }
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
    }

    // Один фоновый проход по всем владельцам. nowUtc — параметром (тесты без ожиданий). Идёт
    // ручной проход — тик пропускается целиком, а не ждёт его. Локаль не настроена — тик молча
    // ставит нижние границы и выходит: ни вызова модели, ни строки в лог раз в 30 минут.
    public async Task TickAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        if (!await _passLock.WaitAsync(0, ct)) return;
        try
        {
            var ready = LocalRouteReady;
            foreach (var owner in users.GetAll())
            {
                var budget = MaxBatchesPerOwnerTick;
                foreach (var project in projects.GetByOwner(owner.Id))
                {
                    // Нижняя граница ставится на первом тике после выкатки фичи — даже без
                    // локали: «с момента выкатки», а не «с момента настройки модели»
                    var since = projects.EnsureSubjectsSweepSince(project.Id, nowUtc);
                    if (!ready || budget <= 0) continue;
                    try
                    {
                        var candidates = sessions.GetSubjectCandidates(project.Id, nowUtc, manual: false,
                            since, IdleThreshold, MinMessages);
                        if (candidates.Count == 0) continue;
                        var pass = await RunProjectAsync(project, candidates, nowUtc, budget, progress: null, ct);
                        budget -= pass.Batches;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Ошибка разбора чатов по темам в проекте {ProjectId}", project.Id);
                    }
                }
            }
        }
        finally { _passLock.Release(); }
    }

    public SubjectRunState GetState(string projectId) =>
        _states.TryGetValue(projectId, out var s) ? s : new SubjectRunState(StateIdle, 0, 0, null, null);

    // Кнопки «Разложить сейчас» (resetAi = false) и «Разложить заново» (true): фоновое
    // исполнение под общим замком. Занят — второй проход не запускается, ответ «идёт разбор».
    // Кнопка берёт все кандидаты проекта, включая залежи и чаты, которые модель уже видела.
    // Владение проверяет контроллер.
    public (bool Started, SubjectRunState State) StartManual(string projectId, bool resetAi, DateTime nowUtc)
    {
        if (!_passLock.Wait(0))
        {
            var cur = GetState(projectId);
            return (false, cur.State == StateRunning
                ? cur
                : cur with { State = StateBusy, LastError = "Идёт разбор в другом проекте — повторите через минуту." });
        }

        var state = new SubjectRunState(StateRunning, 0, 0, null, null);
        _states[projectId] = state;
        CurrentManualRun = Task.Run(async () =>
        {
            string? error = null;
            int done = 0, total = 0;
            try
            {
                if (projects.GetById(projectId) is not { } project) { error = "Проект не найден"; return; }
                // Локаль — ДО сброса: «Разложить заново» без модели стёр бы раскладку и ничего
                // не дал взамен
                if (!LocalRouteReady)
                {
                    error = "Локальная модель не настроена — чаты не разобраны. В облако реплики чатов не отправляются.";
                    return;
                }
                if (resetAi)
                {
                    var resets = sessions.GetByProject(projectId)
                        .Where(s => s.SubjectSource != SubjectSources.Human)
                        .Select(s => new SubjectWrite(s.Id, SubjectWriteKind.ResetAi))
                        .ToList();
                    sessions.ApplySubjects(projectId, resets, nowUtc);
                }
                var candidates = sessions.GetSubjectCandidates(projectId, nowUtc, manual: true);
                total = candidates.Count;
                _states[projectId] = new SubjectRunState(StateRunning, 0, total, null, null);
                var pass = await RunProjectAsync(project, candidates, nowUtc, int.MaxValue,
                    (d, t) => { done = d; _states[projectId] = new SubjectRunState(StateRunning, d, t, null, null); },
                    CancellationToken.None);
                done = pass.Done;
                if (pass.Stalled)
                    error = "Локальная модель не ответила или занята — часть чатов осталась без темы, повторите позже.";
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Ошибка ручного разбора чатов по темам в проекте {ProjectId}", projectId);
                error = "Разбор прервался из-за ошибки сервера.";
            }
            finally
            {
                _states[projectId] = new SubjectRunState(StateIdle, done, total, error, DateTime.UtcNow);
                _passLock.Release();
            }
        });
        return (true, state);
    }

    private sealed record PassResult(int Batches, int Done, bool Stalled);

    // Проход по одному проекту: пачки по планировщику, пока есть кандидаты и бюджет пачек.
    // Не ответившая локаль (null) останавливает проход без отметки попытки — эти чаты придут
    // следующим тиком. Разобранный или мусорный ответ — попытка отмечается всем чатам пачки
    // (в том числе пропущенным моделью и дублям алиасов), иначе вечный повтор того же вызова.
    private async Task<PassResult> RunProjectAsync(Project project, IReadOnlyList<Session> candidates,
        DateTime nowUtc, int maxBatches, Action<int, int>? progress, CancellationToken ct)
    {
        if (!ProjectCapabilities.ChatFeedForServerAi(project)) return new PassResult(0, 0, false);

        var spec = router?.ProfileFor(ActionKey) ?? LocalActionCatalog.ProfileDefaults[CheapProfile.Text];
        var newBudget = ChatSubjectRules.MaxNewPerPass;
        int batches = 0, done = 0, offset = 0;
        var stalled = false;

        while (offset < candidates.Count && batches < maxBatches)
        {
            ct.ThrowIfCancellationRequested();
            var registry = projects.GetSubjects(project.Id);
            var plan = SubjectBatchPlanner.Plan(spec, registry.Count, BatchCeiling);
            if (plan.BatchSize <= 0)
            {
                log.LogWarning("Темы чатов: профиль места {Action} не пускает ни одного чата в пачку", ActionKey);
                break;
            }

            var batch = candidates.Skip(offset).Take(plan.BatchSize).ToList();
            offset += batch.Count;

            // Снимок ДО вызова модели: CAS записи сверяет SubjectId с ним
            var expected = batch.ToDictionary(s => s.Id, s => s.SubjectId);
            var aliasToSession = new Dictionary<string, string>(StringComparer.Ordinal);
            var inputs = new List<SubjectChatInput>(batch.Count);
            for (var i = 0; i < batch.Count; i++)
            {
                var s = batch[i];
                var alias = "c" + (i + 1);
                aliasToSession[alias] = s.Id;
                IReadOnlyList<string> snippets = s.ClaudeSessionId is { } csid
                    ? await history.ReadHumanSnippetsAsync(csid, first: 1, last: 2, maxChars: plan.SnippetChars * 2)
                    : [];
                inputs.Add(new SubjectChatInput(alias, s.Name ?? "", snippets));
            }
            var regEntries = registry
                .Select((t, i) => new SubjectRegistryEntry("t" + (i + 1), t.Id,
                    ChatSubjectRules.SanitizeName(t.Name) ?? t.Name))
                .ToList();
            // Потолок MaxSubjectsPerProject — на темы МОДЕЛИ: темы человека его не съедают
            var aiCount = registry.Count(t => t.CreatedBy == SubjectSources.Ai);
            var allowedNew = Math.Min(newBudget, ChatSubjectRules.MaxSubjectsPerProject - aiCount);
            var prompt = SubjectPrompt.Build(regEntries, inputs, plan.SnippetChars, allowedNew);

            string? answer = null;
            try { answer = await cheap.RunLocalOnlyAsync(ActionKey, prompt, jsonFormat: "json", ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Темы чатов: локальная модель упала на пачке проекта {ProjectId}", project.Id);
            }
            batches++;

            if (string.IsNullOrWhiteSpace(answer))
            {
                log.LogInformation("Темы чатов: локальная модель не ответила (проект {ProjectId}), " +
                    "пачка из {Count} чатов ждёт следующего прохода", project.Id, batch.Count);
                stalled = true;
                break;
            }

            var writes = BuildWrites(project.Id, answer, aliasToSession, expected, regEntries,
                aiCount, ref newBudget, nowUtc);
            sessions.ApplySubjects(project.Id, writes, nowUtc);
            done += batch.Count;
            progress?.Invoke(done, candidates.Count);
        }

        // Уборка: тема модели без единого чата не живёт
        var used = sessions.GetByProject(project.Id)
            .Select(s => s.SubjectId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        projects.RemoveEmptyAiSubjects(project.Id, used);

        return new PassResult(batches, done, stalled);
    }

    private List<SubjectWrite> BuildWrites(string projectId, string answer,
        Dictionary<string, string> aliasToSession, Dictionary<string, string?> expected,
        IReadOnlyList<SubjectRegistryEntry> regEntries, int aiSubjectCount, ref int newBudget, DateTime nowUtc)
    {
        var parsed = SubjectPrompt.Parse(answer, aliasToSession.Keys.ToHashSet(StringComparer.Ordinal),
            regEntries.Select(r => r.Alias).ToHashSet(StringComparer.Ordinal));
        if (!parsed.Parsed)
        {
            ServerMetrics.ChatSubjectsParseFailed.Add(1);
            log.LogWarning("Темы чатов: ответ модели не разобран (проект {ProjectId}), длина {Length}",
                projectId, answer.Length);
            return aliasToSession.Values.Select(id => new SubjectWrite(id, SubjectWriteKind.Tried)).ToList();
        }

        var (targets, newNames) = SubjectPrompt.Resolve(parsed.Answers, regEntries, newBudget, aiSubjectCount);
        var created = projects.AddAiSubjects(projectId, newNames, nowUtc);
        newBudget -= newNames.Count;

        var writes = new List<SubjectWrite>(aliasToSession.Count);
        foreach (var (alias, sessionId) in aliasToSession)
        {
            string? subjectId = null;
            if (targets.TryGetValue(alias, out var t))
                subjectId = t.SubjectId
                    ?? (t.NewName is { } n && created.TryGetValue(ChatSubjectRules.NameKey(n), out var nid) ? nid : null);
            // Чат без ответа (пропущен моделью, дубль алиаса) — тоже попытка
            writes.Add(subjectId is null
                ? new SubjectWrite(sessionId, SubjectWriteKind.Tried)
                : new SubjectWrite(sessionId, SubjectWriteKind.Ai, subjectId, expected[sessionId]));
        }
        return writes;
    }
}

// Состояние ручного прохода по проекту для GET /api/projects/{id}/subjects/run
public sealed record SubjectRunState(string State, int Done, int Total, string? LastError, DateTime? FinishedAt);
