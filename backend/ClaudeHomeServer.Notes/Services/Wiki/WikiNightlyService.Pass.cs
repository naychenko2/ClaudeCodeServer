using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Тело прохода владельца (хвост R-W6a, шаг 0 доработки W5 №2: вынесено из WikiNightlyService.cs
// без изменения поведения). Здесь: состояние ряда отказов «модель недоступна» по исполнителям,
// сам проход (RunPassAsync с подготовкой чата PrepareAsync и обработкой его исхода ApplyResult),
// координатор слотов, признаки отказа и проверка «что мешает продолжать» перед каждой выдачей,
// очередь прохода. Тик, замок владельца, файл итога, строка журнала и уведомление — в основном
// partial; смысл и инварианты прохода описаны в шапке того файла.
public sealed partial class WikiNightlyService
{
    /// <summary>Выключатель мёртвого исполнителя (дефект 2e13d45c, J-O1; по исполнителю — с W5):
    /// три окна разбора подряд, отказанных этим исполнителем потому, что локальной модели нет
    /// (причина WikiIngestService.ReasonLocalUnavailable, InputTokens 0 — модель не звалась), —
    /// это не три несчастливых чата, а один и тот же отказ трижды: дальше по очереди у него
    /// будет то же самое, и ночь 12.11 сожгла так 41 отказ на проход. Исполнитель выбывает до
    /// конца прохода (ExecutorDownState): его слоты чатов больше не получают; если жив другой,
    /// проход доигрывает очередь на нём и остаётся completed. Выбыли все — проход стоит, итог
    /// error (дату не закрывает, следующий тик окна пробует снова — как любой error). Ряд
    /// считается по исполнителю, а не по месту: к ночи может быть назначено несколько движков
    /// (J-W2). Отказ «слот движка занят» в ряд не входит: движок занят, а не мёртв, —
    /// см. IsLocalDownRejection.</summary>
    private const int LocalDownBreakAfter = 3;

    /// <summary>Путь без названного исполнителя (пустой список стора — прежний маршрут места):
    /// в состоянии отказов (W5) он занимает этот ключ (пустая строка). «Исполнителей» там ровно
    /// один, поэтому счёт по нему совпадает с прежним общим рядом прохода (2e13d45c), а его
    /// выбывание даёт тот же итог error. В счётчиках итога (W6a) этот ключ не появляется: имени
    /// движка у пути нет (J-W2), Executors остаётся пустым.</summary>
    private const string RouteKey = "";

    /// <summary>Состояние отказов «модель недоступна» по исполнителям прохода (W5): ряд
    /// подряд идущих отказов ОДНОГО исполнителя и его выбывание до конца прохода. Ключ — имя
    /// движка из списка стора, RouteKey — путь без названного исполнителя. Живёт на
    /// проходе и меняется только его координатором/драйвером: исходы слотов обрабатываются
    /// одним потоком координатора, слоты к состоянию не приписываются. Счётчик ряда — по
    /// исполнителю, как прежний выключатель прохода: другой исход того же исполнителя обнуляет
    /// его ряд; отказ чужого исполнителя его не трогает вовсе.</summary>
    private sealed class ExecutorDownState
    {
        private static string Key(string? engine) => engine ?? RouteKey;

        private readonly Dictionary<string, int> _streak = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTimeOffset> _withdrawn = new(StringComparer.Ordinal);

        public bool IsWithdrawn(string? engine) => _withdrawn.ContainsKey(Key(engine));

        /// <summary>Зарегистрировать отказ «модель недоступна» этого исполнителя. true — ряд
        /// дошёл до LocalDownBreakAfter и исполнитель выбыл до конца прохода (время запомнено
        /// для журнала и для разбивки итога, W6a).</summary>
        public bool NoteRejection(string? engine, DateTimeOffset at)
        {
            var k = Key(engine);
            var n = (_streak.TryGetValue(k, out var cur) ? cur : 0) + 1;
            _streak[k] = n;
            if (n < LocalDownBreakAfter) return false;
            _withdrawn[k] = at;
            return true;
        }

        /// <summary>Любой исход исполнителя, не отказ «модель недоступна», обнуляет его ряд —
        /// состоявшийся разбор, отказ плана (модель отвечала) или отказ занятого слота (движок
        /// жив): ряд считается подряд идущими отказами одного вида, а не всей ночью (тот же
        /// смысл, что у 2e13d45c).</summary>
        public void NoteLive(string? engine) => _streak[Key(engine)] = 0;

        public bool AnyLive(IReadOnlyList<LocalEngineExecutor> executors)
        {
            if (executors.Count == 0) return !IsWithdrawn(null);
            foreach (var e in executors)
                if (!IsWithdrawn(e.Engine)) return true;
            return false;
        }

        public IReadOnlyDictionary<string, DateTimeOffset> Withdrawn => _withdrawn;
    }

    private sealed record Candidate(Session Session, WikiChatTriageEntry? Entry, bool Important, string Home);

    /// <summary>Исход подготовки чата до выдачи (W4a2): ровно одно из Stop/Skip/Ready.
    /// Ready — чат выбран (selected), его оценка зарезервирована в бюджете и счётчик раздела
    /// поднят: всё это ДО выдачи, чтобы оценка чата в полёте учитывалась против следующего
    /// («BudgetReservedBeforeIssue»). Cfg — настройки момента подготовки: верхняя проверка
    /// лимита пакетов ночи пользуется ими, как прежний цикл в пределах одной итерации.</summary>
    private sealed record Prep(bool Ready, string? Stop, WikiSettingsStore.NightlyConfig? Cfg, long ChatEst)
    {
        public static readonly Prep Skip = new(false, null, null, 0);
    }

    /// <summary>Исход одного слота координатора (W4a2): результат разбора или ошибка отказа —
    /// задача слота никогда не бывает faulted, кроме отмены хоста (см. RunCoordinatedAsync).
    /// Ссылки на чат и настройки момента выдачи: их результат обрабатывает координатор.</summary>
    private sealed record SlotOutcome(
        Candidate Chat, string Engine, WikiSettingsStore.NightlyConfig Cfg,
        WikiChatIngestService.ChatResult Result, Exception? Error);

    private async Task<NightResult> RunPassAsync(string ownerId, string root, DateOnly date, bool manual, CancellationToken ct)
    {
        int selected = 0, analyzed = 0, applied = 0, toInbox = 0, packagesNight = 0, overrodeHuman = 0, rejected = 0;
        string? firstRejectReason = null;
        // Ряд отказов «модель недоступна» по исполнителям (W5, см. ExecutorDownState): ряд свой
        // у каждого исполнителя; другой исход того же исполнителя обнуляет его ряд.
        var down = new ExecutorDownState();
        // Список исполнителей места и счётчики по ним (W6a): список читается здесь и один раз —
        // он же порядок Executors и тот же, что видит драйвер. Путь без названных исполнителей
        // (engine=null) счётчиков не заполняет вовсе — Executors пуст.
        IReadOnlyList<LocalEngineExecutor> executors = [];
        var perEngine = new Dictionary<string, (int Analyzed, int Rejected, long InputTokens)>(StringComparer.Ordinal);
        var dropped = 0;
        string? firstDropReason = null;
        // Отказы ступени 2 (хвост ревью R-J-W0): раньше их не было ни в журнале, ни в итоге.
        var stage2Refused = 0;
        string? firstStage2Refusal = null;
        // Судья (J-J4): нули до её прохода — и для ранних выходов, где прохода судья не было.
        int judgeCalls = 0, judgeApplied = 0, judgeRejected = 0, judgeDeferred = 0, codeRejected = 0;
        decimal judgeCost = 0m;
        var disputedPages = new HashSet<string>(StringComparer.Ordinal);
        long est = 0, inputTokens = 0;
        var outcome = OutcomeCompleted;
        try
        {
            var recalc = await _recalc.RecalculateAsync(ownerId);
            if (!recalc.Ok)
            {
                LogInfoSafe("Ночной разбор {Owner}: пересчёт отбора не удался ({Code}), ночь не начата", ownerId, recalc.Code);
                return Result(OutcomeError);
            }
            var read = WikiChatTriageStore.Get(root);
            if (read.State == WikiTriageFileState.Invalid) return Result(OutcomeError);
            var entries = read.Entries.ToDictionary(e => e.SessionId, StringComparer.Ordinal);

            var queue = BuildQueue(ownerId, entries);
            var cfg = WikiSettingsStore.Read(root).Settings.NightlyOrDefaults();
            var chatCap = (long)(cfg.InputTokenBudget * ChatBudgetShare);
            var chatsBySection = new Dictionary<string, int>(StringComparer.Ordinal);
            var packagesBySection = new Dictionary<string, int>(StringComparer.Ordinal);

            // ── подготовка одного чата до выдачи — общая у обоих драйверов (W4a2) ──
            // Порядок прежнего цикла: CheckStop (open_limit, окно, флаг, маршрут) и лимит пакетов
            // ночи — перед каждой выдачей; лимиты раздела, дельта, ступень 2 и бюджет чата —
            // как были. «Ready»: чат выбран, его оценка зарезервирована в бюджете и счётчик
            // раздела поднят — до выдачи, оценка чата в полёте учитывается против следующей.
            // Repeat — чат уже был выбран и его оценка уже зарезерирована его первой выдачей
            // (повтор мёртвого исполнителя, W5 п.3): проверка перед выдачей остаётся полной,
            // но selected, счётчик раздела и reserve бюджета пропускаются — иначе один чат
            // резерировал бы свою оценку несколько раз за проход. Дельта проверяется и для
            // повтора: её отсутствие означало бы вызов с пустой дельтой.
            async Task<Prep> PrepareAsync(Candidate c, bool repeat = false)
            {
                ct.ThrowIfCancellationRequested();
                // Лимит пакетов за ночь: при остатке 0 чат не начинается.
                var (stop, cfgNow) = CheckStop(ownerId, root, date, manual, down);
                if (stop is not null) return new Prep(false, stop, null, 0);
                if (packagesNight >= cfgNow!.MaxPackagesPerNight) return new Prep(false, OutcomeBudget, null, 0);

                if (chatsBySection.GetValueOrDefault(c.Home) >= cfgNow.MaxChatsPerSection) return Prep.Skip;
                if (packagesBySection.GetValueOrDefault(c.Home) >= cfgNow.MaxPackagesPerSection) return Prep.Skip;

                var history = await _sessions.GetHistoryAsync(c.Session.Id);
                var lines = WikiChatSlicer.Lines(history, 1, history.Count);
                var cursor = c.Entry?.Cursor ?? 0;
                if (cursor > history.Count) cursor = 0; // история переписана — курсор в 0, как у разбора
                var delta = lines.Where(l => l.Index > cursor).ToList();
                if (delta.Count(l => l.IsHuman) < MinHumanDelta) return Prep.Skip;

                // Серый чат — только с «да» ступени 2. Спрашиваем лениво: очередь дошла до чата.
                // Ступень 2 остаётся в координаторе и идёт последовательно (W4a2).
                // Повтор не спрашивает второй раз (доработка W5 №2, п.2): первую выдачу ступень 2
                // уже спросила и вердикт вынесла — снимок c.Entry взят до ночи и его не видит.
                // Второй вызов был бы вторым решением по одному чату за проход и удвоил бы
                // оценку и вход ступени 2 в итоге.
                if (!repeat && !c.Important && c.Entry?.Stage1Zone == WikiChatScorer.ZoneGray)
                {
                    var verdict = c.Entry.Stage2Zone;
                    var humanTotal = lines.Count(l => l.IsHuman);
                    var stale = humanTotal - (c.Entry.Stage2HistoryCount ?? 0) >= MinHumanDelta;
                    // Свежий отказ — чат отложен до следующей ночи без переспроса. Неопрешённый
                    // вердикт и устаревший (≥3 новых реплик человека) переспрашиваются одинаково —
                    // «да» так же, как «нет»: чат берётся только по новому решению.
                    if (verdict == WikiChatScorer.ZoneSkipped && !stale) return Prep.Skip;
                    if (verdict != WikiChatScorer.ZoneTriage || stale)
                    {
                        var judged = await _stage2.JudgeAsync(ownerId, c.Session.Id, ct);
                        if (judged.Outcome != WikiChatStage2.Outcome.NotApplicable)
                        {
                            est += (WikiChatStage2.MaxInputChars + WikiChatStage2.PromptHeader.Length) / 4;
                            // Фактический вход ступени 2 (WC-4c): метрики того же ответа модели,
                            // по которому вынесен вердикт. Считается по факту вызова: отказ
                            // ступени 2 понесённого расхода не отменяет (тот же смысл, что у
                            // ChatResult). NotApplicable — модель не звалась, расхода нет.
                            inputTokens += judged.InputTokens;
                        }
                        // Отказ ступени 2 → серая зона: чат отложен до следующей ночи. Он назван
                        // построчно и держится в итоге (хвост ревью R-J-W0): без этого админ,
                        // поставивший место на облачную модель, видел только «чаты серые».
                        // NotApplicable (чата нет в серой зоне, пометка человека) — не отказ: его
                        // в счётчик не берём.
                        if (judged.Outcome == WikiChatStage2.Outcome.Refused)
                        {
                            stage2Refused++;
                            firstStage2Refusal ??= judged.Detail;
                            LogInfoSafe("Ночной разбор {Owner}: чат {Chat} ступень 2 отказана: {Reason}",
                                ownerId, c.Session.Id, judged.Detail ?? "причина не названа");
                        }
                        if (judged.Outcome != WikiChatStage2.Outcome.Judged
                            || judged.Zone != WikiChatScorer.ZoneTriage) return Prep.Skip;
                    }
                }

                if (!repeat) selected++;
                var chatEst = (delta.Sum(l => (long)l.Text.Length) + 3) / 4;
                if (chatEst > chatCap)
                {
                    LogInfoSafe("Ночной разбор {Owner}: дельта чата {Chat} (~{Tokens} ток.) не помещается в бюджет, чат пропущен",
                        ownerId, c.Session.Id, chatEst);
                    return Prep.Skip;
                }
                if (!repeat)
                {
                    if (est + chatEst > chatCap) return new Prep(false, OutcomeBudget, null, 0);
                    chatsBySection[c.Home] = chatsBySection.GetValueOrDefault(c.Home) + 1;
                    est += chatEst;
                }
                return new Prep(true, null, cfgNow, chatEst);
            }

            // Обработка одного результата разбора — общая у обоих драйверов: счётчики, ряд отказов
            // по исполнителю, лимит пакетов ночи. Engine — исполнитель, на котором шёл этот разбор
            // (null — путь без названного исполнителя): по нему считается ряд и его выбывание.
            // Возвращает причину остановить ночь (null — продолжать). Исключение разбора сюда не
            // доходит: его обрабатывает вызывающий (курсор не двигает, в analyzed не считает) —
            // как прежний catch.
            string? ApplyResult(Candidate c, string? engine, WikiSettingsStore.NightlyConfig cfgNow,
                WikiChatIngestService.ChatResult r)
            {
                analyzed++;
                // Фактический вход (WC-4b): сумма по всем разобранным чатам независимо от исхода —
                // отказанный план понесённый расход не отменяет (тот же смысл, что у ChatResult).
                // Чат, упавший исключением выше, до analyzed++ не доходит: его токенов нет.
                inputTokens += r.InputTokens;
                // Счётчики по исполнителю (W6a): тот же учёт, что по ночи, только по своему
                // движку; путь без названного исполнителя (engine=null) в них не входит.
                if (engine is not null)
                {
                    var st = perEngine.GetValueOrDefault(engine);
                    perEngine[engine] = (st.Analyzed + 1,
                        st.Rejected + (r.Outcome == WikiChatIngestService.OutcomeRejected ? 1 : 0),
                        st.InputTokens + r.InputTokens);
                }
                dropped += r.Dropped;
                firstDropReason ??= r.FirstDropReason;
                // Отказанные разборы (WN-2): считаются и называются в журнале — без них ночь из
                // 100 отказов выглядела completed и не оставила ни строки (отчёт 79e98305, Ш2).
                if (r.Outcome == WikiChatIngestService.OutcomeRejected)
                {
                    rejected++;
                    firstRejectReason ??= r.Reason;
                    LogInfoSafe("Ночной разбор {Owner}: чат {Chat} отказан: {Reason}", ownerId, c.Session.Id, r.Reason);
                    // Выключатель мёртвого исполнителя (W5, см. ExecutorDownState): здесь только
                    // отметка выбывания и её строка. Продолжать ли проход, решает «есть ли живой
                    // исполнитель» в CheckStop перед следующей выдачей: при мёртвом маршруте места
                    // (путь без исполнителей) это ровно тот же итог, что был у 2e13d45c —
                    // следующая выдача не состоится и проход вернёт error (дату он не закрывает,
                    // следующий тик окна пробует снова), при живом другом — очередь доиграет на
                    // нём. Причина первого отказа (firstRejectReason) названа выше — она и есть
                    // то, чем объяснится итог.
                    if (IsLocalDownRejection(r))
                    {
                        if (down.NoteRejection(engine, _clock()))
                            LogInfoSafe("Ночной разбор {Owner}: {Engine}: {Reason} — {Streak} отказов подряд, исполнитель выбыл до конца прохода",
                                ownerId, EngineName(engine), WikiIngestService.ReasonLocalUnavailable, LocalDownBreakAfter);
                    }
                    else
                    {
                        // Отказ не «модель недоступна» (отказ плана с InputTokens > 0, отказ занятого
                        // слота): модель отвечала или движок жив, но занят — это не повтор того же
                        // отказа, ряд обнуляется, как при состоявшемся разборе (тот же смысл, что
                        // у 2e13d45c в master: в ряд входят только подряд идущие отказы «модели нет»).
                        down.NoteLive(engine);
                    }
                }
                else
                {
                    // Любой несостоявшийся этим отказом разбор ряд своего исполнителя обнуляет:
                    // выключатель считает подряд идущие, а не всего ночи.
                    down.NoteLive(engine);
                }
                var made = r.Outcome switch
                {
                    // Частичный исход (Partial, WC-3) — тоже applied: его применённые пакеты
                    // считаются здесь же, отказ позднего окна их не перечёркивает.
                    WikiChatIngestService.OutcomeApplied => r.PackageIds.Count,
                    WikiChatIngestService.OutcomeAwaitingHuman => 1, // число неизвестно: нижняя граница
                    _ => 0,
                };
                if (r.Outcome == WikiChatIngestService.OutcomeApplied)
                {
                    applied += made;
                    overrodeHuman += r.OverrodeHuman;
                    // Страница, оспоренная в двух чатах за ночь, — одна страница: счёт по множеству путей.
                    foreach (var p in r.DisputedPages ?? []) disputedPages.Add(p);
                }
                else if (r.Outcome == WikiChatIngestService.OutcomeAwaitingHuman) toInbox++;
                packagesNight += made;
                packagesBySection[c.Home] = packagesBySection.GetValueOrDefault(c.Home) + made;
                // Пакеты чата прибавляются пачкой и уже лежат в Хранителе — их не откатывать.
                // Превышение остатка этим чатом допустимо. Лимит ночи — конец ночи: outcome=budget,
                // дальше не идти. Лимит раздела закрывает только его раздел: счётчик раздела уже
                // выше лимита, его последующие чаты отсекает предпроверка (как раздел с нулевым
                // остатком), ночь продолжает по другим разделам, итог остаётся completed.
                if (packagesNight > cfgNow.MaxPackagesPerNight) return OutcomeBudget;
                return null;
            }

            // Исполнители места — список стора админа через шов (уже урезанный до maxTotal,
            // см. CheapTextRunner.LocalExecutorsFor). Он читается ДО подготовки чата: тот же
            // список смотрит CheckStop, когда решает «есть ли живой исполнитель» (W5, п.4).
            // Здесь список читается один раз на проход: на нём держатся порядок и счётчики итога
            // (W6a) — две разные картины в одном итоге были бы, если бы порядок брали из другого
            // чтения. CheckStop перечитывает стора сам перед каждой выдачей (свежий PUT посреди
            // ночи виден следующему чату) — это решение «идти дальше или нет», к итогу оно не
            // относится.
            executors = _llm.LocalExecutorsFor(LocalActionCatalog.WikiNightly);

            // ── драйвер прохода (W4a2) ────────────────────────────────────────────
            // Пустой список — прежний последовательный проход с engine=null: ночь не параллельна,
            // исполнитель места не назван (как до фичи); его ряд отказов — под ключом RouteKey.
            if (executors.Count == 0)
            {
                foreach (var c in queue)
                {
                    var prep = await PrepareAsync(c);
                    if (prep.Stop is not null) { outcome = prep.Stop; break; }
                    if (!prep.Ready) continue;
                    WikiChatIngestService.ChatResult r;
                    try { r = await _analyze(ownerId, c.Session.Id, null, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // Сбой одного разбора курсор не двигает (его пишет только AnalyzeAsync) — к следующему.
                        LogSafe(ex, "Ночной разбор {Owner}: чат {Chat} не разобран", ownerId, c.Session.Id);
                        continue;
                    }
                    var stopAfter = ApplyResult(c, null, prep.Cfg!, r);
                    if (stopAfter is not null) { outcome = stopAfter; break; }
                }
            }
            else
            {
                outcome = await RunCoordinatedAsync(ownerId, queue, executors, down, PrepareAsync, ApplyResult, ct);
            }

            // Граница выключателя мёртвого исполнителя (доработка W5 №2, п.1 — регрессия 2e13d45c):
            // третий отказ подряд мог прийти последним чатом очереди — следующей выдачи нет, и
            // CheckStop своего error не называет; в координаторе та же дыра, когда повторный чат
            // был последним (stop ?? Completed). Итог completed закрыл бы дату при мёртвых
            // исполнителях. Предохранитель назван здесь, после обоих драйверов: к концу прохода
            // живых исполнителей не осталось — итог error (дату не закрывает, как любой error).
            // Раньше названный упор (budget, open_limit, window_closed, disabled_midway) держит
            // свой итог: он назван своей причиной, и до этой строки outcome уже не Completed.
            if (outcome == OutcomeCompleted && !down.AnyLive(executors)) outcome = OutcomeError;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogSafe(ex, "Ночной разбор {Owner}: проход прерван", ownerId);
            outcome = OutcomeError;
        }
        // Инвариант «нулевой вход при всех отказах ≠ completed» (WN-2): когда каждый состоявшийся
        // разбор отказан и ни один токен не дошёл до модели, ночь не «завершена» — она не удалась:
        // error дату не закрывает, следующий тик окна пробует снова (повтор и уведомление — выше,
        // их не меняем). Ветки budget/open_limit/window_closed/disabled_midway сюда не доходят:
        // их итог назван своей причиной. Сбой разбора исключением в rejected не идёт (catch выше
        // analyzed++) и инварианту не мешает: без отказа он не был бы и разобранным.
        if (outcome == OutcomeCompleted && analyzed > 0 && rejected == analyzed && inputTokens == 0)
        {
            LogInfoSafe("Ночной разбор {Owner}: локальная модель не ответила — отказаны все {Analyzed} чатов с нулевым входом",
                ownerId, analyzed);
            outcome = OutcomeError;
        }

        // Точка запуска (а) (J-J4): проход судьи после разбора чатов — под тем же замком владельца
        // (его держит RunOwnerAsync/StartManual вокруг RunOwnerPassAsync) и до записи
        // keeper/nightly-last.json: его счётчики попадают в тот же файл, ту же строку журнала и то
        // же утреннее уведомление. Ранние выходы прохода (пересчёт отбора, нечитаемый стор) идут
        // изнутри try и сюда не доходят: там вики не начала проход. Вызов сериализуется замком
        // самого прохода судьи (J-J3a) с его точкой (б) в очереди заявок разбора: двойного вызова
        // модели на пакет нет — судья берёт только нерешённые спорные операции. Мёртвый локальный
        // маршрут ночи к нему не относится: это другое место (wiki-judge), его выбирает админ.
        if (_judge is not null)
        {
            var sweep = await _judge(ownerId, root, ct).ConfigureAwait(false);
            judgeCalls = sweep.Calls;
            judgeCost = sweep.CostUsd;
            judgeApplied = sweep.Applied;
            judgeRejected = sweep.Rejected;
            judgeDeferred = sweep.Deferred;
            codeRejected = sweep.CodeRejected;
            // Сам проход судьи исключений не бросает; его FirstFailure — единственный способ
            // узнать о несделанном, и он в журнале.
            if (sweep.FirstFailure is not null)
                LogWarnSafe("Ночной разбор {Owner}: проход судьи не доведён: {Reason}", ownerId, sweep.FirstFailure);
        }
        return Result(outcome);

        NightResult Result(string o) => new(FormatDate(date), selected, analyzed, applied, toInbox, est, o,
            overrodeHuman, disputedPages.Count, inputTokens, rejected, firstRejectReason, manual, dropped, firstDropReason,
            judgeCalls, judgeCost, judgeApplied, judgeRejected, judgeDeferred, codeRejected,
            stage2Refused, firstStage2Refusal, ExecutorStats());

        // Счётчики итога по исполнителям (W6a): порядок — как список стора. Движок, которому ни
        // одного чата не досталось (не был готов, был занят), в списке есть с нулями: владельцу
        // видно, что ночь на нём не шла. Путь без названных исполнителей — пустой список.
        List<NightExecutorStat> ExecutorStats()
        {
            if (executors.Count == 0) return [];
            var list = new List<NightExecutorStat>(executors.Count);
            foreach (var e in executors)
            {
                var st = perEngine.GetValueOrDefault(e.Engine);
                list.Add(new NightExecutorStat(e.Engine, st.Analyzed, st.Rejected, st.InputTokens,
                    down.Withdrawn.TryGetValue(e.Engine, out var at) ? at : null));
            }
            return list;
        }
    }

    // ── координатор ночи (W4a2, разрез «ночь вики на нескольких локальных исполнителях») ──────
    // Слоты = сумма Parallel исполнителей (шов уже урезал её до maxTotal места, см.
    // CheapTextRunner.LocalExecutorsFor): координатор заводит ровно столько окон, сколько ему
    // отдали. Свободный слот получает первый чат очереди, чей раздел сейчас не в работе:
    // сродство — два чата одного раздела никогда не выдаются одновременно, порядок внутри
    // раздела прежний (очередь не переставляется: к занятому разделу возвращаются позже).
    // Счётчики и бюджет меняет только координатор (через prepare/apply в своей же последовательности);
    // слот только зовёт _analyze на своём исполнителе и возвращает исход. CheckStop и лимиты
    // проверяются перед каждой выдачей; при упоре выдача останавливается, чаты в полёте
    // доигрываются и их результаты обрабатываются как обычно. Исключение слота — отказ чата
    // (тот же смысл, что прежний catch): координатор и соседние слоты живы; отмена хоста —
    // конец прохода. Исключение prepare/apply — отказ прохода (его ловит общий catch RunPassAsync),
    // но и на этом выходе ни один слот не остаётся в полёте: их ожидание — в finally координатора,
    // иначе их _analyze шёл бы рядом с точкой (а) судьи или со следующим проходом. Отказ
    // исполнителя (W5): ряд «модель недоступна» считается по исполнителю (down, п.1), перед
    // выдачей — проба готовности (п.2), чат, отказанный без применённого окна, возвращается в
    // голову очереди не больше одного раза за проход (п.3). Счётные слова и отметка выбывания
    // живут только здесь: исходы слотов обрабатывает один поток координатора.
    private async Task<string> RunCoordinatedAsync(
        string ownerId, List<Candidate> queue,
        IReadOnlyList<LocalEngineExecutor> executors, ExecutorDownState down,
        Func<Candidate, bool, Task<Prep>> prepare,
        Func<Candidate, string?, WikiSettingsStore.NightlyConfig, WikiChatIngestService.ChatResult, string?> apply,
        CancellationToken ct)
    {
        var remaining = new List<Candidate>(queue);
        var busy = new Dictionary<string, int>(StringComparer.Ordinal); // раздел → чаты в полёте
        var engineBusy = new Dictionary<string, int>(StringComparer.Ordinal); // исполнитель → занятые слоты
        // Чаты, которых этот проход уже ставил в голову очереди (п.3: не больше одного повтора),
        // и исполнитель, который их отказал: повторному чату его прежний исполнитель не отдаётся —
        // иначе мёртвый движок, ещё не выбывший (ряд не дошёл до лимита), взял бы его снова и
        // «добрали остальные» не случилось бы.
        var retried = new Dictionary<string, string>(StringComparer.Ordinal);
        var inflight = new List<Task<SlotOutcome>>();
        string? stop = null;
        // Сколько чатов подряд этого обхода очереди некому отдать (см. сдвиг в конец очереди):
        // до полного обхода координатор пробует следующего чата, дальше — ждёт завершения слотов.
        var stalled = 0;

        try
        {
            while (true)
            {
                stalled = 0; // каждый обход очереди — заново: прошлый счёт чужого обхода не в счёт
                // Выдача, пока есть исполнитель, которому можно дать чат, нет упора и есть чат со
                // свободным разделом.
                while (stop is null && remaining.Count > 0)
                {
                    // Кому и что дать: сначала чат — первый из очереди, чей раздел сейчас не в
                    // работе (сродство), затем исполнитель, которому этого чата можно дать ПРЯМО
                    // СЕЙЧАС (W5, п.1–2): его слот свободен, он не выбыл до конца прохода, он
                    // готов принять работу (проба — до того, как чат взят из очереди и
                    // подготовлен: неготовый пропускается, чата не сжигает и сам не выбывает) и
                    // он не отказал этот чат прежде (повтор берёт другой). Порядок пары тот же,
                    // что был: любой свободный исполнитель × первый чат со свободным разделом.
                    var idx = -1;
                    for (var i = 0; i < remaining.Count; i++)
                        if (!busy.ContainsKey(remaining[i].Home)) { idx = i; break; }
                    if (idx < 0) break; // все оставшиеся чаты — занятых разделов: ждать завершения

                    var c = remaining[idx];
                    string? engine = null;
                    var anyReadyFree = false; // живой исполнитель со свободным слотом есть
                    foreach (var e in executors)
                    {
                        if (engineBusy.GetValueOrDefault(e.Engine) >= e.Parallel) continue;
                        if (down.IsWithdrawn(e.Engine)) continue;
                        bool ready;
                        try { ready = await _llm.IsLocalEngineReadyAsync(e.Engine, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            // Исключение пробы готовности (R-W5, п.2): движок считается не готовым —
                            // ровно как негативная проба: пропустить, чата не сжигать, не выбывать.
                            // Координатор не падает: причина названа в журнале, выдача идёт на
                            // других исполнителях; отмена хоста — конец прохода (как везде).
                            LogSafe(ex, "Ночной разбор {Owner}: проба готовности {Engine} не удалась", ownerId, EngineName(e.Engine));
                            continue;
                        }
                        if (!ready) continue;
                        anyReadyFree = true;
                        if (retried.TryGetValue(c.Session.Id, out var failedBy) && failedBy == e.Engine) continue;
                        engine = e.Engine;
                        break;
                    }
                    if (engine is null)
                    {
                        // Ни одного живого и готового — и в полёте никого: продолжать нечем.
                        // Тот же итог, что «выбыли все» в CheckStop (п.4): error, дату он не
                        // закрывает, следующий тик окна пробует снова.
                        if (inflight.Count == 0 && !anyReadyFree)
                        {
                            LogInfoSafe("Ночной разбор {Owner}: из {Total} исполнителей живых {Live}, ни один не готов принять работу, в полёте никого — проход остановлен",
                                ownerId, executors.Count, executors.Count(e => !down.IsWithdrawn(e.Engine)));
                            return OutcomeError;
                        }
                        // Этому чату сейчас некому (его исполнитель занят, или этот чат ему уже
                        // отказан и другого нет): он не держит очередь — уходит в её конец, а
                        // координатор пробует следующего. Слот освободится, и к нему вернутся; ряда
                        // исполнителя это не касается — до разбора чат не дошёл.
                        if (++stalled >= remaining.Count) break; // ни одному оставшемуся некому — ждать завершения
                        remaining.RemoveAt(idx);
                        remaining.Add(c);
                        continue;
                    }

                    stalled = 0;
                    remaining.RemoveAt(idx);
                    // Чат вне очереди только если его вернул сюда координатор как повтор (п.3) —
                    // пометка retried и есть признак того, что это вторая выдача того же чата.
                    var prep = await prepare(c, retried.ContainsKey(c.Session.Id));
                    if (prep.Stop is not null) { stop = prep.Stop; break; }
                    if (!prep.Ready) continue; // чат пропущен — слот свободен, к следующему

                    busy[c.Home] = 1;
                    engineBusy[engine] = engineBusy.GetValueOrDefault(engine) + 1;
                    inflight.Add(RunSlotAsync(c, engine, prep.Cfg!));
                }

                if (inflight.Count == 0) break;
                var done = await Task.WhenAny(inflight);
                inflight.Remove(done);
                SlotOutcome o;
                try { o = await done; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Отмена хоста: их ожидание — в finally ниже (тот же инвариант на любом выходе).
                    throw;
                }
                // Ключ снимают при нуле: ContainsKey по оставшемуся ключу с нулём читался бы как
                // «раздел в работе», и второй его чат не вышел бы никогда.
                if (--busy[o.Chat.Home] == 0) busy.Remove(o.Chat.Home);
                engineBusy[o.Engine]--;
                if (o.Error is not null)
                {
                    // Сбой одного разбора курсор не двигает (его пишет только AnalyzeAsync) — к следующему;
                    // координатор и соседние слоты живы.
                    LogSafe(o.Error, "Ночной разбор {Owner}: чат {Chat} не разобран", ownerId, o.Chat.Session.Id);
                    continue;
                }
                var stopAfter = apply(o.Chat, o.Engine, o.Cfg, o.Result);
                // Отказ «модель недоступна» или «слот движка занят» с нулевым входом и без единого
                // применённого окна (доработка W5 №2, п.3): чат возвращается в голову очереди ОДИН
                // раз на проход (п.3) — его добирает другой исполнитель, отказавший его не получает
                // (retried). Курсор чата не двигался (его пишет только AnalyzeAsync при успехе),
                // чат законен снова. Занятость — не выбывание: ряда она не касается, в строке
                // итога это отказ (ApplyResult считает его rejected и обнуляет ряд).
                // Отказ плана (плохой JSON, InputTokens > 0) и частичный исход (что-то применено)
                // не повторяются: иначе чат пошёл бы по кругу, а его остаток и так ждёт следующей
                // ночи. Повтор не резервирует бюджет второй раз: оценка чата уже зарезерирована
                // его первой выдачей и остаётся зарезерированной.
                if (stopAfter is null && (IsLocalDownRejection(o.Result) || IsSlotRefusalRejection(o.Result))
                    && o.Result.PackageIds is not { Count: > 0 } && !retried.ContainsKey(o.Chat.Session.Id))
                {
                    retried[o.Chat.Session.Id] = o.Engine;
                    remaining.Insert(0, o.Chat);
                    LogInfoSafe("Ночной разбор {Owner}: чат {Chat} отказан {Engine}, возвращён в голову очереди (первый повтор)",
                        ownerId, o.Chat.Session.Id, EngineName(o.Engine));
                }
                if (stopAfter is not null) stop ??= stopAfter; // первый названный упор держит итог
            }
        }
        finally
        {
            // Инвариант координатора: после его возврата ни одной задачи слота в полёте. Исключение
            // prepare/apply уходит в общий catch прохода (это отказ прохода), и без этого ожидания
            // чаты в полёте доигрывали бы ПОСЛЕ возврата: их _analyze шёл бы рядом с точкой (а) судьи
            // или со следующим проходом того же владельца. Исходы дожданных слотов проходу уже не
            // нужны (он назван отказом), но их полёт — нет; исключения дожданных здесь не поднимаются.
            if (inflight.Count > 0)
            {
                try { await Task.WhenAll(inflight).ConfigureAwait(false); }
                catch { /* их исходы проходу уже не нужны */ }
            }
        }
        return stop ?? OutcomeCompleted;

        // Один слот: только _analyze на названном исполнителе (W4a1). Исключение становится
        // отказом чата (тот же смысл, что прежний catch) — задача слота не faulted: один сбой
        // не рвёт координатор и соседние слоты. Отмена хоста остаётся исключением: проход кончается.
        async Task<SlotOutcome> RunSlotAsync(Candidate c, string engine, WikiSettingsStore.NightlyConfig cfg)
        {
            try
            {
                var r = await _analyze(ownerId, c.Session.Id, engine, ct).ConfigureAwait(false);
                return new SlotOutcome(c, engine, cfg, r, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return new SlotOutcome(c, engine, cfg, default, ex); }
        }
    }

    /// <summary>Отказ «локальной модели нет» (2e13d45c): причина несёт формулировку
    /// WikiIngestService.ReasonLocalUnavailable, а нулевой вход подтверждает, что модель не
    /// звалась ни разу. Проверка по подстроке: до ночи причина проходит через обёртки
    /// («разбор окна m1-m8 не удался: синтез: …»). Нулевой вход — обязательная половина
    /// признака: отказ, в котором модель отвечала (плохой план, пустой ответ, часть окон уже
    /// разобрана), даёт InputTokens больше нуля и по нулевому входу отсечён — ряд по нему не
    /// растёт, чат не повторяется, исполнитель не выбывает. Отказ «слот движка занят»
    /// (IsSlotRefusalRejection ниже) в ряд выбывания не входит — по формулировке задачи: движок
    /// занят, а не мёртв; но чат с таким отказом не сгорает — он получает единственный повтор
    /// на другом исполнителе (доработка W5 №2, п.3).</summary>
    private static bool IsLocalDownRejection(WikiChatIngestService.ChatResult r) =>
        r.InputTokens == 0 && r.Reason?.Contains(WikiIngestService.ReasonLocalUnavailable, StringComparison.Ordinal) == true;

    /// <summary>Отказ «слот движка занят» (LocalEngineSlotRefusalException, WN-4): причина
    /// доезжает до ночи дословно — «локальный движок «…»: занят: слота не дождались (…, занято N
    /// из M)» (метка ReasonMarker, LocalEngineBusyTracker.cs:224; обёртка passName в
    /// WikiIngestService.cs:788), и нулевой вход подтверждает, что модель не звалась. Опознание
    /// держится общей константы, не местного литерала: правка формулировки меняет его заодно.
    /// В ряд выбывания не входит: движок занят, а не мёртв (ApplyResult обнуляет его ряд через
    /// NoteLive); чат с таким отказом координатор отдаёт один раз другому исполнителю.</summary>
    private static bool IsSlotRefusalRejection(WikiChatIngestService.ChatResult r) =>
        r.InputTokens == 0 && r.Reason?.Contains(LocalEngineSlotRefusalException.ReasonMarker, StringComparison.Ordinal) == true;

    /// <summary>Имя исполнителя для журнала и итога: имя движка из списка стора; путь без
    /// названного исполнителя называется маршрутом места (имени движка там нет, J-W2).</summary>
    private static string EngineName(string? engine) =>
        string.IsNullOrEmpty(engine) ? "маршрут места" : engine;

    // Что мешает продолжать ПЕРЕД чатом: тумблер/флаг сняли (disabled_midway), окно закрылось
    // (window_closed), модель недоступна (error — облако молча не подставляется), не осталось
    // живого исполнителя (error, W5 п.4: все выбыли — дату не закрывает, следующий тик пробует
    // снова), упор в лимит открытых пакетов (open_limit). Настройки перечитываются каждый раз:
    // PUT посреди ночи виден следующему чату. При ручном проходе (WN-5) окно не проверяется
    // вовсе: человек запустил разбор вне окна — оно ему не преграда; флаг, тумблер, маршрут,
    // живые исполнители и open_limit — как у тика.
    private (string? Stop, WikiSettingsStore.NightlyConfig? Cfg) CheckStop(
        string ownerId, string root, DateOnly date, bool manual, ExecutorDownState down)
    {
        if (_flags?.IsEnabled(ownerId, FeatureFlagKeys.LlmWiki) != true) return (OutcomeDisabledMidway, null);
        var cfg = WikiSettingsStore.Read(root).Settings.NightlyOrDefaults();
        if (!cfg.Enabled) return (OutcomeDisabledMidway, null);
        if (!manual)
        {
            var now = _clock();
            if (!InWindow(now.Hour, cfg.WindowStartHour, cfg.WindowEndHour)
                || WindowDate(now, cfg.WindowStartHour, cfg.WindowEndHour) != date) return (OutcomeWindowClosed, null);
        }
        // Наличие маршрута места требуется только пути без названных исполнителей (доработка
        // W5 №2, п.4): при заданном списке исполнителей ночь идёт по списку, и отсутствие
        // маршрута места не гасит живой именованный движок — иначе ночь падала в error при
        // живых исполнителях, а UI в это время говорит «ночь идёт по списку исполнителей».
        var executors = _llm.LocalExecutorsFor(LocalActionCatalog.WikiNightly);
        if (executors.Count == 0 && !_llm.HasRoute(LocalActionCatalog.WikiNightly)) return (OutcomeError, null);
        // Пустой список — путь без названных исполнителей: там «живой исполнитель» — сам маршрут
        // места, и его выбывание даёт прежний итог 2e13d45c. Список непустой — жив хоть один из
        // назначенных движков (не выбыл до конца прохода).
        if (!down.AnyLive(executors))
        {
            LogInfoSafe("Ночной разбор {Owner}: живых исполнителей не осталось ({Withdrawn} выбыло), проход остановлен",
                ownerId, down.Withdrawn.Count);
            return (OutcomeError, null);
        }
        if (WikiPackageStore.CountOpen(root) >= WikiPackage.MaxOpenPackages) return (OutcomeOpenLimit, null);
        return (null, cfg);
    }

    // Очередь прохода: чаты владельца, которые ночь вообще может взять. Чат исполнения задач,
    // чат с пометкой «не разбирать», приватный чат и личный раздел отсекаются здесь
    // (skip побеждает балл); берутся «важные» (минуя ступени 1–2), зона triage и серые
    // (их судит ступень 2 на подходе очереди). Порядок: «важные», затем по UpdatedAt убыв.
    private List<Candidate> BuildQueue(string ownerId, IReadOnlyDictionary<string, WikiChatTriageEntry> entries)
    {
        var result = new List<Candidate>();
        foreach (var s in _sessions.GetAll().Where(x => _sessions.ResolveOwnerId(x) == ownerId))
        {
            // Приватный чат (FIN3-3, инвариант в шапке): PrivateToUserId != null — виден только
            // автору; чат участника общего проекта резолвится на владельца, но в его очередь
            // не входит ни под каким баллом (тот же фильтр, что у автообучения памяти).
            if (s.TaskExecution || s.WikiMark == WikiMarks.Skip || s.PrivateToUserId is not null) continue;
            var home = WikiChatIngestService.HomeOf(s);
            if (home == WikiSections.Personal) continue;
            entries.TryGetValue(s.Id, out var e);
            var important = s.WikiMark == WikiMarks.Important;
            if (!important && e?.Stage1Zone is not (WikiChatScorer.ZoneTriage or WikiChatScorer.ZoneGray)) continue;
            result.Add(new Candidate(s, e, important, home));
        }
        return [.. result.OrderByDescending(c => c.Important).ThenByDescending(c => c.Session.UpdatedAt)];
    }
}
