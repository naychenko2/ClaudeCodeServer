using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm.DeepSeek;

// Адаптер DeepSeek: chat completions по HTTP (SSE) + собственный tool-цикл.
// Контракт сообщений тот же, что у ClaudeSession, — фронт работает без изменений.
// Каждый ход ОБЯЗАН завершиться ExitedMessage, иначе SessionManager не переведёт статус.
public class DeepSeekSession : ILlmSessionAdapter
{
    public Session Info { get; }

    public LlmCapabilities Capabilities => LlmCapabilitiesCatalog.DeepSeek;

    private readonly string _rootPath;
    private readonly Func<ServerMessage, Task> _onMessage;
    private readonly Func<IReadOnlyList<PermissionRule>>? _permissionRules;
    private readonly string? _rawSystemPrompt;
    private readonly DeepSeekClient _client;
    private readonly IOptions<DeepSeekOptions> _options;
    private readonly DeepSeekToolRegistry _registry;
    private readonly DeepSeekConversationStore _store;
    private readonly TurnFileWatcher _fileWatcher;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    // Ожидающие permission-диалоги; мутируют памп хода и SignalR-потоки параллельно
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _permissionWaiters = new();
    // Инструменты, для которых пользователь выбрал «всегда разрешать» в этой сессии
    private readonly ConcurrentDictionary<string, byte> _autoAllowTools = new();
    private volatile CancellationTokenSource? _currentTurnCts;

    // Если API не выдаёт ни одного события дольше этого — считаем зависшим
    private static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PermissionTimeout = TimeSpan.FromMinutes(60);

    public DeepSeekSession(Session info, LlmSessionContext context, DeepSeekClient client,
        IOptions<DeepSeekOptions> options, FileService files, string sessionsBasePath)
    {
        Info = info;
        _rootPath = context.RootPath;
        _onMessage = context.OnMessage;
        _permissionRules = context.PermissionRules;
        _rawSystemPrompt = context.RawSystemPrompt;
        _client = client;
        _options = options;
        _registry = new DeepSeekToolRegistry(_rootPath, files);
        _store = new DeepSeekConversationStore(sessionsBasePath);
        _fileWatcher = new TurnFileWatcher(_rootPath, _onMessage);
    }

    public Task StartAsync() => Task.CompletedTask;

    public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null)
    {
        Info.MessageCount++;
        Info.LastMessage = text.Length > 100 ? text[..100] + "…" : text;
        Info.UpdatedAt = DateTime.UtcNow;

        // Изображения DeepSeek не принимает — честная пометка; остальное инлайним в текст
        var (imagePaths, otherPaths) = AttachmentInliner.SplitImagePaths(attachedPaths);
        var fullText = AttachmentInliner.BuildMessageText(_rootPath, text, otherPaths);
        if (imagePaths.Count > 0)
            fullText += "\n\n---\n" + string.Join("\n", imagePaths.Select(p =>
                $"Прикреплено изображение {p} — провайдер DeepSeek не поддерживает изображения, содержимое недоступно."));

        return QueueTurnAsync(fullText);
    }

    // Compact не поддержан (Capabilities.SupportsCompact=false, SessionManager не вызывает)
    public Task CompactAsync() => Task.CompletedTask;

    public void RespondPermission(string requestId, string behavior)
    {
        if (_permissionWaiters.TryGetValue(requestId, out var tcs))
            tcs.TrySetResult(behavior);
    }

    // AskUserQuestion/ExitPlanMode — механики Claude, у DeepSeek их нет
    public void AnswerQuestion(string toolUseId, string updatedInputJson) { }
    public void RespondPlan(string requestId, bool approve, string? feedback) { }

    public void Interrupt()
    {
        _currentTurnCts?.Cancel();
        foreach (var tcs in _permissionWaiters.Values)
            tcs.TrySetCanceled();
        _permissionWaiters.Clear();
    }

    // Ставит ход в очередь в фоне, чтобы не блокировать SignalR-соединение
    private Task QueueTurnAsync(string fullText)
    {
        _ = Task.Run(async () =>
        {
            if (_cts.IsCancellationRequested) return;
            await _turnLock.WaitAsync(_cts.Token);
            try { await RunTurnAsync(fullText); }
            catch (OperationCanceledException) { /* остановка сессии — штатно */ }
            catch (Exception ex)
            {
                // Статус Error выставит SessionManager по ErrorMessage
                await _onMessage(new ErrorMessage(ex.Message));
                await _onMessage(new ExitedMessage());
            }
            finally { _turnLock.Release(); }
        });

        return Task.CompletedTask;
    }

    private async Task RunTurnAsync(string text)
    {
        var opts = _options.Value;
        var modelCfg = opts.FindModel(Info.Model);
        if (modelCfg is null)
        {
            await _onMessage(new ErrorMessage(
                $"Модель «{Info.Model}» не настроена в DeepSeek:Models — выбери другую модель"));
            await _onMessage(new ExitedMessage());
            return;
        }

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _currentTurnCts = turnCts;
        var ct = turnCts.Token;

        var sw = Stopwatch.StartNew();
        long promptMiss = 0, cacheHit = 0, completion = 0;
        var iterations = 0;

        // engine id: GUID в Session.ClaudeSessionId — тот же ключ, что у TurnAccumulator/UI-истории
        var isResume = Info.ClaudeSessionId is not null;
        var engineId = Info.ClaudeSessionId ??= Guid.NewGuid().ToString();
        _store.Bind(engineId);
        if (_store.Messages.Count == 0)
            _store.Append(new JsonObject { ["role"] = "system", ["content"] = BuildSystemPrompt() });

        _store.Append(new JsonObject { ["role"] = "user", ["content"] = text });

        await _onMessage(new SessionStartedMessage(engineId, isResume, modelCfg.EffectiveApiModel,
            Info.Mode.ToWireToken(), _rootPath, _registry.All.Count, null,
            Capabilities.Provider, Capabilities));

        _fileWatcher.Start();
        try
        {
            var tools = modelCfg.SupportsTools ? _registry.BuildToolsJson() : null;

            while (iterations < opts.MaxToolIterations)
            {
                iterations++;
                ct.ThrowIfCancellationRequested();
                _store.TrimToFit(modelCfg.ContextWindow, opts.MaxTokens);

                var turn = await StreamOneRequestAsync(modelCfg, tools, opts.MaxTokens, ct);
                promptMiss += Math.Max(0, turn.PromptTokens - turn.CacheHitTokens);
                cacheHit += turn.CacheHitTokens;
                completion += turn.CompletionTokens;

                AppendAssistantMessage(turn);

                if (turn.FinishReason == "tool_calls" && turn.ToolCalls.Count > 0)
                {
                    foreach (var call in turn.ToolCalls)
                    {
                        ct.ThrowIfCancellationRequested();
                        var result = await ExecuteToolCallAsync(call, ct);
                        _store.Append(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = call.Id,
                            ["content"] = result.Content,
                        });
                    }
                    await _store.SaveAsync();
                    continue; // следующий запрос с результатами инструментов
                }

                if (turn.FinishReason == "length")
                    await _onMessage(new TruncatedMessage());
                break;
            }

            if (iterations >= opts.MaxToolIterations)
                await _onMessage(new TextDeltaMessage(
                    $"\n\n⚠️ Достигнут лимит итераций инструментов ({opts.MaxToolIterations}) — продолжи отдельным сообщением."));

            var usage = new UsageInfo((int)promptMiss, (int)completion, (int)cacheHit, 0);
            var cost = ComputeCost(modelCfg, promptMiss, cacheHit, completion);
            await _onMessage(new ResultMessage("success", sw.ElapsedMilliseconds, iterations, usage, cost));
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            // Interrupt пользователя: закрываем незакрытые tool_calls в истории,
            // иначе следующий запрос упадёт с ошибкой валидации messages
            FixupUnresolvedToolCalls();
        }
        catch (DeepSeekApiException ex)
        {
            await _onMessage(new ErrorMessage(ex.Message));
        }
        finally
        {
            _fileWatcher.Stop();
            await _store.SaveAsync();
            _currentTurnCts = null;
            // Статусом владеет SessionManager: Finished/Active он выставит по ExitedMessage
            await _onMessage(new ExitedMessage());
        }
    }

    // Результат одного HTTP-запроса: накопленный текст, tool_calls, finish_reason, usage
    private sealed record ToolCallAcc(int Index, string Id)
    {
        public string Name = "";
        public readonly System.Text.StringBuilder Args = new();
    }

    private sealed record TurnResult(string Content, List<ToolCallAcc> ToolCalls, string? FinishReason,
        int PromptTokens, int CompletionTokens, int CacheHitTokens);

    private async Task<TurnResult> StreamOneRequestAsync(DeepSeekModelConfig modelCfg, JsonArray? tools,
        int maxTokens, CancellationToken ct)
    {
        var req = new DsChatRequest(modelCfg.EffectiveApiModel, _store.Messages, tools, maxTokens,
            modelCfg.Thinking ? true : null);

        var contentSb = new System.Text.StringBuilder();
        var toolCalls = new Dictionary<int, ToolCallAcc>();
        string? finishReason = null;
        int prompt = 0, compl = 0, hit = 0;

        // Watchdog: сбрасывается на каждом событии; если API молчит дольше — прерываем ход
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(StreamIdleTimeout);
        try
        {
            await foreach (var evt in _client.StreamChatAsync(req, watchdog.Token))
            {
                watchdog.CancelAfter(StreamIdleTimeout);
                switch (evt)
                {
                    case DsReasoningDelta r:
                        await _onMessage(new ThinkingDeltaMessage(r.Text));
                        break;
                    case DsContentDelta c:
                        contentSb.Append(c.Text);
                        await _onMessage(new TextDeltaMessage(c.Text));
                        break;
                    case DsToolCallStart s:
                        toolCalls[s.Index] = new ToolCallAcc(s.Index, s.Id) { Name = s.Name };
                        // Ранняя карточка инструмента — аргументы дольются стримом
                        await _onMessage(new ToolUseMessage(s.Id, s.Name, new { }));
                        break;
                    case DsToolCallArgsDelta a when toolCalls.TryGetValue(a.Index, out var acc):
                        acc.Args.Append(a.Fragment);
                        await _onMessage(new ToolInputDeltaMessage(acc.Id, acc.Args.ToString()));
                        break;
                    case DsFinish f:
                        finishReason = f.Reason;
                        break;
                    case DsUsage u:
                        prompt = u.PromptTokens;
                        compl = u.CompletionTokens;
                        hit = u.CacheHitTokens;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Сработал watchdog (не Interrupt и не остановка сессии)
            await _onMessage(new ErrorMessage(
                $"DeepSeek не отвечает более {StreamIdleTimeout.TotalMinutes:0} мин — прерываем"));
            finishReason ??= "stop";
        }

        return new TurnResult(contentSb.ToString(),
            toolCalls.Values.OrderBy(t => t.Index).ToList(), finishReason, prompt, compl, hit);
    }

    // Assistant-сообщение в историю: content + tool_calls, БЕЗ reasoning_content (API вернёт 400)
    private void AppendAssistantMessage(TurnResult turn)
    {
        var msg = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = turn.Content.Length > 0 ? turn.Content : null,
        };
        if (turn.ToolCalls.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var call in turn.ToolCalls)
                arr.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.Args.Length > 0 ? call.Args.ToString() : "{}",
                    },
                });
            msg["tool_calls"] = arr;
        }
        _store.Append(msg);
    }

    private async Task<DsToolResult> ExecuteToolCallAsync(ToolCallAcc call, CancellationToken ct)
    {
        var argsJson = call.Args.Length > 0 ? call.Args.ToString() : "{}";
        JsonElement args;
        try { args = JsonDocument.Parse(argsJson).RootElement.Clone(); }
        catch (JsonException)
        {
            var parseError = new DsToolResult("Аргументы инструмента — некорректный JSON", IsError: true);
            await _onMessage(new ToolResultMessage(call.Id, parseError.Content, true));
            return parseError;
        }

        // Финальная карточка с распарсенными аргументами (ранняя ушла с пустым input)
        var input = JsonSerializer.Deserialize<object>(argsJson) ?? new object();
        await _onMessage(new ToolUseMessage(call.Id, call.Name, input));

        DsToolResult result;
        var tool = _registry.Get(call.Name);
        if (tool is null)
        {
            result = new DsToolResult($"Неизвестный инструмент: {call.Name}", IsError: true);
        }
        else
        {
            var behavior = await ResolvePermissionAsync(tool, args, input, ct);
            result = behavior == "deny"
                ? new DsToolResult("Пользователь отклонил выполнение инструмента", IsError: true)
                : await tool.ExecuteAsync(args, ct);
        }

        await _onMessage(new ToolResultMessage(call.Id, result.Content, result.IsError));
        return result;
    }

    // Разрешение на инструмент: правила проекта → «всегда разрешать» → режим сессии → спросить.
    private async Task<string> ResolvePermissionAsync(IDeepSeekTool tool, JsonElement args, object input,
        CancellationToken ct)
    {
        var ruleDecision = PermissionRuleEvaluator.Evaluate(_permissionRules?.Invoke(), tool.Name, args);
        if (ruleDecision == "deny") return "deny";
        if (ruleDecision == "allow" || _autoAllowTools.ContainsKey(tool.Name)) return "allow";
        if (AutoAllowedByMode(tool.PermissionClass)) return "allow";

        var requestId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _permissionWaiters[requestId] = tcs;

        // Статус Waiting выставит SessionManager по PermissionRequestMessage
        await _onMessage(new PermissionRequestMessage(requestId, tool.Name, input));
        string behavior;
        try
        {
            behavior = await tcs.Task.WaitAsync(PermissionTimeout, ct);
        }
        catch (TimeoutException)
        {
            behavior = "deny"; // пользователь не ответил — deny и продолжаем
        }
        catch (TaskCanceledException)
        {
            behavior = "deny"; // Interrupt отменил диалог; сам ход прервёт ct
        }
        finally
        {
            _permissionWaiters.TryRemove(requestId, out _);
        }

        if (behavior == "allow_always")
        {
            _autoAllowTools.TryAdd(tool.Name, 0);
            behavior = "allow";
        }
        return behavior;
    }

    // Маппинг режима сессии на авторазрешения по классу инструмента.
    // Execute (shell, вне MVP) спрашивает всегда — даже в auto/bypass.
    private bool AutoAllowedByMode(ToolPermissionClass cls) => Info.Mode switch
    {
        ClaudeMode.Default or ClaudeMode.Plan => cls == ToolPermissionClass.ReadOnly,
        ClaudeMode.AcceptEdits => cls is ToolPermissionClass.ReadOnly or ToolPermissionClass.Edit,
        _ => cls != ToolPermissionClass.Execute, // Auto/DontAsk/Bypass
    };

    // После Interrupt: на каждый tool_call последнего assistant-сообщения без ответа —
    // синтетический tool-результат, иначе следующий запрос не пройдёт валидацию истории
    private void FixupUnresolvedToolCalls()
    {
        var answered = new HashSet<string>();
        JsonObject? lastAssistantWithTools = null;
        foreach (var m in _store.Messages)
        {
            if (m is not JsonObject o) continue;
            var role = o["role"]?.GetValue<string>();
            if (role == "assistant" && o["tool_calls"] is JsonArray { Count: > 0 })
            {
                lastAssistantWithTools = o;
                answered.Clear();
            }
            else if (role == "tool" && o["tool_call_id"]?.GetValue<string>() is { } id)
            {
                answered.Add(id);
            }
        }
        if (lastAssistantWithTools?["tool_calls"] is not JsonArray calls) return;
        foreach (var call in calls)
        {
            var id = call?["id"]?.GetValue<string>();
            if (id is null || answered.Contains(id)) continue;
            _store.Append(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = id,
                ["content"] = "Прервано пользователем",
            });
        }
    }

    private static double? ComputeCost(DeepSeekModelConfig m, long miss, long hit, long completion)
    {
        if (m.PriceInMissPer1M <= 0 && m.PriceInHitPer1M <= 0 && m.PriceOutPer1M <= 0) return null;
        return (miss * m.PriceInMissPer1M + hit * m.PriceInHitPer1M + completion * m.PriceOutPer1M) / 1_000_000;
    }

    private string BuildSystemPrompt()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Ты — ассистент по работе с проектом. Рабочая папка: ").Append(_rootPath).Append('.');
        sb.Append(" Файловые инструменты принимают пути относительно корня проекта.");
        sb.Append(" Отвечай на языке пользователя.");
        if (!string.IsNullOrWhiteSpace(_rawSystemPrompt))
            sb.Append("\n\n").Append(_rawSystemPrompt);
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _fileWatcher.Dispose();
        _cts.Cancel();
        _currentTurnCts?.Cancel();
        foreach (var tcs in _permissionWaiters.Values)
            tcs.TrySetCanceled();
        _permissionWaiters.Clear();
        await _store.SaveAsync();
        _cts.Dispose();
        _turnLock.Dispose();
    }
}
