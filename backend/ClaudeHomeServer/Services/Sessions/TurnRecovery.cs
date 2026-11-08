using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

using RestartResumeResult = ClaudeHomeServer.Services.SessionManager.RestartResumeResult;
using SendUserOutcome = ClaudeHomeServer.Services.SessionManager.SendUserOutcome;
using StuckTurnRestartResult = ClaudeHomeServer.Services.SessionManager.StuckTurnRestartResult;
using TurnTranscriptDamagedException = ClaudeHomeServer.Services.SessionManager.TurnTranscriptDamagedException;

namespace ClaudeHomeServer.Services.Sessions;

// Возобновление хода после обрыва (S3-5, docs/research/session-manager-split-2026-11.md §1
// «жизненный цикл», §2): ручной перезапуск зависшего хода штаба, авто-возобновление ходов,
// оборванных рестартом сервера, выдача кандидатов на auto-resume и запись маркеров обрыва в
// историю при загрузке стора. Тела перенесены из SessionManager дословно; фасад держит
// делегирующие обёртки прежних публичных сигнатур (на них сидют контроллер и hosted service)
// и тестовые швы IsTailIntactForResume / SendRestartResumeTurnAsync — они остаются virtual
// на фасаде (на них стоит SpySessionManager в Tests/Services/TurnAutoResume), сюда приходят
// делегатами вызова.
//
// Зависимостей семь: стор, очередь (плашка «не доставлено»), маршрут (поиск resume-
// транскрипта), ChatHistoryService (маркер обрыва при загрузке — это спинка, не роль
// истории: запись делает сам стор истории), роль истории SessionHistory (карточки
// «цикл остановлен» / «сервер перезапущен» пишет она; S3-7: тела карточек переехали
// в неё, прежний путь через шов ITurnStarter снят — здесь прямой вызов роли), ленивый
// шов ITurnStarter (остался только разбор очереди — карточки из шва убраны) — и конфиг
// (порог свежести хода).
//
// Отклонения от таблицы плана §2 (каждое — с комментарием у места; отдельным решением они не
// подтверждены — на проверке у Александра):
// — ReviveStuckSession(Async) и StopClosedExecutorProcess не здесь, а в TurnProcessHost:
//   их вызывающие — InterruptCore и join-а TaskExecutionService; ребро Host→Recovery убрано;
// — ResolveTurnCwd не здесь: он уже в CliRoute (S3-2) и нужен только поиску транскрипта;
// — публичные вложенные типы фасада (TurnTranscriptDamagedException, StuckTurnRestartResult,
//   RestartResumeResult) не переезжают: на них смотрит контракт контроллера и тестов, здесь
//   они только используются (алиасы выше);
// — settings (ChatSettings) в конструктор не взят: ни одно тело этой роли его не читает;
//   порог свежести хода читается из конфига здесь же (тот же ключ/дефолт, что был у поля
//   фасада), поэтому в конструкторе стоит IConfiguration, а не ChatSettings.
// Класс public по той же причине, что и роли S3-2…S3-4: его принимает публичный конструктор
// фасада (internal-тип в публичном конструкторе — CS0051). Вовне он не назван ни одним типом:
// граница держит сторож правила 4.
public sealed class TurnRecovery
{
    private readonly SessionStore _store;
    private readonly PendingQueueService _queue;
    private readonly CliRoute _route;
    private readonly ChatHistoryService _history;
    private readonly SessionHistory _sessionHistory;
    private readonly Lazy<ITurnStarter> _turnStarter;
    private readonly IConfiguration _config;

    public TurnRecovery(SessionStore store, PendingQueueService queue,
        CliRoute route, ChatHistoryService history, SessionHistory sessionHistory,
        Lazy<ITurnStarter> turnStarter, IConfiguration config)
    {
        _store = store;
        _queue = queue;
        _route = route;
        _history = history;
        _sessionHistory = sessionHistory;
        _turnStarter = turnStarter;
        _config = config;
    }

    // Порог свежести хода для гейта перезапуска (этап 3): тот же ключ/дефолт, что у пульса
    // волны — TeamWaveService._quietThreshold (выражение переехало из конструктора фасада).
    private TimeSpan FreshTurnThreshold => TimeSpan.FromMinutes(
        int.TryParse(_config["TeamImplement:QuietMinutes"], out var quietMin) && quietMin > 0 ? quietMin : 15);

    private ConcurrentDictionary<string, SessionEntry> _sessions => _store.Sessions;

    // Ожидание смерти процесса после kill: Kill асинхронен, и «вызвали kill» ≠ «процесс
    // мёртв». internal — тесты сужают до сотен миллисекунд; прод живёт 20 с (Kill плюс
    // WaitForExitAsync внутри финализации адаптера занимает до ~10 с).
    internal TimeSpan KillWaitTimeout { get; set; } = TimeSpan.FromSeconds(20);

    // Гейт повторного вызова: перезапуск — это kill → уборка адаптера → revive, второй
    // параллельный вызов того же чата (двойной клик) плодил бы процессы и карточки.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _turnRestarts = new();

    // Кандидаты на auto-resume, захваченные загрузкой стора: исходный Working (ход оборван,
    // result не пришёл). TakeRestartResumeCandidates возвращает и очищает список (второй
    // вызов — пусто).
    private readonly List<string> _restartResumeCandidates = [];

    // Task записи маркеров обрыва ходов. Не fire-and-forget: B2 дождётся его перед
    // запуском resume, чтобы маркер не обогнал снимок. Task.CompletedTask — маркеров не было.
    internal Task AbortMarkersWritten { get; private set; } = Task.CompletedTask;

    // Что делает роль с результатом загрузки sessions.json (её вызывает фасад из LoadSessions):
    // запоминает кандидатов на auto-resume и пишет маркеры обрыва ходов в историю — фоном,
    // чтобы не тормозить старт. Task храним (не fire-and-forget): авто-возобнование дождётся
    // его перед запуском resume, чтобы маркер не обогнал снимок.
    internal void OnSessionsLoaded(IReadOnlyList<string> restartResumeCandidates, IReadOnlyList<string> abortedMidTurn)
    {
        foreach (var id in restartResumeCandidates)
            _restartResumeCandidates.Add(id);
        if (abortedMidTurn.Count > 0)
            AbortMarkersWritten = Task.Run(async () =>
            {
                foreach (var csid in abortedMidTurn)
                    try { await _history.AppendTurnAbortedAsync(csid); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[SessionManager] Маркер обрыва хода ({csid}) не записан: {ex.Message}");
                    }
            });
    }

    // Выдача кандидатов на auto-resume: возвращает захваченный список и очищает его в памяти,
    // чтобы второй вызов не получил кандидатов повторно.
    internal IReadOnlyList<string> TakeRestartResumeCandidates()
    {
        lock (_restartResumeCandidates)
        {
            var result = _restartResumeCandidates.ToList();
            _restartResumeCandidates.Clear();
            return result;
        }
    }

    // После того как авто-возобновление отработало: чаты без живого хода с восстановленной
    // незамороженной очередью разбираются штатным DrainNextPendingAsync; просроченные
    // сообщения получают плашку «не доставлено». Чат с возобновлённым ходом (Working/Waiting/
    // Starting) не трогаем — очередь разберёт его конец. Идемпотентно: PendingRestored гасится.
    internal async Task DrainRestoredPendingAsync()
    {
        foreach (var (sessionId, entry) in _sessions.ToArray())
        {
            try
            {
                var expired = entry.ExpiredOnRestore;
                if (expired is not null)
                {
                    entry.ExpiredOnRestore = null;
                    await _queue.AppendPendingExpiredCardAsync(sessionId, entry, expired);
                }

                if (!entry.PendingRestored) continue;
                if (entry.Info.Status is SessionStatus.Working or SessionStatus.Starting or SessionStatus.Waiting)
                    continue;
                entry.PendingRestored = false;
                if (entry.QueueFrozen || !PendingQueueService.HasPending(entry)) continue;
                await _turnStarter.Value.DrainNextPendingAsync(sessionId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Автодренаж очереди ({sessionId}) после рестарта: {ex.Message}");
            }
        }
    }

    // === КР-наблюдаемость, этап 3: перезапуск хода штаба без потери работы ===

    // Перезапуск зависшего хода (главный сценарий этапа 3): чат занят, процесс молчит,
    // написать в него нельзя. Строго по шагам: проверка живости → kill → дождаться смерти →
    // валидация транскрипта → revive → новый ход с --resume (разбор отложенной очереди:
    // EnsureProcessAsync поднимёт процесс с --resume по ClaudeSessionId). Рецепт уборки —
    // тот же, что ReviveStuckSession (карточка → адаптер → статус), но отдельной сборкой:
    // та шлёт СВОЮ карточку «зависший ход сброшен» fire-and-forget, а здесь статус обязан
    // встать в Active до разбора очереди и с карточкой перезапуска. Стадия, бюджет и версия
    // плана живут на Session.TeamImplement и этим путём не трогаются — режим переживает.
    // Ходов модели сам путь не запускает и квоту пробуждений не тратит: до модели доехают
    // только сообщения, уже стоявшие в очереди до зависания.
    internal async Task<StuckTurnRestartResult> RestartStuckTurnAsync(string sessionId, bool startFresh)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");
        // Гейт режима: кнопка и тексты — про штаб «Командной реализации», семантика
        // перезапуска (kill → resume хода штаба) работает только там. Обобщение на
        // все чаты — отдельное решение, не побочный эффект.
        if (entry.Info.TeamImplement is null)
            throw new InvalidOperationException(
                "Перезапуск хода доступен только в чате штаба «Командной реализации»");
        if (!_turnRestarts.TryAdd(sessionId, 0))
            throw new InvalidOperationException("Ход уже перезапускается — дождитесь результата");
        try
        {
            // 1. Живость: чат обязан быть занят — свободному чату перезапуск не нужен.
            // Спасательная ветка (major ревью): первый вызов успел убить процесс и
            // вернуть 409 transcript_damaged, финализация убитого прогона перевела
            // чат в Active, а отложенная очередь умерла на --resume по тому же битому
            // файлу. Чат свободен, но resume-якорь отравлен — без пропуска здесь
            // повторный startFresh вечно упирался бы в «Чат не занят», и у чата не
            // было бы штатного выхода из повреждённого транскрипта.
            if (entry.Info.Status is not (SessionStatus.Working or SessionStatus.Waiting))
            {
                var rescue = startFresh
                    && _route.FindResumeTranscript(entry) is { } damaged
                    && !Llm.Claude.TranscriptProbe.IsTailIntact(damaged);
                if (!rescue)
                    throw new InvalidOperationException(
                        "Чат не занят: ход не идёт. Просто напишите сообщение — оно продолжит разговор");
            }
            // Штаб ждёт ответа человека на карточку (разрешение/вопрос/план) — это не
            // зависание: ответ лежит в ленте чата, прерывать ожидание рестартом нельзя
            if (entry.PendingInteraction is not null)
                throw new InvalidOperationException(
                    "Штаб ждёт вашего ответа на карточку в чате — перезапуск не нужен, ответьте на неё");
            // Живой прогон, начавшийся недавно, — честная работа, а не зависание (тот же
            // порог тишины, что у пульса волны). Долгий немой ход прервать можно — с
            // сохранением контекста через resume.
            var quiet = DateTime.UtcNow - entry.Info.UpdatedAt;
            if (_store.HasLiveTurnProcess(sessionId) && quiet < FreshTurnThreshold)
                throw new InvalidOperationException(
                    $"Штаб работает — {(int)Math.Max(0, quiet.TotalMinutes)} мин назад была активность. " +
                    "Дождитесь результата или остановите ход кнопкой «Стоп»");

            // 2-3. Kill и ОЖИДАНИЕ смерти: HasLiveTurn гаснет в финализации прогона — уже
            // после WaitForExit самого процесса, поэтому поллинг по нему и есть ожидание
            // настоящей смерти, а не факта вызова kill.
            if (entry.Process is { HasLiveTurn: true })
            {
                entry.Process.Interrupt();
                var deadline = DateTime.UtcNow + KillWaitTimeout;
                while (entry.Process is { HasLiveTurn: true })
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new InvalidOperationException(
                            "Не удалось остановить зависший процесс — попробуйте ещё раз через минуту");
                    await Task.Delay(TimeSpan.FromMilliseconds(250));
                }
            }

            // 4. Валидация транскрипта: повреждённый (оборванная последняя строка) через
            // --resume не продолжится. Файл не нашёлся — проверку пропускаем: профиль
            // подписки пула может не входить в AllowedRoots, и ложный «повреждён» на
            // здоровом разговоре хуже пропущенной проверки (CLI сам скажет при resume).
            // startFresh идёт мимо — транскрипт всё равно выбрасываем.
            if (!startFresh && _route.FindResumeTranscript(entry) is { } transcript
                && !Llm.Claude.TranscriptProbe.IsTailIntact(transcript))
                throw new TurnTranscriptDamagedException(
                    "Файл разговора повреждён — продолжить с прежним контекстом нельзя. " +
                    "Можно начать ход заново: старый контекст будет потерян, чат снова заработает");

            // 5. Revive: убираем ожидающую карточку и отравленный адаптер (у него мог
            // остаться захваченным _turnLock зависшей финализацией — DisposeAsync с
            // _cts.Cancel разблокирует ожидателей), статус в Active.
            entry.PendingInteraction = null;
            if (entry.Process is { } dead)
            {
                entry.Process = null;
                DeferredRun.FireAndForget(dead.DisposeAsync().AsTask(),
                    $"уборка адаптера перезапущенного хода ({sessionId})");
            }
            // M5 — тот же сброс буфера маркеров, что у Interrupt: убитый ход result не
            // пришлёт, буфер не потребляется, и маркер мёртвого хода (<escalate:*>,
            // <team:work>) доклеился бы к следующему и применился задним числом.
            lock (entry.TeamTurnLock)
            {
                entry.TeamTurnText.Clear();
                entry.TeamTurnShownLength = 0;
                entry.TurnSawAngleBracket = false;
                entry.TeamTurnAsked = false;
            }
            // «Начать заново»: снимаем resume-якорь — следующий адаптер стартует без
            // --resume, CLI заведёт новую сессию и пришлёт новый id (init ниже допишет)
            if (startFresh && entry.Info.ClaudeSessionId is not null)
            {
                entry.Info.ClaudeSessionId = null;
                _store.SaveSessions();
            }
            await _store.ApplyStatusAsync(sessionId, entry, SessionStatus.Active);
            // Честный текст (minor ревью): продолжение разговора обещаем, только если
            // в очереди правда что-то стоит или активный цикл продолжит себя сам —
            // при пустой очереди нового хода не будет, и «разговор продолжится
            // с того же места» обещало бы его напрасно.
            bool willContinue;
            lock (entry.PendingLock) willContinue = !entry.QueueFrozen && entry.Pending.Count > 0;
            willContinue |= entry.Info.WorkLoop is not null;
            await _sessionHistory.AddWorkLoopStoppedNoticeAsync(sessionId, "team_restart",
                startFresh
                    ? "Ход перезапущен вручную с чистого листа: прежний контекст утерян — напишите, что делать дальше"
                    : willContinue
                        ? "Ход перезапущен вручную — разговор продолжится с сохранённым контекстом"
                        : "Ход перезапущен вручную — чат снова доступен, контекст сохранён");

            // Новый ход с --resume: сообщения, отложенные зависшим ходом, уходят обычной
            // доставкой (DrainInFlight гасит параллельные разборы). Замороженная «Стоп»
            // очередь не разбирается — её держал человек, не мы.
            await _turnStarter.Value.DrainNextPendingAsync(sessionId);

            return new StuckTurnRestartResult(
                Resumed: !startFresh,
                Message: startFresh
                    ? willContinue
                        ? "Ход начнётся заново — контекст сброшен, чат снова доступен"
                        : "Контекст сброшен, чат снова доступен — напишите сообщение, чтобы начать новый ход"
                    : willContinue
                        ? "Ход перезапущен: чат снова доступен, разговор продолжится с того же места"
                        : "Ход перезапущен: чат снова доступен — напишите сообщение, чтобы продолжить разговор");
        }
        finally { _turnRestarts.TryRemove(sessionId, out _); }
    }

    // === Авто-возобновление ходов, оборванных рестартом сервера (фича turn-auto-resume) ===

    // Возобновление хода, оборванного рестартом сервера (B3). Кандидат — сессия, чей ход
    // шёл (Working) в момент падения: LoadSessions перевёл её в Orphaned. Шаги строго по
    // порядку, отказ любого — не шлём, в ленте остаётся карточка-баннер:
    //   1. статус Orphaned (кандидат, а не idle/упавший);
    //   2. хвост транскрипта цел (битый — через --resume не продолжится);
    //   3. гидратация аккумулятора (ДО карточки — карточка пишется в историю);
    //   4. карточка «Сервер был перезапущен — продолжаю ход с того же места»;
    //   5. RestartResumeAttempts += 1 + SaveSessions (UpdatedAt не двигаем);
    //   6. resume-ход обычным путём (auto, cause=ServerRestartResume).
    // Швы тестов (IsTailIntactForResume / SendRestartResumeTurnAsync) приходят делегатами от
    // фасада: они остаются virtual на нём — на них стоит SpySessionManager (TurnAutoResume).
    internal async Task<RestartResumeResult> ResumeAfterRestartAsync(string sessionId,
        Func<string, bool> isTailIntactForResume, Func<string, string, Task<SendUserOutcome>> sendRestartResumeTurn)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            return new RestartResumeResult(false, "Сессия не найдена");

        // 1. Кандидат: исходный Working → Orphaned при рестарте.
        if (entry.Info.Status != SessionStatus.Orphaned)
            return new RestartResumeResult(false,
                $"Статус {entry.Info.Status}, ход не оборван рестартом");

        // 2. Целостность хвоста: битый транскрипт через --resume не продолжится.
        if (!isTailIntactForResume(sessionId))
            return new RestartResumeResult(false, "Хвост транскрипта повреждён");

        // 3. Явная гидратация аккумулятора + карточка в ленту — делает их роль истории
        //    целиком (S3-7): AppendServerRestartCardAsync сама создаёт аккумулятор из
        //    существующей истории, если он null (в сценарии 1 — чат уже имел ClaudeSessionId,
        //    ход был Error — после LoadSessions он может остаться null), и лишь потом пишет
        //    карточку в историю и сохраняет её.
        await _sessionHistory.AppendServerRestartCardAsync(sessionId);

        // 5. Счётчик попыток + сохранение. UpdatedAt не двигаем: SaveSessions лишь
        //    персистит, Touch/UpdatedAt=now здесь не вызываем (служебная операция).
        entry.Info.RestartResumeAttempts += 1;
        _store.SaveSessions();

        // 6. Resume-ход обычным путём: auto — для прохода через DeviceWaitGate.
        var prompt = Prompts.TurnResumePrompts.ServerRestartResume();
        await sendRestartResumeTurn(sessionId, prompt);
        return new RestartResumeResult(true);
    }
}
