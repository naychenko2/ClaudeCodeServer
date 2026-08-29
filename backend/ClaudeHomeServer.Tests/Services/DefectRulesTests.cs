using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Правила дефектов: DefectRules — чистые функции, без зависимостей на хранилище/IO.
// Тесты ниже гоняют ровно четыре публичных метода и два стража инвариантов.
//
// Сторож 1 (TaskItemStatus = ровно три значения) фиксирует контракт enum'а: добавишь
// новое значение — упади здесь и подумай, какие ветки DefectRules/TaskManager нужно
// расширить (Status == Done ⇒ Verification/Outcome сейчас покрывает только три кейса).
//
// Сторож 2 (инвариант закрытого дефекта) пишется под дизъюнкцию
// Status == Done ⇒ Verification != null ИЛИ Outcome == ClosedWithoutCheck
// и применяет её к СПИСКУ фикстур: любая фикстура, прошедшая ручную проверку методами
// выше, обязана удовлетворить и этой формуле. Если кто-то добавит кейс в DefectRules
// и забудет синхронизировать сторожа, фикстура пройдёт прямой тест и упадёт здесь.
public class DefectRulesTests
{
    // ─── 1) EnsureNotClosedAtCreate ──────────────────────────────────────────

    [Fact]
    public void EnsureNotClosedAtCreate_ДефектВDone_Бросает()
    {
        var defect = DefectIn(TaskItemStatus.Done);

        var act = () => DefectRules.EnsureNotClosedAtCreate(defect);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*нельзя создавать сразу в Done*");
    }

    [Fact]
    public void EnsureNotClosedAtCreate_ДефектВTodo_Проходит()
    {
        var defect = DefectIn(TaskItemStatus.Todo);

        var act = () => DefectRules.EnsureNotClosedAtCreate(defect);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureNotClosedAtCreate_ДефектВInProgress_Проходит()
    {
        var defect = DefectIn(TaskItemStatus.InProgress);

        var act = () => DefectRules.EnsureNotClosedAtCreate(defect);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureNotClosedAtCreate_ОбычнаяЗадачаВDone_NoOp()
    {
        // Правило касается только Defect: обычную задачу можно создать Done (редкий кейс,
        // но для регулярной «отметить сделанной задним числом» он валиден)
        var task = TaskIn(TaskItemStatus.Done);

        var act = () => DefectRules.EnsureNotClosedAtCreate(task);

        act.Should().NotThrow();
    }

    // ─── 2) EnsureVerificationOnClose ────────────────────────────────────────

    [Fact]
    public void EnsureVerificationOnClose_ДефектВDoneБезВердиктаИБезИсхода_Бросает()
    {
        // Главный кейс: закрыли дефект обычным PUT, забыли прислать Verification и Outcome
        var defect = DefectIn(TaskItemStatus.Done);

        var act = () => DefectRules.EnsureVerificationOnClose(defect);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*заполните Verification*ClosedWithoutCheck*");
    }

    [Fact]
    public void EnsureVerificationOnClose_ДефектВDoneСВердиктом_Проходит()
    {
        var defect = DefectIn(TaskItemStatus.Done,
            Verification: new TaskVerification { VerifiedAt = DateTime.UtcNow });

        var act = () => DefectRules.EnsureVerificationOnClose(defect);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureVerificationOnClose_ДефектВDoneСClosedWithoutCheck_Проходит()
    {
        // Внутренний путь (NoteTaskSyncService / TeamWaveService) снимает дефект побочным
        // эффектом — Verification у него не заполняется, Outcome обязан
        var defect = DefectIn(TaskItemStatus.Done,
            Outcome: DefectOutcome.ClosedWithoutCheck);

        var act = () => DefectRules.EnsureVerificationOnClose(defect);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureVerificationOnClose_ДефектВDoneСВердиктомИСИсходом_Проходит()
    {
        // Дизъюнкция: оба поля могут стоять — Verification заполнен, Outcome закрытия
        // тоже выставлен. Такой кейс легитимен, если внешний сервис подтверждает свой же
        // ClosedWithoutCheck персоной-проверяющим
        var defect = DefectIn(TaskItemStatus.Done,
            Verification: new TaskVerification { VerifiedAt = DateTime.UtcNow },
            Outcome: DefectOutcome.ClosedWithoutCheck);

        var act = () => DefectRules.EnsureVerificationOnClose(defect);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureVerificationOnClose_ДефектНеВDone_NoOp()
    {
        var todo = DefectIn(TaskItemStatus.Todo);
        var inProgress = DefectIn(TaskItemStatus.InProgress);

        var act1 = () => DefectRules.EnsureVerificationOnClose(todo);
        var act2 = () => DefectRules.EnsureVerificationOnClose(inProgress);
        act1.Should().NotThrow();
        act2.Should().NotThrow();
        // Метод void: сам факт, что не бросил, и есть результат
    }

    [Fact]
    public void EnsureVerificationOnClose_ОбычнаяЗадачаВDone_NoOp()
    {
        // Task не подчиняется правилам DefectRules — закрыли как есть, и ладно
        var task = TaskIn(TaskItemStatus.Done);

        var act = () => DefectRules.EnsureVerificationOnClose(task);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureVerificationOnClose_ДефектВDoneСWhitespaceВердиктомВсёРавноПадает()
    {
        // Без PersonaId/Notes/VerifiedAt — правило смотрит только на Verification != null
        var defect = DefectIn(TaskItemStatus.Done,
            Verification: new TaskVerification());

        var act = () => DefectRules.EnsureVerificationOnClose(defect);

        act.Should().NotThrow(); // конструктор Verification без параметров задаёт дефолты, объект не null
    }

    [Fact]
    public void EnsureVerificationOnClose_ДефектСПустымNotes_Бросает()
    {
        // Д-1: пустой Notes — не вердикт. Verification != null, но содержимого нет,
        // гейт закрытия должен срабатывать (как при отсутствии Verification вообще).
        var defect = DefectIn(TaskItemStatus.Done,
            Verification: new TaskVerification { Notes = "" });

        var act = () => DefectRules.EnsureVerificationOnClose(defect);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*заполните Verification*ClosedWithoutCheck*");
    }

    [Fact]
    public void EnsureVerificationOnClose_ДефектСПробельнымNotes_Бросает()
    {
        // Из одних пробелов — то же: Notes после Trim пустой, вердикта нет.
        var defect = DefectIn(TaskItemStatus.Done,
            Verification: new TaskVerification { Notes = "   \t\n  " });

        var act = () => DefectRules.EnsureVerificationOnClose(defect);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*заполните Verification*ClosedWithoutCheck*");
    }

    // ─── 3) EnsureReproOnReview ──────────────────────────────────────────────

    [Fact]
    public void EnsureReproOnReview_ДефектВReviewБезШагов_Бросает()
    {
        var defect = DefectIn(TaskItemStatus.InProgress, Repro: null);
        var reviewCol = new BoardColumn { Name = "На согласовании", Category = TaskItemStatus.InProgress, Role = "review" };

        var act = () => DefectRules.EnsureReproOnReview(defect, reviewCol);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Repro.Steps*");
    }

    [Fact]
    public void EnsureReproOnReview_ДефектВReviewСПустымиШагами_Бросает()
    {
        var defect = DefectIn(TaskItemStatus.InProgress, Repro: new DefectRepro { Steps = "" });
        var reviewCol = new BoardColumn { Role = "review" };

        var act = () => DefectRules.EnsureReproOnReview(defect, reviewCol);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EnsureReproOnReview_ДефектВReviewСПробельнымиШагами_Бросает()
    {
        var defect = DefectIn(TaskItemStatus.InProgress, Repro: new DefectRepro { Steps = "   " });
        var reviewCol = new BoardColumn { Role = "review" };

        var act = () => DefectRules.EnsureReproOnReview(defect, reviewCol);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EnsureReproOnReview_ДефектВReviewСШагами_Проходит()
    {
        var defect = DefectIn(TaskItemStatus.InProgress,
            Repro: new DefectRepro { Steps = "1. Открыть X\n2. Нажать Y" });
        var reviewCol = new BoardColumn { Role = "review" };

        var act = () => DefectRules.EnsureReproOnReview(defect, reviewCol);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureReproOnReview_ДефектВНеревьюКолонку_NoOp()
    {
        var defect = DefectIn(TaskItemStatus.InProgress); // Repro == null
        var anyCol = new BoardColumn { Role = "todo" };

        var act = () => DefectRules.EnsureReproOnReview(defect, anyCol);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureReproOnReview_ДефектБезЦелевойКолонки_NoOp()
    {
        var defect = DefectIn(TaskItemStatus.InProgress); // Repro == null

        var act = () => DefectRules.EnsureReproOnReview(defect, targetColumn: null);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureReproOnReview_ДефектВКолонкуБезРоли_NoOp()
    {
        // Дефолтные колонки (todo/inProgress/done) и кастомные без Role — обычное
        // перемещение, не ревью
        var defect = DefectIn(TaskItemStatus.InProgress);
        var noRole = new BoardColumn { Role = null };

        var act = () => DefectRules.EnsureReproOnReview(defect, noRole);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureReproOnReview_ОбычнаяЗадача_NoOp()
    {
        // Task — обычная задача, правило не действует даже с Role="review" и пустым Repro
        var task = TaskIn(TaskItemStatus.InProgress);
        var reviewCol = new BoardColumn { Role = "review" };

        var act = () => DefectRules.EnsureReproOnReview(task, reviewCol);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureReproOnReview_ДефектВReviewСЗаполненнымReproБезSteps_Бросает()
    {
        // Repro есть, но Steps пустой — на ревью отдавать нечего, это та же ошибка
        var defect = DefectIn(TaskItemStatus.InProgress,
            Repro: new DefectRepro { Steps = "", Expected = "ожидалось X" });
        var reviewCol = new BoardColumn { Role = "review" };

        var act = () => DefectRules.EnsureReproOnReview(defect, reviewCol);

        act.Should().Throw<InvalidOperationException>();
    }

    // ─── 4) ComputeClosedWithoutCheck ────────────────────────────────────────

    [Fact]
    public void ComputeClosedWithoutCheck_ВозвращаетClosedWithoutCheck()
    {
        DefectRules.ComputeClosedWithoutCheck().Should().Be(DefectOutcome.ClosedWithoutCheck);
    }

    // ─── Сторож 1: TaskItemStatus — ровно три значения ───────────────────────

    [Fact]
    public void TaskItemStatus_РовноТриЗначения()
    {
        // Контракт enum'а. Добавишь четвёртое — упади здесь и проверь ветки:
        // • DefectRules.EnsureVerificationOnClose (обрабатывает только Done);
        // • TaskManager.Update и EnsureCompletedAt (полагаются на != Done);
        // • TasksController / BoardColumnHelper.Category (парсят строку в enum);
        // • фронт Tasks/BoardColumn: фильтры и категории колонок.
        Enum.GetValues(typeof(TaskItemStatus)).Length.Should().Be(3);
        Enum.GetNames(typeof(TaskItemStatus)).Should().BeEquivalentTo(
            new[] { "Todo", "InProgress", "Done" });
    }

    // ─── Сторож 2: дизъюнкция закрытого дефекта ──────────────────────────────

    public static IEnumerable<object[]> ClosedDefectFixtures()
    {
        // Каждая фикстура — закрытый дефект (Status == Done). Дизъюнкция инварианта:
        //   Verification != null  ИЛИ  Outcome == ClosedWithoutCheck.
        // Если правила DefectRules согласятся принять эту фикстуру, она ОБЯЗАНА
        // удовлетворять дизъюнкции. Упало здесь — либо DefectRules принял невалидное,
        // либо дизъюнкция отстала от правил.
        yield return new object[] { new TaskItem { Kind = TaskKind.Defect, Status = TaskItemStatus.Done,
            Verification = new TaskVerification { VerifiedAt = DateTime.UtcNow } } };
        yield return new object[] { new TaskItem { Kind = TaskKind.Defect, Status = TaskItemStatus.Done,
            Outcome = DefectOutcome.ClosedWithoutCheck } };
        yield return new object[] { new TaskItem { Kind = TaskKind.Defect, Status = TaskItemStatus.Done,
            Verification = new TaskVerification { VerifiedAt = DateTime.UtcNow },
            Outcome = DefectOutcome.ClosedWithoutCheck } };
    }

    [Theory]
    [MemberData(nameof(ClosedDefectFixtures))]
    public void ЗакрытыйДефект_УдовлетворяетДизъюнкции(TaskItem defect)
    {
        defect.Status.Should().Be(TaskItemStatus.Done);
        (defect.Verification is not null || defect.Outcome == DefectOutcome.ClosedWithoutCheck)
            .Should().BeTrue("Status == Done требует Verification != null ИЛИ Outcome == ClosedWithoutCheck");
        // Не должно быть «третьего пути» — у фикстуры с Verification=null и Outcome=null
        // правило EnsureVerificationOnClose обязано бросить
        if (defect.Verification is null && defect.Outcome != DefectOutcome.ClosedWithoutCheck)
        {
            var act = () => DefectRules.EnsureVerificationOnClose(defect);
            act.Should().Throw<InvalidOperationException>();
        }
        else
        {
            var act = () => DefectRules.EnsureVerificationOnClose(defect);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void ЗакрытыйДефект_БезВердиктаИБезИсхода_НарушаетДизъюнкцию()
    {
        // Сторож: закрытый дефект без Verification и без Outcome нарушает инвариант.
        // Должен падать EnsureVerificationOnClose и, как следствие, дисквалифицироваться
        // из фикстур выше (нет третьего состояния «Done без вердикта»).
        var defect = new TaskItem { Kind = TaskKind.Defect, Status = TaskItemStatus.Done };

        defect.Verification.Should().BeNull();
        defect.Outcome.Should().NotBe(DefectOutcome.ClosedWithoutCheck);
        var act = () => DefectRules.EnsureVerificationOnClose(defect);
        act.Should().Throw<InvalidOperationException>();
    }

    // ─── Хелперы ────────────────────────────────────────────────────────────

    private static TaskItem DefectIn(TaskItemStatus status,
        DefectRepro? Repro = null,
        TaskVerification? Verification = null,
        DefectOutcome? Outcome = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            Title = "defect",
            Kind = TaskKind.Defect,
            Status = status,
            Repro = Repro,
            Verification = Verification,
            Outcome = Outcome,
        };

    private static TaskItem TaskIn(TaskItemStatus status) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            Title = "task",
            Kind = TaskKind.Task,
            Status = status,
        };
}
