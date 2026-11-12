using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;

namespace ClaudeHomeServer.Tests.Services.Access;

/// <summary>
/// W3-B1a: одна точка «персона → область запроса» (разрез shared-projects-personas-cut-2026-11 §2,
/// И-1 и И-3). Матрица: 4 роли (владелец / редактор / читатель / посторонний) × 3 вида персоны
/// (проектная общего проекта, глобальная, сферная), плюс грань 403/404 фильтра
/// <see cref="ScopeSource.Persona"/>. Юниты без хоста: <see cref="ProjectAccessService"/> и
/// <see cref="PersonaManager"/> на временном сторе.
/// </summary>
public sealed class ProjectAccessPersonaTests : IDisposable
{
    private readonly string _dir;
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _readerId;
    private readonly string _otherId;
    private readonly string _projectId;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly SharedProjectsGate _gate;
    private readonly PersonaManager _personas;
    private readonly ProjectAccessService _access;

    public ProjectAccessPersonaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "persona_access_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            ["PersonasPath"] = Path.Combine(_dir, "personas.json"),
            [SharedProjectsGate.SwitchKey] = "true",
        }).Build();
        var users = new UserStore(_config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = users.GetFirst()!.Id;
        _editorId = users.Add("persona-editor", "pwd", "user").Id;
        _readerId = users.Add("persona-reader", "pwd", "user").Id;
        _otherId = users.Add("persona-other", "pwd", "user").Id;
        // Рубильник общий только участникам: посторонний его не имеет (как на проде без флага)
        foreach (var id in new[] { _editorId, _readerId })
            users.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);

        _projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        var root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(root);
        _projectId = _projects.Create("Общий проект", root, _ownerId, users.GetFirst()!.Username).Id;
        _projects.SetMembers(_projectId,
        [
            new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
            new ProjectMember { UserId = _readerId, Role = ProjectRole.Reader, AddedBy = _ownerId },
        ]);

        _gate = new SharedProjectsGate(_config, new FeatureFlagService(users));
        _personas = new PersonaManager(_config);
        _access = new ProjectAccessService(_projects, new NoSessions(), _gate, _personas);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // --- Три вида персон одного владельца-арендатора (карточки — через боевой Create) ---

    private Persona ProjectPersona(string name = "Командный", string? projectId = null) =>
        _personas.Create(_ownerId, name, null, null, null, null, null,
            PersonaScope.Project, projectId ?? _projectId, null, null, false);

    private Persona GlobalPersona() =>
        _personas.Create(_ownerId, "Личный", null, null, null, null, null,
            PersonaScope.Global, null, null, null, false);

    private Persona SpherePersona() =>
        _personas.Create(_ownerId, "Сферный", null, null, null, null, null,
            PersonaScope.Sphere, null, null, null, false, sphereId: "sphere-1");

    // --- Проектная персона общего проекта: область = область её проекта (§2.2) ---

    [Fact]
    public void Проектная_Владелец_ОбластьПроекта_РольOwner()
    {
        var persona = ProjectPersona();
        var scope = _access.ResolvePersona(_ownerId, persona.Id);

        scope.Should().NotBeNull();
        scope!.CallerUserId.Should().Be(_ownerId);
        scope.TenantOwnerId.Should().Be(_ownerId);
        scope.Role.Should().Be(ProjectRole.Owner);
    }

    [Fact]
    public void Проектная_Редактор_ОбластьПроекта_КлючВладелецАНеВызывающий()
    {
        var persona = ProjectPersona();
        var scope = _access.ResolvePersona(_editorId, persona.Id);

        // И-2: ключ хранилищ — арендатор; вызывающий остаётся только в CallerUserId
        scope.Should().NotBeNull();
        scope!.CallerUserId.Should().Be(_editorId);
        scope.TenantOwnerId.Should().Be(_ownerId);
        scope.Role.Should().Be(ProjectRole.Editor);
    }

    [Fact]
    public void Проектная_Читатель_Read_ОбластьПроекта()
    {
        var persona = ProjectPersona();
        var scope = _access.ResolvePersona(_readerId, persona.Id);

        scope.Should().NotBeNull();
        scope!.TenantOwnerId.Should().Be(_ownerId);
        scope.Role.Should().Be(ProjectRole.Reader);
    }

    [Fact]
    public void Проектная_Посторонний_null()
    {
        var persona = ProjectPersona();
        _access.ResolvePersona(_otherId, persona.Id).Should().BeNull();
    }

    [Fact]
    public void Проектная_ПравоИдётЧерезResolve_ЧитателюЗаписиНет_РедакторуConfigureНет()
    {
        var persona = ProjectPersona();
        _access.ResolvePersona(_readerId, persona.Id, ProjectRight.WriteContent).Should().BeNull();
        _access.ResolvePersona(_readerId, persona.Id, ProjectRight.RunTurn).Should().BeNull();
        _access.ResolvePersona(_editorId, persona.Id, ProjectRight.WriteContent).Should().NotBeNull();
        _access.ResolvePersona(_editorId, persona.Id, ProjectRight.Configure).Should().BeNull();
        _access.ResolvePersona(_ownerId, persona.Id, ProjectRight.Configure).Should().NotBeNull();
    }

    [Fact]
    public void Проектная_ВладелецПерсоныНеВладелецПроекта_null_БитаяЗаписьANeДоступ()
    {
        // Карточка, заведённая редактором в чужом проекте: OwnerId = редактор, ProjectId = проект владельца
        var broken = _personas.Create(_editorId, "Битая", null, null, null, null, null,
            PersonaScope.Project, _projectId, null, null, false);

        _access.ResolvePersona(_editorId, broken.Id).Should().BeNull();
        _access.ResolvePersona(_ownerId, broken.Id).Should().BeNull();
    }

    [Fact]
    public void Проектная_ПроектИсчез_ТолькоВладельцуПерсоны_РольOwner()
    {
        var orphan = ProjectPersona("Осиротевшая", "нет-такого-проекта");

        var scope = _access.ResolvePersona(_ownerId, orphan.Id);
        scope.Should().NotBeNull();
        scope!.TenantOwnerId.Should().Be(_ownerId);
        scope.Role.Should().Be(ProjectRole.Owner);
        _access.ResolvePersona(_editorId, orphan.Id).Should().BeNull();
    }

    // --- Глобальная персона: только по владению, участнику не видна никогда (И-3) ---

    [Fact]
    public void Глобальная_Владелец_СвояОбласть_РольOwner()
    {
        var persona = GlobalPersona();
        var scope = _access.ResolvePersona(_ownerId, persona.Id, ProjectRight.Configure);

        scope.Should().NotBeNull();
        scope!.TenantOwnerId.Should().Be(_ownerId);
        scope.Role.Should().Be(ProjectRole.Owner);
    }

    [Fact]
    public void Глобальная_ПерсонаВладельца_РедакторУчастник_НеВидит()
    {
        var persona = GlobalPersona();
        _access.ResolvePersona(_editorId, persona.Id).Should().BeNull();
    }

    [Fact]
    public void Глобальная_ПерсонаВладельца_ЧитательУчастник_НеВидит()
    {
        var persona = GlobalPersona();
        _access.ResolvePersona(_readerId, persona.Id).Should().BeNull();
    }

    [Fact]
    public void Глобальная_ПерсонаВладельца_Посторонний_НеВидит()
    {
        var persona = GlobalPersona();
        _access.ResolvePersona(_otherId, persona.Id).Should().BeNull();
    }

    // --- Сферная персона: то же правило, что у глобальной (§2.3) ---

    [Fact]
    public void Сферная_Владелец_СвояОбласть_РольOwner()
    {
        var persona = SpherePersona();
        var scope = _access.ResolvePersona(_ownerId, persona.Id);

        scope.Should().NotBeNull();
        scope!.TenantOwnerId.Should().Be(_ownerId);
        scope.Role.Should().Be(ProjectRole.Owner);
    }

    [Fact]
    public void Сферная_ПерсонаВладельца_РедакторУчастник_НеВидит()
    {
        var persona = SpherePersona();
        _access.ResolvePersona(_editorId, persona.Id).Should().BeNull();
    }

    [Fact]
    public void Сферная_ПерсонаВладельца_ЧитательУчастник_НеВидит()
    {
        var persona = SpherePersona();
        _access.ResolvePersona(_readerId, persona.Id).Should().BeNull();
    }

    [Fact]
    public void Сферная_ПерсонаВладельца_Посторонний_НеВидит()
    {
        var persona = SpherePersona();
        _access.ResolvePersona(_otherId, persona.Id).Should().BeNull();
    }

    // --- Нет входа, нет персоны, нет каталога персон — везде null (fail-closed) ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ПустойВызывающий_null(string? caller)
    {
        var persona = ProjectPersona();
        _access.ResolvePersona(caller, persona.Id).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ПустойIdПерсоны_null(string? id)
    {
        ProjectPersona();
        _access.ResolvePersona(_ownerId, id).Should().BeNull();
    }

    [Fact]
    public void ПерсоныНет_null()
    {
        _access.ResolvePersona(_ownerId, "нет-такой-персоны").Should().BeNull();
    }

    [Fact]
    public void КонструкторБезКаталогаПерсон_ResolvePersona_null()
    {
        // Существующие new ProjectAccessService(...) без PersonaManager (тесты прочих вертикалей)
        // остаются как были: без каталога персон резолвер не даёт области
        var bare = new ProjectAccessService(_projects, new NoSessions(), _gate);
        var persona = ProjectPersona();
        bare.ResolvePersona(_ownerId, persona.Id).Should().BeNull();
    }

    // Дефолт метода интерфейса: реализация без персон (тестовые фейки IProjectAccess) не трогается
    private sealed class NoPersonasAccess : IProjectAccess
    {
        public RequestScope? Resolve(string? callerUserId, string? projectId, ProjectRight right = ProjectRight.Read) =>
            new RequestScope(callerUserId!, "tenant", ProjectRole.Owner);

        public RequestScope? ResolveSession(string? callerUserId, string? sessionId, ProjectRight right = ProjectRight.Read) => null;
    }

    [Fact]
    public void ФейкБезResolvePersona_ДефолтИнтерфейса_null()
    {
        IProjectAccess access = new NoPersonasAccess();
        access.ResolvePersona(_ownerId, "любая").Should().BeNull();
    }

    // --- Фильтр [ProjectScope(From = Persona)]: право персоны — тем же резолвером ---

    private (ActionExecutingContext ctx, bool[] nextCalled, ActionExecutionDelegate next) Ctx(
        ProjectScopeAttribute attr, string? caller, string? routeId)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<IProjectAccess>(_access).BuildServiceProvider(),
        };
        if (caller is not null)
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", caller)], "test"));
        var route = new RouteData();
        if (routeId is not null) route.Values["id"] = routeId;
        var ctx = new ActionExecutingContext(new ActionContext(http, route, new ActionDescriptor()),
            [], new Dictionary<string, object?>(), controller: new object());
        var called = new[] { false };
        ActionExecutionDelegate next = () =>
        {
            called[0] = true;
            return Task.FromResult(new ActionExecutedContext(ctx, [], new object()));
        };
        return (ctx, called, next);
    }

    private static ObjectResult Denied(ActionExecutingContext ctx) => (ObjectResult)ctx.Result!;

    private static string? ErrorText(ObjectResult result) =>
        result.Value?.GetType().GetProperty("error")?.GetValue(result.Value) as string;

    [Fact]
    public async Task Фильтр_Персона_Читатель_RunTurn_403_ТелоНеВызвано()
    {
        var persona = ProjectPersona();
        var attr = new ProjectScopeAttribute { From = ScopeSource.Persona, Right = ProjectRight.RunTurn };
        var (ctx, called, next) = Ctx(attr, _readerId, persona.Id);
        await attr.OnActionExecutionAsync(ctx, next);

        // Персону проекта читатель видит — отказ правом, а не статусом (§2, S2d-1c)
        called[0].Should().BeFalse();
        Denied(ctx).StatusCode.Should().Be(403);
        ErrorText(Denied(ctx)).Should().Be("Нет права вести ход");
    }

    [Fact]
    public async Task Фильтр_Персона_Посторонний_RunTurn_404()
    {
        var persona = ProjectPersona();
        var attr = new ProjectScopeAttribute { From = ScopeSource.Persona, Right = ProjectRight.RunTurn };
        var (ctx, called, next) = Ctx(attr, _otherId, persona.Id);
        await attr.OnActionExecutionAsync(ctx, next);

        called[0].Should().BeFalse();
        Denied(ctx).StatusCode.Should().Be(404);
    }

    [Theory]
    [InlineData(ProjectRight.Read)]
    [InlineData(ProjectRight.RunTurn)]
    public async Task Фильтр_Персона_Несуществующая_404_ДажеВладельцу(ProjectRight right)
    {
        // W3-B1a, минор ревью: 404 несуществующей персоны был покрыт только посторонним.
        // Владелец и несуществующая персона — то же «нет объекта», существование не раскрываем;
        // для RunTurn и VisibleWithRead даёт null (той же точки резолва), поэтому не 403.
        var attr = new ProjectScopeAttribute { From = ScopeSource.Persona, Right = right };
        var (ctx, called, next) = Ctx(attr, _ownerId, "нет-такой-персоны");
        await attr.OnActionExecutionAsync(ctx, next);

        called[0].Should().BeFalse();
        Denied(ctx).StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Фильтр_Персона_Редактор_RunTurn_ОбластьВHttpContext_КлючВладелец()
    {
        var persona = ProjectPersona();
        var attr = new ProjectScopeAttribute { From = ScopeSource.Persona, Right = ProjectRight.RunTurn };
        var (ctx, called, next) = Ctx(attr, _editorId, persona.Id);
        await attr.OnActionExecutionAsync(ctx, next);

        called[0].Should().BeTrue();
        ctx.Result.Should().BeNull();
        var scope = ctx.HttpContext.GetRequestScope();
        scope.CallerUserId.Should().Be(_editorId);
        scope.TenantOwnerId.Should().Be(_ownerId);
        scope.Role.Should().Be(ProjectRole.Editor);
    }

    [Fact]
    public async Task Фильтр_Персона_ГлобальнаяПерсонаВладельца_Редактор_404()
    {
        // И-3 через фильтр: личная персона владельца для участника — как несуществующая
        var persona = GlobalPersona();
        var attr = new ProjectScopeAttribute { From = ScopeSource.Persona, Right = ProjectRight.Read };
        var (ctx, called, next) = Ctx(attr, _editorId, persona.Id);
        await attr.OnActionExecutionAsync(ctx, next);

        called[0].Should().BeFalse();
        Denied(ctx).StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Фильтр_Персона_БезId_400_КлючПоУмолчанию_id()
    {
        var attr = new ProjectScopeAttribute { From = ScopeSource.Persona };
        var (ctx, called, next) = Ctx(attr, _ownerId, null);
        await attr.OnActionExecutionAsync(ctx, next);

        called[0].Should().BeFalse();
        Denied(ctx).StatusCode.Should().Be(400);
        ErrorText(Denied(ctx)).Should().Be("Не указан параметр id");
    }

    private sealed class NoSessions : ISessionDirectory
    {
        public Session? GetById(string id) => null;
        public IReadOnlyCollection<Session> GetAll() => [];
        public string? ResolveOwnerId(Session s) => null;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
        public bool SetWikiMark(string sessionId, string? mark) => false;
    }
}
