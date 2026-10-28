using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

namespace ClaudeHomeServer.Tests.Services.Access;

/// <summary>
/// S0-2: фильтр <see cref="ProjectScopeAttribute"/> и <see cref="RequestScope"/>.
/// Главное: ключ хранилищ (TenantOwnerId) = владелец проекта, а не sub вызывающего;
/// без фильтра GetRequestScope бросает (fail-closed).
/// </summary>
public class RequestScopeTests
{
    private const string OwnerId = "owner-1";
    private const string OtherId = "other-2";

    // Тестовая реализация поверх чистых Core-примитивов
    private sealed class FakeAccess(Project? project, Session? session) : IProjectAccess
    {
        public RequestScope? Resolve(string? callerUserId, string? projectId, ProjectRight right = ProjectRight.Read) =>
            projectId == "p-1" && ProjectAccess.Can(project, callerUserId, right)
                ? new RequestScope(callerUserId!, project!.OwnerId!, ProjectAccess.Check(project, callerUserId)!.Value)
                : null;

        public RequestScope? ResolveSession(string? callerUserId, string? sessionId, ProjectRight right = ProjectRight.Read) =>
            session is not null && sessionId == "s-1" && session.ProjectId is not null
                ? Resolve(callerUserId, session.ProjectId, right)
                : null;
    }

    private static Project P() => new() { Name = "p", RootPath = "/srv/p", OwnerId = OwnerId };

    private static (ActionExecutingContext ctx, bool[] nextCalled, ActionExecutionDelegate next) Ctx(
        ProjectScopeAttribute attr, string? caller, string? routeKey, string? routeValue, IProjectAccess access)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(access).BuildServiceProvider(),
        };
        if (caller is not null)
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", caller)], "test"));
        var route = new RouteData();
        if (routeKey is not null) route.Values[routeKey] = routeValue;
        var ctx = new ActionExecutingContext(
            new ActionContext(http, route, new ActionDescriptor()),
            [], new Dictionary<string, object?>(), controller: new object());
        var called = new[] { false };
        ActionExecutionDelegate next = () =>
        {
            called[0] = true;
            return Task.FromResult(new ActionExecutedContext(ctx, [], new object()));
        };
        return (ctx, called, next);
    }

    [Fact]
    public async Task Владелец_TenantOwnerId_РавенВладельцуПроекта()
    {
        var (ctx, called, next) = Ctx(new ProjectScopeAttribute(), OwnerId, "projectId", "p-1", new FakeAccess(P(), null));
        await new ProjectScopeAttribute().OnActionExecutionAsync(ctx, next);

        called[0].Should().BeTrue();
        var scope = ctx.HttpContext.GetRequestScope();
        scope.TenantOwnerId.Should().Be(P().OwnerId);
        scope.CallerUserId.Should().Be(OwnerId);
        scope.Role.Should().Be(ProjectRole.Owner);
    }

    [Fact]
    public async Task ЧужойПроект_404_ТелоНеВызвано()
    {
        var (ctx, called, next) = Ctx(new ProjectScopeAttribute(), OtherId, "projectId", "p-1", new FakeAccess(P(), null));
        await new ProjectScopeAttribute().OnActionExecutionAsync(ctx, next);

        called[0].Should().BeFalse();
        ((ObjectResult)ctx.Result!).StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task НетIdПроекта_400()
    {
        var (ctx, called, next) = Ctx(new ProjectScopeAttribute(), OwnerId, null, null, new FakeAccess(P(), null));
        await new ProjectScopeAttribute().OnActionExecutionAsync(ctx, next);

        called[0].Should().BeFalse();
        ((ObjectResult)ctx.Result!).StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task НетSub_403()
    {
        var (ctx, called, next) = Ctx(new ProjectScopeAttribute(), null, "projectId", "p-1", new FakeAccess(P(), null));
        await new ProjectScopeAttribute().OnActionExecutionAsync(ctx, next);

        called[0].Should().BeFalse();
        ((ObjectResult)ctx.Result!).StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task РежимSession_ЧатПроекта_ДаётОбластьПроекта()
    {
        var attr = new ProjectScopeAttribute { From = ScopeSource.Session };
        var session = new Session { ProjectId = "p-1" };
        var (ctx, called, next) = Ctx(attr, OwnerId, "sessionId", "s-1", new FakeAccess(P(), session));
        await attr.OnActionExecutionAsync(ctx, next);

        called[0].Should().BeTrue();
        ctx.HttpContext.GetRequestScope().TenantOwnerId.Should().Be(OwnerId);
    }

    [Fact]
    public async Task РежимSession_ЧужойЧат_404()
    {
        var attr = new ProjectScopeAttribute { From = ScopeSource.Session };
        var (ctx, called, next) = Ctx(attr, OtherId, "sessionId", "s-1", new FakeAccess(P(), new Session { ProjectId = "p-1" }));
        await attr.OnActionExecutionAsync(ctx, next);

        called[0].Should().BeFalse();
        ((ObjectResult)ctx.Result!).StatusCode.Should().Be(404);
    }

    [Fact]
    public void GetRequestScope_БезФильтра_Бросает()
    {
        var act = () => new DefaultHttpContext().GetRequestScope();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SetRequestScope_ЯвныйРезолв_ЧитаетсяГет()
    {
        var http = new DefaultHttpContext();
        http.SetRequestScope(new RequestScope("u", "t", ProjectRole.Owner));
        http.GetRequestScope().TenantOwnerId.Should().Be("t");
    }
}
