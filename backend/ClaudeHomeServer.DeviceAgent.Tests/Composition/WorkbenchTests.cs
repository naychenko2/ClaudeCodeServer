using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Tests.Composition;

/// <summary>
/// Рабочие подсистемы проекта в агенте (ADR-016, задача 4.3) на живом Kestrel: терминал по
/// билету хаба, дев-серверы и превью на своём loopback-порту, навыки и агенты проекта,
/// вложения чата — и граница машины (симлинки наружу) уже через них.
/// </summary>
public sealed class WorkbenchTests : IAsyncLifetime
{
    private const string ServerOrigin = "https://home.example";
    private const string Ticket = "good-ticket";
    private const int WriteLimit = 1024 * 1024;

    private readonly AgentSandbox _box = new();
    private readonly AgentLauncherFactory _launchers = new();
    private WebApplication _app = null!;
    private int _port;
    private int _previewPort;
    private HttpClient _http = null!;

    private sealed class Introspector(string root) : IAgentTicketIntrospector
    {
        public Task<AgentTicketIntrospection?> IntrospectAsync(string ticket, CancellationToken ct) =>
            Task.FromResult(ticket == Ticket
                ? new AgentTicketIntrospection("owner-1", "p1", root, DateTimeOffset.UtcNow.AddMinutes(5))
                : null);
    }

    public async Task InitializeAsync()
    {
        _port = FreePort();
        _previewPort = FreePort();
        var git = new GitService(_launchers);
        var files = new AgentProjectFiles(new FileService(git), _box.Policy(new AgentLimits { MaxWriteBytes = WriteLimit }));
        _app = LocalApi.Build(new LocalApiOptions(_port, ServerOrigin, "test"), files, git,
            new AgentTicketCache(new Introspector(_box.Project)), watchers: null, NullLoggerFactory.Instance,
            workbench: new AgentWorkbench(Path.Combine(_box.Base, "data"), Path.Combine(_box.Base, "profile"), _previewPort, _launchers));
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
    }

    public async Task DisposeAsync()
    {
        _launchers.KillAll();
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

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null, string? ticket = Ticket)
    {
        var r = new HttpRequestMessage(method, url);
        if (ticket is not null) r.Headers.Add(DeviceAgentApi.TicketHeader, ticket);
        r.Headers.Add("Origin", ServerOrigin);
        if (body is not null) r.Content = JsonContent.Create(body);
        return await _http.SendAsync(r);
    }

    private async Task<JsonElement> Json(HttpMethod method, string url, object? body = null)
    {
        var response = await Send(method, url, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HubConnection> ConnectHub(string? token = null)
    {
        token ??= (await Json(HttpMethod.Post, "/api/projects/p1/" + DeviceAgentApi.HubTicketRoute)).GetProperty("hubTicket").GetString();
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_port}{DeviceAgentApi.HubPath}", o => o.AccessTokenProvider = () => Task.FromResult(token))
            .Build();
        await connection.StartAsync();
        return connection;
    }

    // ---------- терминал ----------

    [Fact]
    public async Task Терминал_ПоБилетуХаба_ВПапкеПроекта_ГруппойПроцессов()
    {
        await using var hub = await ConnectHub();
        var output = new System.Text.StringBuilder();
        var seen = new TaskCompletionSource();
        hub.On<JsonElement>("message", m =>
        {
            if (m.TryGetProperty("data", out var data))
                lock (output)
                {
                    output.Append(data.GetString());
                    if (output.ToString().Contains("agent-42")) seen.TrySetResult();
                }
        });

        var terminal = await hub.InvokeAsync<JsonElement>("CreateTerminal", "p1", 80, 24, null);
        var id = terminal.GetProperty("id").GetString()!;
        _launchers.TrackedCount.Should().Be(1, "терминал живёт группой/Job Object, как ход");
        await hub.InvokeAsync("TerminalInput", id, "pwd; echo agent-$((40+2))\n");
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(20));

        lock (output) output.ToString().Should().Contain(AgentPathPolicy.RealPath(_box.Project));
        (await hub.InvokeAsync<List<JsonElement>>("ListTerminals", "p1")).Should().ContainSingle();
        await hub.InvokeAsync("StopTerminal", id);
        _launchers.TrackedCount.Should().Be(0);
    }

    [Fact]
    public async Task Хаб_БезБилетаИСОсновнымБилетом_Отказ_ЧужойПроект_Отказ()
    {
        var noTicket = () => ConnectHub(token: "");
        var mainTicket = () => ConnectHub(token: Ticket);
        await noTicket.Should().ThrowAsync<HttpRequestException>();
        await mainTicket.Should().ThrowAsync<HttpRequestException>("основной билет в URL не годится нигде");

        await using var hub = await ConnectHub();
        var foreign = () => hub.InvokeAsync<JsonElement>("CreateTerminal", "p2", 80, 24, null);
        await foreign.Should().ThrowAsync<HubException>();
        _launchers.TrackedCount.Should().Be(0);
    }

    [Fact]
    public async Task Хаб_ЧужойOriginИЧужойHost_Отказ_ДоБилета()
    {
        var token = (await Json(HttpMethod.Post, "/api/projects/p1/" + DeviceAgentApi.HubTicketRoute)).GetProperty("hubTicket").GetString();
        HttpRequestMessage Negotiate(string? origin = null, string? host = null)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, $"{DeviceAgentApi.HubPath}/negotiate?negotiateVersion=1");
            r.Headers.Add("Authorization", "Bearer " + token);
            if (origin is not null) r.Headers.Add("Origin", origin);
            if (host is not null) r.Headers.Host = host;
            return r;
        }

        (await _http.SendAsync(Negotiate(ServerOrigin))).StatusCode.Should().Be(HttpStatusCode.OK, "контроль: свой origin с билетом проходит");
        (await _http.SendAsync(Negotiate("https://evil.example"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _http.SendAsync(Negotiate(ServerOrigin, host: $"evil.example:{_port}"))).StatusCode
            .Should().Be(HttpStatusCode.MisdirectedRequest, "DNS-rebinding на хаб");

        var preflight = new HttpRequestMessage(HttpMethod.Options, $"{DeviceAgentApi.HubPath}/negotiate");
        preflight.Headers.Add("Origin", "https://evil.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        (await _http.SendAsync(preflight)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------- сервисы и превью ----------

    [Fact]
    public async Task Превью_САгента_ПоБилетуИКуке_НаСвоёмПорту()
    {
        var devPort = FreePort();
        await using var dev = WebApplication.CreateSlimBuilder().Build();
        dev.Urls.Add($"http://127.0.0.1:{devPort}");
        dev.Run(ctx => ctx.Response.WriteAsync($"dev:{ctx.Request.Path}{ctx.Request.QueryString}|cookie:{ctx.Request.Headers.Cookie}|auth:{ctx.Request.Headers.Authorization}"));
        await dev.StartAsync();

        await Json(HttpMethod.Put, "/api/projects/p1/launch-config", new
        {
            configurations = new[] { new { name = "web", runtimeExecutable = "node", port = devPort } },
        });
        var services = await Json(HttpMethod.Get, "/api/projects/p1/services");
        var web = services.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("source").GetString() == "launch.json");
        web.GetProperty("status").GetString().Should().Be("external", "порт конфигурации слушается — сервис поднят снаружи");
        await Json(HttpMethod.Post, "/api/projects/p1/preview/active-external", new { serviceId = web.GetProperty("id").GetString() });
        var issued = await Json(HttpMethod.Post, "/api/projects/p1/" + DeviceAgentApi.PreviewTicketRoute);
        var url = new Uri(issued.GetProperty("url").GetString()!);
        url.Port.Should().Be(_previewPort);

        using var browser = new HttpClient(new HttpClientHandler { UseCookies = false });
        var first = await browser.GetAsync(new Uri(url, "page?x=1&" + DeviceAgentApi.PreviewTicketQuery + "=" + issued.GetProperty("previewTicket").GetString()));
        var body = await first.Content.ReadAsStringAsync();
        first.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Be("dev:/page?x=1|cookie:|auth:").And.NotContain(issued.GetProperty("previewTicket").GetString()!);
        var cookie = first.Headers.GetValues("Set-Cookie").Single();
        cookie.Should().Contain(DeviceAgentApi.PreviewCookie).And.Contain("httponly").And.Contain("samesite=none")
            .And.Contain("secure").And.Contain("Partitioned").And.Contain("path=/preview/p1/");

        // Браузер прикладывает к loopback куки всех сервисов на 127.0.0.1 — дев-серверу не уходит ни одна
        var sub = new HttpRequestMessage(HttpMethod.Get, new Uri(url, "asset.js"));
        sub.Headers.Add("Cookie", "dify_session=s3cr3t; " + cookie.Split(';')[0] + "; admin_token=adm1n; other=1");
        sub.Headers.Add("Authorization", "Bearer foreign-token");
        var subBody = await (await browser.SendAsync(sub)).Content.ReadAsStringAsync();
        subBody.Should().Be("dev:/asset.js|cookie:|auth:", "подресурс идёт по куке, а дев-серверу не уходит ни одна кука и учётка");

        (await browser.GetAsync(new Uri(url, "asset.js"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await browser.GetAsync($"http://127.0.0.1:{_previewPort}/api/projects/p1/files")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "на порту превью API нет вовсе");
        (await Send(HttpMethod.Get, "/preview/p1/")).StatusCode.Should().Be(HttpStatusCode.NotFound, "превью — только на своём порту");
    }

    [Fact]
    public async Task Превью_ЧужойOriginИЧужойHost_Отказ_БилетДругогоПроекта_401()
    {
        var issued = await Json(HttpMethod.Post, "/api/projects/p1/" + DeviceAgentApi.PreviewTicketRoute);
        var ticket = issued.GetProperty("previewTicket").GetString();
        using var browser = new HttpClient();

        var evil = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_previewPort}/preview/p1/?{DeviceAgentApi.PreviewTicketQuery}={ticket}");
        evil.Headers.Add("Origin", "https://evil.example");
        (await browser.SendAsync(evil)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var rebinding = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_previewPort}/preview/p1/?{DeviceAgentApi.PreviewTicketQuery}={ticket}");
        rebinding.Headers.Host = $"evil.example:{_previewPort}";
        (await browser.SendAsync(rebinding)).StatusCode.Should().Be(HttpStatusCode.MisdirectedRequest);

        (await browser.GetAsync($"http://127.0.0.1:{_previewPort}/preview/p2/?{DeviceAgentApi.PreviewTicketQuery}={ticket}"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await browser.GetAsync($"http://127.0.0.1:{_previewPort}/preview/p1/?{DeviceAgentApi.PreviewTicketQuery}={Ticket}"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "основной билет не годится и для превью");
    }

    [SkippableFact]
    public async Task Сервисы_МанифестПоСсылкеНаружу_НеВидны_КаталогНаружу_Отказ()
    {
        File.WriteAllText(Path.Combine(_box.Outside, "package.json"), """{ "scripts": { "dev": "evil" } }""");
        Directory.CreateDirectory(Path.Combine(_box.Project, "sub"));
        _box.Link(Path.Combine(_box.Project, "sub", "package.json"), Path.Combine(_box.Outside, "package.json"), directory: false);
        _box.Link(Path.Combine(_box.Project, "out"), _box.Outside, directory: true);
        File.WriteAllText(Path.Combine(_box.Project, "package.json"), """{ "scripts": { "dev": "vite" } }""");

        var services = await Json(HttpMethod.Get, "/api/projects/p1/services");
        var names = services.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToList();
        names.Should().Contain("dev").And.NotContain(n => n!.Contains("sub")).And.NotContain(n => n!.Contains("out"));

        var start = await Json(HttpMethod.Post, "/api/projects/p1/preview/start", new { command = "node", serviceId = "escape", cwd = "out" });
        start.GetProperty("status").GetString().Should().Be("error");
        start.GetProperty("error").GetString().Should().Be("Недопустимый рабочий каталог");
        _launchers.TrackedCount.Should().Be(0, "процесс в каталоге за корнем не запускается вовсе");
    }

    // ---------- навыки и агенты проекта ----------

    [SkippableFact]
    public async Task Навыки_АгентыПроекта_ЧитаютсяИПишутсяАгентом_СсылкаНаружу_НеЧитается()
    {
        Directory.CreateDirectory(Path.Combine(_box.Project, ".claude", "agents"));
        File.WriteAllText(Path.Combine(_box.Project, ".claude", "agents", "scout.md"), "---\nname: scout\ndescription: разведка\n---\nтело");
        File.WriteAllText(Path.Combine(_box.Outside, "secret.md"), "---\nname: leak\n---\nсекрет");
        _box.Link(Path.Combine(_box.Project, ".claude", "agents", "leak.md"), Path.Combine(_box.Outside, "secret.md"), directory: false);

        var list = await Json(HttpMethod.Get, "/api/projects/p1/skills");
        list.GetProperty("agents").EnumerateArray().Select(a => a.GetProperty("fileName").GetString()).Should().Equal("scout");
        (await Send(HttpMethod.Get, "/api/projects/p1/agents/leak")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(HttpMethod.Get, "/api/projects/p1/agents/scout")).GetProperty("content").GetString().Should().EndWith("тело");

        await Json(HttpMethod.Post, "/api/projects/p1/agents", new { name = "helper", content = "помощник" });
        File.ReadAllText(Path.Combine(_box.Project, ".claude", "agents", "helper.md")).Should().Be("помощник");
    }

    // ---------- вложения ----------

    [Fact]
    public async Task Вложение_КладётсяНаУстройство_ВCcAttachments_СИгноромGit()
    {
        var init = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", ["init", "-q", _box.Project]))!;
        await init.WaitForExitAsync();

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("данные"u8.ToArray()), "file", "../../отчёт.txt");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/projects/p1/" + DeviceAgentApi.AttachmentsRoute) { Content = form };
        request.Headers.Add(DeviceAgentApi.TicketHeader, Ticket);
        var response = await _http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var rel = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString()!;
        rel.Should().MatchRegex(@"^\.cc-attachments/[0-9a-f]{32}/отчёт\.txt$");
        File.ReadAllText(Path.Combine(_box.Project, rel)).Should().Be("данные");
        File.ReadAllText(Path.Combine(_box.Project, ".git", "info", "exclude")).Should().Contain(".cc-attachments/");

        var anonymous = new HttpRequestMessage(HttpMethod.Post, "/api/projects/p1/" + DeviceAgentApi.AttachmentsRoute) { Content = form };
        (await _http.SendAsync(anonymous)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task Вложение_CcAttachmentsСсылкаНаружу_Отказ_ВнеКорняПусто()
    {
        var outsideAttachments = Path.Combine(_box.Outside, "att");
        Directory.CreateDirectory(outsideAttachments);
        _box.Link(Path.Combine(_box.Project, ".cc-attachments"), outsideAttachments, directory: true);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("данные"u8.ToArray()), "file", "отчёт.txt");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/projects/p1/" + DeviceAgentApi.AttachmentsRoute) { Content = form };
        request.Headers.Add(DeviceAgentApi.TicketHeader, Ticket);
        var response = await _http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        Directory.EnumerateFileSystemEntries(outsideAttachments, "*", SearchOption.AllDirectories)
            .Should().BeEmpty("за корнем проекта не появилось ничего");
    }

    [Fact]
    public async Task Вложение_БольшеПотолкаПоContentLength_413_ДоЧтенияТела()
    {
        // Сырой сокет: объявляем тело больше потолка, шлём только начало и ждём ответа.
        // Отказ обязан прийти, не дожидаясь остального тела — его может и не быть
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _port);
        var stream = tcp.GetStream();
        var head = $"POST /api/projects/p1/{DeviceAgentApi.AttachmentsRoute} HTTP/1.1\r\n" +
                   $"Host: 127.0.0.1:{_port}\r\n" +
                   $"{DeviceAgentApi.TicketHeader}: {Ticket}\r\n" +
                   "Content-Type: multipart/form-data; boundary=b\r\n" +
                   $"Content-Length: {WriteLimit * 4}\r\n\r\n" +
                   "--b\r\nContent-Disposition: form-data; name=\"file\"; filename=\"big.bin\"\r\n\r\n";
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(head));
        await stream.WriteAsync(new byte[64 * 1024]);

        using var reader = new StreamReader(stream);
        var status = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        status.Should().StartWith("HTTP/1.1 413");
        Directory.Exists(Path.Combine(_box.Project, ".cc-attachments")).Should().BeFalse();
    }

    [Fact]
    public async Task Вложение_ФайлБольшеПотолка_413_НичегоНеЗаписано()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[WriteLimit + 1]), "file", "big.bin");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/projects/p1/" + DeviceAgentApi.AttachmentsRoute) { Content = form };
        request.Headers.Add(DeviceAgentApi.TicketHeader, Ticket);

        (await _http.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        Directory.Exists(Path.Combine(_box.Project, ".cc-attachments")).Should().BeFalse();
    }
}
