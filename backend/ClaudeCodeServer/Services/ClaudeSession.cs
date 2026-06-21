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
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private Process? _currentProcess;

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

        // Собираем полный текст с вложениями
        var fullText = BuildMessageText(text, attachedPaths);

        // Запускаем ход в фоне, чтобы не блокировать SignalR-соединение
        _ = Task.Run(async () =>
        {
            await _turnLock.WaitAsync(_cts.Token);
            try { await RunTurnAsync(fullText, _cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Info.Status = SessionStatus.Error;
                await _onMessage(new ErrorMessage(ex.Message));
                await _onMessage(new ExitedMessage());
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
            tcs.SetResult(behavior);
    }

    public void Interrupt()
    {
        try { _currentProcess?.Kill(); } catch { }
    }

    private async Task RunTurnAsync(string text, CancellationToken ct)
    {
        var args = new List<string>
        {
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--permission-prompt-tool", "stdio"
        };

        if (Info.ClaudeSessionId is not null)
            args.AddRange(["--resume", Info.ClaudeSessionId]);

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
        Info.Status = SessionStatus.Active;
        StartFileWatcher();

        // Отправляем сообщение. stdin оставляем открытым — claude может запросить разрешение
        // через sdk_control_request и ждать control_response в stdin
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
                var line = await process.StandardOutput.ReadLineAsync(ct);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                await ProcessLineAsync(line);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            StopFileWatcher();
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
            process.Dispose();
            _currentProcess = null;

            // Если статус не был выставлен через result, ставим Finished
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
        using var doc = JsonDocument.Parse(line);
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
                    await _onMessage(new SessionStartedMessage(
                        Info.ClaudeSessionId!, isResume, model, Info.Mode.ToString().ToLower()));
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
                Info.Status = subtype == "error" ? SessionStatus.Error : SessionStatus.Finished;
                await _onMessage(new ResultMessage(subtype, durationMs, numTurns, ParseUsage(root)));
                break;

            case "user":
                await HandleUserMessageAsync(root);
                break;

            case "sdk_control_request":
                await HandlePermissionAsync(root);
                break;
        }
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
        if (et.GetString() != "content_block_delta") return;
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
        }
    }

    private async Task HandleAssistantToolsAsync(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("content", out var content)) return;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var bt)) continue;
            if (bt.GetString() != "tool_use") continue;

            var toolId = block.TryGetProperty("id", out var tid) ? tid.GetString() ?? "" : "";
            var toolName = block.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
            var toolInput = block.TryGetProperty("input", out var ti)
                ? JsonSerializer.Deserialize<object>(ti.GetRawText())! : new object();
            await _onMessage(new ToolUseMessage(toolId, toolName, toolInput));
        }
    }

    private async Task HandlePermissionAsync(JsonElement root)
    {
        var requestId = Guid.NewGuid().ToString();
        var toolName = root.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
        var toolInput = root.TryGetProperty("tool_input", out var ti)
            ? JsonSerializer.Deserialize<object>(ti.GetRawText())! : new object();

        var tcs = new TaskCompletionSource<string>();
        _permissionWaiters[requestId] = tcs;
        Info.Status = SessionStatus.Waiting;

        await _onMessage(new PermissionRequestMessage(requestId, toolName, toolInput));

        var behavior = await tcs.Task.WaitAsync(TimeSpan.FromMinutes(5));
        _permissionWaiters.Remove(requestId);
        Info.Status = SessionStatus.Active;

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
        // Игнорируем .git и временные файлы
        if (fullPath.Contains(".git") || fullPath.EndsWith("~") || fullPath.EndsWith(".tmp")) return;

        if (_debounce.TryRemove(fullPath, out var old)) old.Cancel();
        var cts = new CancellationTokenSource();
        _debounce[fullPath] = cts;

        Task.Delay(400, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _debounce.TryRemove(fullPath, out var __);
            try
            {
                var rel = Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');
                var newContent = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
                _fileCache.TryGetValue(fullPath, out var oldContent);
                _fileCache[fullPath] = newContent;
                var (added, removed) = CountLineDiff(oldContent, newContent);
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
            await _currentProcess.WaitForExitAsync();
        }
        _currentProcess?.Dispose();
        _cts.Dispose();
        _turnLock.Dispose();
    }
}
