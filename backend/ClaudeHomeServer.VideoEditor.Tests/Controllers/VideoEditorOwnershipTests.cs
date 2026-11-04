using System.Security.Claims;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Controllers;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Controllers;

// «Свой/чужой» для ручек модуля «Видео» (S0-5d): свой читает сцены проекта по ключу арендатора, чужой получает
// тот же 404, что и несуществующий проект/чат, а нити чужого чата не просачиваются
public sealed class VideoEditorOwnershipTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Stranger = "stranger-9";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vown-" + Guid.NewGuid().ToString("N"));
    private readonly VideoThreadStore _store;
    private readonly VideoEditScopeGate _gate;

    public VideoEditorOwnershipTests()
    {
        _store = new VideoThreadStore(_root);
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById("p1")).Returns(new Project { Id = "p1", OwnerId = Owner, RootPath = "/p1" });
        var dir = new Mock<ISessionDirectory>();
        dir.Setup(d => d.GetById("chat")).Returns(new Session { Id = "chat", OwnerId = Owner, ProjectId = "p1" });
        var access = new Mock<IProjectAccess>();
        access.Setup(a => a.Resolve(Owner, "p1", It.IsAny<ProjectRight>()))
            .Returns(new RequestScope(Owner, Owner, ProjectRole.Owner));
        _gate = new VideoEditScopeGate(projects.Object, dir.Object, access.Object);
        _store.AddScene(Owner, "chat", "video/a",
            new VideoSceneSettingsDto(null, null, "т", "fal", "veo", 5, "16:9", false, 1), null);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // Ручки «сцены чата» используют только гейт и нити: остальные зависимости им не нужны
    private VideoEditorController Controller(string userId) =>
        new(_gate, null!, null!, new VideoJobThreads(_store, NullLogger<VideoJobThreads>.Instance), null!, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)], "test")),
                },
            },
        };

    [Fact]
    public void Свой_читает_сцены_чата_проекта()
    {
        var result = Controller(Owner).Scenes("p1", "chat");

        var state = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<VideoThreadsStateDto>().Subject;
        state.Scenes.Should().HaveCount(1);
    }

    [Fact]
    public void Чужой_получает_404_как_на_несуществующий_проект()
    {
        Controller(Stranger).Scenes("p1", "chat").Should().BeOfType<NotFoundObjectResult>();
        Controller(Owner).Scenes("p1", "no-such-chat").Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }
}
