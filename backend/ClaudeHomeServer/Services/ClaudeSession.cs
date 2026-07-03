using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Контекст MCP-сервера задач для сессии: адрес API, сервисный токен владельца
// и проект (null — чат вне проекта, контекст личных задач)
public record TasksMcpContext(string ApiUrl, string Token, string? ProjectId);

public class ClaudeSession : IAsyncDisposable
{
    public Session Info { get; }

    private readonly string _rootPath;
    private readonly Func<ServerMessage, Task> _onMessage;
    private readonly Dictionary<string, TaskCompletionSource<string>> _permissionWaiters = new();
    // Инструменты, для которых пользователь выбрал «всегда разрешать» в этой сессии
    private readonly HashSet<string> _autoAllowTools = new();
    // tool_use_id → request_id вопросов AskUserQuestion (приходят как control_request can_use_tool, ждут control_response)
    private readonly Dictionary<string, string> _pendingQuestions = new();
    // request_id → исходный input ожидающего согласования ExitPlanMode (режим «План»)
    private readonly Dictionary<string, object> _pendingPlans = new();
    // Гарантированное исполнение одобренного плана:
    // после approve ждём реализацию; если ход завершится без правок — дошлём команду.
    private volatile bool _awaitPlanExecution;
    private volatile bool _sawToolSinceApprove;
    // Следующий ход запустить без --permission-mode plan (исполнение одобренного плана)
    private volatile bool _forceNonPlanNextTurn;
    // Стриминг tool_use: индекс content-блока → (id инструмента, накопленный partial_json)
    private readonly Dictionary<int, (string Id, System.Text.StringBuilder Sb)> _toolStream = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private Process? _currentProcess;

    // Ватчеры фоновых Workflow (по одному на каждый запущенный workflow в сессии)
    private readonly List<WorkflowWatcher> _workflowWatchers = [];

    // Если claude не выдаёт ни одной строки дольше этого — считаем зависшим
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(60);

    // Отслеживание изменений файлов
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, string?> _fileCache = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounce = new();

    private readonly string? _rawSystemPrompt;
    private readonly string? _mcpConfigPath;
    private readonly SkillsService? _skills;
    private readonly WorkspaceKnowledgeStore? _wkStore;
    // Провайдер правил разрешений проекта — резолвим каждый запрос (правила могут меняться)
    private readonly Func<IReadOnlyList<PermissionRule>>? _permissionRules;
    private readonly TasksMcpContext? _tasksMcp;

    public ClaudeSession(Session info, string rootPath, Func<ServerMessage, Task> onMessage,
        string? mcpConfigPath = null, string? rawSystemPrompt = null,
        SkillsService? skills = null, WorkspaceKnowledgeStore? workspaceStore = null,
        Func<IReadOnlyList<PermissionRule>>? permissionRules = null,
        TasksMcpContext? tasksMcp = null)
    {
        Info = info;
        _rootPath = rootPath;
        _onMessage = onMessage;
        _mcpConfigPath = mcpConfigPath;
        _rawSystemPrompt = rawSystemPrompt;
        _skills = skills;
        _wkStore = workspaceStore;
        _permissionRules = permissionRules;
        _tasksMcp = tasksMcp;
    }

    // Объединённый MCP-конфиг хода: серверы из базового конфига (Dify с инжекцией
    // dataset id) + tasks-server с контекстом сессии. null → базовый конфиг как есть.
    private string? BuildTurnMcpConfig(string? datasetId)
    {
        var tasksServerPath = _tasksMcp is not null ? FindTasksServerPath() : null;
        var hasTasks = tasksServerPath is not null;
        var hasDataset = !string.IsNullOrEmpty(datasetId);
        if (!hasTasks && !hasDataset) return null;

        try
        {
            var servers = new System.Text.Json.Nodes.JsonObject();

            // Серверы из базового конфига (+ dataset id в env Dify)
            if (!string.IsNullOrEmpty(_mcpConfigPath) && File.Exists(_mcpConfigPath))
            {
                var baseDoc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_mcpConfigPath));
                if (baseDoc?["mcpServers"] is System.Text.Json.Nodes.JsonObject baseServers)
                {
                    foreach (var (key, val) in baseServers)
                    {
                        var clone = val?.DeepClone();
                        if (clone is null) continue;
                        if (key == "dify" && hasDataset && clone["env"] is { } env)
                        {
                            env["DIFY_DEFAULT_DATASET_ID"] = datasetId;
                            env["DIFY_SEARCH_ONLY"] = "true";
                        }
                        servers[key] = clone;
                    }
                }
            }

            if (hasTasks)
            {
                servers["tasks"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new System.Text.Json.Nodes.JsonArray { tasksServerPath! },
                    ["env"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["TASKS_API_URL"] = _tasksMcp!.ApiUrl,
                        ["TASKS_API_TOKEN"] = _tasksMcp.Token,
                        ["TASKS_PROJECT_ID"] = _tasksMcp.ProjectId ?? "",
                    },
                };
            }

            if (servers.Count == 0) return null;
            var combined = new System.Text.Json.Nodes.JsonObject { ["mcpServers"] = servers };
            var tmpPath = Path.Combine(Path.GetTempPath(), $"claude-mcp-{Guid.NewGuid():N}.json");
            File.WriteAllText(tmpPath, combined.ToJsonString());
            return tmpPath;
        }
        catch { return null; }
    }

    // index.js MCP-сервера задач: рядом с exe (prod) или в корне репо (dev)
    private static string? FindTasksServerPath()
    {
        var nearExe = Path.Combine(AppContext.BaseDirectory, "mcp", "tasks-server", "index.js");
        if (File.Exists(nearExe)) return nearExe;
        var nearCwd = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "..", "..", "mcp", "tasks-server", "index.js"));
        if (File.Exists(nearCwd)) return nearCwd;
        return null;
    }

    // Ничего не делаем при старте — процесс запускается при первом сообщении
    public Task StartAsync() => Task.CompletedTask;

    public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null)
    {
        Info.MessageCount++;
        Info.LastMessage = text.Length > 100 ? text[..100] + "…" : text;
        Info.UpdatedAt = DateTime.UtcNow;

        // Если сообщение — вызов скилла (/skill-name [args]), разворачиваем его содержимое
        var effectiveText = _skills?.TryExpandSkill(text) ?? text;
        // Картинки отправляем как image-блоки (base64), остальные файлы — инлайним в текст
        var (imagePaths, otherPaths) = SplitImagePaths(attachedPaths);
        var fullText = BuildMessageText(effectiveText, otherPaths);

        // Запускаем ход в фоне, чтобы не блокировать SignalR-соединение
        _ = Task.Run(async () =>
        {
            if (_cts.IsCancellationRequested) return;
            await _turnLock.WaitAsync(_cts.Token);
            try { await RunTurnAsync(fullText, imagePaths, _cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Info.Status = SessionStatus.Error;
                await _onMessage(new ErrorMessage(ex.Message));
            }
            finally { _turnLock.Release(); }
        });

        return Task.CompletedTask;
    }

    // Расширения, которые отправляем как image-блоки, а не инлайним текстом
    private static readonly HashSet<string> _imageExts =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    private static (List<string> images, List<string> others) SplitImagePaths(IReadOnlyList<string>? paths)
    {
        var images = new List<string>();
        var others = new List<string>();
        if (paths != null)
            foreach (var p in paths)
                (_imageExts.Contains(Path.GetExtension(p)) ? images : others).Add(p);
        return (images, others);
    }

    private static string MediaTypeForExt(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    // Блоки изображений для content стартового сообщения. Пустые/слишком большие (>8 МБ) пропускаем.
    private List<object> BuildImageBlocks(IReadOnlyList<string> imagePaths)
    {
        var blocks = new List<object>();
        foreach (var rel in imagePaths)
        {
            try
            {
                var full = FileService.SafeJoin(_rootPath, rel);
                if (!File.Exists(full)) continue;
                var bytes = File.ReadAllBytes(full);
                if (bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) continue;
                blocks.Add(new
                {
                    type = "image",
                    source = new { type = "base64", media_type = MediaTypeForExt(Path.GetExtension(rel)), data = Convert.ToBase64String(bytes) }
                });
            }
            catch { }
        }
        return blocks;
    }

    // Максимум текста на один инлайн-файл — чтобы вложение не раздуло сообщение
    private const int MaxInlineBytes = 256 * 1024;

    private string BuildMessageText(string text, IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) return text;

        var sb = new System.Text.StringBuilder(text);
        foreach (var relativePath in paths)
        {
            try
            {
                var fullPath = FileService.SafeJoin(_rootPath, relativePath);
                if (!File.Exists(fullPath)) continue;

                var info = new FileInfo(fullPath);
                byte[] bytes;
                using (var fs = info.OpenRead())
                {
                    var len = (int)Math.Min(info.Length, MaxInlineBytes);
                    bytes = new byte[len];
                    var read = 0;
                    while (read < len)
                    {
                        var n = fs.Read(bytes, read, len - read);
                        if (n == 0) break;
                        read += n;
                    }
                    if (read < len) Array.Resize(ref bytes, read);
                }

                // Бинарный файл (PDF/docx/xlsx/архив и т.п.) определяем по нулевому байту.
                // Не инлайним мусор-кракозябры: даём ссылку — рабочая папка = корень проекта,
                // Claude при необходимости откроет файл инструментом Read (умеет PDF/изображения/notebook).
                if (Array.IndexOf(bytes, (byte)0) >= 0)
                {
                    sb.Append($"\n\n---\nПрикреплён файл: {relativePath} (бинарный/документ — открой инструментом Read, если нужно его содержимое).");
                    continue;
                }

                var content = System.Text.Encoding.UTF8.GetString(bytes);
                var truncated = info.Length > MaxInlineBytes ? "\n…(файл обрезан по размеру)" : "";
                var ext = Path.GetExtension(relativePath).TrimStart('.');
                sb.Append($"\n\n---\nФайл: {relativePath}\n```{ext}\n{content}{truncated}\n```");
            }
            catch { }
        }
        return sb.ToString();
    }

    public void RespondPermission(string requestId, string behavior)
    {
        if (_permissionWaiters.TryGetValue(requestId, out var tcs))
            tcs.TrySetResult(behavior);
    }

    // Ответ пользователя на AskUserQuestion — control_response на исходный can_use_tool запрос
    public void AnswerQuestion(string toolUseId, string updatedInputJson)
    {
        if (!_pendingQuestions.Remove(toolUseId, out var requestId)) return;
        Info.Status = SessionStatus.Working;
        object updatedInput;
        try { updatedInput = JsonSerializer.Deserialize<object>(updatedInputJson)!; }
        catch { updatedInput = new { }; }
        SendControlResponse(requestId, new { behavior = "allow", updatedInput });
    }

    // Решение пользователя по плану (ExitPlanMode): approve → allow и Claude продолжает выполнение;
    // reject → deny с комментарием, Claude остаётся в режиме планирования
    public void RespondPlan(string requestId, bool approve, string? feedback)
    {
        if (!_pendingPlans.Remove(requestId, out var input)) return;
        Info.Status = SessionStatus.Working;
        if (approve)
        {
            // Ждём, что Claude реализует план в этом ходу; если завершит без правок — дошлём команду
            _awaitPlanExecution = true;
            _sawToolSinceApprove = false;
            SendControlResponse(requestId, new { behavior = "allow", updatedInput = input });
        }
        else
        {
            var message = string.IsNullOrWhiteSpace(feedback)
                ? "Пользователь отклонил план. Уточни план с учётом контекста и предложи заново."
                : $"Пользователь отклонил план с комментарием: {feedback}";
            SendControlResponse(requestId, new { behavior = "deny", message });
        }
    }

    // Обработка control_request(can_use_tool): AskUserQuestion → интерактивная карточка; прочее → авто-allow
    private async Task HandleControlRequestAsync(JsonElement root)
    {
        var requestId = root.TryGetProperty("request_id", out var rid) ? rid.GetString() ?? "" : "";
        if (!root.TryGetProperty("request", out var req)) return;
        var subtype = req.TryGetProperty("subtype", out var st) ? st.GetString() : null;
        if (subtype != "can_use_tool") return;

        var toolName = req.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
        var toolUseId = req.TryGetProperty("tool_use_id", out var tu) ? tu.GetString() ?? "" : "";
        var input = req.TryGetProperty("input", out var ti)
            ? JsonSerializer.Deserialize<object>(ti.GetRawText())! : new object();

        if (toolName == "AskUserQuestion")
        {
            // Ждём выбор пользователя — control_response отправит AnswerQuestion
            _pendingQuestions[toolUseId] = requestId;
            Info.Status = SessionStatus.Waiting;
            await _onMessage(new AskQuestionMessage(toolUseId, input));
            return;
        }

        if (toolName == "ExitPlanMode")
        {
            // Режим «План»: Claude представил план — ждём решения пользователя (RespondPlan),
            // НЕ авто-одобряем, иначе план не выносится на согласование
            _pendingPlans[requestId] = input;
            var plan = req.TryGetProperty("input", out var pin) && pin.TryGetProperty("plan", out var pl)
                ? pl.GetString() ?? "" : "";
            Info.Status = SessionStatus.Waiting;
            await _onMessage(new PlanReviewMessage(requestId, plan));
            return;
        }

        // Прочие инструменты авто-разрешаем (поведение по умолчанию), чтобы ход не завис
        SendControlResponse(requestId, new { behavior = "allow", updatedInput = input });
    }

    private void SendControlResponse(string requestId, object responsePayload)
    {
        var msg = JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new { subtype = "success", request_id = requestId, response = responsePayload }
        });
        if (_currentProcess != null && !_currentProcess.HasExited)
        {
            _currentProcess.StandardInput.WriteLine(msg);
            _currentProcess.StandardInput.Flush();
        }
    }

    public void Interrupt()
    {
        try { _currentProcess?.Kill(); } catch { }
        // Отменяем все ожидающие permission-диалоги: процесс убит, ответа не будет
        foreach (var tcs in _permissionWaiters.Values)
            tcs.TrySetCanceled();
        _permissionWaiters.Clear();
        _pendingQuestions.Clear();
        _pendingPlans.Clear();
        _awaitPlanExecution = false;
        _forceNonPlanNextTurn = false;
    }

    private async Task RunTurnAsync(string text, IReadOnlyList<string> imagePaths, CancellationToken ct)
    {
        // --print обязателен: без него --output-format/--input-format/--include-partial-messages/--permission-prompt-tool не работают
        // --input-format stream-json нужен: мы посылаем JSON-объекты в stdin, а не plain text
        var args = new List<string>
        {
            "--print",
            "--verbose",
            "--output-format", "stream-json",
            "--input-format", "stream-json",
            "--include-partial-messages",
            "--permission-prompt-tool", "stdio"
        };

        if (Info.ClaudeSessionId is not null)
            args.AddRange(["--resume", Info.ClaudeSessionId]);

        // Режим прав у claude CLI задаётся флагом --permission-mode (значения: default,
        // acceptEdits, plan, auto, dontAsk, bypassPermissions), а НЕ --mode (такого флага нет).
        // После одобрения плана один ход выполняем без plan, чтобы Claude реализовал, а не планировал заново.
        if (_forceNonPlanNextTurn)
            _forceNonPlanNextTurn = false;
        else
            args.AddRange(["--permission-mode", Info.Mode.ToCliFlag()]);

        if (!string.IsNullOrWhiteSpace(Info.Model))
            args.AddRange(["--model", Info.Model]);

        if (!string.IsNullOrWhiteSpace(Info.Effort))
            args.AddRange(["--effort", Info.Effort]);

        // MCP-конфиг: создаём каждый ход с актуальным dataset id (мог появиться после создания сессии)
        var currentWk = _wkStore?.GetByPath(_rootPath);
        var currentDatasetId = currentWk?.DifyDatasetId;
        string? turnMcpPath = BuildTurnMcpConfig(currentDatasetId);
        var effectiveMcpConfig = turnMcpPath ?? _mcpConfigPath;
        if (!string.IsNullOrWhiteSpace(effectiveMcpConfig) && File.Exists(effectiveMcpConfig))
            args.AddRange(["--mcp-config", effectiveMcpConfig]);

        // Системный промпт: пересчитываем и передаём КАЖДЫЙ ход. Каждый ход — новый процесс
        // claude --print --resume, а --append-system-prompt не сохраняется в транскрипте сессии:
        // не передать его → инструкции (fal-ai/запрет ASCII, Dify, теги) пропадут на этом ходу.
        {
            var basePrompt = ProjectManager.BuildSystemPrompt(
                _rawSystemPrompt, currentDatasetId != null, currentWk?.DocumentTags);

            // Подсказка про систему задач — только когда tasks-server подключён
            if (_tasksMcp is not null)
            {
                var scope = _tasksMcp.ProjectId is not null
                    ? "Текущий контекст — задачи этого проекта."
                    : "Текущий контекст — личные задачи пользователя (вне проектов).";
                var tasksHint =
                    "У пользователя есть встроенная система задач (вкладка «Задачи» в проекте и раздел «Календарь»). " +
                    "Управляй ею через MCP-инструменты mcp__tasks__* (tasks_list, tasks_search, tasks_get, tasks_create, " +
                    "tasks_update, tasks_complete, tasks_delete, tasks_add_subtask, tasks_toggle_subtask). " + scope + " " +
                    "Когда пользователь просит создать/найти/изменить задачу, напоминание или список дел — используй эти инструменты, " +
                    "а не файлы или собственный список. Даты — в формате YYYY-MM-DD, время HH:MM.";
                basePrompt = string.IsNullOrWhiteSpace(basePrompt)
                    ? tasksHint
                    : basePrompt + "\n\n" + tasksHint;
            }

            string? agentPrompt = null;
            if (!string.IsNullOrEmpty(Info.AgentName) && _skills is not null)
                agentPrompt = _skills.GetAgentSystemPrompt(_rootPath, Info.AgentName);

            var combinedPrompt = agentPrompt is not null
                ? (string.IsNullOrWhiteSpace(basePrompt)
                    ? agentPrompt
                    : basePrompt + "\n\n---\n\n" + agentPrompt)
                : basePrompt;

            if (!string.IsNullOrWhiteSpace(combinedPrompt))
                args.AddRange(["--append-system-prompt", combinedPrompt]);
        }

        // claude.exe пишет/читает UTF-8. Без явной кодировки .NET берёт системную
        // OEM code page (напр. CP866 на русской Windows) → кракозябры в ответах.
        // Задаём UTF-8 без BOM (BOM сломал бы первое сообщение в stdin).
        var utf8NoBom = new System.Text.UTF8Encoding(false);

        var psi = new ProcessStartInfo
        {
            FileName = FindClaudeExecutable(),
            WorkingDirectory = _rootPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = utf8NoBom,
            StandardErrorEncoding = utf8NoBom,
            StandardInputEncoding = utf8NoBom,
            CreateNoWindow = true
        };
        // ArgumentList экранирует каждый аргумент корректно (важно для многострочного системного промпта)
        foreach (var a in args) psi.ArgumentList.Add(a);

        // claude --print по умолчанию ждёт фоновые задачи (субагентов workflow) не дольше 600с,
        // затем принудительно завершается: «Background tasks still running after 600s; terminating».
        // Из-за этого длинные workflow обрывались на 10-й минуте, не доходя до конца. 0 = ждать без
        // ограничения по времени; нас страхует watchdog IdleTimeout (если claude замолчит дольше — прервём сами).
        psi.Environment["CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS"] = "0";

        var process = new Process { StartInfo = psi };

        process.Start();
        _currentProcess = process;

        if (_currentProcess.HasExited)
            throw new InvalidOperationException("Не удалось запустить claude process");

        StartFileWatcher();

        // Читаем stderr асинхронно, иначе при переполнении буфера процесс зависнет
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        // stdin оставляем открытым — claude пишет control_response в него при permission-запросах.
        // С картинками content — массив блоков (text + image base64), иначе просто строка.
        var imageBlocks = BuildImageBlocks(imagePaths);
        object content;
        if (imageBlocks.Count == 0)
        {
            content = text;
        }
        else
        {
            var blocks = new List<object> { new { type = "text", text } };
            blocks.AddRange(imageBlocks);
            content = blocks;
        }
        var msg = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content }
        });
        await process.StandardInput.WriteLineAsync(msg);
        await process.StandardInput.FlushAsync();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Watchdog: пересоздаём linked CTS на каждую строку.
                // Если claude замолчал дольше IdleTimeout — прерываем и сообщаем об ошибке.
                using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                watchdogCts.CancelAfter(IdleTimeout);

                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(watchdogCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Сработал watchdog (не внешняя отмена сессии)
                    await _onMessage(new ErrorMessage(
                        $"Claude не отвечает более {IdleTimeout.TotalMinutes:0} мин — прерываем"));
                    try { process.Kill(); } catch { }
                    break;
                }

                if (line is null) break; // stdout закрыт — процесс завершился
                if (string.IsNullOrWhiteSpace(line)) continue;
                await ProcessLineAsync(line);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            StopFileWatcher();
            try { process.StandardInput.Close(); } catch { }
            if (!process.HasExited)
            {
                try { process.Kill(); } catch { }
                // Ограниченное ожидание завершения — Kill() асинхронен на некоторых ОС
                using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(exitCts.Token); }
                catch (OperationCanceledException) { } // 10 с истекло — идём дальше
            }
            try
            {
                var stderr = await stderrTask;
                if (!string.IsNullOrWhiteSpace(stderr))
                    Console.Error.WriteLine($"[ClaudeSession stderr] {stderr.Trim()}");
            }
            catch { }
            process.Dispose();
            _currentProcess = null;
            if (turnMcpPath != null) try { File.Delete(turnMcpPath); } catch { }

            if (Info.Status == SessionStatus.Active)
                Info.Status = SessionStatus.Finished;

            await _onMessage(new ExitedMessage());
        }
    }

    // На Windows ищем claude.exe напрямую — cmd.exe /c не проксирует stdin корректно
    // (используется также ModelCatalogService для опроса списка моделей)
    internal static string FindClaudeExecutable()
    {
        if (!OperatingSystem.IsWindows()) return "claude";
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var exePath = Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        if (File.Exists(exePath)) return exePath;
        try
        {
            var where = Process.Start(new ProcessStartInfo("where.exe", "claude.exe")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
            var line = where.StandardOutput.ReadLine();
            if (!string.IsNullOrEmpty(line) && File.Exists(line)) return line.Trim();
        }
        catch { }
        return "claude.exe";
    }

    private async Task ProcessLineAsync(string line)
    {
        // Невалидный JSON от CLI не должен убивать весь turn
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }

        using (doc)
        {
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp)) return;

        switch (typeProp.GetString())
        {
            case "system":
                var sysSubtype = root.TryGetProperty("subtype", out var sst) ? sst.GetString() : null;
                if (sysSubtype == "init" && root.TryGetProperty("session_id", out var sid))
                {
                    var isResume = Info.ClaudeSessionId is not null;
                    Info.ClaudeSessionId = sid.GetString();
                    var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                    var cwd = root.TryGetProperty("cwd", out var cw) && cw.ValueKind == JsonValueKind.String ? cw.GetString() : null;
                    var toolCount = root.TryGetProperty("tools", out var tl) && tl.ValueKind == JsonValueKind.Array ? tl.GetArrayLength() : 0;
                    List<McpServerInfo>? mcp = null;
                    if (root.TryGetProperty("mcp_servers", out var ms) && ms.ValueKind == JsonValueKind.Array)
                    {
                        mcp = [];
                        foreach (var s in ms.EnumerateArray())
                        {
                            var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var status = s.TryGetProperty("status", out var st2) ? st2.GetString() ?? "" : "";
                            if (name.Length > 0) mcp.Add(new McpServerInfo(name, status));
                        }
                    }
                    await _onMessage(new SessionStartedMessage(
                        Info.ClaudeSessionId!, isResume, model, Info.Mode.ToWireToken(), cwd, toolCount, mcp));
                }
                else if (sysSubtype == "compact_boundary")
                {
                    // Claude свернул контекст — показываем разделитель
                    var meta = root.TryGetProperty("compact_metadata", out var cm) ? cm : default;
                    var trigger = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("trigger", out var tr)
                        ? tr.GetString() ?? "auto" : "auto";
                    int? preTokens = meta.ValueKind == JsonValueKind.Object
                        && meta.TryGetProperty("pre_tokens", out var pt) && pt.TryGetInt32(out var ptv) ? ptv : null;
                    await _onMessage(new CompactBoundaryMessage(trigger, preTokens));
                }
                break;

            case "stream_event":
                await HandleStreamEventAsync(root);
                break;

            case "assistant":
                await HandleAssistantToolsAsync(root);
                break;

            case "result":
                // Результаты субагентов имеют parent_tool_use_id — не завершаем сессию по ним
                if (root.TryGetProperty("parent_tool_use_id", out var rPid) && rPid.ValueKind == JsonValueKind.String)
                    break;
                var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() ?? "success" : "success";
                var durationMs = root.TryGetProperty("duration_ms", out var d) ? d.GetInt64() : 0;
                var numTurns = root.TryGetProperty("num_turns", out var nt) ? nt.GetInt32() : 0;
                var totalCost = root.TryGetProperty("total_cost_usd", out var tc) ? tc.GetDouble() : (double?)null;
                var apiErr = root.TryGetProperty("api_error_status", out var ae) && ae.ValueKind == JsonValueKind.String
                    ? ae.GetString() : null;
                List<string>? denials = null;
                if (root.TryGetProperty("permission_denials", out var pd) && pd.ValueKind == JsonValueKind.Array && pd.GetArrayLength() > 0)
                {
                    denials = [];
                    foreach (var x in pd.EnumerateArray())
                        denials.Add(x.TryGetProperty("tool_name", out var tnm) ? tnm.GetString() ?? "?" : "?");
                }
                Info.Status = subtype == "error" ? SessionStatus.Error : SessionStatus.Finished;
                await _onMessage(new ResultMessage(subtype, durationMs, numTurns, ParseUsage(root), totalCost, apiErr, denials));
                // Закрываем stdin: все permission-запросы уже обработаны, Claude может завершить процесс
                try { _currentProcess?.StandardInput.Close(); } catch { }
                // Гарантия исполнения одобренного плана: если ход завершился, а Claude так и не
                // приступил к правкам — дошлём команду на реализацию (следующий ход — без plan-режима)
                if (_awaitPlanExecution)
                {
                    var needFollowUp = !_sawToolSinceApprove && subtype != "error";
                    _awaitPlanExecution = false;
                    if (needFollowUp)
                    {
                        _forceNonPlanNextTurn = true;
                        _ = SendMessageAsync("Одобренный план согласован. Реализуй его полностью сейчас — без повторного планирования.");
                    }
                }
                break;

            case "user":
                await HandleUserMessageAsync(root);
                break;

            case "sdk_control_request":
                await HandlePermissionAsync(root);
                break;

            case "control_request":
                await HandleControlRequestAsync(root);
                break;

            case "rate_limit_event":
                await HandleRateLimitAsync(root);
                break;
        }
        } // using (doc)
    }

    // Мягкий лимит API: claude шлёт rate_limit_event и приостанавливается до сброса окна
    private async Task HandleRateLimitAsync(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limit_info", out var info)) return;

        // Форвардим ВСЕ события (включая "allowed"): utilization нужен для непрерывного индикатора
        // использования подписки. Баннер на фронте решается по status (allowed_warning/rejected),
        // "allowed" просто тихо обновляет индикатор.
        var status = info.TryGetProperty("status", out var stEl) ? stEl.GetString() : null;

        var utilization = info.TryGetProperty("utilization", out var utEl) && utEl.ValueKind == JsonValueKind.Number
            ? utEl.GetDouble() : (double?)null;
        var isUsingOverage = info.TryGetProperty("isUsingOverage", out var ovEl) && ovEl.ValueKind == JsonValueKind.True;

        var limitType =
            (info.TryGetProperty("rateLimitType", out var lt) ? lt.GetString() : null)
            ?? (info.TryGetProperty("rate_limit_type", out var lt2) ? lt2.GetString() : null)
            ?? "";

        // Нет ни типа окна, ни utilization — нечего показывать
        if (string.IsNullOrEmpty(limitType) && utilization is null) return;

        // resetsAt может прийти как ISO-строка или unix-время (сек/мс) — нормализуем в ISO
        var resetsAt = NormalizeReset(info, "resetsAt", "resets_at");

        // Overage (перерасход сверх лимита, у тарифа Max): статус + время сброса окна перерасхода
        var overageStatus = info.TryGetProperty("overageStatus", out var osEl) ? osEl.GetString() : null;
        var overageResetsAt = NormalizeReset(info, "overageResetsAt", "overage_resets_at");

        await _onMessage(new RateLimitMessage(limitType, resetsAt, status, utilization, isUsingOverage, overageStatus, overageResetsAt));
    }

    // Нормализует поле времени сброса (ISO-строка или unix сек/мс) в ISO-строку
    private static string? NormalizeReset(JsonElement info, string key1, string key2)
    {
        if (info.TryGetProperty(key1, out var ra) || info.TryGetProperty(key2, out ra))
        {
            if (ra.ValueKind == JsonValueKind.String) return ra.GetString();
            if (ra.ValueKind == JsonValueKind.Number && ra.TryGetInt64(out var n))
                return (n > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(n)
                    : DateTimeOffset.FromUnixTimeSeconds(n)).ToString("o");
        }
        return null;
    }

    private async Task HandleUserMessageAsync(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("content", out var content)) return;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var bt)) continue;
            if (bt.GetString() != "tool_result") continue;

            var toolUseId = block.TryGetProperty("tool_use_id", out var tuid) ? tuid.GetString() ?? "" : "";
            var isError = block.TryGetProperty("is_error", out var ie) && ie.GetBoolean();

            var resultContent = "";
            if (block.TryGetProperty("content", out var c))
            {
                if (c.ValueKind == JsonValueKind.String)
                    resultContent = c.GetString() ?? "";
                else if (c.ValueKind == JsonValueKind.Array)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var cb in c.EnumerateArray())
                        if (cb.TryGetProperty("text", out var t))
                            sb.AppendLine(t.GetString());
                    resultContent = sb.ToString().TrimEnd();
                }
            }

            await _onMessage(new ToolResultMessage(toolUseId, resultContent, isError));

            // Если это результат Workflow с транскриптом — запускаем watcher
            if (!isError && resultContent.Contains("Transcript dir:"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(resultContent, @"Transcript dir:\s*(.+)");
                if (m.Success)
                {
                    var transcriptDir = m.Groups[1].Value.Trim();
                    var watcher = new WorkflowWatcher(transcriptDir, toolUseId, _onMessage);
                    _workflowWatchers.Add(watcher);
                    watcher.Start();
                }
            }
        }
    }

    private async Task HandleStreamEventAsync(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var evt)) return;
        if (!evt.TryGetProperty("type", out var et)) return;
        var eventType = et.GetString();
        var index = evt.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var ixv) ? ixv : -1;

        // Начало блока tool_use — показываем карточку сразу (до прихода полного assistant-сообщения)
        if (eventType == "content_block_start")
        {
            if (!evt.TryGetProperty("content_block", out var cb)) return;
            if (!cb.TryGetProperty("type", out var cbt) || cbt.GetString() != "tool_use") return;
            var id = cb.TryGetProperty("id", out var cid) ? cid.GetString() ?? "" : "";
            var name = cb.TryGetProperty("name", out var cn) ? cn.GetString() ?? "" : "";
            // Служебные инструменты не показываем: AskUserQuestion/ExitPlanMode идут отдельными
            // карточками (вопрос/план), ToolSearch — внутренний механизм загрузки схем инструментов
            if (id.Length == 0 || name is "AskUserQuestion" or "ExitPlanMode" or "ToolSearch") return;
            _toolStream[index] = (id, new System.Text.StringBuilder());
            await _onMessage(new ToolUseMessage(id, name, new { }));
            return;
        }

        if (eventType == "content_block_stop") { _toolStream.Remove(index); return; }

        if (eventType != "content_block_delta") return;
        if (!evt.TryGetProperty("delta", out var delta)) return;
        if (!delta.TryGetProperty("type", out var dt)) return;

        switch (dt.GetString())
        {
            case "text_delta":
                if (delta.TryGetProperty("text", out var text))
                    await _onMessage(new TextDeltaMessage(text.GetString() ?? ""));
                break;

            case "thinking_delta":
                if (delta.TryGetProperty("thinking", out var thinking))
                    await _onMessage(new ThinkingDeltaMessage(thinking.GetString() ?? ""));
                break;

            case "input_json_delta":
                if (_toolStream.TryGetValue(index, out var ts) && delta.TryGetProperty("partial_json", out var pj))
                {
                    ts.Sb.Append(pj.GetString());
                    await _onMessage(new ToolInputDeltaMessage(ts.Id, ts.Sb.ToString()));
                }
                break;
        }
    }

    private async Task HandleAssistantToolsAsync(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("content", out var content)) return;

        // Сообщения субагента (Task) несут parent_tool_use_id на уровне строки — для вложенности
        var parentId = root.TryGetProperty("parent_tool_use_id", out var pid) && pid.ValueKind == JsonValueKind.String
            ? pid.GetString() : null;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var bt)) continue;
            var blockType = bt.GetString();

            // Скрытое размышление — показываем плашку-плейсхолдер
            if (blockType == "redacted_thinking") { await _onMessage(new RedactedThinkingMessage()); continue; }
            if (blockType != "tool_use") continue;

            var toolId = block.TryGetProperty("id", out var tid) ? tid.GetString() ?? "" : "";
            var toolName = block.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
            var toolInput = block.TryGetProperty("input", out var ti)
                ? JsonSerializer.Deserialize<object>(ti.GetRawText())! : new object();

            // Служебные инструменты не дублируем в ленте: AskUserQuestion/ExitPlanMode показываем
            // отдельными карточками (вопрос/план), ToolSearch — внутренняя загрузка схем инструментов
            if (toolName is "AskUserQuestion" or "ExitPlanMode" or "ToolSearch") continue;
            // После одобрения плана любой реальный инструмент означает, что Claude приступил к реализации
            if (_awaitPlanExecution) _sawToolSinceApprove = true;
            await _onMessage(new ToolUseMessage(toolId, toolName, toolInput, parentId));
        }

        // Ответ оборван по лимиту токенов
        if (msg.TryGetProperty("stop_reason", out var stopReason) && stopReason.GetString() == "max_tokens")
            await _onMessage(new TruncatedMessage());
    }

    private async Task HandlePermissionAsync(JsonElement root)
    {
        // Используем request_id из CLI — именно его ждёт claude в control_response
        var requestId = root.TryGetProperty("request_id", out var rid)
            ? rid.GetString() ?? Guid.NewGuid().ToString()
            : Guid.NewGuid().ToString();

        var toolName = root.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
        var inputEl = root.TryGetProperty("tool_input", out var ti) ? ti : default;
        var toolInput = inputEl.ValueKind != JsonValueKind.Undefined
            ? JsonSerializer.Deserialize<object>(inputEl.GetRawText())! : new object();

        // Правила проекта: deny приоритетнее; allow — авто-разрешить; null — спросить пользователя
        var ruleDecision = EvaluateRules(toolName, inputEl);

        string behavior;
        if (ruleDecision == "deny")
        {
            behavior = "deny";
        }
        else if (ruleDecision == "allow" || _autoAllowTools.Contains(toolName))
        {
            // Разрешено правилом проекта или ранее выбрано «всегда разрешать» — не спрашиваем
            behavior = "allow";
        }
        else
        {
            var tcs = new TaskCompletionSource<string>();
            _permissionWaiters[requestId] = tcs;
            Info.Status = SessionStatus.Waiting;

            await _onMessage(new PermissionRequestMessage(requestId, toolName, toolInput));

            try
            {
                // Ждём ответа пользователя или таймаута 60 минут
                behavior = await tcs.Task.WaitAsync(TimeSpan.FromMinutes(60));
            }
            catch (TaskCanceledException)
            {
                // Interrupt() отменил TCS через TrySetCanceled() — процесс уже убит
                _permissionWaiters.Remove(requestId);
                return;
            }
            catch (TimeoutException)
            {
                // Пользователь не ответил — deny и продолжаем
                _permissionWaiters.Remove(requestId);
                behavior = "deny";
            }

            _permissionWaiters.Remove(requestId);

            // «Всегда разрешать»: запоминаем инструмент и отвечаем claude обычным allow
            if (behavior == "allow_always")
            {
                _autoAllowTools.Add(toolName);
                behavior = "allow";
            }

            Info.Status = SessionStatus.Working;
        }

        var response = JsonSerializer.Serialize(new
        {
            type = "control_response",
            behavior,
            updated_input = toolInput
        });
        if (_currentProcess != null && !_currentProcess.HasExited)
        {
            await _currentProcess.StandardInput.WriteLineAsync(response);
            await _currentProcess.StandardInput.FlushAsync();
        }
    }

    // Применение правил проекта к permission-запросу.
    // Возвращает "deny" (запрет приоритетен), "allow" или null (решает пользователь).
    private string? EvaluateRules(string toolName, JsonElement input)
    {
        var rules = _permissionRules?.Invoke();
        if (rules is null || rules.Count == 0) return null;
        string? allow = null;
        foreach (var r in rules)
        {
            if (!RuleMatches(r, toolName, input)) continue;
            if (string.Equals(r.Action, "deny", StringComparison.OrdinalIgnoreCase)) return "deny";
            if (string.Equals(r.Action, "allow", StringComparison.OrdinalIgnoreCase)) allow = "allow";
        }
        return allow;
    }

    private static bool RuleMatches(PermissionRule rule, string toolName, JsonElement input)
    {
        var (tool, spec) = ParseRule(rule.Pattern);
        if (tool != "*" && !string.Equals(tool, toolName, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrEmpty(spec)) return true; // правило по имени инструмента — матчит любой вызов
        return GlobMatch(spec, ExtractRuleArg(toolName, input));
    }

    // "Tool(specifier)" → (Tool, specifier); "Tool" → (Tool, null)
    private static (string tool, string? spec) ParseRule(string pattern)
    {
        pattern = pattern.Trim();
        var i = pattern.IndexOf('(');
        if (i < 0 || !pattern.EndsWith(")")) return (pattern, null);
        return (pattern[..i].Trim(), pattern[(i + 1)..^1]);
    }

    // Основной аргумент инструмента, по которому матчим specifier
    private static string? ExtractRuleArg(string toolName, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        var keys = toolName.ToLowerInvariant() switch
        {
            "bash" => new[] { "command" },
            "edit" or "write" or "read" or "notebookedit" => new[] { "file_path", "path" },
            "glob" or "grep" => new[] { "pattern", "path" },
            "webfetch" => new[] { "url" },
            "websearch" => new[] { "query" },
            _ => [],
        };
        foreach (var k in keys)
            if (input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    // Glob: '*' — любая подстрока; остальное — буквально, без учёта регистра
    private static bool GlobMatch(string pattern, string? value)
    {
        if (value is null) return false;
        var rx = "^" + string.Join(".*",
            pattern.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            value, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static UsageInfo? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var u)) return null;
        return new UsageInfo(
            u.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0,
            u.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0,
            u.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0,
            u.TryGetProperty("cache_creation_input_tokens", out var cc) ? cc.GetInt32() : 0
        );
    }

    private void StartFileWatcher()
    {
        if (!Directory.Exists(_rootPath)) return;
        _watcher = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileSystemEvent;
        _watcher.Created += OnFileSystemEvent;
    }

    private void StopFileWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        foreach (var cts in _debounce.Values) cts.Cancel();
        _debounce.Clear();
    }

    private void OnFileSystemEvent(object _, FileSystemEventArgs e)
    {
        var fullPath = e.FullPath;
        var fileName = Path.GetFileName(fullPath);
        // Игнорируем .git, временные файлы компиляторов, служебные директории
        var sep = Path.DirectorySeparatorChar;
        if (fullPath.Contains(sep + ".git" + sep) ||
            fullPath.EndsWith(sep + ".git") ||
            fullPath.Contains(sep + ".playwright") ||
            fullPath.Contains(sep + "obj" + sep) ||
            fullPath.Contains(sep + "node_modules" + sep) ||
            fileName.EndsWith("~") ||
            fileName.EndsWith(".tmp") ||
            fileName.Contains(".tmp.")) return;

        if (_debounce.TryRemove(fullPath, out var old)) old.Cancel();
        var cts = new CancellationTokenSource();
        _debounce[fullPath] = cts;

        Task.Delay(400, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _debounce.TryRemove(fullPath, out CancellationTokenSource? _);
            try
            {
                if (!File.Exists(fullPath) && !_fileCache.ContainsKey(fullPath)) return;

                var rel = Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');
                var newContent = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
                _fileCache.TryGetValue(fullPath, out var oldContent);
                _fileCache[fullPath] = newContent;
                var (added, removed) = CountLineDiff(oldContent, newContent);
                if (added == 0 && removed == 0) return;
                _ = _onMessage(new FileChangedMessage(rel, added, removed));
            }
            catch { }
        }, TaskScheduler.Default);
    }

    private static (int added, int removed) CountLineDiff(string? oldContent, string? newContent)
    {
        var oldCount = oldContent?.Split('\n').Length ?? 0;
        var newCount = newContent?.Split('\n').Length ?? 0;
        return (Math.Max(0, newCount - oldCount), Math.Max(0, oldCount - newCount));
    }

    public async ValueTask DisposeAsync()
    {
        StopFileWatcher();
        foreach (var w in _workflowWatchers) w.Dispose();
        _workflowWatchers.Clear();
        _cts.Cancel();
        if (_currentProcess != null && !_currentProcess.HasExited)
        {
            _currentProcess.Kill();
            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _currentProcess.WaitForExitAsync(exitCts.Token); }
            catch (OperationCanceledException) { }
        }
        _currentProcess?.Dispose();
        _cts.Dispose();
        _turnLock.Dispose();
    }
}
