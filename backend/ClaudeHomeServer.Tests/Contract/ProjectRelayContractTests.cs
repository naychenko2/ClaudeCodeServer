using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.DeviceAgent.Relay;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Contract;

/// <summary>
/// Контракт ретранслятора (ADR-016 §5, задача 5.1; расширение контракт-теста 4.2а): фронт
/// другого устройства ходит тем же клиентом, что к серверу, меняется только база адреса —
/// <c>api/projects/{id}/relay/</c>. Сценарий чтения идёт на серверные FilesController/GitController
/// (серверный проект) и на ретранслятор (локальный проект) — через настоящий контроллер,
/// настоящий канал исполнения поверх loopback WebSocket и настоящий RelayHandler агента на
/// одинаковых деревьях. Успешные ответы обязаны совпасть формой JSON, отказы — кодом.
/// </summary>
public sealed class ProjectRelayContractTests : IAsyncLifetime
{
    private const string Device = "dev-contract";

    private readonly string _base = Path.Combine(Path.GetTempPath(), "relay-contract-" + Guid.NewGuid().ToString("N"));
    private readonly LoopbackRelayChannel _relay = new();
    private TestWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private string _serverProjectId = null!;
    private string _localProjectId = null!;

    private string ServerRoot => Path.Combine(_base, "server", "proj");
    private string AgentAllowed => Path.Combine(_base, "agent");
    private string AgentRoot => Path.Combine(AgentAllowed, "proj");

    private sealed class AllowedRoots(string root) : IAgentRoots
    {
        public IReadOnlyList<string> Roots { get; } = [root];
    }

    public async Task InitializeAsync()
    {
        DeviceAgentApiContractTests.SeedProject(ServerRoot);
        DeviceAgentApiContractTests.SeedProject(AgentRoot);
        File.WriteAllText(Path.Combine(_base, "server", "outside.txt"), "чужое");
        File.WriteAllText(Path.Combine(_base, "agent", "outside.txt"), "чужое");

        var git = new GitService(AgentLauncherFactory.Instance);
        var relay = new RelayHandler(new AgentProjectFiles(new FileService(git), new AgentPathPolicy(new AllowedRoots(AgentAllowed))), git);
        _relay.Agent = relay.RunAsync;

        var devices = new FakeDeviceStatus();
        devices.Devices[Device] = FakeDeviceStatus.Status(Device, online: true,
            DeviceCapabilities.Exec, DeviceCapabilities.Files, DeviceCapabilities.Relay);
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = s =>
            {
                s.AddSingleton<IDeviceExecChannel>(devices);
                s.AddSingleton<IDeviceRelayChannel>(_relay);
            },
        };
        _client = _factory.CreateAuthenticatedClient();
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.LocalProjects}", new { enabled = true }))
            .EnsureSuccessStatusCode();
        _serverProjectId = await CreateAsync(ServerRoot, deviceId: null);
        _localProjectId = await CreateAsync(AgentRoot, Device);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        try { Directory.Delete(_base, recursive: true); } catch { /* временная папка */ }
        return Task.CompletedTask;
    }

    private async Task<string> CreateAsync(string root, string? deviceId)
    {
        var r = await _client.PostAsJsonAsync("/api/projects",
            new { name = "relay-" + Guid.NewGuid().ToString("N")[..8], rootPath = root, deviceId });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private sealed record Call(string Label, string Template, string Url);

    // {sha} в адресе — коммит своего дерева: у двух репозиториев разные sha
    private static readonly Call[] Scenario =
    [
        new("листинг корня", "files", "files"),
        new("листинг папки", "files", "files?path=sub"),
        new("листинг: нет папки", "files", "files?path=nope"),
        new("листинг: вне корня", "files", "files?path=..%2F"),
        new("дерево", "files/tree", "files/tree"),
        new("дерево: нет папки", "files/tree", "files/tree?path=nope"),
        new("дерево: вне корня", "files/tree", "files/tree?path=..%2F"),
        new("поиск", "files/search", "files/search?q=b"),
        new("содержимое", "files/content", "files/content?path=a.txt"),
        new("содержимое видео", "files/content", "files/content?path=clip.mp4"),
        new("содержимое: нет файла", "files/content", "files/content?path=nope.txt"),
        new("содержимое: папка", "files/content", "files/content?path=sub"),
        new("содержимое: вне корня", "files/content", "files/content?path=..%2Foutside.txt"),
        new("поток", "files/stream", "files/stream?path=clip.mp4"),
        new("поток: нет файла", "files/stream", "files/stream?path=nope.mp4"),
        new("поток: вне корня", "files/stream", "files/stream?path=..%2Foutside.txt"),
        new("дифф файла", "files/diff", "files/diff?path=a.txt"),
        new("дифф файла: без правок", "files/diff", "files/diff?path=clip.mp4"),
        new("статус", "git/status", "git/status"),
        new("git-дифф", "git/diff", "git/diff?path=sub%2Fb.txt"),
        new("git-дифф индекса", "git/diff", "git/diff?path=sub%2Fb.txt&staged=true"),
        new("git-дифф: вне корня", "git/diff", "git/diff?path=..%2Foutside.txt"),
        new("история", "git/log", "git/log?limit=10"),
        new("коммит", "git/commits/{sha}", "git/commits/{sha}"),
        new("коммит: нет такого", "git/commits/{sha}", "git/commits/0000000"),
        new("дифф в коммите", "git/commits/{sha}/diff", "git/commits/{sha}/diff?path=a.txt"),
        new("дифф в коммите: вне корня", "git/commits/{sha}/diff", "git/commits/{sha}/diff?path=..%2Foutside.txt"),
        new("файл в коммите", "git/commits/{sha}/file", "git/commits/{sha}/file?path=a.txt"),
        new("файл в коммите: вне корня", "git/commits/{sha}/file", "git/commits/{sha}/file?path=..%2Foutside.txt"),
    ];

    private sealed record Outcome(int Status, string? Shape, string? ContentType)
    {
        public override string ToString() => $"{Status} {ContentType} {Shape}";
    }

    private async Task<Outcome> SendAsync(string prefix, string root, Call call)
    {
        var response = await _client.GetAsync(prefix + call.Url.Replace("{sha}", HeadSha(root)));
        var status = (int)response.StatusCode;
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (status is < 200 or >= 300) return new Outcome(status, null, null);
        if (mediaType is null || !mediaType.Contains("json")) return new Outcome(status, null, mediaType);
        var body = await response.Content.ReadAsStringAsync();
        return new Outcome(status, DeviceAgentApiContractTests.ShapeOf(JsonDocument.Parse(body).RootElement), "json");
    }

    [Fact]
    public async Task Ретранслятор_ТеЖеКодыИФормаОтветов_ЧтоУСервера()
    {
        var mismatches = new List<string>();
        foreach (var call in Scenario)
        {
            var server = await SendAsync($"/api/projects/{_serverProjectId}/", ServerRoot, call);
            var relay = await SendAsync($"/api/projects/{_localProjectId}/{RelayProtocol.RouteSegment}/", AgentRoot, call);
            if (server.Status != relay.Status || server.ContentType != relay.ContentType
                || !DeviceAgentApiContractTests.SameShape(server.Shape, relay.Shape))
                mismatches.Add($"GET {call.Template} [{call.Label}]\n  сервер:       {server}\n  ретранслятор: {relay}");
        }

        mismatches.Should().BeEmpty("ретранслятор обязан отвечать как сервер:\n" + string.Join("\n", mismatches));
    }

    [Fact]
    public async Task Поток_БайтыФайлаЦеликом()
    {
        var bytes = await _client.GetByteArrayAsync($"/api/projects/{_localProjectId}/relay/files/stream?path=clip.mp4");

        bytes.Should().Equal(File.ReadAllBytes(Path.Combine(AgentRoot, "clip.mp4")));
    }

    [Fact]
    public void СценарийПокрываетКаждыйМаршрутРетранслятора()
    {
        Scenario.Select(c => c.Template).Distinct().Should().BeEquivalentTo(
            RelayProtocol.Routes.Select(r => r.Template).Except(RelayProtocol.RelayOnly),
            "у каждого маршрута ретранслятора, кроме своих, должен быть шаг контракт-сценария");
    }

    [Fact]
    public void МаршрутыРетранслятора_ЭтоGetМаршрутыСервера()
    {
        const string prefix = "api/projects/{projectId}/";
        var server = _factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>()?.ControllerName is "Files" or "Git")
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true)
            .Select(e => e.RoutePattern.RawText!.Trim('/'))
            .Where(t => t.StartsWith(prefix, StringComparison.Ordinal))
            .Select(t => t[prefix.Length..])
            .ToHashSet();

        RelayProtocol.Routes.Select(r => r.Template).Except(RelayProtocol.RelayOnly).Should().OnlyContain(t => server.Contains(t),
            "маршрут ретранслятора — это маршрут чтения сервера под другой базой адреса");
        RelayProtocol.RelayOnly.Should().NotIntersectWith(server);
    }

    private static string HeadSha(string root)
    {
        var psi = new ProcessStartInfo("git", ["rev-parse", "HEAD"]) { WorkingDirectory = root, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var sha = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return sha;
    }
}
