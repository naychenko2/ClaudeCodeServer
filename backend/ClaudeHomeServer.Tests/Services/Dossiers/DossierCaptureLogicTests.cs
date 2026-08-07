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

    // §6 opt-out: тумблер «Не сохранять решения из этого чата». Коммит из чата с opt-out → паспорта
    // нет; коммит из того же проекта без opt-out → паспорт есть. Композит с guard принадлежности.
    public static IEnumerable<object[]> OptOutCases() => new[]
    {
        new object[] { true,  false, true,  "свой чат, opt-out выкл — паспорт создаётся" },
        new object[] { true,  true,  false, "opt-out вкл — паспорта нет, даже если чат наш" },
        new object[] { false, true,  false, "чужой чат + opt-out — паспорта нет" },
        new object[] { false, false, false, "чужой чат без opt-out — паспорта нет (guard принадлежности)" },
    };

    [Theory]
    [MemberData(nameof(OptOutCases))]
    public void OptOutЧата_БлокируетЗахват(bool belongsToProject, bool optedOut, bool expected, string _)
    {
        DossierCaptureService.ShouldCaptureSession(belongsToProject, optedOut).Should().Be(expected);
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

    // §7 + MAJOR-фикс ревью: несколько похожих subject'ов при squash не должны дать несколько
    // записей на одном commitSha (инвариант «один коммит — один паспорт»). Subject матчится как
    // ОТДЕЛЬНАЯ строка тела (не подстрока), и среди сматченных выбирается один — самый длинный
    // (специфичный). На нём ProcessCommit и переякоривает ровно одно досье (break/return).
    [Fact]
    public void Переякорение_НесколькоПохожихSubject_ВыбираетСамоеУзкое()
    {
        // Все три subject-а после squash выложены в теле отдельными строками — line-match всех
        // трёх, но переякорение уходит на самый специфичный (длиннейший).
        var msg = "squash: fix bug in x/y\n\nfix bug\nfix bug in x\nfix bug in x/y";

        var best = DossierCaptureService.PickReanchorSubject(
            ["fix bug", "fix bug in x", "fix bug in x/y"], msg);

        best.Should().Be("fix bug in x/y");
    }

    // Сердце MAJOR-фикса: Contains без границ ловил бы «fix bug» внутри «fix bug in x/y». Line-match
    // (отдельная строка) эту ложную подстроку отбрасывает — subject, присутствующий только как часть
    // более длинной строки, не матчится.
    [Fact]
    public void Переякорение_SubjectТолькоПодстрока_НеМатчится()
    {
        var msg = "fix bug in x/y\n\nтут тело без нужной строки";

        DossierCaptureService.PickReanchorSubject(["fix bug"], msg).Should().BeNull(
            "«fix bug» присутствует лишь внутри «fix bug in x/y», не отдельной строкой");
        DossierCaptureService.PickReanchorSubject(["fix bug in x"], msg).Should().BeNull();
    }

    [Fact]
    public void Переякорение_ОдинSubjectСтрокой_Матчится()
    {
        var msg = "новый коммит\n\nfix bug\nчто-то ещё";

        DossierCaptureService.PickReanchorSubject(["fix bug"], msg).Should().Be("fix bug");
    }

    [Fact]
    public void Переякорение_ПустойСписокИлиСообщение_Null()
    {
        DossierCaptureService.PickReanchorSubject([], "любое сообщение").Should().BeNull();
        DossierCaptureService.PickReanchorSubject(["fix bug"], "").Should().BeNull();
    }
}
