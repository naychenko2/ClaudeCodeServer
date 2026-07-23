using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Тесты чистой логики Claude-исполнителя: маппинг result → итог задачи и уведомления.
// Полный пайплайн (запуск сессии) требует claude.exe и здесь не гоняется.
public class TaskExecutionServiceTests
{
    private static ResultMessage Result(string subtype) =>
        new(subtype, DurationMs: 100, NumTurns: 1, Usage: null, TotalCostUsd: null);

    // ─── result → success/error ──────────────────────────────────────────────

    [Theory]
    [InlineData("success", true)]
    [InlineData("error", false)]
    [InlineData("error_max_turns", true)] // не "error" буквально — считается успехом хода
    public void IsSuccess_ПоSubtype(string subtype, bool expected)
    {
        TaskExecutionService.IsSuccess(Result(subtype)).Should().Be(expected);
    }

    // ─── Отслеживание сессии ─────────────────────────────────────────────────

    [Fact]
    public void IsAwaitingResult_ЗапущенаИБезИтога_True()
    {
        var task = new TaskItem { Title = "t", ClaudeStartedAt = DateTime.UtcNow };
        TaskExecutionService.IsAwaitingResult(task).Should().BeTrue();
    }

    [Fact]
    public void IsAwaitingResult_НеЗапускалась_False()
    {
        TaskExecutionService.IsAwaitingResult(new TaskItem { Title = "t" }).Should().BeFalse();
    }

    [Fact]
    public void IsAwaitingResult_ИтогУжеЕсть_False()
    {
        var task = new TaskItem
        {
            Title = "t",
            ClaudeStartedAt = DateTime.UtcNow,
            ClaudeResult = "success",
        };
        TaskExecutionService.IsAwaitingResult(task).Should().BeFalse();
    }

    // ─── Уведомления ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildResultNotification_УспехИЗадачаDone_ЧистыйЗаголовок()
    {
        var task = new TaskItem { Title = "Задача", Status = TaskItemStatus.Done };

        var n = TaskExecutionService.BuildResultNotification(task, ok: true);

        // Title чистый (без вклейки исполнителя — персона идёт структурно через PersonaId)
        n.Title.Should().Be("Завершил работу над задачей");
        n.Body.Should().Be("Задача");
        n.Kind.Should().Be("success");
        n.PersonaId.Should().BeNull();
        n.Url.Should().Be(TaskSchedulerService.TaskUrl(task));
    }

    [Fact]
    public void BuildResultNotification_УспехНоЗадачаНеDone_ПроситПроверить()
    {
        // Claude завершил ход, но не вызвал tasks_complete — нужен взгляд пользователя
        var task = new TaskItem { Title = "Задача", Status = TaskItemStatus.InProgress };

        var n = TaskExecutionService.BuildResultNotification(task, ok: true);

        n.Body.Should().Be("Задача — проверь результат в чате");
    }

    [Fact]
    public void BuildResultNotification_Ошибка_ЗаголовокПроНеудачу()
    {
        var task = new TaskItem { Title = "Задача", Status = TaskItemStatus.InProgress };

        var n = TaskExecutionService.BuildResultNotification(task, ok: false);

        n.Title.Should().Be("Не смог выполнить задачу");
        n.Kind.Should().Be("claude");
    }

    [Fact]
    public void BuildWaitingNotification_permission_request_ЖдётОтвета()
    {
        var task = new TaskItem { Title = "Задача", ProjectId = "p1" };

        var n = TaskExecutionService.BuildWaitingNotification(task);

        n.Title.Should().Be("Ждёт ответа по задаче");
        n.Body.Should().Be("Задача");
        n.Kind.Should().Be("claude");
        n.ProjectId.Should().Be("p1");
        n.Url.Should().Be($"/project/p1/task/{task.Id}");
    }

    // ─── Промпт постановки ───────────────────────────────────────────────────

    [Fact]
    public void BuildPrompt_СодержитКонтекстЗадачиИПравила()
    {
        var task = new TaskItem
        {
            Title = "Починить сборку",
            Description = "Падает на CI",
            LinkedFiles = ["src/Program.cs"],
            Subtasks = [new TaskSubtask { Title = "Найти причину" }],
        };

        var prompt = TaskExecutionService.BuildPrompt(task);

        prompt.Should().Contain(task.Id);
        prompt.Should().Contain("# Починить сборку");
        prompt.Should().Contain("Падает на CI");
        prompt.Should().Contain("Найти причину").And.Contain(task.Subtasks[0].Id);
        prompt.Should().Contain("src/Program.cs");
        prompt.Should().Contain("tasks_complete");
        prompt.Should().Contain("tasks_toggle_subtask");
    }

    [Fact]
    public void BuildPrompt_БезПерсоны_ПрежнийФормат()
    {
        var prompt = TaskExecutionService.BuildPrompt(new TaskItem { Title = "t" });

        // Обратная совместимость: без персоны — прежний формат с блоком «Правила», без секций
        prompt.Should().Contain("Правила:");
        prompt.Should().NotContain("## ЗАДАЧА");
    }

    [Fact]
    public void BuildPrompt_СПерсоной_ШестьСекцийКонтракта()
    {
        var task = new TaskItem
        {
            Title = "Починить сборку",
            Description = "Падает на CI",
            LinkedFiles = ["src/Program.cs"],
            Subtasks = [new TaskSubtask { Title = "Найти причину" }],
        };
        var persona = new Persona { Name = "Вера", Role = "Планировщик" };

        var prompt = TaskExecutionService.BuildPrompt(task, persona);

        // Все 6 секций контракта, в порядке следования (КОНТЕКСТ — последняя:
        // блок заметок дописывается после и попадает в неё)
        string[] sections = ["## ЗАДАЧА", "## ОЖИДАЕМЫЙ РЕЗУЛЬТАТ", "## ИНСТРУМЕНТЫ",
            "## ОБЯЗАТЕЛЬНО", "## НЕЛЬЗЯ", "## КОНТЕКСТ"];
        var positions = sections.Select(s => prompt.IndexOf(s, StringComparison.Ordinal)).ToList();
        positions.Should().OnlyContain(p => p >= 0);
        positions.Should().BeInAscendingOrder();

        prompt.Should().Contain(task.Id);
        prompt.Should().Contain("Падает на CI");
        prompt.Should().Contain("Найти причину").And.Contain(task.Subtasks[0].Id);
        prompt.Should().Contain("src/Program.cs");
        prompt.Should().Contain("tasks_complete").And.Contain("tasks_toggle_subtask");
        // Дисциплина: не выходить за рамки, при невозможности — не завершать
        prompt.Should().Contain("Не выходи за рамки задачи");
        prompt.Should().Contain("не завершай задачу");
    }

    // ─── Уведомления от лица персоны ─────────────────────────────────────────
    // Персона теперь передаётся структурно (PersonaId), а не вклеивается в заголовок:
    // имя/роль/аватар денормализует NotificationService и показывает мета-строкой.

    [Fact]
    public void BuildResultNotification_СПерсоной_ПробрасываетPersonaId()
    {
        var task = new TaskItem { Title = "Задача", Status = TaskItemStatus.Done };
        var persona = new Persona { Name = "Вера", Role = "Планировщик" };

        var n = TaskExecutionService.BuildResultNotification(task, ok: true, persona);

        n.Title.Should().Be("Завершил работу над задачей");   // без имени в тексте
        n.PersonaId.Should().Be(persona.Id);
    }

    [Fact]
    public void BuildWaitingNotification_СПерсоной_ПробрасываетPersonaId()
    {
        var task = new TaskItem { Title = "Задача" };
        var persona = new Persona { Name = "Вера" };

        var n = TaskExecutionService.BuildWaitingNotification(task, persona);

        n.Title.Should().Be("Ждёт ответа по задаче");
        n.PersonaId.Should().Be(persona.Id);
    }

    // ─── Модель Z: доклад делегированной задачи в чат ────────────────────────

    [Fact]
    public void BuildDelegationReportText_СИтогом_МаркерПлюсResultMarkdown()
    {
        var task = new TaskItem { Title = "Починить сборку", ResultMarkdown = "Собрал, тесты зелёные." };

        var text = TaskExecutionService.BuildDelegationReportText(task);

        text.Should().StartWith(TaskExecutionService.DelegationReportMarker + "Починить сборку");
        text.Should().Contain("Собрал, тесты зелёные.");
    }

    [Fact]
    public void BuildDelegationReportText_БезИтога_Фолбэк()
    {
        var task = new TaskItem { Title = "Задача без итога", ResultMarkdown = null };

        var text = TaskExecutionService.BuildDelegationReportText(task);

        text.Should().Contain("(итог не указан)");
    }

    [Fact]
    public void BuildDelegatorReactionPrompt_СодержитИсполнителяЗадачуИИтог()
    {
        var task = new TaskItem { Title = "Починить сборку", ResultMarkdown = "Готово." };
        var executor = new Persona { Name = "Вера", Role = "Тестировщик" };

        var prompt = TaskExecutionService.BuildDelegatorReactionPrompt(task, executor);

        prompt.Should().Contain("Тестировщик (Вера)");
        prompt.Should().Contain("Починить сборку");
        prompt.Should().Contain("Готово.");
        prompt.Should().Contain("Отреагируй");
    }
}
