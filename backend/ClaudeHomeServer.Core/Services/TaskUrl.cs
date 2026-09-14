using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Hash-диплинк на задачу: проектная → детали в проекте, личная → модалка в календаре.
// Stateless-формат URL, общий для всех потребителей (TaskExecutionService в Main
// формирует сообщения о завершении/напоминания, TaskSchedulerService в Tasks
// публикует уведомления планировщика, фронт матчит тот же шаблон).
//
// Перенесён из TaskSchedulerService.TaskUrl в Core (Этап 5, вынос Tasks): прежде
// `TaskExecutionService` в Main ради одной статической функции тянул в зависимости
// целую вертикаль Tasks — после выноса Tasks в отдельный csproj такая ссылка
// стала циклом и переехала в Core по той же логике, что и PersonaLabel/ModelTiers.
public static class TaskUrl
{
    public static string Of(TaskItem task) =>
        task.ProjectId is null
            ? $"/calendar/task/{task.Id}"
            : $"/project/{task.ProjectId}/task/{task.Id}";
}
