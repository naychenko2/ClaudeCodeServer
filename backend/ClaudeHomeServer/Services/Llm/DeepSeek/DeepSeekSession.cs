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
    // Ожидающие ответа карточки вопросов (tool_call id → JSON ответа с answers)
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _questionWaiters = new();
    // Ожидающие решения по плану (tool_call id → approve+feedback)
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(bool Approve, string? Feedback)>> _planWaiters = new();
    // После одобрения плана следующий ход идёт без режима планирования (как у Claude)
    private volatile bool _forceNonPlanNextTurn;
    // План одобрен в текущем ходу → остаток хода с полным набором инструментов
    private volatile bool _planApprovedInTurn;
    // Гарантия исполнения плана: после approve ждём правки; если ход завершится без них — дошлём команду
    private volatile bool _awaitPlanExecution;
    private volatile bool _sawMutationSinceApprove;
    private volatile CancellationTokenSource? _currentTurnCts;
    // Результаты фоновых команд, завершившихся между ходами — вольются в начало следующего хода
    private readonly ConcurrentQueue<string> _backgroundResults = new();

    // Виртуальные инструменты сессии (не из реестра): вопросы, план, todo-список, субагенты.
    // Имена совпадают с тем, что распознаёт фронт (useSessionArtifacts): TodoWrite, Task
    private const string AskQuestionTool = "ask_user_question";
    private const string ExitPlanTool = "exit_plan_mode";
    private const string TodoTool = "TodoWrite";
    private const string SpawnAgentTool = "Task";
    private const string WorkflowTool = "run_workflow";
    private const string WorkflowCardName = "Workflow"; // имя, которое распознаёт панель «Агенты»
    // Предохранители оркестрации
    private const int MaxWorkflowConcurrency = 6;
    private const int MaxWorkflowAgents = 24;

    // Если API не выдаёт ни одного события дольше этого — считаем зависшим
    private static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PermissionTimeout = TimeSpan.FromMinutes(60);

    private readonly SkillsService? _skills;
    private readonly TasksMcpContext? _tasksMcp;
    private readonly DeepSeekMcpManager _mcp;

    public DeepSeekSession(Session info, LlmSessionContext context, DeepSeekClient client,
        IOptions<DeepSeekOptions> options, FileService files, string sessionsBasePath,
        SkillsService? skills = null, string? mcpConfigPath = null,
        WorkspaceKnowledgeStore? workspaceStore = null, IHttpClientFactory? httpFactory = null)
    {
        Info = info;
        _rootPath = context.RootPath;
        _onMessage = context.OnMessage;
        _permissionRules = context.PermissionRules;
        _rawSystemPrompt = context.RawSystemPrompt;
        _client = client;
        _options = options;
        _skills = skills;
        _tasksMcp = context.TasksMcp;
        _mcp = new DeepSeekMcpManager(
            [mcpConfigPath, options.Value.McpConfigPath], context.TasksMcp,
            () => workspaceStore?.GetByPath(context.RootPath)?.DifyDatasetId, httpFactory);
        _registry = new DeepSeekToolRegistry(_rootPath, files,
            options.Value.EnableShellTool, options.Value.ShellTimeoutSeconds, OnBackgroundCommandDoneAsync);
        _store = new DeepSeekConversationStore(sessionsBasePath);
        _fileWatcher = new TurnFileWatcher(_rootPath, _onMessage);
    }

    public Task StartAsync() => Task.CompletedTask;

    public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null)
    {
        Info.MessageCount++;
        Info.LastMessage = text.Length > 100 ? text[..100] + "…" : text;
        Info.UpdatedAt = DateTime.UtcNow;

        // Вызов скилла (/skill-name [args]) разворачиваем в его содержимое
        var effectiveText = _skills?.TryExpandSkill(text) ?? text;
        // Изображения DeepSeek не принимает — честная пометка; остальное инлайним в текст
        var (imagePaths, otherPaths) = AttachmentInliner.SplitImagePaths(attachedPaths);
        var fullText = AttachmentInliner.BuildMessageText(_rootPath, effectiveText, otherPaths);
        if (imagePaths.Count > 0)
            fullText += "\n\n---\n" + string.Join("\n", imagePaths.Select(p =>
                $"Прикреплено изображение {p} — провайдер DeepSeek не поддерживает изображения, содержимое недоступно."));

        return QueueTurnAsync(fullText);
    }

    // Ручное сворачивание контекста: суммаризация истории отдельным запросом (без инструментов),
    // затем замена messages на [system, сводка]. Выполняется как ход — под _turnLock.
    public Task CompactAsync()
    {
        _ = Task.Run(async () =>
        {
            if (_cts.IsCancellationRequested) return;
            await _turnLock.WaitAsync(_cts.Token);
            try { await RunCompactAsync(); }
            catch (OperationCanceledException) { /* остановка сессии — штатно */ }
            catch (Exception ex)
            {
                await _onMessage(new CompactStatusMessage(null, "failed", ex.Message));
                await _onMessage(new ExitedMessage());
            }
            finally { _turnLock.Release(); }
        });
        return Task.CompletedTask;
    }

    private async Task RunCompactAsync()
    {
        var opts = _options.Value;
        var modelCfg = opts.FindModel(Info.Model);
        if (modelCfg is null || Info.ClaudeSessionId is null)
        {
            await _onMessage(new ExitedMessage());
            return;
        }

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _currentTurnCts = turnCts;
        try
        {
            _store.Bind(Info.ClaudeSessionId);
            await SummarizeAndReplaceAsync(modelCfg, "manual", turnCts.Token);
        }
        catch (DeepSeekApiException ex)
        {
            await _onMessage(new CompactStatusMessage(null, "failed", ex.Message));
        }
        finally
        {
            _currentTurnCts = null;
            await _onMessage(new ExitedMessage());
        }
    }

    // Ядро суммаризации: заменяет историю сводкой. trigger — "manual"/"auto".
    // Не управляет _turnLock/_currentTurnCts/ExitedMessage — это делает вызывающий.
    private async Task SummarizeAndReplaceAsync(DeepSeekModelConfig modelCfg, string trigger, CancellationToken ct)
    {
        var preTokens = _store.EstimateTotalTokens();
        await _onMessage(new CompactStatusMessage("compacting"));

        // Запрос сводки: копия истории + инструкция, без tools и thinking
        var messages = (JsonArray)_store.Messages.DeepClone();
        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] =
                "Составь подробную сводку нашего диалога для продолжения работы с чистым контекстом: " +
                "ключевые факты, принятые решения, состояние задач, важные пути файлов и незавершённые шаги. " +
                "Только сводка, без вступлений.",
        });
        var req = new DsChatRequest(modelCfg.EffectiveApiModel, messages, Tools: null,
            _options.Value.MaxTokens, Thinking: modelCfg.Thinking ? false : null);

        var summary = new System.Text.StringBuilder();
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(StreamIdleTimeout);
        await foreach (var evt in _client.StreamChatAsync(req, watchdog.Token))
        {
            watchdog.CancelAfter(StreamIdleTimeout);
            if (evt is DsContentDelta c) summary.Append(c.Text); // сводку в ленту не стримим
        }

        if (summary.Length == 0)
        {
            await _onMessage(new CompactStatusMessage(null, "failed", "Модель вернула пустую сводку"));
            return;
        }

        _store.ReplaceWithSummary(summary.ToString());
        await _store.SaveAsync();
        await _onMessage(new CompactStatusMessage(null, "success"));
        await _onMessage(new CompactBoundaryMessage(trigger, preTokens, _store.EstimateTotalTokens()));
    }

    public void RespondPermission(string requestId, string behavior)
    {
        if (_permissionWaiters.TryGetValue(requestId, out var tcs))
            tcs.TrySetResult(behavior);
    }

    // Ответ пользователя на карточку вопроса: JSON {questions, answers} → tool result
    public void AnswerQuestion(string toolUseId, string updatedInputJson)
    {
        if (_questionWaiters.TryGetValue(toolUseId, out var tcs))
            tcs.TrySetResult(updatedInputJson);
    }

    // Решение пользователя по плану (exit_plan_mode)
    public void RespondPlan(string requestId, bool approve, string? feedback)
    {
        if (_planWaiters.TryGetValue(requestId, out var tcs))
            tcs.TrySetResult((approve, feedback));
    }

    public void Interrupt()
    {
        _currentTurnCts?.Cancel();
        foreach (var tcs in _permissionWaiters.Values) tcs.TrySetCanceled();
        foreach (var tcs in _questionWaiters.Values) tcs.TrySetCanceled();
        foreach (var tcs in _planWaiters.Values) tcs.TrySetCanceled();
        _permissionWaiters.Clear();
        _questionWaiters.Clear();
        _planWaiters.Clear();
        _forceNonPlanNextTurn = false;
    }

    // Колбэк завершения фоновой команды: результат кладём в очередь (вольётся в следующий ход)
    // + тост пользователю. Если сессия простаивает — досылаем ход, чтобы модель отреагировала.
    private async Task OnBackgroundCommandDoneAsync(string label, string output)
    {
        _backgroundResults.Enqueue($"[Фоновая команда «{label}» завершилась]\n{output}");
        await _onMessage(new NotificationMessage(
            "Фоновая команда завершилась", label, Kind: "info"));
        // Если ход уже не идёт — пнём модель отдельным ходом, чтобы она учла результат сразу
        if (_turnLock.CurrentCount > 0)
            _ = SendMessageAsync("Фоновая команда завершилась — учти её результат (он добавлен в контекст).");
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

        // Результаты фоновых команд, пришедшие между ходами — вливаем перед сообщением пользователя
        while (_backgroundResults.TryDequeue(out var bg))
            _store.Append(new JsonObject { ["role"] = "user", ["content"] = bg });

        _store.Append(new JsonObject { ["role"] = "user", ["content"] = text });

        // MCP-серверы стартуют лениво на первом ходе (недоступные пропускаются с логом)
        if (modelCfg.SupportsTools)
            await _mcp.EnsureStartedAsync(ct);

        await _onMessage(new SessionStartedMessage(engineId, isResume, modelCfg.EffectiveApiModel,
            Info.Mode.ToWireToken(), _rootPath, _registry.All.Count, _mcp.ServerInfos,
            Capabilities.Provider, Capabilities));

        // Режим «План»: только чтение + exit_plan_mode; одобрение снимает ограничение
        // до конца хода, следующий ход тоже идёт без планирования (консумация флага — как у Claude)
        var planPhase = Info.Mode == ClaudeMode.Plan && modelCfg.SupportsTools;
        if (_forceNonPlanNextTurn) { _forceNonPlanNextTurn = false; planPhase = false; }
        _planApprovedInTurn = false;

        _fileWatcher.Start();
        try
        {
            while (iterations < opts.MaxToolIterations)
            {
                iterations++;
                ct.ThrowIfCancellationRequested();
                _store.TrimToFit(modelCfg.ContextWindow, opts.MaxTokens);

                var planningNow = planPhase && !_planApprovedInTurn;
                var tools = BuildTurnTools(modelCfg, planningNow);
                var messages = planningNow ? WithPlanInstruction(_store.Messages) : _store.Messages;

                var turn = await StreamOneRequestAsync(modelCfg, messages, tools, opts.MaxTokens, ct);
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

            // Гарантия исполнения плана: одобрили, но ход завершился без правок — дошлём команду
            if (_awaitPlanExecution)
            {
                var needFollowUp = !_sawMutationSinceApprove;
                _awaitPlanExecution = false;
                if (needFollowUp)
                {
                    _forceNonPlanNextTurn = true;
                    _ = SendMessageAsync("Одобренный план согласован. Реализуй его полностью сейчас — без повторного планирования.");
                }
            }

            // Авто-компакт: если контекст переполнен — суммаризируем, чтобы следующий ход влез
            if (opts.AutoCompactThresholdPct is > 0 and < 100 && !_forceNonPlanNextTurn)
            {
                var budget = modelCfg.ContextWindow * opts.AutoCompactThresholdPct / 100;
                if (_store.EstimateTotalTokens() > budget)
                    await SummarizeAndReplaceAsync(modelCfg, "auto", ct);
            }
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

    // Полный набор инструментов хода: реестр (в план-фазе — только чтение) + MCP + виртуальные.
    // MCP-инструменты в план-фазе не даём: среди них есть мутирующие (tasks_create и т.п.).
    // isSubAgent — набор для субагента: без вложенных spawn_agent/вопросов/плана
    private JsonArray? BuildTurnTools(DeepSeekModelConfig modelCfg, bool planningNow, bool isSubAgent = false)
    {
        if (!modelCfg.SupportsTools) return null;
        var tools = _registry.BuildToolsJson(readOnlyOnly: planningNow);
        if (!planningNow) _mcp.AppendToolsJson(tools);
        tools.Add(BuildTodoSchema());
        if (isSubAgent) return tools; // субагенту — только рабочие инструменты
        tools.Add(BuildAskQuestionSchema());
        tools.Add(BuildSpawnAgentSchema());
        if (!planningNow) tools.Add(BuildWorkflowSchema());
        if (planningNow) tools.Add(BuildExitPlanSchema());
        return tools;
    }

    private static JsonObject BuildTodoSchema() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = TodoTool,
            ["description"] =
                "Вести чек-лист текущей работы (показывается пользователю). Присылай ПОЛНЫЙ список " +
                "при каждом изменении: помечай выполненные completed, текущий — in_progress.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["todos"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["content"] = new JsonObject { ["type"] = "string", ["description"] = "Что нужно сделать" },
                                ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "pending", "in_progress", "completed" } },
                                ["activeForm"] = new JsonObject { ["type"] = "string", ["description"] = "Форма в процессе (например «Собираю проект»)" },
                            },
                            ["required"] = new JsonArray { "content", "status" },
                        },
                    },
                },
                ["required"] = new JsonArray { "todos" },
            },
        },
    };

    private static JsonObject BuildSpawnAgentSchema() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = SpawnAgentTool,
            ["description"] =
                "Запустить одного субагента для автономной подзадачи в отдельном контексте. У него те же " +
                "инструменты работы с файлами и командами, но нет доступа к этому диалогу — передай всё " +
                "необходимое в prompt. Верни задачу, которую можно выполнить без уточнений. Субагент " +
                "работает синхронно и возвращает итоговый результат. Для нескольких параллельных агентов " +
                "или многофазной работы используй run_workflow.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["description"] = new JsonObject { ["type"] = "string", ["description"] = "Короткое описание задачи (3-6 слов)" },
                    ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "Полное самодостаточное задание субагенту" },
                },
                ["required"] = new JsonArray { "prompt" },
            },
        },
    };

    private static JsonObject BuildWorkflowSchema() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = WorkflowTool,
            ["description"] =
                "Оркестрация нескольких субагентов по фазам. Каждая фаза — список агентов, которые " +
                "выполняются ПАРАЛЛЕЛЬНО; фазы идут последовательно, и результаты предыдущих фаз " +
                "передаются агентам следующей. Используй для декомпозиции: параллельный сбор/анализ, " +
                "затем синтез. Каждому агенту дай самодостаточный prompt (доступа к диалогу у него нет). " +
                $"Максимум {MaxWorkflowAgents} агентов суммарно. По завершении верни сводный результат.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Название workflow (человекочитаемое)" },
                    ["phases"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Фазы по порядку",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Название фазы" },
                                ["agents"] = new JsonObject
                                {
                                    ["type"] = "array",
                                    ["description"] = "Агенты фазы (выполняются параллельно)",
                                    ["items"] = new JsonObject
                                    {
                                        ["type"] = "object",
                                        ["properties"] = new JsonObject
                                        {
                                            ["label"] = new JsonObject { ["type"] = "string", ["description"] = "Короткая метка агента" },
                                            ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "Самодостаточное задание агенту" },
                                        },
                                        ["required"] = new JsonArray { "prompt" },
                                    },
                                },
                            },
                            ["required"] = new JsonArray { "agents" },
                        },
                    },
                },
                ["required"] = new JsonArray { "phases" },
            },
        },
    };

    // Инструкция режима планирования — во временной копии messages, в историю не пишется
    private static JsonArray WithPlanInstruction(JsonArray messages)
    {
        var copy = (JsonArray)messages.DeepClone();
        copy.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] =
                "Сейчас режим планирования: изменения вносить НЕЛЬЗЯ (доступны только инструменты чтения). " +
                "Изучи задачу, составь подробный план в markdown и вызови exit_plan_mode с этим планом. " +
                "После одобрения пользователем приступишь к реализации.",
        });
        return copy;
    }

    private static JsonObject BuildAskQuestionSchema() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = AskQuestionTool,
            ["description"] =
                "Задать пользователю уточняющий вопрос с вариантами ответа (интерактивная карточка). " +
                "Используй, когда требования неоднозначны. 1–4 вопроса, у каждого 2–4 варианта.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["questions"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["question"] = new JsonObject { ["type"] = "string", ["description"] = "Полный текст вопроса" },
                                ["header"] = new JsonObject { ["type"] = "string", ["description"] = "Короткая метка (до 12 символов)" },
                                ["multiSelect"] = new JsonObject { ["type"] = "boolean", ["description"] = "Разрешить несколько вариантов" },
                                ["options"] = new JsonObject
                                {
                                    ["type"] = "array",
                                    ["items"] = new JsonObject
                                    {
                                        ["type"] = "object",
                                        ["properties"] = new JsonObject
                                        {
                                            ["label"] = new JsonObject { ["type"] = "string", ["description"] = "Вариант (1-5 слов)" },
                                            ["description"] = new JsonObject { ["type"] = "string", ["description"] = "Пояснение варианта" },
                                        },
                                        ["required"] = new JsonArray { "label" },
                                    },
                                },
                            },
                            ["required"] = new JsonArray { "question", "options" },
                        },
                    },
                },
                ["required"] = new JsonArray { "questions" },
            },
        },
    };

    private static JsonObject BuildExitPlanSchema() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = ExitPlanTool,
            ["description"] = "Представить готовый план пользователю на согласование. Вызывай, когда план составлен.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["plan"] = new JsonObject { ["type"] = "string", ["description"] = "Полный план в markdown" },
                },
                ["required"] = new JsonArray { "plan" },
            },
        },
    };

    private async Task<TurnResult> StreamOneRequestAsync(DeepSeekModelConfig modelCfg, JsonArray messages,
        JsonArray? tools, int maxTokens, CancellationToken ct)
    {
        var req = new DsChatRequest(modelCfg.EffectiveApiModel, messages, tools, maxTokens,
            modelCfg.Thinking ? true : null, modelCfg.Thinking ? MapReasoningEffort(Info.Effort) : null);

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
                        // Ранняя карточка инструмента — аргументы дольются стримом.
                        // Виртуальные (вопрос/план) идут своими карточками — tool-карточку не дублируем
                        if (s.Name is not (AskQuestionTool or ExitPlanTool))
                            await _onMessage(new ToolUseMessage(s.Id, s.Name, new { }));
                        break;
                    case DsToolCallArgsDelta a when toolCalls.TryGetValue(a.Index, out var acc):
                        acc.Args.Append(a.Fragment);
                        if (acc.Name is not (AskQuestionTool or ExitPlanTool))
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
    private void AppendAssistantMessage(TurnResult turn) => AppendAssistantMessageTo(_store.Messages, turn);

    private static void AppendAssistantMessageTo(JsonArray target, TurnResult turn)
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
        target.Add(msg);
    }

    // Стрим запроса субагента: дочерние tool_use идут с parentToolUseId (для панели «Агенты»),
    // текст/thinking субагента в главную ленту НЕ выводим — он вернётся итогом в tool_result родителя
    private async Task<TurnResult> StreamSubAgentRequestAsync(SubAgentSink sink, DeepSeekModelConfig modelCfg,
        JsonArray messages, JsonArray? tools, int maxTokens, CancellationToken ct)
    {
        var req = new DsChatRequest(modelCfg.EffectiveApiModel, messages, tools, maxTokens,
            modelCfg.Thinking ? false : null); // субагенту reasoning не нужен — экономим

        var contentSb = new System.Text.StringBuilder();
        var toolCalls = new Dictionary<int, ToolCallAcc>();
        string? finishReason = null;
        int prompt = 0, compl = 0, hit = 0;

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(StreamIdleTimeout);
        await foreach (var evt in _client.StreamChatAsync(req, watchdog.Token))
        {
            watchdog.CancelAfter(StreamIdleTimeout);
            switch (evt)
            {
                case DsContentDelta c: contentSb.Append(c.Text); break;
                case DsToolCallStart s:
                    toolCalls[s.Index] = new ToolCallAcc(s.Index, s.Id) { Name = s.Name };
                    if (sink.EmitCards)
                        await _onMessage(new ToolUseMessage(s.Id, s.Name, new { }, sink.ParentId));
                    break;
                case DsToolCallArgsDelta a when toolCalls.TryGetValue(a.Index, out var acc):
                    acc.Args.Append(a.Fragment);
                    break;
                case DsFinish f: finishReason = f.Reason; break;
                case DsUsage u: prompt = u.PromptTokens; compl = u.CompletionTokens; hit = u.CacheHitTokens; break;
            }
        }
        return new TurnResult(contentSb.ToString(),
            toolCalls.Values.OrderBy(t => t.Index).ToList(), finishReason, prompt, compl, hit);
    }

    // Вызов инструмента субагентом: только рабочие инструменты (файлы/команды/MCP) с permission,
    // дочерний tool_use/tool_result помечаем parentId (в тихом режиме карточки не шлём)
    private async Task<DsToolResult> ExecuteSubAgentToolAsync(SubAgentSink sink, ToolCallAcc call, CancellationToken ct)
    {
        var argsJson = call.Args.Length > 0 ? call.Args.ToString() : "{}";
        JsonElement args;
        try { args = JsonDocument.Parse(argsJson).RootElement.Clone(); }
        catch (JsonException)
        {
            var err = new DsToolResult("Аргументы инструмента — некорректный JSON", IsError: true);
            if (sink.EmitCards) await _onMessage(new ToolResultMessage(call.Id, err.Content, true));
            return err;
        }

        var input = JsonSerializer.Deserialize<object>(argsJson) ?? new object();
        if (sink.EmitCards) await _onMessage(new ToolUseMessage(call.Id, call.Name, input, sink.ParentId));

        DsToolResult result;
        if (call.Name.StartsWith(DeepSeekMcpManager.ToolPrefix, StringComparison.Ordinal))
        {
            var behavior = await ResolvePermissionAsync(call.Name, ToolPermissionClass.Edit, args, input, ct);
            if (behavior == "deny")
            {
                result = new DsToolResult("Пользователь отклонил выполнение инструмента", IsError: true);
            }
            else
            {
                var (content, isErr) = await _mcp.CallAsync(call.Name, args, ct);
                result = new DsToolResult(DeepSeekToolRegistry.Truncate(content), isErr);
            }
        }
        else if (call.Name is TodoTool)
        {
            result = new DsToolResult("Чек-лист доступен только основному агенту");
        }
        else if (_registry.Get(call.Name) is not { } tool)
        {
            result = new DsToolResult($"Неизвестный инструмент: {call.Name}", IsError: true);
        }
        else
        {
            var behavior = await ResolvePermissionAsync(tool.Name, tool.PermissionClass, args, input, ct);
            result = behavior == "deny"
                ? new DsToolResult("Пользователь отклонил выполнение инструмента", IsError: true)
                : await tool.ExecuteAsync(args, ct);
        }

        await _onMessage(new ToolResultMessage(call.Id, result.Content, result.IsError));
        return result;
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

        // Виртуальные интерактивные инструменты — свои карточки, без permission-проверок
        if (call.Name == AskQuestionTool) return await HandleAskQuestionAsync(call.Id, argsJson, ct);
        if (call.Name == ExitPlanTool) return await HandleExitPlanAsync(call.Id, args, ct);
        if (call.Name == TodoTool) return await HandleTodoAsync(call.Id, args, ct);
        if (call.Name == SpawnAgentTool) return await HandleSpawnAgentAsync(call.Id, args, ct);
        if (call.Name == WorkflowTool) return await HandleWorkflowAsync(call.Id, args, ct);

        // Реальный инструмент — после одобрения плана считаем его началом реализации
        if (_awaitPlanExecution) _sawMutationSinceApprove = true;

        // Финальная карточка с распарсенными аргументами (ранняя ушла с пустым input)
        var input = JsonSerializer.Deserialize<object>(argsJson) ?? new object();
        await _onMessage(new ToolUseMessage(call.Id, call.Name, input));

        DsToolResult result;
        if (call.Name.StartsWith(DeepSeekMcpManager.ToolPrefix, StringComparison.Ordinal))
        {
            // MCP-инструменты: класс Edit — в Default спрашиваем, acceptEdits/auto разрешают
            var behavior = await ResolvePermissionAsync(call.Name, ToolPermissionClass.Edit, args, input, ct);
            if (behavior == "deny")
            {
                result = new DsToolResult("Пользователь отклонил выполнение инструмента", IsError: true);
            }
            else
            {
                var (content, isError) = await _mcp.CallAsync(call.Name, args, ct);
                result = new DsToolResult(DeepSeekToolRegistry.Truncate(content), isError);
            }
        }
        else if (_registry.Get(call.Name) is not { } tool)
        {
            result = new DsToolResult($"Неизвестный инструмент: {call.Name}", IsError: true);
        }
        else
        {
            var behavior = await ResolvePermissionAsync(tool.Name, tool.PermissionClass, args, input, ct);
            result = behavior == "deny"
                ? new DsToolResult("Пользователь отклонил выполнение инструмента", IsError: true)
                : await tool.ExecuteAsync(args, ct);
        }

        await _onMessage(new ToolResultMessage(call.Id, result.Content, result.IsError));
        return result;
    }

    // Карточка вопроса: AskQuestionMessage → ждём AnswerQuestion → ответы в tool result
    private async Task<DsToolResult> HandleAskQuestionAsync(string callId, string argsJson, CancellationToken ct)
    {
        var input = JsonSerializer.Deserialize<object>(argsJson) ?? new object();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _questionWaiters[callId] = tcs;
        // Статус Waiting выставит SessionManager по AskQuestionMessage
        await _onMessage(new AskQuestionMessage(callId, input));
        try
        {
            var answerJson = await tcs.Task.WaitAsync(PermissionTimeout, ct);
            return new DsToolResult(FormatAnswers(answerJson));
        }
        catch (TimeoutException)
        {
            return new DsToolResult("Пользователь не ответил на вопрос — продолжай по своему усмотрению", IsError: true);
        }
        finally
        {
            _questionWaiters.TryRemove(callId, out _);
        }
    }

    // {"answers":{"<вопрос>":"<label>"|[labels]}} → человекочитаемый текст для модели
    private static string FormatAnswers(string answerJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(answerJson);
            if (!doc.RootElement.TryGetProperty("answers", out var answers)
                || answers.ValueKind != JsonValueKind.Object)
                return $"Ответ пользователя: {answerJson}";
            var sb = new System.Text.StringBuilder("Пользователь ответил:");
            foreach (var q in answers.EnumerateObject())
            {
                var value = q.Value.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", q.Value.EnumerateArray().Select(v => v.GetString()))
                    : q.Value.ToString();
                sb.Append("\n— ").Append(q.Name).Append(": ").Append(value);
            }
            return sb.ToString();
        }
        catch (JsonException)
        {
            return $"Ответ пользователя: {answerJson}";
        }
    }

    // Согласование плана: PlanReviewMessage → ждём RespondPlan.
    // Approve снимает план-ограничение до конца хода и на следующий ход
    private async Task<DsToolResult> HandleExitPlanAsync(string callId, JsonElement args, CancellationToken ct)
    {
        var plan = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("plan", out var p)
            && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        var tcs = new TaskCompletionSource<(bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _planWaiters[callId] = tcs;
        // Статус Waiting выставит SessionManager по PlanReviewMessage
        await _onMessage(new PlanReviewMessage(callId, plan));
        try
        {
            var (approve, feedback) = await tcs.Task.WaitAsync(PermissionTimeout, ct);
            if (approve)
            {
                _planApprovedInTurn = true;
                _forceNonPlanNextTurn = true;
                // Гарантия исполнения: если ход завершится без правок — дошлём команду
                _awaitPlanExecution = true;
                _sawMutationSinceApprove = false;
                return new DsToolResult(
                    "План одобрен пользователем. Приступай к реализации немедленно, без повторного планирования.");
            }
            return new DsToolResult(string.IsNullOrWhiteSpace(feedback)
                ? "Пользователь отклонил план. Уточни план с учётом контекста и предложи заново."
                : $"Пользователь отклонил план с комментарием: {feedback}");
        }
        catch (TimeoutException)
        {
            return new DsToolResult("Пользователь не рассмотрел план — ход завершён", IsError: true);
        }
        finally
        {
            _planWaiters.TryRemove(callId, out _);
        }
    }

    // Чек-лист прогресса: карточку рендерит фронт по tool_use name=TodoWrite + input.todos.
    // Модели просто подтверждаем приём; результат в ленту как есть.
    private async Task<DsToolResult> HandleTodoAsync(string callId, JsonElement args, CancellationToken ct)
    {
        var input = JsonSerializer.Deserialize<object>(args.GetRawText()) ?? new object();
        await _onMessage(new ToolUseMessage(callId, TodoTool, input));
        var count = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("todos", out var t)
            && t.ValueKind == JsonValueKind.Array ? t.GetArrayLength() : 0;
        var result = new DsToolResult($"Чек-лист обновлён ({count} пунктов)");
        await _onMessage(new ToolResultMessage(callId, result.Content, false));
        return result;
    }

    // Субагент: изолированная под-сессия со своим контекстом. tool_use name=Task —
    // фронт (useSessionArtifacts) показывает его карточкой на вкладке «Агенты»;
    // дочерние вызовы инструментов идут с parentToolUseId = callId
    private async Task<DsToolResult> HandleSpawnAgentAsync(string callId, JsonElement args, CancellationToken ct)
    {
        if (_awaitPlanExecution) _sawMutationSinceApprove = true;

        var prompt = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("prompt", out var p)
            && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        var description = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("description", out var d)
            && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            var err = new DsToolResult("Субагенту не передан prompt", IsError: true);
            await _onMessage(new ToolResultMessage(callId, err.Content, true));
            return err;
        }

        await _onMessage(new ToolUseMessage(callId, SpawnAgentTool,
            new { description, prompt, subagent_type = "deepseek" }));

        try
        {
            var result = await RunSubAgentAsync(new SubAgentSink(callId, emitCards: true), prompt, ct);
            await _onMessage(new ToolResultMessage(callId, result.Content, result.IsError));
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var interrupted = new DsToolResult("Субагент прерван", IsError: true);
            await _onMessage(new ToolResultMessage(callId, interrupted.Content, true));
            return interrupted;
        }
    }

    // Первая строка текста для метки/подписи
    private static string firstLine(string s)
    {
        var line = s.Split('\n')[0].Trim();
        return line.Length > 80 ? line[..77] + "…" : line;
    }

    // Состояние одного агента workflow — снапшотится в WorkflowAgentDto для панели «Агенты»
    private sealed class WfAgentState(string id, string prompt, string label)
    {
        public string Id { get; } = id;
        public string Prompt { get; } = prompt;
        public string Label { get; } = label;
        public volatile bool Done;
        public string? Summary;
        public List<string> Tools = [];
    }

    // Оркестрация субагентов по фазам: агенты фазы — параллельно, фазы — последовательно,
    // результаты предыдущих фаз передаются следующей. Прогресс — через WorkflowProgressMessage.
    private async Task<DsToolResult> HandleWorkflowAsync(string callId, JsonElement args, CancellationToken ct)
    {
        if (_awaitPlanExecution) _sawMutationSinceApprove = true;

        var name = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("name", out var n)
            && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        if (!args.TryGetProperty("phases", out var phasesEl) || phasesEl.ValueKind != JsonValueKind.Array
            || phasesEl.GetArrayLength() == 0)
        {
            var err = new DsToolResult("Workflow без фаз", IsError: true);
            await _onMessage(new ToolUseMessage(callId, WorkflowCardName, new { name }));
            await _onMessage(new ToolResultMessage(callId, err.Content, true));
            return err;
        }

        // Карточка workflow: имя распознаётся фронтом (workflowName по input.name)
        await _onMessage(new ToolUseMessage(callId, WorkflowCardName, new { name = name ?? "Workflow" }));

        var allStates = new List<WfAgentState>();
        var stateLock = new object();
        var gate = new SemaphoreSlim(MaxWorkflowConcurrency);

        // Снимок всех агентов в WorkflowProgressMessage (потокобезопасно)
        async Task PublishAsync(bool done)
        {
            List<WorkflowAgentDto> dtos;
            lock (stateLock)
                dtos = allStates.Select(s => new WorkflowAgentDto(
                    s.Id, s.Prompt, s.Summary,
                    s.Tools.GroupBy(t => t).Select(g => new WorkflowToolDto(g.Key, g.Count())).ToList(),
                    Files: null, IsDone: s.Done)).ToList();
            await _onMessage(new WorkflowProgressMessage(callId, dtos, done));
        }

        var phaseSummaries = new List<string>(); // «фаза N (title): агент → итог» для передачи дальше
        var totalAgents = 0;
        var phaseIdx = 0;

        foreach (var phaseEl in phasesEl.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            phaseIdx++;
            var title = phaseEl.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? $"Фаза {phaseIdx}" : $"Фаза {phaseIdx}";
            if (!phaseEl.TryGetProperty("agents", out var agentsEl) || agentsEl.ValueKind != JsonValueKind.Array)
                continue;

            var priorContext = phaseSummaries.Count > 0
                ? "Результаты предыдущих фаз:\n" + string.Join("\n", phaseSummaries) + "\n\n"
                : "";

            var phaseAgents = new List<(WfAgentState State, string Prompt)>();
            var agentIdx = 0;
            foreach (var agentEl in agentsEl.EnumerateArray())
            {
                if (totalAgents >= MaxWorkflowAgents) break;
                agentIdx++;
                var prompt = agentEl.TryGetProperty("prompt", out var pr) && pr.ValueKind == JsonValueKind.String
                    ? pr.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(prompt)) continue;
                var label = agentEl.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String
                    ? l.GetString() ?? "" : firstLine(prompt);
                var state = new WfAgentState($"{callId}:p{phaseIdx}a{agentIdx}", prompt, label);
                lock (stateLock) allStates.Add(state);
                phaseAgents.Add((state, priorContext + prompt));
                totalAgents++;
            }

            if (phaseAgents.Count == 0) continue;
            await PublishAsync(false);

            // Агенты фазы — параллельно (с ограничением конкурентности)
            var tasks = phaseAgents.Select(async pa =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var sink = new SubAgentSink(pa.State.Id, emitCards: false);
                    DsToolResult res;
                    try { res = await RunSubAgentAsync(sink, pa.Prompt, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { res = new DsToolResult($"Агент упал: {ex.Message}", IsError: true); }
                    lock (stateLock)
                    {
                        pa.State.Summary = res.Content;
                        pa.State.Tools = sink.ToolNames;
                        pa.State.Done = true;
                    }
                    await PublishAsync(false);
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);

            lock (stateLock)
                foreach (var pa in phaseAgents)
                    phaseSummaries.Add($"[Фаза «{title}» · {pa.State.Label}]: {pa.State.Summary}");
        }

        await PublishAsync(true);

        // Сводный результат родителю — для синтеза финального ответа пользователю
        var sb = new System.Text.StringBuilder("Workflow завершён. Итоги агентов:\n");
        lock (stateLock)
            foreach (var s in allStates)
                sb.Append("\n— ").Append(s.Label).Append(":\n").Append(s.Summary).Append('\n');
        var result = new DsToolResult(DeepSeekToolRegistry.Truncate(sb.ToString()));
        await _onMessage(new ToolResultMessage(callId, "Workflow завершён", false));
        return result;
    }

    // Приёмник событий субагента: карточки в ленту (обычный субагент) либо тихий сбор
    // счётчиков инструментов (агент внутри workflow — показывается через workflow_progress)
    private sealed class SubAgentSink(string parentId, bool emitCards)
    {
        public string ParentId { get; } = parentId;
        public bool EmitCards { get; } = emitCards;
        public readonly List<string> ToolNames = [];
    }

    // Прогон субагента: свой messages[] (изолированный контекст), рабочие инструменты
    // (файлы/команды/MCP, без вложенных субагентов/вопросов/плана), синхронно до финального текста
    private async Task<DsToolResult> RunSubAgentAsync(SubAgentSink sink, string prompt, CancellationToken ct)
    {
        var opts = _options.Value;
        var modelCfg = opts.FindModel(Info.Model)!;
        var tools = BuildTurnTools(modelCfg, planningNow: false, isSubAgent: true);

        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = "Ты — автономный субагент. Рабочая папка: " + _rootPath +
                    ". Выполни поставленную задачу инструментами и верни краткий итог. " +
                    "Доступа к диалогу пользователя у тебя нет.",
            },
            new JsonObject { ["role"] = "user", ["content"] = prompt },
        };

        var finalText = new System.Text.StringBuilder();
        for (var i = 0; i < opts.MaxToolIterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            var turn = await StreamSubAgentRequestAsync(sink, modelCfg, messages, tools, opts.MaxTokens, ct);
            AppendAssistantMessageTo(messages, turn);
            finalText.Clear();
            finalText.Append(turn.Content);

            if (turn.FinishReason == "tool_calls" && turn.ToolCalls.Count > 0)
            {
                foreach (var call in turn.ToolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    sink.ToolNames.Add(call.Name);
                    var res = await ExecuteSubAgentToolAsync(sink, call, ct);
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = call.Id,
                        ["content"] = res.Content,
                    });
                }
                continue;
            }
            break;
        }

        var text = finalText.ToString().Trim();
        return new DsToolResult(text.Length > 0 ? text : "Субагент завершил работу без текстового итога.");
    }

    // Разрешение на инструмент: правила проекта → «всегда разрешать» → режим сессии → спросить.
    private async Task<string> ResolvePermissionAsync(string toolName, ToolPermissionClass cls,
        JsonElement args, object input, CancellationToken ct)
    {
        var ruleDecision = PermissionRuleEvaluator.Evaluate(_permissionRules?.Invoke(), toolName, args);
        if (ruleDecision == "deny") return "deny";
        if (ruleDecision == "allow" || _autoAllowTools.ContainsKey(toolName)) return "allow";
        if (AutoAllowedByMode(cls)) return "allow";

        var requestId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _permissionWaiters[requestId] = tcs;

        // Статус Waiting выставит SessionManager по PermissionRequestMessage
        await _onMessage(new PermissionRequestMessage(requestId, toolName, input));
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
            _autoAllowTools.TryAdd(toolName, 0);
            behavior = "allow";
        }
        return behavior;
    }

    // Маппинг режима сессии на авторазрешения по классу инструмента.
    // Execute (команды, web_fetch): в Auto/Bypass разрешён без вопросов,
    // в Default/AcceptEdits/DontAsk — с permission-карточкой.
    private bool AutoAllowedByMode(ToolPermissionClass cls) => Info.Mode switch
    {
        ClaudeMode.Default or ClaudeMode.Plan => cls == ToolPermissionClass.ReadOnly,
        ClaudeMode.AcceptEdits or ClaudeMode.DontAsk => cls is ToolPermissionClass.ReadOnly or ToolPermissionClass.Edit,
        _ => true, // Auto/Bypass — без вопросов
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

    // У DeepSeek два уровня reasoning_effort: high (дефолт) и max.
    // Claude-шкалу low/medium/high/xhigh/max маппим: xhigh/max → max, остальное → дефолт API
    private static string? MapReasoningEffort(string? effort) =>
        effort?.ToLowerInvariant() switch
        {
            "xhigh" or "max" => "max",
            "high" => "high",
            _ => null,
        };

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
        // Подсказка про систему задач — только когда tasks-server подключён (как у Claude)
        if (_tasksMcp is not null)
        {
            var scope = _tasksMcp.ProjectId is not null
                ? "Текущий контекст — задачи этого проекта."
                : "Текущий контекст — личные задачи пользователя (вне проектов).";
            sb.Append("\n\nУ пользователя есть встроенная система задач (вкладка «Задачи» и «Календарь»). ")
              .Append("Управляй ею через инструменты mcp__tasks__* (tasks_list, tasks_search, tasks_get, tasks_create, ")
              .Append("tasks_update, tasks_complete, tasks_delete, tasks_add_subtask, tasks_toggle_subtask). ")
              .Append(scope)
              .Append(" Даты — в формате YYYY-MM-DD, время HH:MM.");
        }
        // Промпт агента (.claude/agents/<name>.md) — как у Claude, поверх базового
        if (!string.IsNullOrEmpty(Info.AgentName)
            && _skills?.GetAgentSystemPrompt(_rootPath, Info.AgentName) is { } agentPrompt)
            sb.Append("\n\n---\n\n").Append(agentPrompt);
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _fileWatcher.Dispose();
        _cts.Cancel();
        _currentTurnCts?.Cancel();
        foreach (var tcs in _permissionWaiters.Values) tcs.TrySetCanceled();
        foreach (var tcs in _questionWaiters.Values) tcs.TrySetCanceled();
        foreach (var tcs in _planWaiters.Values) tcs.TrySetCanceled();
        _permissionWaiters.Clear();
        _questionWaiters.Clear();
        _planWaiters.Clear();
        await _mcp.DisposeAsync();
        await _store.SaveAsync();
        _cts.Dispose();
        _turnLock.Dispose();
    }
}
