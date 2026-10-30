using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeHomeServer.Tests.Hubs;

/// <summary>
/// S1-7a / S2a-10a: право RunTurn. Редактор общего проекта отправляет сообщения в чаты (его ход
/// идёт в узкой рамке S2a-2…S2a-5) и останавливает/отвечает на вопросы СВОЕГО хода; режим прав,
/// одобрение разрешений, план, штаб, сворачивание контекста и shell на хосте — только владельцу.
/// Таблица «метод хаба → кто может → почему» — <see cref="EditorMayCall"/>. Точка доступа
/// подменена на «чистую» (только <see cref="ProjectAccess"/>, без рубильника общих проектов
/// из <c>ProjectAccessService</c>): иначе отказ редактору давал бы сам рубильник, и тест не
/// краснел бы на подмене права в хабе.
/// </summary>
public class RunTurnGateTests : IClassFixture<RunTurnGateTests.Fixture>, IAsyncLifetime
{
    /// <summary>Ход-методы SessionHub: запускают ход или управляют идущим (размечены RunTurn).</summary>
    private static readonly string[] TurnMethods =
    [
        "SendMessage", "RespondPermission", "SetMode", "Interrupt", "CompactSession",
        "AnswerQuestion", "RespondPlan", "RespondTeamPlan", "RespondTeamEscalation",
    ];

    /// <summary>
    /// Методы, которые редактор вправе вызвать (S2a-10): SendMessage — отправка в чат проекта (право
    /// RunTurn). Остальные ход-методы владельцу: SetMode/RespondPermission/RespondPlan — права и
    /// план владельца, CompactSession — общая история чата, RespondTeam* — бюджет и штаб, CreateTerminal —
    /// shell на хосте. Interrupt и AnswerQuestion — владельцу или автору хода, чужому участнику нет
    /// (редактор, не запускавший ход, здесь отказ — <see cref="EditorMayCallOwnTurn"/>).
    /// </summary>
    private static readonly string[] EditorMayCall = ["SendMessage"];

    /// <summary>Методы, доступные владельцу и автору хода (по актору хода), но не чужому участнику.</summary>
    private static readonly string[] EditorMayCallOwnTurn = ["Interrupt", "AnswerQuestion"];

    /// <summary>Методы хаба, ход не запускающие и не ведущие (подписка на события).</summary>
    private static readonly string[] NonTurnMethods =
    [
        "JoinSession", "LeaveSession", "JoinProject", "LeaveProject",
        "JoinPreviewLog", "LeavePreviewLog", "JoinUser", "LeaveUser",
    ];

    public sealed class Fixture : IDisposable
    {
        public TestWebApplicationFactory Factory { get; } = new()
        {
            ExtraServices = services =>
            {
                services.RemoveAll<IProjectAccess>();
                services.AddSingleton<IProjectAccess, PureAccess>();
            },
        };

        public void Dispose() => Factory.Dispose();
    }

    /// <summary>Точка доступа без рубильника: решает только матрица <see cref="ProjectAccess"/>.</summary>
    private sealed class PureAccess(ProjectManager projects, ISessionDirectory sessions) : IProjectAccess
    {
        public RequestScope? Resolve(string? callerUserId, string? projectId, ProjectRight right = ProjectRight.Read)
        {
            var project = projects.GetById(projectId ?? "");
            if (!ProjectAccess.Can(project, callerUserId, right)) return null;
            return new RequestScope(callerUserId!, project!.OwnerId!, ProjectAccess.Check(project, callerUserId)!.Value);
        }

        public RequestScope? ResolveSession(string? callerUserId, string? sessionId, ProjectRight right = ProjectRight.Read)
        {
            var session = sessions.GetById(sessionId ?? "");
            return session?.ProjectId is null ? null : Resolve(callerUserId, session.ProjectId, right);
        }
    }

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _owner;
    private readonly string _tempDir;
    private readonly List<HubConnection> _connections = [];

    public RunTurnGateTests(Fixture fixture)
    {
        _factory = fixture.Factory;
        _owner = _factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(_factory.TempDir, "runturn_tests");
        Directory.CreateDirectory(_tempDir);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
    }

    // --- матрица «роль × метод»: владелец проходит, редактор и читатель получают отказ

    public static IEnumerable<object[]> HubMatrix() =>
        from m in TurnMethods.Concat(["CreateTerminal"])
        from r in new[] { ProjectRole.Owner, ProjectRole.Editor, ProjectRole.Reader }
        select new object[] { m, r };

    [Theory]
    [MemberData(nameof(HubMatrix))]
    public async Task ХодМетодХаба_РазрешёнПоТаблицеРолей(string method, ProjectRole role)
    {
        var (projectId, sessionId, userId) = await CreateSharedProjectAsync(role);
        _ = userId;
        var (hub, args) = method == "CreateTerminal"
            ? ("hubs/terminal", new object?[] { projectId, 80, 24, null })
            : ("hubs/session", ArgsOf(method, sessionId));
        var conn = await ConnectAsync(hub, role);

        string? denied = null;
        try { await conn.InvokeCoreAsync(method, args); }
        catch (HubException ex) { if (ex.Message.Contains("Доступ запрещён")) denied = ex.Message; }
        catch (Exception) { /* прошёл гейт и упал позже на заглушечных аргументах — не отказ доступа */ }

        if (role == ProjectRole.Owner || (role == ProjectRole.Editor && EditorMayCall.Contains(method)))
            denied.Should().BeNull($"{role} должен пройти {method}");
        else denied.Should().NotBeNull($"{role} не должен проходить {method}");
    }

    private static object?[] ArgsOf(string method, string sessionId) => method switch
    {
        "SendMessage" => [sessionId, "привет", null, null, false],
        "RespondPermission" => [sessionId, "req", "deny"],
        "SetMode" => [sessionId, "auto"],
        "Interrupt" => [sessionId],
        "CompactSession" => [sessionId],
        "AnswerQuestion" => [sessionId, "tool", "ответ"],
        "RespondPlan" => [sessionId, "req", false, null],
        "RespondTeamPlan" => [sessionId, "plan", "cancel", null, null, null],
        "RespondTeamEscalation" => [sessionId, "esc", null, null],
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Нет аргументов для метода"),
    };

    [Theory]
    [InlineData(ProjectRole.Owner)]
    [InlineData(ProjectRole.Editor)]
    [InlineData(ProjectRole.Reader)]
    public async Task StartProject_РазрешёнТолькоВладельцу(ProjectRole role)
    {
        var (projectId, _, _) = await CreateSharedProjectAsync(role);
        var client = role == ProjectRole.Owner
            ? _owner
            : _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await client.PostAsync($"/api/onboarding/project/{projectId}/start", null);

        if (role == ProjectRole.Owner)
            response.StatusCode.Should().NotBe(HttpStatusCode.NotFound).And.NotBe(HttpStatusCode.Forbidden);
        else
            response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
    }

    // --- сторож: новый метод хаба не может остаться без классификации и без RunTurn

    [Fact]
    public void Сторож_КаждыйПубличныйМетодSessionHub_ХодМетодИлиПодписка()
    {
        var actual = typeof(SessionHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name).Distinct().ToHashSet();
        actual.Remove("OnConnectedAsync");
        actual.Remove("OnDisconnectedAsync");

        var known = TurnMethods.Concat(NonTurnMethods).ToHashSet();
        actual.Except(known).Should().BeEmpty(
            "новый метод хаба нужно отнести к ход-методам (RunTurn) или подпискам этого сторожа");
        known.Except(actual).Should().BeEmpty("в списках сторожа остались методы, которых в хабе уже нет");
    }

    [Fact]
    public void Сторож_ХодМетодыХаба_ПроверяютRunTurn_иНеWrite()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "backend", "ClaudeHomeServer", "Hubs", "SessionHub.cs"));
        foreach (var method in TurnMethods)
        {
            var m = Regex.Match(source, @"public [^\n]*\b" + method + @"\(");
            m.Success.Should().BeTrue($"{method} не найден в SessionHub.cs");
            var next = source.IndexOf("\n    public ", m.Index + 1, StringComparison.Ordinal);
            var body = next < 0 ? source[m.Index..] : source[m.Index..next];
            var expected = method == "SendMessage" ? "ProjectRight.RunTurn"
                : EditorMayCallOwnTurn.Contains(method) ? "OwnerOrTurnAuthor"
                : "ProjectRight.Configure";
            body.Should().Contain(expected, $"{method} обязан проверять {expected}");
            body.Should().NotContain("ProjectRight.Write", $"{method} — ход-метод, Write редактору не даёт ходить");
        }
    }

    [Fact]
    public void Сторож_СозданиеТерминала_иСтартОнбордингаПроекта_ТребуютТолькоВладельца()
    {
        var hubs = File.ReadAllText(Path.Combine(RepoRoot(), "backend", "ClaudeHomeServer", "Hubs", "TerminalHub.cs"));
        Regex.Match(hubs, @"CreateTerminal\([^\n]*\n\s*\{[^\n]*\n[^\n]*").Value.Should().Contain("ProjectRight.Configure");
        var onboarding = File.ReadAllText(Path.Combine(RepoRoot(), "backend", "ClaudeHomeServer", "Controllers", "OnboardingController.cs"));
        Regex.Match(onboarding, @"\[ProjectScope[^\n]*\]\s*public async Task<IActionResult> StartProject").Value
            .Should().Contain("ProjectRight.Configure");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
               && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден");
    }

    // Проект владельца (testuser); второй пользователь добавляется участником с ролью role
    // (для Owner — не добавляется, к хабу подключается сам владелец)
    private async Task<(string ProjectId, string SessionId, string UserId)> CreateSharedProjectAsync(ProjectRole role)
    {
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await _owner.PostAsJsonAsync("/api/projects", new { name = "RunTurnProject", rootPath = dir });
        response.EnsureSuccessStatusCode();
        var projectId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var s = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        s.EnsureSuccessStatusCode();
        var sessionId = (await s.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var second = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.SecondUsername)!;
        if (role != ProjectRole.Owner)
        {
            var project = _factory.Services.GetRequiredService<ProjectManager>().GetById(projectId)!;
            project.Members = [new ProjectMember { UserId = second.Id, Role = role, AddedBy = project.OwnerId! }];
        }
        return (projectId, sessionId, second.Id);
    }

    private async Task<HubConnection> ConnectAsync(string hub, ProjectRole role)
    {
        var (user, pass) = role == ProjectRole.Owner
            ? (TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword)
            : (TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var token = _factory.GetToken(user, pass);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, hub), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        connection.HandshakeTimeout = TimeSpan.FromSeconds(60);
        connection.ServerTimeout = TimeSpan.FromSeconds(60);
        await connection.StartAsync();
        _connections.Add(connection);
        return connection;
    }
}
