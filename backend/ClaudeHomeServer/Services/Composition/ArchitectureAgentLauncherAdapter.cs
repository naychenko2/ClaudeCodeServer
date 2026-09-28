using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Tasks;

namespace ClaudeHomeServer.Services.Composition;

// Реализация IArchitectureAgentLauncher (Core) поверх TaskManager + ITaskExecutor
// (Main) — ровно путь TeamWaveService, новых механизмов нет: создать задачу, разослать
// task_changed, автозапуск исполнителя. Исполнитель — персона-архитектор проекта
// (Specialty == Planner, проектная предпочтительнее глобальной, как ResolvePlanner штаба),
// причём только та, кому arch_* реально доступны на запись — тот же предикат, что у гейта
// тулсета (привязка tool:architecture + не «Только чтение»); нет такой — задача БЕЗ персоны,
// обычный исполнитель Claude (решение Григория, не отказ). arch_* у него есть и без
// персоны: гейт BuildArchitectureContext deny-only по привязке.
public sealed class ArchitectureAgentLauncherAdapter(
    TaskManager tasks,
    PersonaManager personas,
    IPersonaServerToolGate toolGate,
    ITaskExecutor executor,
    ISessionBroadcaster broadcaster,
    ILogger<ArchitectureAgentLauncherAdapter> log) : IArchitectureAgentLauncher
{
    // Ключ привязки тулсета — копия ArchitectureToolset.ToolKey: Main сборку модуля
    // на компиляции не видит (динамический модуль), ссылаться на константу нельзя
    private const string ArchitectureToolKey = "architecture";

    // Проверка «сборка уже идёт» и создание задачи — одна критическая секция: иначе два
    // быстрых клика оба проходят проверку и заводят по задаче.
    private readonly object _gate = new();

    // ct осознанно не передаётся дальше: задача, созданная под замком, должна дойти до
    // task_changed и автозапуска, даже если клиент отвалился, — иначе она повиснет
    // незапущенной и заблокирует следующие сборки меткой arch-build.
    public async Task<ArchitectureAgentLaunch> LaunchAsync(
        string projectId, string ownerId, ArchitectureAgentBrief brief, CancellationToken ct)
    {
        var architect = ResolveArchitect(ownerId, projectId);

        TaskItem task;
        lock (_gate)
        {
            // Незавершённая задача агентной сборки в этом проекте — вторую не заводим, а
            // отдаём её id (ссылка в UI)
            if (FindBlocking(projectId, ownerId) is { } blocking)
                return new ArchitectureAgentLaunch(blocking.Id, blocking.PersonaId, "build_in_progress");

            task = tasks.Create(projectId, ownerId, new CreateTaskRequest(
                Title: brief.Title,
                Description: brief.Description,
                Priority: TaskItemPriority.Medium,
                Assignee: TaskItemAssignee.Claude,
                // null — обычный исполнитель Claude; PersonaId подразумевает Assignee=Claude
                PersonaId: architect?.Id,
                Labels: [.. brief.Labels],
                // Сборка редкая и тяжёлая — сильный слот
                ModelTier: "strong"));
        }

        await broadcaster.ToOwner(ownerId, new TaskChangedMessage("created", task));
        try
        {
            await executor.ExecuteAsync(task, auto: true);
            return new ArchitectureAgentLaunch(task.Id, architect?.Id, null);
        }
        catch (Exception ex)
        {
            // Задача остаётся: её можно запустить руками из карточки
            log.LogError(ex, "Исполнитель сборки архитектуры по задаче {TaskId} не стартовал", task.Id);
            return new ArchitectureAgentLaunch(task.Id, architect?.Id, "launch_failed");
        }
    }

    public string? IsBuildInProgress(string projectId, string ownerId) =>
        FindBlocking(projectId, ownerId)?.Id;

    // Блокирует любая незавершённая задача с меткой — включая остановленные исполнителем
    // (ExecutorStoppedAt) и брошенные человеком (DroppedByHumanAt): их перезапускают из
    // карточки, и без блокировки агентов стало бы два.
    private TaskItem? FindBlocking(string projectId, string ownerId) =>
        tasks.GetByProject(projectId).FirstOrDefault(t => t.OwnerId == ownerId
            && t.Status != TaskItemStatus.Done
            && t.Labels.Contains(IArchitectureAgentLauncher.BuildLabel, StringComparer.OrdinalIgnoreCase));

    private Persona? ResolveArchitect(string ownerId, string projectId) =>
        personas.GetForContext(ownerId, projectId)
            .Where(p => p.Specialty == PersonaSpecialty.Planner
                && p.Access != PersonaAccess.ReadOnly
                && toolGate.IsServerToolEnabled(ownerId, p, ArchitectureToolKey))
            .OrderBy(p => p.ProjectId == projectId ? 0 : 1)
            .FirstOrDefault();
}
