using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Access;

/// <summary>
/// Core-примитив доступа (S0-1, «только владелец»): роль по владению проектом, права от
/// роли, видимость чата = доступ к его проекту (проектный чат) / владение самим чатом
/// (чат вне проекта).
/// </summary>
public class ProjectAccessTests
{
    private const string OwnerId = "owner-1";
    private const string OtherId = "other-2";

    private static Project OwnedProject() => new() { Name = "p", RootPath = "/srv/p", OwnerId = OwnerId };
    private static Session ProjectChat() => new() { ProjectId = "p-1" };

    // --- ProjectAccess.Check: роль по владению проектом

    [Fact]
    public void Владелец_ПолучаетРольOwner()
    {
        ProjectAccess.Check(OwnedProject(), OwnerId).Should().Be(ProjectRole.Owner);
    }

    [Fact]
    public void ЧужойПроект_РолиНет()
    {
        ProjectAccess.Check(OwnedProject(), OtherId).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ПустойUserId_РолиНет(string? userId)
    {
        ProjectAccess.Check(OwnedProject(), userId).Should().BeNull();
    }

    [Fact]
    public void ПроектНеНайден_РолиНет()
    {
        ProjectAccess.Check(null, OwnerId).Should().BeNull();
    }

    // --- ProjectAccess.Can: права от роли

    [Fact]
    public void Can_ВладелецИмеетПрава_ЧужойНикаких()
    {
        ProjectAccess.Can(OwnedProject(), OwnerId, ProjectRight.Read).Should().BeTrue();
        ProjectAccess.Can(OwnedProject(), OwnerId, ProjectRight.Write).Should().BeTrue();
        ProjectAccess.Can(OwnedProject(), OtherId, ProjectRight.Read).Should().BeFalse();
        ProjectAccess.Can(OwnedProject(), OtherId, ProjectRight.Write).Should().BeFalse();
    }

    // --- SessionAccess.CanSee: видимость = владение (те же 3 кейса, что у Check)

    [Fact]
    public void CanSee_ПроектныйЧат_ВладелецПроектаВидит()
    {
        SessionAccess.CanSee(ProjectChat(), OwnedProject(), OwnerId).Should().BeTrue();
    }

    [Fact]
    public void CanSee_ПроектныйЧат_ЧужойНеВидит()
    {
        SessionAccess.CanSee(ProjectChat(), OwnedProject(), OtherId).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CanSee_ПроектныйЧат_ПустойUserIdНеВидит(string? userId)
    {
        SessionAccess.CanSee(ProjectChat(), OwnedProject(), userId).Should().BeFalse();
    }

    [Fact]
    public void CanSee_ПроектныйЧатБезПроекта_НеВидитНикто()
    {
        // Проект не найден — резолвить владельца проектного чата нечем (fail-closed)
        SessionAccess.CanSee(ProjectChat(), null, OwnerId).Should().BeFalse();
    }

    [Fact]
    public void CanSee_ЧатВнеПроекта_ПоВладениюСамимЧатом()
    {
        var chat = new Session { OwnerId = OwnerId };
        SessionAccess.CanSee(chat, null, OwnerId).Should().BeTrue();
        SessionAccess.CanSee(chat, null, OtherId).Should().BeFalse();
    }
}
