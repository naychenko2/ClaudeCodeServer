using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Access;

/// <summary>
/// Рамка хода участника (S2a-2, разрез §И.3): чистая функция вердикта Wide/Narrow.
/// Широкая рамка — только когда инициатор достоверно арендатор; в общем проекте без
/// инициатора — узкая (fail-closed); в личном проекте без инициатора — широкая.
/// </summary>
public class TurnFrameTests
{
    private const string TenantId = "tenant-1";
    private const string OtherId = "other-2";
    private const string EditorId = "editor-1";
    private const string ReaderId = "reader-1";

    private static Project PersonalProject() => new() { Name = "p", RootPath = "/srv/p", OwnerId = TenantId };

    private static Project SharedProject(params (string UserId, ProjectRole Role)[] members)
    {
        var project = PersonalProject();
        project.Members = [.. members.Select(m => new ProjectMember { UserId = m.UserId, Role = m.Role, AddedBy = TenantId })];
        return project;
    }

    // --- актор достоверно арендатор → Wide

    [Fact]
    public void АкторРавенАрендатору_РамкаШирокая()
    {
        TurnFrame.Resolve(PersonalProject(), TenantId, TenantId).Should().Be(TurnFrameKind.Wide);
        TurnFrame.Resolve(SharedProject((EditorId, ProjectRole.Editor), (ReaderId, ProjectRole.Reader)), TenantId, TenantId)
            .Should().Be(TurnFrameKind.Wide);
    }

    // --- актор есть, но он не арендатор → Narrow (роль участника не важна)

    [Theory]
    [InlineData(EditorId)]
    [InlineData(ReaderId)]
    [InlineData(OtherId)]
    public void АкторНеАрендатор_РамкаУзкая(string actorId)
    {
        var project = SharedProject((EditorId, ProjectRole.Editor), (ReaderId, ProjectRole.Reader));
        TurnFrame.Resolve(project, TenantId, actorId).Should().Be(TurnFrameKind.Narrow);
    }

    [Fact]
    public void АкторНеАрендаторВЛичномПроекте_РамкаУзкая()
    {
        // Чужой актор и в личном проекте не получает личное пространство арендатора
        TurnFrame.Resolve(PersonalProject(), TenantId, OtherId).Should().Be(TurnFrameKind.Narrow);
    }

    // --- актора нет: в общем проекте — узкая (fail-closed), в личном — широкая

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void БезАктораВОбщемПроекте_РамкаУзкая_FailClosed(string? actorId)
    {
        var project = SharedProject((ReaderId, ProjectRole.Reader));
        TurnFrame.Resolve(project, TenantId, actorId).Should().Be(TurnFrameKind.Narrow);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void БезАктораВЛичномПроекте_РамкаШирокая(string? actorId)
    {
        // Защищать не от кого: всё пространство и так принадлежит арендатору
        TurnFrame.Resolve(PersonalProject(), TenantId, actorId).Should().Be(TurnFrameKind.Wide);
    }

    [Fact]
    public void БезАктораБезПроекта_РамкаШирокая_ЛичноеНеОбщийПроект()
    {
        // null-проект общим быть не может (общий всегда с участниками) — это личное пространство
        TurnFrame.Resolve(null, TenantId, null).Should().Be(TurnFrameKind.Wide);
    }

    [Fact]
    public void ПустойСписокУчастников_ПроектЛичный_БезАктораШирокая()
    {
        // Members нормализован в null (Project.Members): пустой список — не общий проект
        var project = PersonalProject();
        project.Members = [];
        TurnFrame.Resolve(project, TenantId, null).Should().Be(TurnFrameKind.Wide);
    }

    // --- арендатора нет при известном акторе → Narrow: что актор не чужой, нечем установить

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void БезАрендатораПриИзвестномАкторе_РамкаУзкая_FailClosed(string? tenantId)
    {
        TurnFrame.Resolve(PersonalProject(), tenantId, OtherId).Should().Be(TurnFrameKind.Narrow);
    }
}
