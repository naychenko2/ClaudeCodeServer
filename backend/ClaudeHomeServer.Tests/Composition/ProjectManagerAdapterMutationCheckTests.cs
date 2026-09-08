using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Terminal;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Composition;

// Мутационная проверка Ф5.1 (критерий готовности #5): подменяем seam IProjectManager
// на stub, отдающий null/пустоту по всем 4 методам. Если шов действительно
// узкое горлышко для чтения — вертикали, зовущие seam, должны падать
// на expected-местах (TerminalService.CreateAsync бросает HubException
// «Проект не найден», потому что _projects.GetById отдаёт null).
public class ProjectManagerAdapterMutationCheckTests
{
    [Fact]
    public void StubReturnsNullForAllFourMethods()
    {
        // Sanity-check stub: 4 метода шва согласованы.
        var stub = new NullProjectManagerStub();
        stub.GetById("anything").Should().BeNull();
        stub.GetAll().Should().BeEmpty();
        stub.GetByOwner("anyone").Should().BeEmpty();
        stub.GetByRootPath("anywhere").Should().BeEmpty();
    }

    [Fact]
    public async Task EndToEnd_TerminalCreateAsync_ThrowsOnNullProject()
    {
        // Stub seam, возвращающий null. Это и есть «подменить seam на возвращающий
        // null» из критерия готовности #5.
        IProjectManager seam = new NullProjectManagerStub();

        // TerminalService.CreateAsync зовёт _projects.GetById(projectId) и бросает
        // HubException, если проекта нет. Stub отдаёт null → ожидаем HubException.
        var terminal = new TerminalService(
            new Mock<IHubContext<TerminalHub>>().Object,
            seam,
            NullLogger<TerminalService>.Instance,
            new Mock<ILauncherFactory>().Object);

        var act = async () => await terminal.CreateAsync("does-not-exist", "user", "conn", 80, 24);
        await act.Should().ThrowAsync<HubException>()
            .WithMessage("Проект не найден");
    }

    private sealed class NullProjectManagerStub : IProjectManager
    {
        public Project? GetById(string id) => null;
        public IReadOnlyCollection<Project> GetByOwner(string userId) => Array.Empty<Project>();
        public IReadOnlyCollection<Project> GetAll() => Array.Empty<Project>();
        public IReadOnlyCollection<Project> GetByRootPath(string rootPath) => Array.Empty<Project>();
    }
}
