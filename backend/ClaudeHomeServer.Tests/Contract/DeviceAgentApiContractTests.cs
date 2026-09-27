using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.ProjectServices;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Contract;

/// <summary>
/// Контракт маршрутов проекта (ADR-016, задачи 4.2а и 4.3): фронт ходит одним клиентом то к
/// серверу, то к localhost-API агента устройства. Один и тот же сценарий идёт на серверные
/// FilesController/GitController/PreviewController/SkillsController (живой хост) и на
/// <see cref="LocalApi"/> (живой Kestrel) на одинаковых деревьях проекта: успешные ответы
/// обязаны совпасть набором полей и типами JSON, отказы — кодом. Формы ответов — общие типы из <c>Protocol/ProjectFilesApiContract.cs</c>;
/// тест добивает то, что компилятор не видит: анонимный объект на одной стороне, коды, пустое
/// тело против тела.
///
/// Отказ «слишком большой» (413) есть только у агента — серверные маршруты читают и пишут
/// без потолка (тело режет хост); сам отказ агента проверяет LocalApiTests в тестах агента.
/// </summary>
public sealed class DeviceAgentApiContractTests : IClassFixture<TestWebApplicationFactory>, IAsyncLifetime
{
    private const string AgentProjectId = "p1";
    private const string Ticket = "contract-ticket";
    private const string ServerOrigin = "https://home.example";

    private readonly TestWebApplicationFactory _factory;
    private readonly string _base = Path.Combine(Path.GetTempPath(), "agent-contract-" + Guid.NewGuid().ToString("N"));
    private HttpClient _server = null!;
    private string _serverProjectId = null!;
    private WebApplication _agentApp = null!;
    private HttpClient _agent = null!;

    public DeviceAgentApiContractTests(TestWebApplicationFactory factory) => _factory = factory;

    private string ServerRoot => Path.Combine(_base, "server", "proj");
    private string AgentAllowed => Path.Combine(_base, "agent");
    private string AgentRoot => Path.Combine(AgentAllowed, "proj");

    private sealed class AllowedRoots(string root) : IAgentRoots
    {
        public IReadOnlyList<string> Roots { get; } = [root];
    }

    private sealed class Introspector(string rootPath) : IAgentTicketIntrospector
    {
        public Task<AgentTicketIntrospection?> IntrospectAsync(string ticket, CancellationToken ct) =>
            Task.FromResult<AgentTicketIntrospection?>(ticket == Ticket
                ? new AgentTicketIntrospection("owner-1", AgentProjectId, rootPath, DateTimeOffset.UtcNow.AddMinutes(5))
                : null);
    }

    public async Task InitializeAsync()
    {
        SeedProject(ServerRoot);
        SeedProject(AgentRoot);
        // Соседи корня: ссылки «вне корня» на обеих сторонах ведут в существующий файл
        File.WriteAllText(Path.Combine(_base, "server", "outside.txt"), "чужое");
        File.WriteAllText(Path.Combine(_base, "agent", "outside.txt"), "чужое");

        _server = _factory.CreateAuthenticatedClient();
        var created = await _server.PostAsJsonAsync("/api/projects", new { name = "contract-" + Guid.NewGuid().ToString("N")[..8], rootPath = ServerRoot });
        created.EnsureSuccessStatusCode();
        _serverProjectId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var port = FreePort();
        var git = new GitService(AgentLauncherFactory.Instance);
        var files = new AgentProjectFiles(new FileService(git), new AgentPathPolicy(new AllowedRoots(AgentAllowed)));
        // Глобальные навыки агент берёт из профиля CLI, сервер — из ~/.claude: для сравнения
        // формы профилем агента служит тот же каталог
        var cliProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        _agentApp = LocalApi.Build(new LocalApiOptions(port, ServerOrigin, "contract"), files, git,
            new AgentTicketCache(new Introspector(AgentRoot)), watchers: null, NullLoggerFactory.Instance,
            workbench: new AgentWorkbench(Path.Combine(_base, "agent-data"), cliProfile, FreePort(), new AgentLauncherFactory()));
        await _agentApp.StartAsync();
        _agent = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        _agent.DefaultRequestHeaders.Add(DeviceAgentApi.TicketHeader, Ticket);
    }

    public async Task DisposeAsync()
    {
        _agent.Dispose();
        _server.Dispose();
        await _agentApp.DisposeAsync();
        try { Directory.Delete(_base, recursive: true); } catch { /* временная папка */ }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // Одинаковое дерево на обеих сторонах: git-репозиторий с коммитом и правкой поверх;
    // манифест с дев-скриптом, агент и навык проекта — для сервисов и панели навыков
    internal static void SeedProject(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(root, ".claude", "agents"));
        Directory.CreateDirectory(Path.Combine(root, ".claude", "skills", "demo"));
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "scripts": { "dev": "vite", "build": "vite build" } }""");
        File.WriteAllText(Path.Combine(root, ".claude", "agents", "scout.md"), "---\nname: scout\ndescription: разведка\ntools: Read, Grep\n---\nтело");
        File.WriteAllText(Path.Combine(root, ".claude", "skills", "demo", "SKILL.md"), "---\nname: demo\ndescription: навык\n---\nтело");
        File.WriteAllText(Path.Combine(root, "a.txt"), "привет\n");
        File.WriteAllText(Path.Combine(root, "sub", "b.txt"), "b\n");
        File.WriteAllText(Path.Combine(root, "clip.mp4"), new string('x', 64));
        Git(root, "init", "-q", "-b", "main");
        Git(root, "config", "user.name", "Тест");
        Git(root, "config", "user.email", "test@test");
        Git(root, "add", "-A");
        Git(root, "commit", "-qm", "начальный");
        File.WriteAllText(Path.Combine(root, "a.txt"), "изменено\n");
        File.WriteAllText(Path.Combine(root, "sub", "b.txt"), "b2\n");
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        p.ExitCode.Should().Be(0, "арранж: git " + string.Join(' ', args));
    }

    /// <summary>Шаг сценария: маршрут контракта и сам запрос относительно <c>api/projects/{id}/</c>.</summary>
    private sealed record Call(string Label, ProjectApiRoute Route, string Url, object? Body = null);

    private static Call C(string label, string method, string template, string url, object? body = null) =>
        new(label, new ProjectApiRoute(method, template), url, body);

    // Порядок важен: мутации идут одинаково на обе стороны, следующий шаг видит их результат
    private static readonly Call[] Scenario =
    [
        C("листинг корня", "GET", "files", "files"),
        C("листинг папки", "GET", "files", "files?path=sub"),
        C("листинг: нет папки", "GET", "files", "files?path=nope"),
        C("листинг: вне корня", "GET", "files", "files?path=..%2F"),
        C("дерево", "GET", "files/tree", "files/tree"),
        C("дерево: нет папки", "GET", "files/tree", "files/tree?path=nope"),
        C("дерево: вне корня", "GET", "files/tree", "files/tree?path=..%2F"),
        C("поиск", "GET", "files/search", "files/search?q=b"),
        C("содержимое", "GET", "files/content", "files/content?path=a.txt"),
        C("содержимое видео", "GET", "files/content", "files/content?path=clip.mp4"),
        C("содержимое: нет файла", "GET", "files/content", "files/content?path=nope.txt"),
        C("содержимое: папка", "GET", "files/content", "files/content?path=sub"),
        C("содержимое: вне корня", "GET", "files/content", "files/content?path=..%2Foutside.txt"),
        C("запись", "PUT", "files/content", "files/content?path=a.txt", new SaveContentRequest("правка\n")),
        C("запись: папка", "PUT", "files/content", "files/content?path=sub", new SaveContentRequest("x")),
        C("запись: вне корня", "PUT", "files/content", "files/content?path=..%2Foutside.txt", new SaveContentRequest("x")),
        C("дифф файла", "GET", "files/diff", "files/diff?path=a.txt"),
        C("дифф файла: без правок", "GET", "files/diff", "files/diff?path=clip.mp4"),
        C("создание", "POST", "files/create", "files/create", new CreateFileRequest("new.txt", "n")),
        C("создание: уже есть", "POST", "files/create", "files/create", new CreateFileRequest("new.txt")),
        C("создание: вне корня", "POST", "files/create", "files/create", new CreateFileRequest("../evil.txt")),
        C("папка", "POST", "files/mkdir", "files/mkdir", new PathRequest("dir2")),
        C("папка: вне корня", "POST", "files/mkdir", "files/mkdir", new PathRequest("../evil")),
        C("переименование", "POST", "files/rename", "files/rename", new RenameRequest("new.txt", "dir2/moved.txt")),
        C("переименование: вне корня", "POST", "files/rename", "files/rename", new RenameRequest("dir2/moved.txt", "../evil.txt")),
        C("удаление", "DELETE", "files", "files?path=dir2%2Fmoved.txt"),
        C("удаление: нет файла", "DELETE", "files", "files?path=nope.txt"),
        C("удаление: вне корня", "DELETE", "files", "files?path=..%2Foutside.txt"),
        C("поток", "GET", "files/stream", "files/stream?path=clip.mp4"),
        C("поток: нет файла", "GET", "files/stream", "files/stream?path=nope.mp4"),
        C("поток: вне корня", "GET", "files/stream", "files/stream?path=..%2Foutside.txt"),
        C("откат файла", "POST", "files/revert", "files/revert", new PathRequest("a.txt")),

        C("статус", "GET", "git/status", "git/status"),
        C("git-дифф", "GET", "git/diff", "git/diff?path=sub%2Fb.txt"),
        C("git-дифф индекса", "GET", "git/diff", "git/diff?path=sub%2Fb.txt&staged=true"),
        C("git-дифф: вне корня", "GET", "git/diff", "git/diff?path=..%2Foutside.txt"),
        C("история", "GET", "git/log", "git/log?limit=10"),
        C("ветки", "GET", "git/branches", "git/branches"),
        C("в индекс", "POST", "git/stage", "git/stage", new GitPathRequest("sub/b.txt")),
        C("в индекс: вне корня", "POST", "git/stage", "git/stage", new GitPathRequest("../outside.txt")),
        C("из индекса", "POST", "git/unstage", "git/unstage", new GitPathRequest("sub/b.txt")),
        C("из индекса: вне корня", "POST", "git/unstage", "git/unstage", new GitPathRequest("../outside.txt")),
        C("снова в индекс", "POST", "git/stage", "git/stage", new GitPathRequest("sub/b.txt")),
        C("коммит: пустое сообщение", "POST", "git/commit", "git/commit", new GitCommitRequest("  ")),
        C("коммит", "POST", "git/commit", "git/commit", new GitCommitRequest("контракт")),
        C("коммит: нечего коммитить", "POST", "git/commit", "git/commit", new GitCommitRequest("пусто")),
        C("сброс правок", "POST", "git/discard", "git/discard", new GitPathRequest("clip.mp4")),
        C("сброс правок: вне корня", "POST", "git/discard", "git/discard", new GitPathRequest("../outside.txt")),

        C("сервисы", "GET", "services", "services"),
        C("статус превью", "GET", "preview/status", "preview/status"),
        C("запуск: без команды", "POST", "preview/start", "preview/start", new PreviewStartRequest("")),
        C("запуск: нет такой команды", "POST", "preview/start", "preview/start",
            new PreviewStartRequest("ai-home-contract-no-such-command", ServiceId: "ghost")),
        C("запуск: каталог вне корня", "POST", "preview/start", "preview/start",
            new PreviewStartRequest("node", ServiceId: "escape", Cwd: "../outside.txt")),
        C("остановка: без сервиса", "POST", "preview/stop", "preview/stop", new PreviewStopRequest(null)),
        C("остановка", "POST", "preview/stop", "preview/stop", new PreviewStopRequest("ghost")),
        C("остановка внешнего: порт неизвестен", "POST", "preview/stop-external", "preview/stop-external",
            new StopExternalRequest("ghost")),
        C("активный: без сервиса", "POST", "preview/active", "preview/active", new PreviewActiveRequest("")),
        C("активный", "POST", "preview/active", "preview/active", new PreviewActiveRequest("npm-dev")),
        C("внешний: нет сервиса", "POST", "preview/active-external", "preview/active-external", new PreviewActiveRequest("nope")),
        C("launch.json: пусто", "GET", "launch-config", "launch-config"),
        C("launch.json: запись", "PUT", "launch-config", "launch-config", new LaunchConfigPutRequest(
        [
            new LaunchConfigEntry { Name = "web", RuntimeExecutable = "npm", RuntimeArgs = ["run", "dev"], Port = 1 },
        ])),
        C("launch.json: чтение", "GET", "launch-config", "launch-config"),
        C("сервисы после записи", "GET", "services", "services"),
        C("внешний: порт не слушается", "POST", "preview/active-external", "preview/active-external",
            new PreviewActiveRequest("launch-web")),

        C("навыки", "GET", "skills", "skills"),
        C("агент", "GET", "agents/{agentName}", "agents/scout"),
        C("агент: нет такого", "GET", "agents/{agentName}", "agents/nope"),
        C("агент: запись", "PUT", "agents/{agentName}", "agents/scout", new SkillContentRequest("---\nname: scout\n---\nновое")),
        C("агент: создание", "POST", "agents", "agents", new CreateSkillRequest("helper", "тело")),
        C("агент: пустое имя", "POST", "agents", "agents", new CreateSkillRequest(" ", "тело")),
        C("навыки после записи", "GET", "skills", "skills"),
    ];

    private sealed record Outcome(int Status, string? Shape, string? ContentType)
    {
        public override string ToString() => $"{Status} {ContentType} {Shape}";
    }

    private static async Task<Outcome> SendAsync(HttpClient client, string prefix, Call call)
    {
        var request = new HttpRequestMessage(new HttpMethod(call.Route.Method), prefix + call.Url);
        if (call.Body is not null) request.Content = JsonContent.Create(call.Body, call.Body.GetType());
        HttpResponseMessage response;
        try { response = await client.SendAsync(request); }
        // Необработанное исключение контроллера в тестовом хосте долетает до клиента
        catch (Exception e) { return new Outcome(500, "исключение " + e.GetType().Name, null); }

        var status = (int)response.StatusCode;
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (status is < 200 or >= 300) return new Outcome(status, null, null);
        if (mediaType is null || !mediaType.Contains("json"))
            return new Outcome(status, null, mediaType);
        var body = await response.Content.ReadAsStringAsync();
        return new Outcome(status, ShapeOf(JsonDocument.Parse(body).RootElement), "json");
    }

    /// <summary>
    /// Форма JSON: путь поля → множество видов значений. Массив сливает формы элементов,
    /// null — не вид, а отсутствие значения: необязательное поле на стороне, где оно пустое,
    /// с непустым не спорит, но само поле обязано быть в наборе.
    /// </summary>
    internal static string ShapeOf(JsonElement root)
    {
        var shape = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Walk(JsonElement e, string path)
        {
            if (!shape.TryGetValue(path, out var kinds)) shape[path] = kinds = new SortedSet<string>(StringComparer.Ordinal);
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    kinds.Add("object");
                    foreach (var p in e.EnumerateObject()) Walk(p.Value, path + "." + p.Name);
                    break;
                case JsonValueKind.Array:
                    kinds.Add("array");
                    foreach (var item in e.EnumerateArray()) Walk(item, path + "[]");
                    break;
                case JsonValueKind.True or JsonValueKind.False: kinds.Add("bool"); break;
                case JsonValueKind.Null: break;
                default: kinds.Add(e.ValueKind.ToString().ToLowerInvariant()); break;
            }
        }
        Walk(root, "$");
        return string.Join("; ", shape.Select(kv => kv.Key + ":" + string.Join("|", kv.Value)));
    }

    // Формы совпадают: тот же набор путей, у путей с непустыми видами на обеих сторонах — те же виды
    internal static bool SameShape(string? a, string? b)
    {
        if (a is null || b is null) return a == b;
        static Dictionary<string, string> Parse(string s) => s.Split("; ").Select(x => x.Split(':', 2)).ToDictionary(x => x[0], x => x[1]);
        var (pa, pb) = (Parse(a), Parse(b));
        return pa.Keys.ToHashSet().SetEquals(pb.Keys)
            && pa.All(kv => kv.Value.Length == 0 || pb[kv.Key].Length == 0 || kv.Value == pb[kv.Key]);
    }

    [Fact]
    public async Task ОбщиеМаршруты_ОдинаковыеКодыИФормаОтветов()
    {
        var mismatches = new List<string>();
        foreach (var call in Scenario)
        {
            var server = await SendAsync(_server, $"/api/projects/{_serverProjectId}/", call);
            var agent = await SendAsync(_agent, $"/api/projects/{AgentProjectId}/", call);
            if (server.Status != agent.Status || server.ContentType != agent.ContentType || !SameShape(server.Shape, agent.Shape))
                mismatches.Add($"{call.Route} [{call.Label}]\n  сервер: {server}\n  агент:  {agent}");
        }

        mismatches.Should().BeEmpty("сервер и агент обязаны отвечать одинаково:\n" + string.Join("\n", mismatches));
    }

    [Fact]
    public void СценарийПокрываетКаждыйОбщийМаршрут()
    {
        Scenario.Select(c => c.Route).Distinct().Should().BeEquivalentTo(DeviceAgentRoutes.Shared,
            "у каждого общего маршрута должен быть шаг контракт-сценария");
    }

    // Маршрут относительно api/projects/{projectId}/, как в списках DeviceAgentRoutes
    private static IEnumerable<ProjectApiRoute> ProjectRoutes(IEnumerable<EndpointDataSource> sources, Func<Endpoint, bool> filter)
    {
        const string prefix = "api/projects/{projectId}/";
        foreach (var endpoint in sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().Where(e => filter(e)))
        {
            var template = endpoint.RoutePattern.RawText!.Trim('/');
            if (!template.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var relative = template[prefix.Length..].TrimEnd('/');
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                yield return new ProjectApiRoute(method, relative);
        }
    }

    // Контроллеры, чьи маршруты проекта обслуживает и агент
    private static readonly string[] SharedControllers = ["Files", "Git", "Preview", "Skills"];

    [Fact]
    public void КаждыйСерверныйМаршрут_ЛибоОбщий_ЛибоВСпискеНеподдержанных()
    {
        var server = ProjectRoutes(_factory.Services.GetServices<EndpointDataSource>(), e =>
            SharedControllers.Contains(e.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>()?.ControllerName))
            .ToHashSet();
        var agent = ProjectRoutes(((IEndpointRouteBuilder)_agentApp).DataSources, _ => true).ToHashSet();

        DeviceAgentRoutes.Shared.Intersect(DeviceAgentRoutes.Unsupported).Should().BeEmpty("маршрут не может быть и общим, и неподдержанным");
        DeviceAgentRoutes.AgentOnly.Intersect(DeviceAgentRoutes.Shared.Concat(DeviceAgentRoutes.Unsupported)).Should()
            .BeEmpty("маршрут только агента не может быть серверным");
        server.Should().BeEquivalentTo(DeviceAgentRoutes.Shared.Concat(DeviceAgentRoutes.Unsupported),
            "каждый маршрут проекта у Files/Git/Preview/SkillsController обязан стоять ровно в одном списке DeviceAgentRoutes");
        agent.Should().BeEquivalentTo(DeviceAgentRoutes.Shared.Concat(DeviceAgentRoutes.AgentOnly),
            "у агента ровно общие маршруты и свои билеты: лишний — вне контракта, недостающий — ложь матрицы возможностей");
    }

    /// <summary>
    /// Хаб агента: те же методы, что клиент зовёт у серверных TerminalHub и SessionHub
    /// (логи дев-серверов), — имя, параметры и результат. Клиенту меняется только адрес.
    /// </summary>
    [Fact]
    public void ХабАгента_ТеЖеМетоды_ЧтоУСерверныхХабов()
    {
        static IEnumerable<string> Signatures(Type hub, Func<System.Reflection.MethodInfo, bool>? only = null) =>
            hub.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && m.Name is not ("OnConnectedAsync" or "OnDisconnectedAsync") && (only is null || only(m)))
                .Select(m => $"{m.ReturnType} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType + " " + p.Name))})");

        var server = Signatures(typeof(ClaudeHomeServer.Hubs.TerminalHub))
            .Concat(Signatures(typeof(ClaudeHomeServer.Hubs.SessionHub), m => m.Name is "JoinPreviewLog" or "LeavePreviewLog"));

        Signatures(typeof(AgentHub)).Should().BeEquivalentTo(server);
    }
}
