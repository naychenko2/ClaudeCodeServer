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
    // Сессия с ролью: служебные строки-маркеры "[MEMORY] …" не должны попадать в историю
    private readonly bool _stripMemory;
    // Защищает _history/_currentTurn от гонки: стоимость fal.ai добавляется из фонового
    // потока опроса billing-events, параллельно с пампом сообщений и GetAll() из HTTP-потоков.
    private readonly object _lock = new();

    public TurnAccumulator(List<StoredMessage> history, string? saveKey = null, bool stripMemoryMarkers = false)
    {
        _history = history;
        _saveKey = saveKey;
        _stripMemory = stripMemoryMarkers;
    }

    // Убирает из текста ответа строки-маркеры памяти роли (строка начинается с "[MEMORY]").
    // Маркеры обрабатываются отдельным каналом (RoleMemoryService) и юзеру не показываются.
    internal static string StripMemoryLines(string text)
    {
        if (!text.Contains("[MEMORY]", StringComparison.OrdinalIgnoreCase)) return text;
        var kept = text.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("[MEMORY]", StringComparison.OrdinalIgnoreCase));
        return string.Join('\n', kept).TrimEnd();
    }

    // Текст ответа для истории: для сессий с ролью — без [MEMORY]-строк
    private string TextForStore() =>
        _stripMemory ? StripMemoryLines(_textBuf.ToString()) : _textBuf.ToString();

    public void SetSaveKey(string claudeSessionId) => _saveKey = claudeSessionId;

    public void OnUserMessage(string text, IReadOnlyList<string> attachedPaths)
        => _currentTurn.Add(new StoredUserMessage(text, attachedPaths.Count > 0 ? [.. attachedPaths] : null));

    public void OnSessionStarted(string model, string mode)
        => _currentTurn.Add(new StoredSessionStartedMessage(model, mode));

    public void OnTextDelta(string text) => _textBuf.Append(text);

    public void OnThinkingDelta(string text) => _thinkingBuf.Append(text);

    public void OnToolUse(string id, string name, object? input, string? parentToolUseId = null)
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

    public void OnToolResult(string toolUseId, string content, bool isError)
    {
        if (_pendingTools.TryGetValue(toolUseId, out var msg))
        {
            msg.Result = content;
            msg.IsError = isError;
        }
    }

    public void OnFileChanged(string path, int added, int removed)
    {
        FlushBuffers();
        _currentTurn.Add(new StoredFileChangedMessage(path, added, removed));
    }

    public void OnCompactBoundary(string trigger, int? preTokens, int? postTokens)
    {
        FlushBuffers();
        _currentTurn.Add(new StoredCompactBoundaryMessage(trigger, preTokens, postTokens));
    }

    public void OnAskQuestion(string toolUseId, object? input)
    {
        FlushBuffers();
        var msg = new StoredAskQuestionMessage { ToolUseId = toolUseId, Input = input };
        _pendingQuestions[toolUseId] = msg;
        _currentTurn.Add(msg);
    }

    public void OnQuestionAnswered(string toolUseId, object? answers)
    {
        if (_pendingQuestions.TryGetValue(toolUseId, out var msg)) { msg.Resolved = true; msg.Answers = answers; }
    }

    public void OnPlanReview(string requestId, string plan)
    {
        FlushBuffers();
        var msg = new StoredPlanReviewMessage { RequestId = requestId, Plan = plan };
        _pendingPlans[requestId] = msg;
        _currentTurn.Add(msg);
    }

    public void OnPlanResolved(string requestId, bool approved, string? feedback)
    {
        if (_pendingPlans.TryGetValue(requestId, out var msg)) { msg.Resolved = true; msg.Approved = approved; msg.Feedback = feedback; }
    }

    public async Task OnResultAsync(string subtype, long durationMs, int numTurns,
        UsageInfo? usage, double? totalCostUsd, string? apiErrorStatus, IReadOnlyList<string>? permissionDenials, ChatHistoryService svc)
    {
        FlushBuffers();
        _currentTurn.Add(new StoredResultMessage(subtype, durationMs, numTurns, usage, totalCostUsd, apiErrorStatus, permissionDenials));
        await FlushAsync(svc);
    }

    public async Task OnErrorAsync(string text, ChatHistoryService svc)
    {
        FlushBuffers();
        _currentTurn.Add(new StoredErrorMessage(text));
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
            {
                var text = TextForStore();
                if (text.Length > 0) result.Add(new StoredTextMessage(text));
            }
            return result;
        }
    }

    // Сохраняет снимок текущего состояния не закрывая ход.
    // Вызывается после каждого tool_result чтобы частичная история
    // была доступна на диске даже при рестарте сервера.
    public async Task SaveSnapshotAsync(ChatHistoryService svc)
    {
        if (_saveKey is null) return;
        await svc.SaveAsync(_saveKey, GetAll());
    }

    private void FlushBuffers()
    {
        if (_textBuf.Length > 0)
        {
            var text = TextForStore();
            if (text.Length > 0) _currentTurn.Add(new StoredTextMessage(text));
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
        if (_saveKey is not null)
            await svc.SaveAsync(_saveKey, GetAll());
    }
}
