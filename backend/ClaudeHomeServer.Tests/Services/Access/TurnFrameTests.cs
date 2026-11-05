using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Access;

/// <summary>
/// Рамка хода (S2a-2, разрез §И.3 в редакции решений 29.10 и S2d-1b): в общем проекте — узкая при любом
/// акторе (общий чат владельца, участники, неизвестный актор), Wide — только приватному чату владельца
/// при акторе-владельце; в личном проекте — широкая и только при известном арендаторе
/// (иначе — Narrow, fail-closed).
/// Таблица приёмки S2d-1b: личный проект — Wide; общий чат владельца — Narrow; приватный чат
/// владельца с актором-владельцем — Wide; приватный чат редактора — Narrow; приватный чат
/// владельца при неизвестном акторе — Narrow.
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

    /// <summary>Приватный чат общего проекта с указанным автором (S2d-1: Session.PrivateToUserId).</summary>
    private static Session PrivateChat(string authorId) =>
        new() { ProjectId = "p", OwnerId = TenantId, PrivateToUserId = authorId };

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

    // --- строка 2 таблицы: общий чат (без признака приватности) — Narrow и при акторе-владельце (29.10)

    [Fact]
    public void ОбщийЧатВладельца_РамкаУзкая_Решение29_10()
    {
        // Чат без признака приватности (PrivateToUserId null) — общий: вердикт тот же, что без чата.
        var project = SharedProject((EditorId, ProjectRole.Editor));
        TurnFrame.Resolve(project, SharedChat(), TenantId, TenantId).Should().Be(TurnFrameKind.Narrow);
        TurnFrame.Resolve(project, SharedChat(), TenantId, EditorId).Should().Be(TurnFrameKind.Narrow);
    }

    // --- строки 3–5 таблицы: приватный чат общего проекта (S2d-1b) — Wide только владельцу в своём приватном

    [Fact]
    public void ПриватныйЧатВладельцаСАкторомВладельцем_РамкаШирокая()
    {
        // Рамка открывает личное пространство арендатора — в его собственном приватном чате
        // оно своё: Wide (правило S2d-1b: PrivateToUserId == tenant && actor == tenant).
        var project = SharedProject((EditorId, ProjectRole.Editor), (ReaderId, ProjectRole.Reader));
        TurnFrame.Resolve(project, PrivateChat(TenantId), TenantId, TenantId).Should().Be(TurnFrameKind.Wide);
    }

    [Theory]
    [InlineData(EditorId)]
    [InlineData(TenantId)]
    public void ПриватныйЧатРедактора_РамкаУзкая(string actorId)
    {
        // Рамка открывает личное пространство арендатора (владельца), не редактора: в его приватном
        // чате она отдала бы ему всё личное владельца. Личное пространство редактора под токеном
        // арендатора недостижимо — Narrow (ограничение, в плашку). И при акторе-владельце:
        // чужой приватный он не видит (S2d-1), и рамка его не открывает.
        var project = SharedProject((EditorId, ProjectRole.Editor));
        TurnFrame.Resolve(project, PrivateChat(EditorId), TenantId, actorId).Should().Be(TurnFrameKind.Narrow);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ПриватныйЧатВладельцаПриНеизвестномАкторе_РамкаУзкая_FailClosed(string? actorId)
    {
        // Фоновое действие или исполнитель без инициатора не получает личное пространство владельца
        // и в его собственном приватном чате (fail-closed).
        var project = SharedProject((ReaderId, ProjectRole.Reader));
        TurnFrame.Resolve(project, PrivateChat(TenantId), TenantId, actorId).Should().Be(TurnFrameKind.Narrow);
    }

    // --- F2-O3 (финальное ревью «Общих проектов»): приватный чат с автором не-арендатором —
    // Narrow независимо от формы проекта; отзыв последнего участника не расширяет рамку

    [Theory]
    [InlineData(OtherId)]  // актор — сам отозванный
    [InlineData(TenantId)] // актор — владелец проекта
    [InlineData(null)]     // актор неизвестен
    public void ОтзывПоследнегоУчастника_ПриватныйЧатОтозванного_РамкаНеРасширяется(string? actorId)
    {
        // После отзыва последнего участника проект формально становится «личным» (Members пуст,
        // нормализован в null), но чат остаётся приватным отозванного: ход в нём не должен
        // получить личное пространство владельца (projects_list, files_read других проектов,
        // search_unified). Приватность чата важнее формы проекта.
        var project = PersonalProject();
        project.Members = [];
        TurnFrame.Resolve(project, PrivateChat(OtherId), TenantId, actorId).Should().Be(TurnFrameKind.Narrow);
    }

    [Fact]
    public void ОтзывПоследнегоУчастника_ПриватныйЧатВладельца_РамкаШирокая()
    {
        // Граница нового правила: приоритет приватности играет только когда автор — не арендатор.
        // Приватный чат владельца в опустевшем проекте остаётся Wide: пространство своё.
        var project = PersonalProject();
        project.Members = [];
        TurnFrame.Resolve(project, PrivateChat(TenantId), TenantId, TenantId).Should().Be(TurnFrameKind.Wide);
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
