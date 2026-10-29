using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;
using FluentAssertions;
using System.Text.Json;

namespace ClaudeHomeServer.Tests.Services.Access;

/// <summary>
/// Core-примитив доступа (S0-1 + S1-1): роль по владению проектом и по списку участников
/// (Editor/Reader), матрица прав от роли (читатель не пишет), видимость чата = доступ к его
/// проекту (проектный чат) / владение самим чатом (чат вне проекта). Плюс контракт поля
/// Project.Members в projects.json: без участников поля в JSON нет.
/// </summary>
public class ProjectAccessTests
{
    private const string OwnerId = "owner-1";
    private const string OtherId = "other-2";
    private const string EditorId = "editor-1";
    private const string ReaderId = "reader-1";

    private static Project OwnedProject() => new() { Name = "p", RootPath = "/srv/p", OwnerId = OwnerId };
    private static Session ProjectChat() => new() { ProjectId = "p-1" };

    // Общий проект: участники по матрице разреза §2.1 (приглашает владелец)
    private static Project SharedProject(params (string UserId, ProjectRole Role)[] members)
    {
        var project = OwnedProject();
        project.Members = [.. members.Select(m => new ProjectMember { UserId = m.UserId, Role = m.Role, AddedBy = OwnerId })];
        return project;
    }

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

    // --- S1-1: роли из списка участников (Project.Members, разрез §2.1)

    [Fact]
    public void УчастникРедактор_ПолучаетРольEditor()
    {
        ProjectAccess.Check(SharedProject((EditorId, ProjectRole.Editor)), EditorId).Should().Be(ProjectRole.Editor);
    }

    [Fact]
    public void УчастникЧитатель_ПолучаетРольReader()
    {
        ProjectAccess.Check(SharedProject((ReaderId, ProjectRole.Reader)), ReaderId).Should().Be(ProjectRole.Reader);
    }

    [Fact]
    public void ВладелецОбщегоПроекта_ВсёРавноOwner_УчастникиЕгоНеМеняют()
    {
        // Роль владельца выводится из OwnerId, а не из списка: Members его не касается
        ProjectAccess.Check(SharedProject((ReaderId, ProjectRole.Reader)), OwnerId).Should().Be(ProjectRole.Owner);
    }

    [Fact]
    public void ЧужойВОбщемПроекте_РолиНет()
    {
        ProjectAccess.Check(SharedProject((EditorId, ProjectRole.Editor), (ReaderId, ProjectRole.Reader)), OtherId).Should().BeNull();
    }

    [Fact]
    public void НесостоявшаясяРольУчастника_РолиНет()
    {
        // Owner в Members не пишется (его выводит OwnerId); мусорное значение не даёт доступа (fail-closed)
        ProjectAccess.Check(SharedProject((OtherId, ProjectRole.Owner)), OtherId).Should().BeNull();
    }

    [Fact]
    public void МатрицаПрав_ЧитательНеПишет_РедакторИВладелецПишут()
    {
        var project = SharedProject((EditorId, ProjectRole.Editor), (ReaderId, ProjectRole.Reader));
        ProjectAccess.Can(project, ReaderId, ProjectRight.Read).Should().BeTrue();
        ProjectAccess.Can(project, ReaderId, ProjectRight.Write).Should().BeFalse();
        ProjectAccess.Can(project, EditorId, ProjectRight.Read).Should().BeTrue();
        ProjectAccess.Can(project, EditorId, ProjectRight.Write).Should().BeTrue();
        ProjectAccess.Can(project, OwnerId, ProjectRight.Write).Should().BeTrue();
    }

    [Fact]
    public void МатрицаПрав_Configure_ТолькоВладелец_РедакторИЧитательНет()
    {
        var project = SharedProject((EditorId, ProjectRole.Editor), (ReaderId, ProjectRole.Reader));
        ProjectAccess.Can(project, OwnerId, ProjectRight.Configure).Should().BeTrue();
        ProjectAccess.Can(project, EditorId, ProjectRight.Configure).Should().BeFalse();
        ProjectAccess.Can(project, ReaderId, ProjectRight.Configure).Should().BeFalse();
        ProjectAccess.Can(project, OtherId, ProjectRight.Configure).Should().BeFalse();
    }

    [Fact]
    public void МатрицаПрав_RunTurn_ТолькоВладелец_РедакторИЧитательНет()
    {
        var project = SharedProject((EditorId, ProjectRole.Editor), (ReaderId, ProjectRole.Reader));
        ProjectAccess.Can(project, OwnerId, ProjectRight.RunTurn).Should().BeTrue();
        ProjectAccess.Can(project, EditorId, ProjectRight.RunTurn).Should().BeFalse();
        ProjectAccess.Can(project, ReaderId, ProjectRight.RunTurn).Should().BeFalse();
        ProjectAccess.Can(project, OtherId, ProjectRight.RunTurn).Should().BeFalse();
    }

    // --- S1-1: поле Members в projects.json — без участников поля нет (round-trip стора)

    [Fact]
    public void Стор_ОбычныйПроект_ПоляMembersНет_иЧитаетсяКакБезУчастников()
    {
        // Те же опции, что у стора: Save — default (ProjectManager.Save), Load — case-insensitive (ProjectManager.Load)
        var json = JsonSerializer.Serialize(OwnedProject());
        json.Should().NotContain("Members");
        var restored = JsonSerializer.Deserialize<Project>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        restored!.Members.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Стор_ПустойСписокУчастников_НормализуетсяВNull_ПоляВJSONНет()
    {
        var project = OwnedProject();
        project.Members = [];
        project.Members.Should().BeNull();
        JsonSerializer.Serialize(project).Should().NotContain("Members");
    }

    [Fact]
    public void Стор_СУчастниками_РондТриПолный()
    {
        var json = JsonSerializer.Serialize(SharedProject((ReaderId, ProjectRole.Reader)));
        json.Should().Contain("Members");
        var restored = JsonSerializer.Deserialize<Project>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        restored!.Members.Should().ContainSingle()
            .Which.Should().Match<ProjectMember>(m => m.UserId == ReaderId && m.Role == ProjectRole.Reader && m.AddedBy == OwnerId);
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
    public void CanSee_ОбщийПроект_ЛюбойУчастникВидитЕгоЧат()
    {
        // «Видеть» — верхняя строка матрицы §2.1: та же точка доступа, роль не различает
        var project = SharedProject((EditorId, ProjectRole.Editor), (ReaderId, ProjectRole.Reader));
        SessionAccess.CanSee(ProjectChat(), project, EditorId).Should().BeTrue();
        SessionAccess.CanSee(ProjectChat(), project, ReaderId).Should().BeTrue();
    }

    [Fact]
    public void CanSee_ЧатВнеПроекта_ПоВладениюСамимЧатом()
    {
        var chat = new Session { OwnerId = OwnerId };
        SessionAccess.CanSee(chat, null, OwnerId).Should().BeTrue();
        SessionAccess.CanSee(chat, null, OtherId).Should().BeFalse();
    }
}
