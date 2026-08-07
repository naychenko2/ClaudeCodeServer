using ClaudeHomeServer.Services.Dossiers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Dossiers;

// Чистые предикторы DossierCaptureService — две критичные развилки ADR-004, вынесенные из
// ProcessCommitAsync ради тестируемости без тяжёлых зависимостей (SessionManager в hosted-сервисе
// не мокается — проектный паттерн). Покрывают critical-правку Глеба №1 (guard принадлежности)
// и major-правку №2 (правило переякорения при squash).
public class DossierCaptureLogicTests
{
    // §1 guard: трейлер принимается ТОЛЬКО если сессия принадлежит тому же владельцу и проекту.
    // Без этого одна папка, подключённая двумя владельцами, дырявит per-owner изоляцию: оба
    // capture-сервиса видят один коммит с трейлером чата A, и сервис B затянул бы реплики чужого чата.
    public static IEnumerable<object[]> GuardCases() => new[]
    {
        new object[] { "ownerA", "proj1", "ownerA", "proj1", true,  "свой чат — паспорт создаётся" },
        new object[] { "ownerB", "proj1", "ownerA", "proj1", false, "общая папка: чужой владелец — паспорта нет" },
        new object[] { "ownerA", "proj2", "ownerA", "proj1", false, "чужой проект — паспорта нет" },
        new object[] { null!, "proj1", "ownerA", "proj1", false, "глобальный/личный чат (OwnerId пуст) — fail-closed" },
        new object[] { "ownerA", null!, "ownerA", "proj1", false, "сессия без проекта — fail-closed" },
    };

    [Theory]
    [MemberData(nameof(GuardCases))]
    public void GuardПринадлежности(string? sessionOwner, string? sessionProject,
        string projectOwner, string projectId, bool expected, string _)
    {
        DossierCaptureService.SessionBelongsToProject(sessionOwner, sessionProject, projectOwner, projectId)
            .Should().Be(expected);
    }

    // §7: переякорение — ОБА условия вместе (subject старого в новом сообщении И старый sha
    // недостижим). Одной недостижимости мало (коммит в невлитой ветке), одного subject-матча
    // мало (amend без переписи предков).
    [Fact]
    public void Переякорение_SubjectМатчитИНедостижим_True()
    {
        DossierCaptureService.ShouldReanchor(subjectMatch: true, oldReachable: false)
            .Should().BeTrue("squash: subject старого в новом, старый sha недостижим");
    }

    [Fact]
    public void Переякорение_СтарыйДостижим_False_ОбычныйКоммит()
    {
        DossierCaptureService.ShouldReanchor(subjectMatch: true, oldReachable: true)
            .Should().BeFalse("старый sha достижим — это обычный второй коммит той же сессии, не перепись");
    }

    [Fact]
    public void Переякорение_SubjectНеМатчит_False_ДажеЕслиНедостижим()
    {
        DossierCaptureService.ShouldReanchor(subjectMatch: false, oldReachable: false)
            .Should().BeFalse("недостижим, но subject не упоминается — коммит в невлитой ветке, не squash");
    }

    [Fact]
    public void Переякорение_ОбаНет_False()
    {
        DossierCaptureService.ShouldReanchor(subjectMatch: false, oldReachable: true).Should().BeFalse();
    }
}
