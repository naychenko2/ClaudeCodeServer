using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Desktop;

/// <summary>
/// Билет браузера к localhost-API агента (ADR-016, задача 4.2): выдаётся веб-сессии владельца
/// только на локальный проект, живёт коротко, интроспекцию проходит только у своего
/// устройства и пока проект привязан к нему. Хаб отдаёт интроспекцию и принимает
/// донесения ватчера лишь по проектам этого устройства.
/// </summary>
public sealed class AgentTicketTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Device = "device-1";

    private readonly Dictionary<string, Project> _projects = new();
    private readonly Clock _clock = new(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly AgentTicketService _tickets;
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ccs_ticket_" + Guid.NewGuid().ToString("N"));

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public AgentTicketTests()
    {
        var pm = new Mock<IProjectManager>();
        pm.Setup(p => p.GetById(It.IsAny<string>())).Returns((string id) => _projects.GetValueOrDefault(id));
        _tickets = new AgentTicketService(pm.Object, _clock);
        Add(new Project { Id = "local", OwnerId = Owner, RootPath = "/home/u/proj", DeviceId = Device });
        Add(new Project { Id = "server", OwnerId = Owner, RootPath = "/srv/proj" });
        Add(new Project { Id = "other-device", OwnerId = Owner, RootPath = "/x", DeviceId = "device-2" });
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* временная папка */ }
    }

    private void Add(Project p) => _projects[p.Id] = p;

    [Fact]
    public void ВыдачаИИнтроспекция_СвоимУстройством()
    {
        var ticket = _tickets.Issue(Owner, _projects["local"])!;

        var grant = _tickets.Introspect(Owner, Device, ticket.Ticket);

        ticket.DeviceId.Should().Be(Device);
        ticket.ExpiresAt.Should().Be(_clock.Now + DeviceAgentApi.TicketLifetime);
        grant.Should().Be(new AgentTicketIntrospection(Owner, "local", "/home/u/proj", ticket.ExpiresAt));
    }

    [Fact]
    public void НаСерверныйИЧужойПроект_БилетаНет()
    {
        _tickets.Issue(Owner, _projects["server"]).Should().BeNull();
        _tickets.Issue("stranger", _projects["local"]).Should().BeNull();
    }

    [Fact]
    public void ЧужоеУстройствоЧужойВладелецПоддельныйБилет_Null()
    {
        var ticket = _tickets.Issue(Owner, _projects["local"])!.Ticket;

        _tickets.Introspect(Owner, "device-2", ticket).Should().BeNull();
        _tickets.Introspect("stranger", Device, ticket).Should().BeNull();
        _tickets.Introspect(Owner, Device, ticket + "x").Should().BeNull();
        _tickets.Introspect(Owner, Device, "").Should().BeNull();
    }

    [Fact]
    public void Просроченный_Null()
    {
        var ticket = _tickets.Issue(Owner, _projects["local"])!.Ticket;

        _clock.Now += DeviceAgentApi.TicketLifetime;

        _tickets.Introspect(Owner, Device, ticket).Should().BeNull();
    }

    [Fact]
    public void ПроектПерепривязанИлиУдалён_БилетГаснет()
    {
        var ticket = _tickets.Issue(Owner, _projects["local"])!.Ticket;

        _projects["local"].DeviceId = "device-2";
        _tickets.Introspect(Owner, Device, ticket).Should().BeNull();

        _projects["local"].DeviceId = Device;
        _projects.Remove("local");
        _tickets.Introspect(Owner, Device, ticket).Should().BeNull();
    }

    [Fact]
    public void ПроектПерепривязан_НовоеУстройствоСтарыйБилетНеПолучает()
    {
        var ticket = _tickets.Issue(Owner, _projects["local"])!.Ticket;

        _projects["local"].DeviceId = "device-2";

        _tickets.Introspect(Owner, "device-2", ticket).Should().BeNull();
    }

    // ---------- хаб ----------

    private DeviceHub NewHub(IProjectFilesChangedNotifier notifier, string deviceId = Device)
    {
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns("conn");
        context.SetupGet(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(DesktopProtocol.OwnerIdClaim, Owner), new Claim(DesktopProtocol.DeviceIdClaim, deviceId)], "device-token")));
        var router = new DesktopCallRouter(Mock.Of<IDeviceCommandSender>(), [], NullLogger<DesktopCallRouter>.Instance);
        var exec = new DeviceExecChannel(new DeviceRegistry(_temp), router,
            new DeviceHarnessPolicy(new ConfigurationBuilder().Build()), Mock.Of<IDeviceExecOpenSender>(),
            NullLogger<DeviceExecChannel>.Instance);
        return new DeviceHub(router, exec, NullLogger<DeviceHub>.Instance, _tickets, notifier) { Context = context.Object };
    }

    [Fact]
    public void Хаб_ИнтроспекцияТолькоСвоемуУстройству()
    {
        var ticket = _tickets.Issue(Owner, _projects["local"])!.Ticket;

        NewHub(Mock.Of<IProjectFilesChangedNotifier>()).IntrospectAgentTicket(ticket)!.ProjectId.Should().Be("local");
        NewHub(Mock.Of<IProjectFilesChangedNotifier>(), "device-2").IntrospectAgentTicket(ticket).Should().BeNull();
    }

    [Fact]
    public async Task Хаб_ДонесениеВатчера_ТолькоПоПроектуЭтогоУстройства()
    {
        var notifier = new Mock<IProjectFilesChangedNotifier>();
        var hub = NewHub(notifier.Object);

        await hub.ProjectFilesChanged(new DeviceFilesChanged("local", ["src/a.cs"], false));
        var foreign = () => hub.ProjectFilesChanged(new DeviceFilesChanged("other-device", ["x"], false));
        var server = () => hub.ProjectFilesChanged(new DeviceFilesChanged("server", ["x"], false));

        await foreign.Should().ThrowAsync<HubException>();
        await server.Should().ThrowAsync<HubException>();
        notifier.Verify(n => n.FilesChangedAsync("local", It.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "src/a.cs" })), false), Times.Once);
        notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Хаб_ЛавинаПутей_ПолнаяПересинхронизация()
    {
        var notifier = new Mock<IProjectFilesChangedNotifier>();
        var paths = Enumerable.Range(0, DeviceAgentApi.MaxChangedPaths + 1).Select(i => $"f{i}").ToArray();

        await NewHub(notifier.Object).ProjectFilesChanged(new DeviceFilesChanged("local", paths, false));

        notifier.Verify(n => n.FilesChangedAsync("local", It.Is<IReadOnlyList<string>>(p => p.Count == 0), true), Times.Once);
    }

    // ---------- контроллер ----------

    private DeviceAgentTicketController NewController(bool flagOn, bool online, bool serviceToken = false)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_temp, "projects.json"),
            ["DefaultProjectsPath"] = _temp,
        }).Build();
        Directory.CreateDirectory(_temp);
        var users = new UserStore(config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var owner = users.Add("u_" + Guid.NewGuid().ToString("N")[..8], "pwd", "admin");
        if (flagOn) owner.FeatureFlags = new() { [FeatureFlagKeys.LocalProjects] = true };
        var projects = new ProjectManager(config, users, new AppSettingsService(config));
        var local = projects.CreateLocal("Локальный", "/home/u/proj", owner.Id, Device);
        projects.Create("Серверный", Path.Combine(_temp, "srv"), owner.Id, "u", createDirectory: true);
        _projects["ctl-local"] = local;

        var exec = new Mock<IDeviceExecChannel>();
        exec.Setup(e => e.GetStatus(owner.Id, Device)).Returns(new DeviceExecStatus(Device, "ноутбук", online, "linux", "1", "1", "1",
            [DeviceCapabilities.Exec, DeviceCapabilities.Files], true, null));
        var pm = new Mock<IProjectManager>();
        pm.Setup(p => p.GetById(It.IsAny<string>())).Returns((string id) => projects.GetById(id));

        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, owner.Id) };
        if (serviceToken) claims.Add(new Claim(JwtService.TokenKindClaim, JwtService.ServiceTokenKind));
        return new DeviceAgentTicketController(projects, new AgentTicketService(pm.Object, _clock),
            new FeatureFlagService(users), exec.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "jwt")) },
            },
        };
    }

    [Fact]
    public void Контроллер_ВыдаётБилетНаЛокальныйПроект()
    {
        var ctl = NewController(flagOn: true, online: true);

        var result = ctl.Issue(_projects["ctl-local"].Id);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public void Контроллер_Отказы_СервисныйТокенФлагОфлайнСерверныйПроект()
    {
        (NewController(true, true, serviceToken: true).Issue(_projects["ctl-local"].Id) as ObjectResult)!.StatusCode.Should().Be(403);
        (NewController(false, true).Issue(_projects["ctl-local"].Id) as ObjectResult)!.StatusCode.Should().Be(403);
        (NewController(true, false).Issue(_projects["ctl-local"].Id) as ObjectResult)!.StatusCode.Should().Be(409);
        NewController(true, true).Issue("nope").Should().BeOfType<NotFoundResult>();
    }
}
