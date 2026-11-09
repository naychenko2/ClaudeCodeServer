using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Services.Tasks;

// Очередь фич (волна 6, разрез Александра 25.10 §4–§7): очередь — не сущность, а удержание
// Hold { reason: "queue" }; сервис лишь снимает голову очереди проекта, пока занято меньше лимита.
// Порядок и лимит — FeatureQueuePolicy, занятость слота — FeatureProjector.OccupiesSlot, снятие —
// тот же IFeatureHoldReleaser, что у DELETE hold. Лимит МЯГКИЙ: он ограничивает только автозапуск
// из очереди, второго гейта «можно ли запускать» нет. Fail-closed: проекция не строится или
// состояние неизвестно — никого не снимаем; автоснятие трогает только Hold.Reason == queue.
public sealed class FeatureQueueService(
    TaskManager tasks,
    FeatureProjector features,
    IFeatureHoldReleaser releaser,
    IConfiguration config,
    ILogger<FeatureQueueService> log) : IHostedService
{
    // Замок на пару (владелец, проект): два события подряд не снимают голову дважды. Проект без
    // id — своя группа (пустая строка в ключе).
    private readonly ConcurrentDictionary<(string Owner, string Project), SemaphoreSlim> _locks = new();

    // Тестовые швы: «замок занят» (вызов встал в ожидание) и точка внутри критической секции
    // (после снимка проекции, перед снятием) — там живёт окно между «посмотрели» и «сделали».
    internal Action? Contended;
    internal Func<Task>? InsideLock;
    // Источник проекции фич владельца; тест подменяет, чтобы проверить fail-closed при сбое проектора.
    internal Func<string, IReadOnlyList<FeatureRun>>? ProjectOverride;

    public Task StartAsync(CancellationToken ct)
    {
        tasks.TaskCompleted += OnTaskChanged;
        tasks.TaskDeleted += OnTaskChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        tasks.TaskCompleted -= OnTaskChanged;
        tasks.TaskDeleted -= OnTaskChanged;
        return Task.CompletedTask;
    }

    // События TaskManager синхронные и приходят из Update/Delete: работу уводим с потока вызова,
    // чтобы пересчёт не шёл внутри стека TaskManager.
    private void OnTaskChanged(TaskItem task) => _ = Task.Run(() => HandleTaskChangedAsync(task));

    // Закрытие или удаление задачи фичи может освободить слот (финал принят, фича закрыта или
    // удалена): пересчитываем очередь проекта фичи. Исключение наружу не выходит — обработчик
    // события не должен ронять TaskManager.
    internal async Task HandleTaskChangedAsync(TaskItem task)
    {
        try
        {
            if (task.OwnerId is null) return;
            var featureId = task.Role == TaskRoles.Feature ? task.Id : task.FeatureId;
            if (featureId is null) return;
            var projectId = tasks.GetById(featureId)?.ProjectId ?? task.ProjectId;
            await RebalanceAsync(task.OwnerId, projectId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Пересчёт очереди фич по задаче {TaskId} не удался", task.Id);
        }
    }

    // Сверка из тика планировщика (страховка потерянных событий): пересчитываем проекты владельца,
    // где есть фича в очереди. Дешёвый выход — без единой фичи в очереди проекция не строится.
    public async Task ReconcileAsync(string ownerId)
    {
        try
        {
            var projectIds = tasks.GetByOwner(ownerId)
                .Where(t => t.Role == TaskRoles.Feature && t.Hold?.Reason == FeatureHoldReasons.Queue)
                .Select(t => t.ProjectId)
                .Distinct()
                .ToList();
            foreach (var projectId in projectIds)
                await RebalanceAsync(ownerId, projectId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Сверка очереди фич владельца {OwnerId} не удалась", ownerId);
        }
    }

    // Пересчёт очереди проекта: пока занято меньше лимита и очередь не пуста — снять голову.
    // Идемпотентен. Возвращает id снятых фич; исключение наружу не выходит (сбой = 0 снятий).
    public async Task<IReadOnlyList<string>> RebalanceAsync(string ownerId, string? projectId)
    {
        var gate = _locks.GetOrAdd((ownerId, projectId ?? ""), _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0))
        {
            Contended?.Invoke();
            await gate.WaitAsync();
        }
        try
        {
            return await RebalanceLockedAsync(ownerId, projectId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Пересчёт очереди фич не удался: владелец {OwnerId}, проект {ProjectId}",
                ownerId, projectId);
            return [];
        }
        finally
        {
            gate.Release();
        }
    }

    // Одна проекция на пересчёт (B3, OOM-разведка 26.10): прежний цикл звал ProjectAll на каждом
    // проходе до MaxPasses — пачка закрытых задач волны стоила десятки полных проекций всех фич
    // владельца. Здесь: дешёвая проверка (в паре есть фича с hold queue — необходимое условие
    // IsQueued, ложных отрицаний нет) отсекает пересчёт без очереди до построения проекции,
    // дальше один снимок и проход по его очереди. Снятие очереди первым пересчётом делает
    // остальные события пачки дешёвым выходом: пачка событий стоит один пересчёт.
    private async Task<IReadOnlyList<string>> RebalanceLockedAsync(string ownerId, string? projectId)
    {
        var released = new List<string>();

        if (!tasks.GetByOwner(ownerId).Any(t => t.Role == TaskRoles.Feature
            && t.Hold?.Reason == FeatureHoldReasons.Queue && t.ProjectId == projectId))
            return released;

        IReadOnlyList<FeatureRun> runs;
        try
        {
            runs = ProjectOverride?.Invoke(ownerId) ?? features.ProjectAll(ownerId);
        }
        catch (Exception ex)
        {
            // Проекция не строится — состояние неизвестно, fail-closed.
            log.LogWarning(ex, "Очередь фич: проекция владельца {OwnerId} не построена, снятий нет", ownerId);
            return released;
        }

        // Лимит и занятость — на проект (волна 6): фичи других проектов владельца в счёт не идут,
        // та же область, что была в старом коде до пересчёта.
        var projectRuns = runs.Where(r => r.Epic.ProjectId == projectId).ToList();
        var limit = FeatureQueuePolicy.Limit(config);
        var queue = FeatureQueuePolicy.Order(projectRuns);
        // Занятые слоты по снимку + собственные снятия: снятое удержание делает фичу not_started
        // (или продолжает её задачи) — она занимает слот, ровно то, что старый цикл видел на
        // следующем проходе по новой проекции. Чужие изменения во время прохода (кто-то закрыл
        // фичу) считаем консервативно, по снимку: их страховка — сверка тика, не перепроекция.
        var occupied = projectRuns.Count(FeatureProjector.OccupiesSlot);

        foreach (var head in queue)
        {
            if (occupied >= limit) break;

            if (InsideLock is { } hook) await hook();

            // Между снимком и снятием человек мог поставить manual или уже снять удержание:
            // автоснятие трогает только queue и читает актуальный Hold, а не снимок.
            var live = tasks.GetById(head.FeatureId);
            if (live is null) continue; // фича удалена: слот не занимает (проекция её тоже не включила бы)
            if (live.Hold is null)
            {
                // Удержание уже снято (человеком или параллельным пересчётом): фича больше не
                // пауза — занимает слот, старый цикл увидел бы её в следующей проекции.
                occupied++;
                continue;
            }
            if (live.Hold.Reason != FeatureHoldReasons.Queue) continue;

            var result = await releaser.ReleaseFeatureHold(head.FeatureId, ownerId, live.Hold, FeatureHoldBy.Queue);
            if (result.Outcome != FeatureHoldReleaseOutcome.Released) break;
            occupied++;
            released.Add(head.FeatureId);
        }
        return released;
    }
}

public static class FeatureQueueRegistration
{
    // Сервис очереди живёт только при включённой подсистеме задач: без неё нет ни FeatureProjector,
    // ни смысла в очереди. Подписки событий встают в StartAsync хоста (в Testing хост не запускается).
    public static IServiceCollection AddFeatureQueue(this IServiceCollection services, IConfiguration config)
    {
        if (!SubsystemGate.IsEnabled(config, "tasks")) return services;
        services.AddSingleton<FeatureQueueService>();
        // IFeatureHoldReleaser здесь не регистрируется: Tasks не видит Main, реализацию шва
        // (TaskExecutionService) ставит Program.cs рядом с самим исполнителем.
        services.AddGatedHostedFrom(config, sp => sp.GetRequiredService<FeatureQueueService>(), "tasks");
        return services;
    }
}
