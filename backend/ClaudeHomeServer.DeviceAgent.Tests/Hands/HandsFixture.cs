using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ClaudeHomeServer.DeviceAgent.Hands;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Tests.Hands;

/// <summary>Каталог версии агента: мост рук в нём лежит или нет.</summary>
internal sealed class HandsFixture : IDisposable
{
    public HandsFixture()
    {
        AgentDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agent-hands-" + Guid.NewGuid().ToString("N")[..10])).FullName;
        Component = new HandsComponent(AgentDirectory);
    }

    public string AgentDirectory { get; }
    public HandsComponent Component { get; }

    /// <summary>Положить мост в каталог версии, как его раскладывает архив агента.</summary>
    public HandsFixture WithBridge()
    {
        File.WriteAllBytes(Path.Combine(AgentDirectory, HandsFiles.BridgeExe), "MZ fake bridge"u8.ToArray());
        return this;
    }

    /// <summary>Корень профилей браузерной руки — как <c>AgentPaths.BrowserProfilesRoot</c> у боевого агента.</summary>
    public string BrowserProfilesRoot => Path.Combine(AgentDirectory, "data", "browser-profiles");

    public HandsRuntime Runtime(IHandsMachineLock? machineLock = null, RecordingSink? sink = null, HandsRegistry? registry = null,
        bool browserProfiles = false) =>
        new(Component, machineLock ?? new InProcessHandsMachineLock(), registry ?? new HandsRegistry(), sink,
            browserProfiles ? BrowserProfilesRoot : null);

    /// <summary>MCP-конфиг хода, каким его шлёт сервер после санитизации.</summary>
    public static string McpConfig(bool vision = true, bool hands = true)
    {
        var servers = new JsonObject
        {
            ["tasks"] = new JsonObject { ["type"] = "http", ["url"] = DeviceExecPlaceholders.Sidecar + "/mcp/tasks" },
        };
        if (hands)
            servers[DeviceExecPlaceholders.HandsServerName] = new JsonObject
            {
                ["type"] = DeviceExecPlaceholders.Hands,
                [DeviceExecPlaceholders.HandsVisionField] = vision,
            };
        return new JsonObject { ["mcpServers"] = servers }.ToJsonString();
    }

    public static DeviceExecFile McpFile(string content) => new("f1", "mcp.json", content);

    /// <summary>Аргументы хода с руками, как их собирает сервер: режим прав стоит явно.</summary>
    public static readonly IReadOnlyList<string> HandsArgs =
        ["-p", "--output-format", "stream-json", "--permission-mode", "acceptEdits", "--mcp-config", DeviceExecPlaceholders.File("f1")];

    public void Dispose()
    {
        try { Directory.Delete(AgentDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Донесения агента о руках — как их увидел бы сервер.</summary>
internal sealed class RecordingSink : IHandsStatusSink
{
    private readonly SemaphoreSlim _arrived = new(0);

    public ConcurrentQueue<DeviceHandsReport> Reports { get; } = new();

    public Task ReportAsync(DeviceHandsReport report, CancellationToken ct)
    {
        Reports.Enqueue(report);
        _arrived.Release();
        return Task.CompletedTask;
    }

    /// <summary>Ждёт донесения по событию прихода, а не опросом по таймеру.</summary>
    public async Task<DeviceHandsReport> WaitAsync(Func<DeviceHandsReport, bool> match, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            if (Reports.FirstOrDefault(match) is { } found) return found;
            try { await _arrived.WaitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("донесение о руках не пришло: " + string.Join(", ", Reports.Select(r => r.State)));
            }
        }
    }
}
