using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Tests.Composition;

/// <summary>
/// Периметр localhost-API (ADR-016 §5, план §2 п. 10) на живом Kestrel: Host против
/// DNS-rebinding, CORS только origin сервера, preflight Private Network Access, билет
/// сервера на проект маршрута — и G7 уже по HTTP.
/// </summary>
public sealed class LocalApiTests : IAsyncLifetime
{
    private const string ServerOrigin = "https://home.example";
    private const string Ticket = "good-ticket";

    private readonly AgentSandbox _box = new();
    private readonly FakeIntrospector _introspector = new();
    private readonly Clock _clock = new();
    private WebApplication _app = null!;
    private int _port;
    private HttpClient _http = null!;

    private sealed class FakeIntrospector : IAgentTicketIntrospector
    {
        public AgentTicketIntrospection? Grant;
        public int Calls;

        public Task<AgentTicketIntrospection?> IntrospectAsync(string ticket, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(ticket == Ticket ? Grant : null);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public TimeSpan Shift;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Shift;
    }

    public async Task InitializeAsync()
    {
        _introspector.Grant = new AgentTicketIntrospection("owner-1", "p1", _box.Project, DateTimeOffset.UtcNow.AddMinutes(5));
        _port = FreePort();
        var git = new GitService(AgentLauncherFactory.Instance);
        var files = new AgentProjectFiles(new FileService(git), _box.Policy(new AgentLimits { MaxReadBytes = 1024 }));
        _app = LocalApi.Build(new LocalApiOptions(_port, ServerOrigin, "test"), files, git,
            new AgentTicketCache(_introspector), watchers: null, NullLoggerFactory.Instance,
            streamTickets: new AgentStreamTickets(_clock));
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _box.Dispose();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private HttpRequestMessage Get(string url, string? ticket = Ticket, string? origin = ServerOrigin)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        if (ticket is not null) r.Headers.Add(DeviceAgentApi.TicketHeader, ticket);
        if (origin is not null) r.Headers.Add("Origin", origin);
        return r;
    }

    [Fact]
    public async Task СБилетом_ЛистингИСодержимое_ФормаКакУСервера()
    {
        var list = await _http.SendAsync(Get("/api/projects/p1/files"));
        var content = await _http.SendAsync(Get("/api/projects/p1/files/content?path=a.txt"));

        list.StatusCode.Should().Be(HttpStatusCode.OK);
        list.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(ServerOrigin);
        (await list.Content.ReadAsStringAsync()).Should().Contain("\"a.txt\"");
        var body = await content.Content.ReadAsStringAsync();
        body.Should().Contain("\"content\":\"привет\"").And.Contain("\"isBinary\":false").And.NotContain("isVideo");
    }

    [Fact]
    public async Task ЧужойHost_421_ДажеСБилетом()
    {
        var r = Get("/api/projects/p1/files");
        r.Headers.Host = $"evil.example:{_port}";

        (await _http.SendAsync(r)).StatusCode.Should().Be(HttpStatusCode.MisdirectedRequest);
    }

    [Fact]
    public async Task ЧужойOrigin_403_ИБилетНеСпрашивается()
    {
        var response = await _http.SendAsync(Get("/api/projects/p1/files", origin: "https://evil.example"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        _introspector.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Preflight_PrivateNetworkAccess_РазрешёнТолькоOriginСервера()
    {
        HttpRequestMessage Preflight(string origin)
        {
            var r = new HttpRequestMessage(HttpMethod.Options, "/api/projects/p1/files");
            r.Headers.Add("Origin", origin);
            r.Headers.Add("Access-Control-Request-Method", "GET");
            r.Headers.Add("Access-Control-Request-Headers", DeviceAgentApi.TicketHeader);
            r.Headers.Add("Access-Control-Request-Private-Network", "true");
            return r;
        }

        var ok = await _http.SendAsync(Preflight(ServerOrigin));
        var evil = await _http.SendAsync(Preflight("https://evil.example"));

        ok.StatusCode.Should().Be(HttpStatusCode.NoContent);
        ok.Headers.GetValues("Access-Control-Allow-Private-Network").Should().Equal("true");
        ok.Headers.GetValues("Access-Control-Allow-Headers").Single().Should().Contain(DeviceAgentApi.TicketHeader);
        evil.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        evil.Headers.Contains("Access-Control-Allow-Private-Network").Should().BeFalse();
    }

    [Fact]
    public async Task БезБилетаИСЧужимБилетом_401_НаДругойПроект_403()
    {
        (await _http.SendAsync(Get("/api/projects/p1/files", ticket: null))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _http.SendAsync(Get("/api/projects/p1/files", ticket: "forged"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _http.SendAsync(Get("/api/projects/p2/files"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task БилетНаПапкуВнеРазрешённыхКорней_403()
    {
        _introspector.Grant = _introspector.Grant! with { RootPath = _box.Outside };

        var response = await _http.SendAsync(Get("/api/projects/p1/files"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [SkippableFact]
    public async Task СимлинкНаружу_403_ПотолокРазмера_413()
    {
        _box.Link(Path.Combine(_box.Project, "leak.txt"), Path.Combine(_box.Outside, "secret.txt"), directory: false);
        File.WriteAllBytes(Path.Combine(_box.Project, "big.txt"), new byte[4096]);

        var leak = await _http.SendAsync(Get("/api/projects/p1/files/content?path=leak.txt"));
        var leakStream = await _http.SendAsync(Get("/api/projects/p1/files/stream?path=leak.txt"));
        var escape = await _http.SendAsync(Get("/api/projects/p1/files/content?path=..%2F..%2Foutside%2Fsecret.txt"));
        var big = await _http.SendAsync(Get("/api/projects/p1/files/content?path=big.txt"));

        leak.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        leakStream.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        escape.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        big.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await leak.Content.ReadAsStringAsync()).Should().NotContain("секрет");
    }

    private async Task<HttpResponseMessage> IssueStreamTicket(string path)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/projects/p1/" + DeviceAgentApi.StreamTicketRoute)
        {
            Content = JsonContent.Create(new { path }),
        };
        r.Headers.Add(DeviceAgentApi.TicketHeader, Ticket);
        r.Headers.Add("Origin", ServerOrigin);
        return await _http.SendAsync(r);
    }

    private async Task<string> StreamTicket(string path)
    {
        var response = await IssueStreamTicket(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("streamTicket").GetString()!;
    }

    [Fact]
    public async Task ОтдачаПотоком_ПоУзкомуБилету_СДиапазоном()
    {
        File.WriteAllBytes(Path.Combine(_box.Project, "clip.mp4"), Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray());
        var streamTicket = await StreamTicket("clip.mp4");

        var ranged = Get($"/api/projects/p1/files/stream?path=clip.mp4&{DeviceAgentApi.StreamTicketQuery}={streamTicket}", ticket: null);
        ranged.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 19);
        var response = await _http.SendAsync(ranged);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentType!.MediaType.Should().Be("video/mp4");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(Enumerable.Range(10, 10).Select(i => (byte)i));
    }

    [Fact]
    public async Task ОсновнойБилетВЗапросе_Отвергается_ДажеДляПотока()
    {
        File.WriteAllBytes(Path.Combine(_box.Project, "clip.mp4"), new byte[16]);

        var stream = await _http.SendAsync(Get($"/api/projects/p1/files/stream?path=clip.mp4&ticket={Ticket}", ticket: null));
        var streamAsNarrow = await _http.SendAsync(Get($"/api/projects/p1/files/stream?path=clip.mp4&{DeviceAgentApi.StreamTicketQuery}={Ticket}", ticket: null));
        var list = await _http.SendAsync(Get($"/api/projects/p1/files?ticket={Ticket}", ticket: null));

        stream.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        streamAsNarrow.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        list.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task УзкийБилет_ТолькоСвойПуть_ТолькоПоток_НеДольше60Секунд()
    {
        File.WriteAllText(Path.Combine(_box.Project, "b.txt"), "другой");
        var streamTicket = await StreamTicket("a.txt");
        string Stream(string path) => $"/api/projects/p1/files/stream?path={path}&{DeviceAgentApi.StreamTicketQuery}={streamTicket}";

        var own = await _http.SendAsync(Get(Stream("a.txt"), ticket: null));
        var otherPath = await _http.SendAsync(Get(Stream("b.txt"), ticket: null));
        var asHeader = await _http.SendAsync(Get("/api/projects/p1/files/content?path=a.txt", ticket: streamTicket));
        var asQueryOnContent = await _http.SendAsync(Get(
            $"/api/projects/p1/files/content?path=a.txt&{DeviceAgentApi.StreamTicketQuery}={streamTicket}", ticket: null));
        _clock.Shift = DeviceAgentApi.StreamTicketLifetime + TimeSpan.FromSeconds(1);
        var expired = await _http.SendAsync(Get(Stream("a.txt"), ticket: null));

        own.StatusCode.Should().Be(HttpStatusCode.OK);
        (await own.Content.ReadAsStringAsync()).Should().Be("привет");
        otherPath.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        asHeader.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        asQueryOnContent.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        expired.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        DeviceAgentApi.StreamTicketLifetime.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
    }

    [SkippableFact]
    public async Task УзкийБилет_НаПутьНаружу_НеВыдаётся_БезОсновногоБилета_401()
    {
        _box.Link(Path.Combine(_box.Project, "leak.txt"), Path.Combine(_box.Outside, "secret.txt"), directory: false);

        (await IssueStreamTicket("leak.txt")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var anonymous = new HttpRequestMessage(HttpMethod.Post, "/api/projects/p1/" + DeviceAgentApi.StreamTicketRoute)
        {
            Content = JsonContent.Create(new { path = "a.txt" }),
        };
        (await _http.SendAsync(anonymous)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ЗаписьПереименованиеУдаление_ЧерезШов()
    {
        async Task<HttpStatusCode> Send(HttpMethod method, string url, object? body = null)
        {
            var r = new HttpRequestMessage(method, url);
            r.Headers.Add(DeviceAgentApi.TicketHeader, Ticket);
            r.Headers.Add("Origin", ServerOrigin);
            if (body is not null) r.Content = JsonContent.Create(body);
            return (await _http.SendAsync(r)).StatusCode;
        }

        (await Send(HttpMethod.Post, "/api/projects/p1/files/create", new { path = "n.txt", content = "x" })).Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Post, "/api/projects/p1/files/create", new { path = "n.txt" })).Should().Be(HttpStatusCode.Conflict);
        (await Send(HttpMethod.Put, "/api/projects/p1/files/content?path=n.txt", new { content = "y" })).Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Post, "/api/projects/p1/files/rename", new { oldPath = "n.txt", newPath = "m.txt" })).Should().Be(HttpStatusCode.OK);
        File.ReadAllText(Path.Combine(_box.Project, "m.txt")).Should().Be("y");
        (await Send(HttpMethod.Delete, "/api/projects/p1/files?path=m.txt")).Should().Be(HttpStatusCode.NoContent);
        File.Exists(Path.Combine(_box.Project, "m.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Здоровье_БезБилета_ДляОбнаруженияАгента()
    {
        var response = await _http.SendAsync(Get("/api/agent/health", ticket: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("ai-home-agent");
    }

    [Fact]
    public async Task Git_СтатусДиффИОткат_ТемЖеGitService()
    {
        void Git(params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = _box.Project, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            p.ExitCode.Should().Be(0, string.Join(' ', args));
        }
        Git("init", "-q");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "init");
        File.WriteAllText(Path.Combine(_box.Project, "a.txt"), "изменено");

        var statusResponse = await _http.SendAsync(Get("/api/projects/p1/git/status"));
        var status = $"{(int)statusResponse.StatusCode} " + await statusResponse.Content.ReadAsStringAsync();
        var diff = await (await _http.SendAsync(Get("/api/projects/p1/files/diff?path=a.txt"))).Content.ReadAsStringAsync();
        var revert = new HttpRequestMessage(HttpMethod.Post, "/api/projects/p1/files/revert") { Content = JsonContent.Create(new { path = "a.txt" }) };
        revert.Headers.Add(DeviceAgentApi.TicketHeader, Ticket);

        status.Should().Contain("a.txt");
        diff.Should().Contain("изменено");
        (await _http.SendAsync(revert)).StatusCode.Should().Be(HttpStatusCode.OK);
        File.ReadAllText(Path.Combine(_box.Project, "a.txt")).Should().Be("привет");
    }
}
