using System.Text;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

internal class TurnAccumulator
{
    private string? _saveKey;
    private readonly List<StoredMessage> _history;
    private readonly List<StoredMessage> _currentTurn = [];
    private readonly Dictionary<string, StoredToolUseMessage> _pendingTools = [];
    // Для обновления решения (resolved) уже добавленных карточек вопроса/плана
    private readonly Dictionary<string, StoredAskQuestionMessage> _pendingQuestions = [];
    private readonly Dictionary<string, StoredPlanReviewMessage> _pendingPlans = [];
    private readonly StringBuilder _textBuf = new();
    private readonly StringBuilder _thinkingBuf = new();
    // Волна 7 (утечка маркеров протокола в сохранённой истории, прод 2026-08-02): весь сырой
    // текст ХОДА целиком (не чистится в FlushBuffers, только на границе хода в FlushAsync) —
    // маркер `<team:work>`/`<escalate:*>` в длинном структурированном ответе координатора может
    // открыться до, а закрыться ПОСЛЕ вызова инструмента/карточки плана/вопроса (все они дёргают
    // FlushBuffers), и раньше пара искалась в каждом куске между flush'ами по отдельности —
    // разъехавшиеся половины маркера не находили друг друга и утекали в историю буквально.
    // Считаем «безопасный» текст от ВСЕГО _teamRawText сразу, как это уже делает живая
    // трансляция (SessionManager.OnMessageAsync, entry.TeamTurnText) — и берём только новый
    // хвост поверх уже показанного (_teamShownLength), симметрично тому же приёму.
    private readonly StringBuilder _teamRawText = new();
    private int _teamShownLength;
    // Защищает ВСЁ изменяемое состояние (_history/_currentTurn/буферы/pending-словари):
    // мутации идут из пампа stdout, SignalR-вызовов (ответы на вопросы/планы) и фонового
    // опроса billing-events fal.ai, чтение — из HTTP-потоков (GetAll).
    // Локи короткие, await под ними нет.
    private readonly object _lock = new();
    // Сериализует запись history.json: снапшоты сохраняются и из пампа (awaited),
    // и fire-and-forget из SessionManager — без семафора записи конкурируют за файл
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public TurnAccumulator(List<StoredMessage> history, string? saveKey = null)
    {
        _history = history;
        _saveKey = saveKey;
    }

    public void SetSaveKey(string claudeSessionId)
    {
        lock (_lock) _saveKey = claudeSessionId;
    }

    // Чат удалён: снапшоты больше не пишем — финализация прогона, доигрывающаяся после
    // удаления (drain сабагентов, поздние tool_result), пересоздавала бы history.json
    public void MarkDeleted()
    {
        lock (_lock) _saveKey = null;
    }

    // Персона текущего хода: её id пишется в text-сообщения истории (авторство реплик).
    // Обновляется перед каждым ходом — после смены собеседника новые реплики получают
    // новую персону, а старые сохраняют прежнюю.
    private string? _personaId;
    public void SetPersona(string? personaId)
    {
        lock (_lock) _personaId = personaId;
    }

    public void OnUserMessage(string text, IReadOnlyList<string> attachedPaths, bool viaAgent = false,
        string? senderPersonaId = null, bool systemDirective = false, bool auto = false,
        string? senderOrigin = null, string? staffNote = null)
    {
        lock (_lock)
            _currentTurn.Add(new StoredUserMessage(text, attachedPaths.Count > 0 ? [.. attachedPaths] : null,
                viaAgent ? true : null, senderPersonaId, systemDirective ? true : null, auto ? true : null,
                senderOrigin, staffNote: staffNote, timestamp: NowMs()));
    }

    // Снимок промпта хода записан — привязываем его к сообщению, которым ход начался
    // (кнопка «какой промпт ушёл» живёт под этим постом). Сообщения может не быть:
    // продолжение цикла «до готово» идёт без нового сообщения человека — тогда снимок
    // остаётся на диске, но кнопки под постом не будет.
    public void SetPromptSnapshot(string snapshotId)
    {
        lock (_lock)
        {
            for (var i = _currentTurn.Count - 1; i >= 0; i--)
                if (_currentTurn[i] is StoredUserMessage user)
                {
                    user.PromptSnapshotId = snapshotId;
                    return;
                }
        }
    }

    public void OnSessionStarted(string model, string mode, TurnWorktreeInfo? worktree = null)
    {
        lock (_lock) _currentTurn.Add(new StoredSessionStartedMessage(model, mode, worktree));
    }

    // Время начала накопления текстового поста (Unix-мс UTC). Берём момент ПЕРВОЙ дельты,
    // а не флаша: пост «написан» тогда, когда ассистент начал его писать, а флаш может
    // случиться сильно позже — на первом же вызове инструмента.
    private long? _textBufStartedAt;

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void OnTextDelta(string text)
    {
        lock (_lock)
        {
            _textBufStartedAt ??= NowMs();
            _textBuf.Append(text);
        }
    }

    public void OnThinkingDelta(string text)
    {
        lock (_lock) _thinkingBuf.Append(text);
    }

    public void OnToolUse(string id, string name, object? input, string? parentToolUseId = null)
    {
        lock (_lock)
        {
            FlushBuffers();
            // Дедуп: ранняя карточка из стрима (пустой input) + финальный assistant с тем же id → обновляем, не дублируем
            if (_pendingTools.TryGetValue(id, out var existing))
            {
                existing.Input = input;
                return;
            }
            var msg = new StoredToolUseMessage { Id = id, Name = name, Input = input, ParentToolUseId = parentToolUseId };
            _pendingTools[id] = msg;
            _currentTurn.Add(msg);
        }
    }

    // Текст/thinking сабагента приходят целыми блоками (не дельтами) — сразу отдельными
    // записями. FlushBuffers обязателен: live-редьюсер в этот момент «разрезает» накапливаемый
    // текст основного агента, история должна повторить тот же порядок элементов.
    public void OnAgentText(string parentToolUseId, string text)
    {
        lock (_lock)
        {
            FlushBuffers();
            _currentTurn.Add(new StoredTextMessage(text, null, parentToolUseId, timestamp: NowMs()));
        }
    }

    public void OnAgentThinking(string parentToolUseId, string text)
    {
        lock (_lock)
        {
            FlushBuffers();
            _currentTurn.Add(new StoredThinkingMessage(text, parentToolUseId));
        }
    }

    public void OnToolResult(string toolUseId, string content, bool isError)
    {
        lock (_lock)
        {
            if (_pendingTools.TryGetValue(toolUseId, out var msg))
            {
                msg.Result = content;
                msg.IsError = isError;
                return;
            }
            // Инструмент завершился после конца хода (дочерний вызов доживающего фонового
            // агента) — его tool_use уже уплыл в _history: дописываем результат туда,
            // иначе после перезагрузки карточка крутила бы спиннер вечно
            for (var i = _history.Count - 1; i >= 0; i--)
                if (_history[i] is StoredToolUseMessage h && h.Id == toolUseId)
                {
                    h.Result = content;
                    h.IsError = isError;
                    return;
                }
        }
    }

    // Завершение фоновых агентов (bg_agent_done): помечаем их tool_use — единственный
    // признак «ответ готов» для карточек с квитанцией фонового запуска
    public void OnBgAgentsDone(IReadOnlyList<string> toolUseIds)
    {
        lock (_lock)
        {
            foreach (var id in toolUseIds)
            {
                if (_pendingTools.TryGetValue(id, out var msg))
                {
                    msg.BgDone = true;
                    continue;
                }
                for (var i = _history.Count - 1; i >= 0; i--)
                    if (_history[i] is StoredToolUseMessage h && h.Id == id)
                    {
                        h.BgDone = true;
                        break;
                    }
            }
        }
    }

    // Последний снапшот прогресса workflow — upsert по ToolUseId прямо в _history
    // (событие внеходовое: тики приходят и после конца хода)
    public void OnWorkflowProgress(string toolUseId, bool isDone, IReadOnlyList<WorkflowAgentDto> agents)
    {
        lock (_lock)
        {
            foreach (var m in _history)
                if (m is StoredWorkflowProgressMessage exists && exists.ToolUseId == toolUseId)
                {
                    exists.IsDone = isDone;
                    exists.Agents = agents;
                    return;
                }
            _history.Add(new StoredWorkflowProgressMessage { ToolUseId = toolUseId, IsDone = isDone, Agents = agents });
        }
    }

    public void OnFileChanged(string path, int added, int removed, bool external = false)
    {
        lock (_lock)
        {
            FlushBuffers();
            // Дедуп за ход: повторная правка того же файла обновляет существующую строку
            // (дельты суммируем), а не плодит новую — иначе командные ходы (OmO, workflow)
            // спамят ленту десятками строк по одним и тем же файлам. External — по И: если
            // хоть один вклад был от модели этого чата, строка в целом не «чужая»
            for (var i = _currentTurn.Count - 1; i >= 0; i--)
            {
                if (_currentTurn[i] is StoredFileChangedMessage prev && prev.Path == path)
                {
                    _currentTurn[i] = new StoredFileChangedMessage(path, prev.Added + added, prev.Removed + removed,
                        prev.External && external);
                    return;
                }
            }
            _currentTurn.Add(new StoredFileChangedMessage(path, added, removed, external));
        }
    }

    public void OnCompactBoundary(string trigger, int? preTokens, int? postTokens)
    {
        lock (_lock)
        {
            FlushBuffers();
            _currentTurn.Add(new StoredCompactBoundaryMessage(trigger, preTokens, postTokens));
        }
    }

    // Пометка «Ответила …» при автоподмене модели фолбэком уровня 2 (сторонний
    // провайдер с моделью-эквивалентом). Уровень 1 (ротация подписок) модель не
    // трогает — пилюля там не нужна. PreviousModel — модель последнего session_started
    // этого хода (провалившаяся попытка успела его прислать). null — пометки не
    // пишем: в начале чата session_started ещё нет, и без PreviousModel пилюля в
    // истории врала бы («Ответила X — была Y»), а Y неизвестна.
    public void OnModelSwitched(string model, string? previousModel, string? reason)
    {
        if (previousModel is null) return;
        lock (_lock)
        {
            FlushBuffers();
            _currentTurn.Add(new StoredModelSwitchedMessage
            {
                Model = model,
                PreviousModel = previousModel,
                Reason = reason,
            });
        }
    }

    // Последняя модель session_started этого хода: точка сравнения для пометки
    // «Ответила …» в OnModelSwitched. Обход — от хвоста текущего хода вглубь истории:
    // session_started предыдущего хода к моменту следующей подмены ещё актуален
    // (модель не менялась между попытками, если не было другой подмены).
    public string? LastStartedModel()
    {
        lock (_lock)
        {
            for (var i = _currentTurn.Count - 1; i >= 0; i--)
                if (_currentTurn[i] is StoredSessionStartedMessage s && !string.IsNullOrEmpty(s.Model))
                    return s.Model;
            for (var i = _history.Count - 1; i >= 0; i--)
                if (_history[i] is StoredSessionStartedMessage s && !string.IsNullOrEmpty(s.Model))
                    return s.Model;
        }
        return null;
    }

    public void OnAskQuestion(string toolUseId, object? input)
    {
        lock (_lock)
        {
            FlushBuffers();
            var msg = new StoredAskQuestionMessage { ToolUseId = toolUseId, Input = input };
            _pendingQuestions[toolUseId] = msg;
            _currentTurn.Add(msg);
        }
    }

    public void OnQuestionAnswered(string toolUseId, object? answers)
    {
        lock (_lock)
        {
            if (_pendingQuestions.TryGetValue(toolUseId, out var msg)) { msg.Resolved = true; msg.Answers = answers; }
        }
    }

    public void OnPlanReview(string requestId, string plan)
    {
        lock (_lock)
        {
            FlushBuffers();
            var msg = new StoredPlanReviewMessage { RequestId = requestId, Plan = plan };
            _pendingPlans[requestId] = msg;
            _currentTurn.Add(msg);
        }
    }

    public void OnPlanResolved(string requestId, bool approved, string? feedback)
    {
        lock (_lock)
        {
            if (_pendingPlans.TryGetValue(requestId, out var msg)) { msg.Resolved = true; msg.Approved = approved; msg.Feedback = feedback; }
        }
    }

    // Карточка плана «Командной реализации» (Э2): публикуется бэкендом, а не CLI.
    public void OnTeamPlan(Models.TeamImplementPlan plan)
    {
        lock (_lock)
        {
            FlushBuffers();
            // PersonaId (Э8) — автор карточки на момент публикации: планировщик. Пишем в
            // stored-слой, чтобы шапка «аватар + имя» жила и после рестарта, и после смены
            // координатора (тот менять историю не должен).
            _currentTurn.Add(new StoredTeamPlanMessage
            {
                PlanId = plan.Id,
                Plan = plan,
                PersonaId = plan.PlannerPersonaId,
            });
        }
    }

    // Правка/решение по карточке плана. В отличие от plan_review ищем по ВСЕЙ истории:
    // карточка ждёт человека дольше хода, а pending-словари чистятся на границе хода
    // (FlushAsync). Возвращает false, если карточки с таким id нет.
    public bool OnTeamPlanUpdated(string planId, Models.TeamImplementPlan plan, bool? approved)
    {
        lock (_lock)
        {
            var card = _currentTurn.Concat(_history).OfType<StoredTeamPlanMessage>()
                .LastOrDefault(m => m.PlanId == planId);
            if (card is null) return false;
            card.Plan = plan;
            if (approved is not null) { card.Resolved = true; card.Approved = approved; }
            return true;
        }
    }

    // Карточка плана по id — источник правды при ответе хаба (правка исполнителя приходит
    // после рестарта сервера, когда состояние есть только в истории).
    public Models.TeamImplementPlan? FindTeamPlan(string planId)
    {
        lock (_lock)
            return _currentTurn.Concat(_history).OfType<StoredTeamPlanMessage>()
                .LastOrDefault(m => m.PlanId == planId && !m.Resolved)?.Plan;
    }

    // План независимо от того, разрешена ли карточка (Э4): после «Запустить» карточка
    // Resolved, а автономный цикл волн ходит по этому же плану — раздаёт остаток и правит
    // счётчик попыток под-задач.
    public Models.TeamImplementPlan? FindTeamPlanAny(string planId)
    {
        lock (_lock)
            return _currentTurn.Concat(_history).OfType<StoredTeamPlanMessage>()
                .LastOrDefault(m => m.PlanId == planId)?.Plan;
    }

    // Карточка остановки (Э4): публикуется бэкендом, как карточка плана.
    public void OnTeamEscalation(Models.TeamEscalation escalation)
    {
        lock (_lock)
        {
            FlushBuffers();
            _currentTurn.Add(new StoredTeamEscalationMessage
            {
                EscalationId = escalation.Id,
                Escalation = escalation,
            });
        }
    }

    // Решение человека по карточке остановки. Ищем по всей истории — карточка ждёт человека
    // дольше хода. false — карточки с таким id нет либо она уже разрешена.
    public bool OnTeamEscalationResolved(string escalationId, string? actionId)
    {
        lock (_lock)
        {
            var card = _currentTurn.Concat(_history).OfType<StoredTeamEscalationMessage>()
                .LastOrDefault(m => m.EscalationId == escalationId);
            if (card is null || card.Escalation.Resolved) return false;
            card.Escalation.Resolved = true;
            card.Escalation.ChosenActionId = actionId;
            return true;
        }
    }

    public Models.TeamEscalation? FindTeamEscalation(string escalationId)
    {
        lock (_lock)
            return _currentTurn.Concat(_history).OfType<StoredTeamEscalationMessage>()
                .LastOrDefault(m => m.EscalationId == escalationId)?.Escalation;
    }

    public async Task OnResultAsync(string subtype, long durationMs, int numTurns,
        UsageInfo? usage, double? totalCostUsd, string? apiErrorStatus, IReadOnlyList<string>? permissionDenials, ChatHistoryService svc,
        int? contextTokens = null, string? usageModel = null)
    {
        lock (_lock)
        {
            FlushBuffers(final: true);
            // Модель хода известна только сейчас (её несёт result), а посты этого хода уже
            // созданы — проставляем задним числом. Сабагентские тексты (ParentToolUseId)
            // пропускаем: они могли идти другой моделью, и UsageModel про них не говорит.
            foreach (var m in _currentTurn)
                if (m is StoredTextMessage t && t.ParentToolUseId is null && t.Model is null)
                    t.Model = usageModel;
            _currentTurn.Add(new StoredResultMessage(subtype, durationMs, numTurns, usage, totalCostUsd, apiErrorStatus, permissionDenials, contextTokens));
        }
        await FlushAsync(svc);
    }

    public async Task OnErrorAsync(string text, ChatHistoryService svc)
    {
        lock (_lock)
        {
            FlushBuffers(final: true);
            _currentTurn.Add(new StoredErrorMessage(text));
        }
        await FlushAsync(svc);
    }

    // Стоимость генерации fal.ai приходит асинхронно (вне хода) — добавляем в историю напрямую.
    // Возвращает false, если запись с таким requestId уже есть (дедуп run_model + get_job_result).
    public bool OnFalCost(string requestId, string? endpointId, double costUsd, double? outputUnits, double? unitPrice)
    {
        lock (_lock)
        {
            bool exists =
                _history.Any(m => m is StoredFalCostMessage f && f.RequestId == requestId) ||
                _currentTurn.Any(m => m is StoredFalCostMessage f && f.RequestId == requestId);
            if (exists) return false;
            _history.Add(new StoredFalCostMessage(requestId, endpointId, costUsd, outputUnits, unitPrice));
            return true;
        }
    }

    // Учёт завершённой glif-генерации приходит синхронно из tool_result — добавляем в историю.
    // Возвращает false, если запись с таким jobId уже есть.
    public bool OnGlifCost(string jobId, string? outputType, int mediaCount, double? credits, string? model)
    {
        lock (_lock)
        {
            bool exists =
                _history.Any(m => m is StoredGlifCostMessage g && g.JobId == jobId) ||
                _currentTurn.Any(m => m is StoredGlifCostMessage g && g.JobId == jobId);
            if (exists) return false;
            _history.Add(new StoredGlifCostMessage(jobId, outputType, mediaCount, credits, model));
            return true;
        }
    }

    // Внеходовая запись (карточка фазы совещания и т.п.) — сразу в _history,
    // минуя текущий ход (как OnFalCost, но без дедупа — он на вызывающей стороне)
    public void Append(StoredMessage message)
    {
        lock (_lock) _history.Add(message);
    }

    // Снапшот: новый список; элементы разделяются (StoredToolUseMessage и карточки
    // вопроса/плана мутируются позже), но их поля — атомарные ссылки/bool,
    // поэтому глубокая копия не нужна.
    public List<StoredMessage> GetAll()
    {
        lock (_lock)
        {
            var result = new List<StoredMessage>(_history.Count + _currentTurn.Count + 2);
            result.AddRange(_history);
            result.AddRange(_currentTurn);
            // Включаем буферизованный текст/думание (ещё не зафиксированный в _currentTurn)
            if (_thinkingBuf.Length > 0)
                result.Add(new StoredThinkingMessage(_thinkingBuf.ToString()));
            // Превью непрокоммиченного хвоста — от полного сырого текста хода (см. _teamRawText),
            // иначе снимок посреди хода показал бы половину маркера, которую итоговый FlushBuffers
            // потом всё равно скроет/довырежет
            if (_textBuf.Length > 0)
            {
                var raw = _teamRawText.ToString() + _textBuf;
                var safe = SessionManager.TrimUnresolvedMarkerOpen(SessionManager.StripTeamProtocolMarkers(raw));
                if (safe.Length > _teamShownLength)
                    result.Add(new StoredTextMessage(safe[_teamShownLength..], _personaId,
                        timestamp: _textBufStartedAt ?? NowMs()));
            }
            return result;
        }
    }

    // Сохраняет снимок текущего состояния не закрывая ход.
    // Вызывается после каждого tool_result чтобы частичная история
    // была доступна на диске даже при рестарте сервера.
    public async Task SaveSnapshotAsync(ChatHistoryService svc)
    {
        string? key;
        lock (_lock) key = _saveKey;
        if (key is null) return;
        // Семафор на инстанс (= одна сессия): параллельные сохранения не должны
        // писать один history.json одновременно
        await _saveLock.WaitAsync();
        try { await svc.SaveAsync(key, GetAll()); }
        finally { _saveLock.Release(); }
    }

    // Вызывать только под _lock. final=true — конец хода (OnResultAsync/OnErrorAsync):
    // дальше дельт не будет, поэтому хвост, придержанный TrimUnresolvedMarkerOpen как «вдруг
    // маркер ещё не закрылся», можно просто показать как обычный текст — симметрично тому,
    // что делает живая трансляция на result/error (SessionManager.OnMessageAsync, finalSafe).
    private void FlushBuffers(bool final = false)
    {
        if (_textBuf.Length > 0)
        {
            _teamRawText.Append(_textBuf);
            _textBuf.Clear();
        }
        if (_teamRawText.Length > 0)
        {
            // Маркеры протокола «Командной реализации» (`<team:work>`, `<escalate:*>`,
            // `<team:talk/>`) — внутренняя договорённость координатора с бэкендом, в
            // сохранённой истории им не место (иначе после перезагрузки/reload они снова
            // всплывают в ленте, даже если живая трансляция их уже отфильтровала). Считаем
            // от ВСЕГО _teamRawText — маркер мог открыться до и закрыться ПОСЛЕ вызова
            // инструмента, разъехавшись между несколькими FlushBuffers.
            var raw = _teamRawText.ToString();
            var safe = final
                ? SessionManager.StripTeamProtocolMarkers(raw)
                : SessionManager.TrimUnresolvedMarkerOpen(SessionManager.StripTeamProtocolMarkers(raw));
            if (safe.Length > _teamShownLength)
            {
                var delta = safe[_teamShownLength..];
                _teamShownLength = safe.Length;
                _currentTurn.Add(new StoredTextMessage(delta, _personaId, timestamp: _textBufStartedAt ?? NowMs()));
                _textBufStartedAt = null;
            }
        }
        if (_thinkingBuf.Length > 0)
        {
            _currentTurn.Add(new StoredThinkingMessage(_thinkingBuf.ToString()));
            _thinkingBuf.Clear();
        }
    }

    private async Task FlushAsync(ChatHistoryService svc)
    {
        lock (_lock)
        {
            _history.AddRange(_currentTurn);
            _currentTurn.Clear();
            _pendingTools.Clear();
            _pendingQuestions.Clear();
            _pendingPlans.Clear();
            _teamRawText.Clear();
            _teamShownLength = 0;
        }
        await SaveSnapshotAsync(svc);
    }
}
