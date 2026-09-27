using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json.Nodes;
using ClaudeHomeServer.DeviceAgent.Hands;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Tests.Hands;

/// <summary>Каталог данных агента с компонентом рук и архив компонента для установки.</summary>
internal sealed class HandsFixture : IDisposable
{
    public HandsFixture()
    {
        DataDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agent-hands-" + Guid.NewGuid().ToString("N")[..10])).FullName;
        Component = new HandsComponent(DataDirectory);
    }

    public string DataDirectory { get; }
    public HandsComponent Component { get; }

    /// <summary>Архив компонента: мост в корне, как его публикует выкатка.</summary>
    public (string Path, HandsOffer Offer) Archive(string version = "1.2.3", byte[]? bridge = null, string entry = HandsFiles.BridgeExe)
    {
        var path = Path.Combine(DataDirectory, "hands-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry(entry).Open()) s.Write(bridge ?? "MZ fake bridge"u8);
            using (var l = zip.CreateEntry("HandsBridge.LICENSE.txt").Open()) l.Write("MIT"u8);
        }
        var offer = new HandsOffer(version, $"{version}/win-x64/{Path.GetFileName(path)}",
            HandsComponent.FileSha256(path), new FileInfo(path).Length);
        return (path, offer);
    }

    /// <summary>Поставить компонент как это сделала бы <c>hands enable</c>.</summary>
    public HandsFixture Installed()
    {
        var (path, offer) = Archive();
        Component.Install(path, offer);
        return this;
    }

    public HandsRuntime Runtime(IHandsMachineLock? machineLock = null, RecordingSink? sink = null, HandsRegistry? registry = null) =>
        new(Component, machineLock ?? new InProcessHandsMachineLock(), registry ?? new HandsRegistry(), sink);

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
        try { Directory.Delete(DataDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
