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

        n.Title.Should().Be("Claude завершил работу над задачей");
        n.Body.Should().Be("Задача");
        n.Kind.Should().Be("claude");
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

        n.Title.Should().Be("Claude не смог выполнить задачу");
    }

    [Fact]
    public void BuildWaitingNotification_permission_request_ЖдётОтвета()
    {
        var task = new TaskItem { Title = "Задача", ProjectId = "p1" };

        var n = TaskExecutionService.BuildWaitingNotification(task);

        n.Title.Should().Be("Claude ждёт ответа по задаче");
        n.Body.Should().Be("Задача");
        n.Kind.Should().Be("claude");
        n.Url.Should().Be($"/#/project/p1/task/{task.Id}");
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
}
