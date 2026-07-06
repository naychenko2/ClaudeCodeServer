using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Llm.DeepSeek;

public sealed record McpToolInfo(string Name, string? Description, JsonObject InputSchema);

// Минимальный MCP-клиент по stdio (JSON-RPC 2.0, одно сообщение на строку):
// initialize → notifications/initialized → tools/list → tools/call.
// Достаточно для tasks-server, Dify и типовых пользовательских серверов из .mcp.json.
public sealed class McpStdioClient : IAsyncDisposable
{
    public string ServerName { get; }
    public IReadOnlyList<McpToolInfo> Tools { get; private set; } = [];

    private readonly Process _process;
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _nextId;
    private readonly CancellationTokenSource _cts = new();

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(5);

    private McpStdioClient(string serverName, Process process)
    {
        ServerName = serverName;
        _process = process;
        _ = Task.Run(ReadLoopAsync);
    }

    public static async Task<McpStdioClient> StartAsync(string serverName, string command,
        IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        var utf8NoBom = new System.Text.UTF8Encoding(false);
        var psi = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = utf8NoBom,
            StandardInputEncoding = utf8NoBom,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Не удалось запустить MCP-сервер {serverName}");
        // stderr дренируем, чтобы сервер не завис на переполненном буфере
        _ = process.StandardError.ReadToEndAsync();

        var client = new McpStdioClient(serverName, process);
        try
        {
            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startCts.CancelAfter(StartupTimeout);
            await client.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "claude-home-deepseek", ["version"] = "1.0" },
            }, startCts.Token);
            await client.NotifyAsync("notifications/initialized");

            var toolsResp = await client.RequestAsync("tools/list", new JsonObject(), startCts.Token);
            var tools = new List<McpToolInfo>();
            if (toolsResp.TryGetProperty("tools", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var t in arr.EnumerateArray())
                {
                    var name = t.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    var desc = t.TryGetProperty("description", out var d) ? d.GetString() : null;
                    var schema = t.TryGetProperty("inputSchema", out var s)
                        && JsonNode.Parse(s.GetRawText()) is JsonObject so
                        ? so : new JsonObject { ["type"] = "object" };
                    tools.Add(new McpToolInfo(name!, desc, schema));
                }
            client.Tools = tools;
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    // Вызов инструмента: конкатенация text-блоков content + признак ошибки
    public async Task<(string Content, bool IsError)> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        callCts.CancelAfter(CallTimeout);
        var result = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = JsonNode.Parse(args.GetRawText()),
        }, callCts.Token);

        var isError = result.TryGetProperty("isError", out var ie) && ie.ValueKind == JsonValueKind.True;
        var sb = new System.Text.StringBuilder();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    sb.AppendLine(text.GetString());
                else
                    sb.AppendLine("[не-текстовый блок содержимого]");
            }
        return (sb.ToString().TrimEnd(), isError);
    }

    private async Task<JsonElement> RequestAsync(string method, JsonObject @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await WriteLineAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = @params,
            }.ToJsonString(), ct);
            return await tcs.Task.WaitAsync(ct);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private Task NotifyAsync(string method) =>
        WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }.ToJsonString(), CancellationToken.None);

    private async Task WriteLineAsync(string line, CancellationToken ct)
    {
        await _stdinLock.WaitAsync(ct);
        try
        {
            await _process.StandardInput.WriteLineAsync(line);
            await _process.StandardInput.FlushAsync(ct);
        }
        finally { _stdinLock.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(_cts.Token);
                if (line is null) break; // сервер завершился
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; } // серверы иногда пишут логи в stdout — пропускаем
                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) continue;
                    if (!_pending.TryRemove(id, out var tcs)) continue;
                    if (root.TryGetProperty("error", out var err))
                        tcs.TrySetException(new InvalidOperationException(
                            $"MCP {ServerName}: {(err.TryGetProperty("message", out var m) ? m.GetString() : err.GetRawText())}"));
                    else if (root.TryGetProperty("result", out var result))
                        tcs.TrySetResult(result.Clone());
                    else
                        tcs.TrySetResult(default);
                }
            }
        }
        catch (OperationCanceledException) { /* остановка клиента */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[McpStdioClient:{ServerName}] Чтение stdout прервано: {ex.Message}");
        }
        // Сервер умер — отменяем все ожидания
        foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
        _pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _process.WaitForExitAsync(exitCts.Token); }
                catch (OperationCanceledException) { }
            }
        }
        catch { /* процесс уже завершился */ }
        _process.Dispose();
        _cts.Dispose();
        _stdinLock.Dispose();
    }
}

// MCP-серверы DeepSeek-сессии: серверы из базового конфига (McpConfigPath, с инжекцией
// Dify dataset id) + встроенный tasks-server. Стартуют лениво при первом ходе;
// их инструменты попадают в tool-цикл под именами mcp__<server>__<tool>.
public sealed class DeepSeekMcpManager(string? mcpConfigPath, TasksMcpContext? tasksMcp,
    Func<string?> difyDatasetId) : IAsyncDisposable
{
    public const string ToolPrefix = "mcp__";

    private readonly List<McpStdioClient> _clients = [];
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private bool _started;

    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_started) return;
        await _startLock.WaitAsync(ct);
        try
        {
            if (_started) return;
            foreach (var (name, command, args, env) in BuildServerSpecs())
            {
                try
                {
                    _clients.Add(await McpStdioClient.StartAsync(name, command, args, env, ct));
                }
                catch (Exception ex)
                {
                    // Недоступный сервер не должен блокировать сессию — работаем без него
                    Console.Error.WriteLine($"[DeepSeekMcp] Сервер «{name}» не подключился: {ex.Message}");
                }
            }
            _started = true;
        }
        finally { _startLock.Release(); }
    }

    private List<(string Name, string Command, List<string> Args, Dictionary<string, string> Env)> BuildServerSpecs()
    {
        var specs = new List<(string, string, List<string>, Dictionary<string, string>)>();
        var datasetId = difyDatasetId();

        // Серверы из базового конфига (.mcp.json): command/args/env
        if (!string.IsNullOrEmpty(mcpConfigPath) && File.Exists(mcpConfigPath))
        {
            try
            {
                var doc = JsonNode.Parse(File.ReadAllText(mcpConfigPath));
                if (doc?["mcpServers"] is JsonObject servers)
                    foreach (var (name, node) in servers)
                    {
                        if (node?["command"]?.GetValue<string>() is not { Length: > 0 } command) continue;
                        var args = (node["args"] as JsonArray)?
                            .Select(a => a?.GetValue<string>() ?? "").ToList() ?? [];
                        var env = new Dictionary<string, string>();
                        if (node["env"] is JsonObject envObj)
                            foreach (var (k, v) in envObj)
                                if (v is not null) env[k] = v.GetValue<string>();
                        // Dify: инжектим dataset id воркспейса (как BuildTurnMcpConfig у Claude)
                        if (name == "dify" && !string.IsNullOrEmpty(datasetId))
                        {
                            env["DIFY_DEFAULT_DATASET_ID"] = datasetId;
                            env["DIFY_SEARCH_ONLY"] = "true";
                        }
                        specs.Add((name, command, args, env));
                    }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DeepSeekMcp] Не удалось прочитать {mcpConfigPath}: {ex.Message}");
            }
        }

        // Встроенный tasks-server
        if (tasksMcp is not null && TasksServerLocator.FindTasksServerPath() is { } tasksPath)
            specs.Add(("tasks", "node", [tasksPath], new Dictionary<string, string>
            {
                ["TASKS_API_URL"] = tasksMcp.ApiUrl,
                ["TASKS_API_TOKEN"] = tasksMcp.Token,
                ["TASKS_PROJECT_ID"] = tasksMcp.ProjectId ?? "",
            }));

        return specs;
    }

    // Инструменты всех подключённых серверов в OpenAI-формате
    public void AppendToolsJson(JsonArray tools)
    {
        foreach (var client in _clients)
            foreach (var tool in client.Tools)
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = $"{ToolPrefix}{client.ServerName}__{tool.Name}",
                        ["description"] = tool.Description ?? "",
                        ["parameters"] = tool.InputSchema.DeepClone(),
                    },
                });
    }

    public bool HasAnyTools => _clients.Any(c => c.Tools.Count > 0);

    // Подключённые серверы — для session_started (как mcp_servers у Claude CLI)
    public IReadOnlyList<Protocol.McpServerInfo> ServerInfos =>
        _clients.Select(c => new Protocol.McpServerInfo(c.ServerName, "connected")).ToList();

    // mcp__<server>__<tool> → вызов на соответствующем сервере
    public async Task<(string Content, bool IsError)> CallAsync(string fullName, JsonElement args, CancellationToken ct)
    {
        var rest = fullName[ToolPrefix.Length..];
        var sep = rest.IndexOf("__", StringComparison.Ordinal);
        if (sep <= 0) return ($"Некорректное имя MCP-инструмента: {fullName}", true);
        var serverName = rest[..sep];
        var toolName = rest[(sep + 2)..];
        var client = _clients.FirstOrDefault(c => c.ServerName == serverName);
        if (client is null) return ($"MCP-сервер «{serverName}» не подключён", true);
        try
        {
            return await client.CallToolAsync(toolName, args, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return ($"Ошибка вызова {fullName}: {ex.Message}", true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();
        _clients.Clear();
        _startLock.Dispose();
    }
}
