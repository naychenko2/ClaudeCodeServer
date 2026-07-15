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

    // Персона текущего хода: её id пишется в text-сообщения истории (авторство реплик).
    // Обновляется перед каждым ходом — после смены собеседника новые реплики получают
    // новую персону, а старые сохраняют прежнюю.
    private string? _personaId;
    public void SetPersona(string? personaId)
    {
        lock (_lock) _personaId = personaId;
    }

    public void OnUserMessage(string text, IReadOnlyList<string> attachedPaths, bool viaAgent = false,
        string? senderPersonaId = null, bool systemDirective = false, bool auto = false)
    {
        lock (_lock)
            _currentTurn.Add(new StoredUserMessage(text, attachedPaths.Count > 0 ? [.. attachedPaths] : null,
                viaAgent ? true : null, senderPersonaId, systemDirective ? true : null, auto ? true : null));
    }

    public void OnSessionStarted(string model, string mode)
    {
        lock (_lock) _currentTurn.Add(new StoredSessionStartedMessage(model, mode));
    }

    public void OnTextDelta(string text)
    {
        lock (_lock) _textBuf.Append(text);
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
            _currentTurn.Add(new StoredTextMessage(text, null, parentToolUseId));
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
            }
        }
    }

    public void OnFileChanged(string path, int added, int removed)
    {
        lock (_lock)
        {
            FlushBuffers();
            _currentTurn.Add(new StoredFileChangedMessage(path, added, removed));
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

    public async Task OnResultAsync(string subtype, long durationMs, int numTurns,
        UsageInfo? usage, double? totalCostUsd, string? apiErrorStatus, IReadOnlyList<string>? permissionDenials, ChatHistoryService svc)
    {
        lock (_lock)
        {
            FlushBuffers();
            _currentTurn.Add(new StoredResultMessage(subtype, durationMs, numTurns, usage, totalCostUsd, apiErrorStatus, permissionDenials));
        }
        await FlushAsync(svc);
    }

    public async Task OnErrorAsync(string text, ChatHistoryService svc)
    {
        lock (_lock)
        {
            FlushBuffers();
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
            if (_textBuf.Length > 0)
                result.Add(new StoredTextMessage(_textBuf.ToString(), _personaId));
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

    // Вызывать только под _lock
    private void FlushBuffers()
    {
        if (_textBuf.Length > 0)
        {
            _currentTurn.Add(new StoredTextMessage(_textBuf.ToString(), _personaId));
            _textBuf.Clear();
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
        }
        await SaveSnapshotAsync(svc);
    }
}
