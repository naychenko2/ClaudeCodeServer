using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Ручки LoRA персонажа висят на двух маршрутах (проект и личный чат). sessionId/projectId из query
/// не должны подменять область маршрута: на проектном маршруте работает проект, на чатовом — чат.
/// </summary>
public sealed class CharacterLoraRouteBindingTests : IDisposable
{
    private const string Owner = "owner-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lora_route_" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IProjectManager> _projects = new();
    private readonly Mock<ISessionDirectory> _directory = new();
    private readonly Mock<IProjectAccess> _access = new();

    public CharacterLoraRouteBindingTests()
    {
        Directory.CreateDirectory(_root);
        _projects.Setup(p => p.GetById("P")).Returns(new Project { Id = "P", OwnerId = Owner, RootPath = _root });
        _access.Setup(a => a.Resolve(Owner, "P", It.IsAny<ProjectRight>()))
            .Returns(new RequestScope(Owner, Owner, ProjectRole.Owner));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* не критично */ }
    }

    private ControllerContext Context(params (string Key, string Value)[] route)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Owner)], "test")),
        };
        var context = new ControllerContext(new Microsoft.AspNetCore.Mvc.ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()));
        foreach (var (key, value) in route) context.RouteData.Values[key] = value;
        return context;
    }

    private ImageEditScopeGate Gate() => new(_projects.Object, _directory.Object, _access.Object);

    private CharacterLoraTrainController Train(ControllerContext context) =>
        new(Gate(), new CharacterTrainingPhotos(new ConfigurationBuilder().Build()),
            new CharacterLoraRuns(NullLogger<CharacterLoraRuns>.Instance), new ConfigurationBuilder().Build())
        { ControllerContext = context };

    [Fact]
    public void Train_проектный_маршрут_игнорирует_sessionId_из_query()
    {
        var controller = Train(Context(("projectId", "P"), ("slug", "net-takogo")));

        var result = controller.Photos("P", "S", "net-takogo");

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        _access.Verify(a => a.Resolve(Owner, "P", It.IsAny<ProjectRight>()), Times.Once);
        _directory.Verify(d => d.GetById(It.IsAny<string>()), Times.Never, "чат из query не должен разбираться");
    }

    [Fact]
    public void Train_чатовый_маршрут_игнорирует_projectId_из_query()
    {
        _directory.Setup(d => d.GetById("S")).Returns((Session?)null);
        var controller = Train(Context(("sessionId", "S"), ("slug", "net-takogo")));

        controller.Photos("P", "S", "net-takogo");

        _directory.Verify(d => d.GetById("S"), Times.Once);
        _access.Verify(a => a.Resolve(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ProjectRight>()), Times.Never);
    }
}
