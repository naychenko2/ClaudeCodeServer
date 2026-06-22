using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ClaudeCodeServer.Models;
using ClaudeCodeServer.Protocol;

namespace ClaudeCodeServer.Services;

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
    // Стриминг tool_use: индекс content-блока → (id инструмента, накопленный partial_json)
    private readonly Dictionary<int, (string Id, System.Text.StringBuilder Sb)> _toolStream = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private Process? _currentProcess;

    // Если claude не выдаёт ни одной строки дольше этого — считаем зависшим
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(60);

    // Отслеживание изменений файлов
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, string?> _fileCache = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounce = new();

    public ClaudeSession(Session info, string rootPath, Func<ServerMessage, Task> onMessage)
    {
        Info = info;
        _rootPath = rootPath;
        _onMessage = onMessage;
    }

    // Ничего не делаем при старте — процесс запускается при первом сообщении
    public Task StartAsync() => Task.CompletedTask;

    public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null)
    {
        Info.MessageCount++;
        Info.LastMessage = text.Length > 100 ? text[..100] + "…" : text;
        Info.UpdatedAt = DateTime.UtcNow;

        var fullText = BuildMessageText(text, attachedPaths);

        // Запускаем ход в фоне, чтобы не блокировать SignalR-соединение
        _ = Task.Run(async () =>
        {
            if (_cts.IsCancellationRequested) return;
            await _turnLock.WaitAsync(_cts.Token);
            try { await RunTurnAsync(fullText, _cts.Token); }
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
                var content = File.ReadAllText(fullPath);
                var ext = Path.GetExtension(relativePath).TrimStart('.');
                sb.Append($"\n\n---\nФайл: {relativePath}\n```{ext}\n{content}\n```");
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
    }

    private async Task RunTurnAsync(string text, CancellationToken ct)
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

        // Режим прав у claude CLI задаётся флагом --permission-mode (plan/default),
        // а НЕ --mode (такого флага нет — claude падает с "unknown option '--mode'").
        // Auto — без флага (поведение по умолчанию); Plan — режим планирования;
        // Ask — default, при котором claude спрашивает разрешение через permission-prompt-tool.
        if (Info.Mode == ClaudeMode.Plan)
            args.AddRange(["--permission-mode", "plan"]);
        else if (Info.Mode == ClaudeMode.Ask)
            args.AddRange(["--permission-mode", "default"]);

        if (!string.IsNullOrWhiteSpace(Info.Model))
            args.AddRange(["--model", Info.Model]);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FindClaudeExecutable(),
                Arguments = string.Join(" ", args),
                WorkingDirectory = _rootPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        _currentProcess = process;

        if (_currentProcess.HasExited)
            throw new InvalidOperationException("Не удалось запустить claude process");

        StartFileWatcher();

        // Читаем stderr асинхронно, иначе при переполнении буфера процесс зависнет
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        // stdin оставляем открытым — claude пишет control_response в него при permission-запросах
        var msg = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = text }
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

            if (Info.Status == SessionStatus.Active)
                Info.Status = SessionStatus.Finished;

            await _onMessage(new ExitedMessage());
        }
    }

    // На Windows ищем claude.exe напрямую — cmd.exe /c не проксирует stdin корректно
    private static string FindClaudeExecutable()
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
                        Info.ClaudeSessionId!, isResume, model, Info.Mode.ToString().ToLower(), cwd, toolCount, mcp));
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

        var limitType =
            (info.TryGetProperty("rateLimitType", out var lt) ? lt.GetString() : null)
            ?? (info.TryGetProperty("rate_limit_type", out var lt2) ? lt2.GetString() : null)
            ?? "";

        // resetsAt может прийти как ISO-строка или unix-время (сек/мс) — нормализуем в ISO
        string? resetsAt = null;
        if (info.TryGetProperty("resetsAt", out var ra) || info.TryGetProperty("resets_at", out ra))
        {
            if (ra.ValueKind == JsonValueKind.String)
                resetsAt = ra.GetString();
            else if (ra.ValueKind == JsonValueKind.Number && ra.TryGetInt64(out var n))
                resetsAt = (n > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(n)
                    : DateTimeOffset.FromUnixTimeSeconds(n)).ToString("o");
        }

        await _onMessage(new RateLimitMessage(limitType, resetsAt));
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
            if (id.Length == 0 || name == "AskUserQuestion") return;
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

            // AskUserQuestion приходит ещё и как control_request(can_use_tool) — карточку показываем оттуда, здесь пропускаем
            if (toolName == "AskUserQuestion") continue;
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
        var toolInput = root.TryGetProperty("tool_input", out var ti)
            ? JsonSerializer.Deserialize<object>(ti.GetRawText())! : new object();

        string behavior;
        if (_autoAllowTools.Contains(toolName))
        {
            // Пользователь ранее выбрал «всегда разрешать» этот инструмент — не спрашиваем повторно
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
