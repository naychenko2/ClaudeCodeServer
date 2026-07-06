using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Llm.DeepSeek;

public sealed record McpToolInfo(string Name, string? Description, JsonObject InputSchema);

// Общий контракт MCP-клиента (stdio или http): имя сервера, его инструменты и вызов
public interface IMcpClient : IAsyncDisposable
{
    string ServerName { get; }
    IReadOnlyList<McpToolInfo> Tools { get; }
    Task<(string Content, bool IsError)> CallToolAsync(string toolName, JsonElement args, CancellationToken ct);
}

// Спецификация MCP-сервера из конфига: stdio (command/args/env) либо http (url/headers)
public sealed record McpServerSpec(string Name, string? Command, List<string> Args,
    Dictionary<string, string> Env, string? Url, Dictionary<string, string> Headers);

// Минимальный MCP-клиент по stdio (JSON-RPC 2.0, одно сообщение на строку):
// initialize → notifications/initialized → tools/list → tools/call.
// Достаточно для tasks-server, Dify и типовых пользовательских серверов из .mcp.json.
public sealed class McpStdioClient : IMcpClient
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

// MCP-клиент по HTTP (Streamable HTTP transport, JSON-RPC 2.0): POST на URL,
// ответ — application/json ЛИБО text/event-stream (SSE). Сессия — заголовок Mcp-Session-Id.
// Для удалённых серверов вроде fal.ai (генерация медиа) с Bearer-авторизацией.
public sealed class McpHttpClient : IMcpClient
{
    public string ServerName { get; }
    public IReadOnlyList<McpToolInfo> Tools { get; private set; } = [];

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly Dictionary<string, string> _headers;
    private string? _sessionId;
    private long _nextId;

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(5);

    private McpHttpClient(string serverName, HttpClient http, string url, Dictionary<string, string> headers)
    {
        ServerName = serverName;
        _http = http;
        _url = url;
        _headers = headers;
    }

    public static async Task<McpHttpClient> StartAsync(string serverName, HttpClient http, string url,
        Dictionary<string, string> headers, CancellationToken ct)
    {
        var client = new McpHttpClient(serverName, http, url, headers);
        using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startCts.CancelAfter(StartupTimeout);

        await client.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "claude-home-deepseek", ["version"] = "1.0" },
        }, startCts.Token);
        await client.NotifyAsync("notifications/initialized", startCts.Token);

        var toolsResp = await client.RequestAsync("tools/list", new JsonObject(), startCts.Token);
        client.Tools = McpJson.ParseTools(toolsResp);
        return client;
    }

    public async Task<(string Content, bool IsError)> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        callCts.CancelAfter(CallTimeout);
        var result = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = JsonNode.Parse(args.GetRawText()),
        }, callCts.Token);
        return McpJson.ParseToolResult(result);
    }

    private async Task<JsonElement> RequestAsync(string method, JsonObject @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = @params,
        };
        using var resp = await SendAsync(body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"MCP {ServerName} HTTP {(int)resp.StatusCode}: {err}");
        }
        CaptureSession(resp);

        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
        var payload = await resp.Content.ReadAsStringAsync(ct);
        // Ответ — либо JSON, либо SSE (несколько data:-строк, ищем сообщение с нашим id + result/error)
        var json = mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase)
            ? McpJson.ExtractSseResponse(payload, id)
            : payload;
        if (json is null) throw new InvalidOperationException($"MCP {ServerName}: пустой ответ на {method}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var errEl))
            throw new InvalidOperationException(
                $"MCP {ServerName}: {(errEl.TryGetProperty("message", out var m) ? m.GetString() : errEl.GetRawText())}");
        return root.TryGetProperty("result", out var res) ? res.Clone() : default;
    }

    private async Task NotifyAsync(string method, CancellationToken ct)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        using var resp = await SendAsync(body, ct);
        CaptureSession(resp);
        // Уведомление: сервер обычно отвечает 202 Accepted без тела — тело не читаем
    }

    private Task<HttpResponseMessage> SendAsync(JsonObject body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        foreach (var (k, v) in _headers) req.Headers.TryAddWithoutValidation(k, v);
        if (_sessionId is not null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        return _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
    }

    private void CaptureSession(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var vals))
            _sessionId = vals.FirstOrDefault() ?? _sessionId;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // HttpClient общий, процессов нет
}

// Общие хелперы разбора MCP-ответов (tools/list, tools/call, SSE)
internal static class McpJson
{
    public static List<McpToolInfo> ParseTools(JsonElement result)
    {
        var tools = new List<McpToolInfo>();
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("tools", out var arr) && arr.ValueKind == JsonValueKind.Array)
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
        return tools;
    }

    public static (string Content, bool IsError) ParseToolResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return ("", false);
        var isError = result.TryGetProperty("isError", out var ie) && ie.ValueKind == JsonValueKind.True;
        var sb = new System.Text.StringBuilder();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    sb.AppendLine(text.GetString());
                else if (block.TryGetProperty("type", out var bt))
                    // Не-текстовые блоки (image/resource) отдаём как есть — модель поймёт по JSON
                    sb.AppendLine(block.GetRawText());
                else sb.AppendLine("[блок содержимого]");
            }
        return (sb.ToString().TrimEnd(), isError);
    }

    // Из SSE-потока (строки data: {...}) достаём JSON-RPC ответ с нужным id
    public static string? ExtractSseResponse(string payload, long id)
    {
        string? last = null;
        foreach (var raw in payload.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("id", out var idEl)
                    && idEl.TryGetInt64(out var mid) && mid == id)
                    return data; // наш ответ
                last = data;
            }
            catch (JsonException) { /* не JSON-строка SSE — пропускаем */ }
        }
        return last; // на случай, если id не совпал — вернём последнее сообщение
    }
}

// MCP-серверы DeepSeek-сессии: серверы из базового конфига (McpConfigPath, с инжекцией
// Dify dataset id) + встроенный tasks-server. Поддержка stdio и http (type: http/url).
// Стартуют лениво при первом ходе; инструменты попадают в tool-цикл под именами mcp__<server>__<tool>.
public sealed class DeepSeekMcpManager(IReadOnlyList<string?> mcpConfigPaths, TasksMcpContext? tasksMcp,
    Func<string?> difyDatasetId, IHttpClientFactory? httpFactory = null) : IAsyncDisposable
{
    public const string ToolPrefix = "mcp__";

    private readonly List<IMcpClient> _clients = [];
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private bool _started;

    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_started) return;
        await _startLock.WaitAsync(ct);
        try
        {
            if (_started) return;
            foreach (var spec in BuildServerSpecs())
            {
                try
                {
                    if (spec.Url is { Length: > 0 } url)
                    {
                        var http = httpFactory?.CreateClient("deepseek-mcp") ?? new HttpClient();
                        http.Timeout = Timeout.InfiniteTimeSpan; // таймауты — на уровне запросов
                        _clients.Add(await McpHttpClient.StartAsync(spec.Name, http, url, spec.Headers, ct));
                    }
                    else if (spec.Command is { Length: > 0 } command)
                    {
                        _clients.Add(await McpStdioClient.StartAsync(spec.Name, command, spec.Args, spec.Env, ct));
                    }
                }
                catch (Exception ex)
                {
                    // Недоступный сервер не должен блокировать сессию — работаем без него
                    Console.Error.WriteLine($"[DeepSeekMcp] Сервер «{spec.Name}» не подключился: {ex.Message}");
                }
            }
            _started = true;
        }
        finally { _startLock.Release(); }
    }

    private List<McpServerSpec> BuildServerSpecs()
    {
        // По имени — дедуп между несколькими конфигами (последний источник побеждает)
        var byName = new Dictionary<string, McpServerSpec>(StringComparer.OrdinalIgnoreCase);
        var datasetId = difyDatasetId();

        foreach (var path in mcpConfigPaths)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
            try
            {
                var doc = JsonNode.Parse(File.ReadAllText(path));
                if (doc?["mcpServers"] is not JsonObject servers) continue;
                foreach (var (name, node) in servers)
                {
                    if (node is not JsonObject obj) continue;
                    var type = obj["type"]?.GetValue<string>();
                    var url = obj["url"]?.GetValue<string>();

                    // HTTP/SSE-сервер (type: http/sse или задан url)
                    if (url is { Length: > 0 } || type is "http" or "sse")
                    {
                        if (url is not { Length: > 0 }) continue;
                        var headers = new Dictionary<string, string>();
                        if (obj["headers"] is JsonObject h)
                            foreach (var (k, v) in h)
                                if (v is not null) headers[k] = v.GetValue<string>();
                        byName[name] = new McpServerSpec(name, null, [], new(), url, headers);
                        continue;
                    }

                    // stdio-сервер
                    if (obj["command"]?.GetValue<string>() is not { Length: > 0 } command) continue;
                    var args = (obj["args"] as JsonArray)?
                        .Select(a => a?.GetValue<string>() ?? "").ToList() ?? [];
                    var env = new Dictionary<string, string>();
                    if (obj["env"] is JsonObject envObj)
                        foreach (var (k, v) in envObj)
                            if (v is not null) env[k] = v.GetValue<string>();
                    // Dify: инжектим dataset id воркспейса (как BuildTurnMcpConfig у Claude)
                    if (name == "dify" && !string.IsNullOrEmpty(datasetId))
                    {
                        env["DIFY_DEFAULT_DATASET_ID"] = datasetId;
                        env["DIFY_SEARCH_ONLY"] = "true";
                    }
                    byName[name] = new McpServerSpec(name, command, args, env, null, new());
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DeepSeekMcp] Не удалось прочитать {path}: {ex.Message}");
            }
        }

        var specs = byName.Values.ToList();

        // Встроенный tasks-server
        if (tasksMcp is not null && TasksServerLocator.FindTasksServerPath() is { } tasksPath)
            specs.Add(new McpServerSpec("tasks", "node", [tasksPath], new Dictionary<string, string>
            {
                ["TASKS_API_URL"] = tasksMcp.ApiUrl,
                ["TASKS_API_TOKEN"] = tasksMcp.Token,
                ["TASKS_PROJECT_ID"] = tasksMcp.ProjectId ?? "",
            }, null, new()));

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
