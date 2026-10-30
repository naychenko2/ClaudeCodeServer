using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Access;

/// <summary>
/// Рамка хода (S2a-2, разрез §И.3 в редакции решения 29.10): в общем проекте — узкая при любом
/// акторе, включая самого владельца и отсутствующего; широкая — только в личном проекте
/// и только при известном арендаторе (иначе — Narrow, fail-closed).
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

    private static Session SharedChat() => new() { ProjectId = "p", OwnerId = TenantId };

    // --- общий проект: узкая рамка всем, включая владельца (решение 29.10)

    [Fact]
    public void ВладелецВОбщемПроекте_РамкаУзкая_Решение29_10()
    {
        // Раньше здесь был Wide («инициатор достоверно владелец»). Решение 29.10: личное
        // пространство владельца не подмешивается и в его собственный чат общего проекта.
        var project = SharedProject((EditorId, ProjectRole.Editor), (ReaderId, ProjectRole.Reader));
        TurnFrame.Resolve(project, null, TenantId, TenantId).Should().Be(TurnFrameKind.Narrow);
    }

    [Theory]
    [InlineData(EditorId)]
    [InlineData(ReaderId)]
    [InlineData(OtherId)]
    public void УчастникОбщегоПроекта_РамкаУзкая(string actorId)
    {
        // Роль участника не важна: в узкой рамке нет широкой ни для кого.
        var project = SharedProject((EditorId, ProjectRole.Editor), (ReaderId, ProjectRole.Reader));
        TurnFrame.Resolve(project, null, TenantId, actorId).Should().Be(TurnFrameKind.Narrow);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void БезАктораВОбщемПроекте_РамкаУзкая_FailClosed(string? actorId)
    {
        var project = SharedProject((ReaderId, ProjectRole.Reader));
        TurnFrame.Resolve(project, null, TenantId, actorId).Should().Be(TurnFrameKind.Narrow);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void БезАрендатораВОбщемПроекте_РамкаУзкая_FailClosed(string? tenantId)
    {
        // Чьё пространство закрывать — неизвестно; в общем проекте вердикт не зависит от этого.
        var project = SharedProject((ReaderId, ProjectRole.Reader));
        TurnFrame.Resolve(project, null, tenantId, ReaderId).Should().Be(TurnFrameKind.Narrow);
    }

    // --- признака приватности в модели ещё нет (его введёт S2d-1): чат вердикт общего проекта не меняет

    [Fact]
    public void ЧатОбщегоПроектаБезПризнакаПриватности_РамкаУзкая()
    {
        // session участвует в сигнатуре под признак S2d-1; сейчас «не приватный» по умолчанию.
        var project = SharedProject((EditorId, ProjectRole.Editor));
        TurnFrame.Resolve(project, SharedChat(), TenantId, TenantId).Should().Be(TurnFrameKind.Narrow);
        TurnFrame.Resolve(project, SharedChat(), TenantId, EditorId).Should().Be(TurnFrameKind.Narrow);
    }

    // --- личный проект: широкая рамка — защищать не от чего

    [Theory]
    [InlineData(TenantId)]
    [InlineData(OtherId)]
    [InlineData(null)]
    [InlineData("")]
    public void ЛичныйПроект_РамкаШирокая(string? actorId)
    {
        // Чужого актора в личном проекте не бывает: доступ к нему даёт только владение
        // (ProjectAccess.Check). Второго guard'а рамка не добавляет: пространство — своё.
        TurnFrame.Resolve(PersonalProject(), null, TenantId, actorId).Should().Be(TurnFrameKind.Wide);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void БезАрендатораВЛичномПроекте_РамкаУзкая_FailClosed(string? tenantId)
    {
        // Wide открывает личное пространство арендатора; при неизвестном арендаторе — чьё оно,
        // не определено: fail-closed и при известном акторе (прежний тест
        // БезАрендатораПриИзвестномАкторе, снят без замены в S2a-2; возвращён по R-S2a-2).
        TurnFrame.Resolve(PersonalProject(), null, tenantId, TenantId).Should().Be(TurnFrameKind.Narrow);
    }

    [Fact]
    public void БезАктораБезПроекта_РамкаШирокая_ЛичноеНеОбщийПроект()
    {
        // null-проект общим быть не может (общий всегда с участниками) — это личное пространство
        TurnFrame.Resolve(null, null, TenantId, null).Should().Be(TurnFrameKind.Wide);
    }

    [Fact]
    public void ПустойСписокУчастников_ПроектЛичный_РамкаШирокая()
    {
        // Members нормализован в null (Project.Members): пустой список — не общий проект
        var project = PersonalProject();
        project.Members = [];
        TurnFrame.Resolve(project, null, TenantId, null).Should().Be(TurnFrameKind.Wide);
    }
}
