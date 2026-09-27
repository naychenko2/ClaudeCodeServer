using System.Net;
using System.Net.Sockets;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Tests.Composition;

/// <summary>
/// Сопряжение по <c>http://localhost:5001</c>, а веб-морда открыта по <c>http://127.0.0.1:5001</c>:
/// loopback-имена при равных схеме и порте — один origin; прочие расхождения — отказ.
/// </summary>
public sealed class LoopbackOriginTests : IAsyncLifetime
{
    private const string ServerOrigin = "http://localhost:5001";
    private const string Ticket = "good-ticket";

    private readonly AgentSandbox _box = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    private sealed class Introspector(AgentTicketIntrospection grant) : IAgentTicketIntrospector
    {
        public Task<AgentTicketIntrospection?> IntrospectAsync(string ticket, CancellationToken ct) =>
            Task.FromResult(ticket == Ticket ? grant : null);
    }

    public async Task InitializeAsync()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var git = new GitService(AgentLauncherFactory.Instance);
        var files = new AgentProjectFiles(new FileService(git), _box.Policy(new AgentLimits()));
        var grant = new AgentTicketIntrospection("owner-1", "p1", _box.Project, DateTimeOffset.UtcNow.AddMinutes(5));
        _app = LocalApi.Build(new LocalApiOptions(port, ServerOrigin, "test"), files, git,
            new AgentTicketCache(new Introspector(grant)), watchers: null, NullLoggerFactory.Instance);
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _box.Dispose();
    }

    private Task<HttpResponseMessage> List(string origin)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, "/api/projects/p1/files");
        r.Headers.Add(DeviceAgentApi.TicketHeader, Ticket);
        r.Headers.Add("Origin", origin);
        return _http.SendAsync(r);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5001")]
    [InlineData("http://[::1]:5001")]
    [InlineData("http://localhost:5001")]
    public async Task ДругоеLoopbackИмя_Проходит_AllowOriginФактический(string origin)
    {
        var response = await List(origin);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(origin);
    }

    [Theory]
    [InlineData("http://evil.localhost:5001")]
    [InlineData("http://127.0.0.1:5002")]
    [InlineData("https://127.0.0.1:5001")]
    [InlineData("http://127.0.0.2:5001")]
    [InlineData("http://0x7f000001:5001")]
    [InlineData("http://user@127.0.0.1:5001")]
    [InlineData("http://192.168.1.10:5001")]
    public async Task ЧужойOrigin_403(string origin)
    {
        var response = await List(origin);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Theory]
    [InlineData("http://127.0.0.1:5001", "http://localhost:5001", true)]
    [InlineData("http://LOCALHOST:5001", "http://127.0.0.1:5001", true)]
    [InlineData("https://home.example", "https://home.example", true)]
    [InlineData("http://127.0.0.1:5001", "http://home.example:5001", false)]
    [InlineData("http://home.example:5001", "http://127.0.0.1:5001", false)]
    [InlineData("null", "http://localhost:5001", false)]
    public void SameOrigin_ЭквивалентныТолькоLoopbackИмена(string origin, string server, bool expected) =>
        LocalApiOptions.SameOrigin(origin, server).Should().Be(expected);
}
