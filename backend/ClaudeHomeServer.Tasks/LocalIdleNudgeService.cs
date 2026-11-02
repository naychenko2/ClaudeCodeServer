using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Tasks;

// Серверный пинок при простое локального исполнителя: раз в минуту проверяет, не простаивает ли
// локальная персона при том, что в открытой фиче её ждут незапущенные шаги, и тогда кладёт
// карточку в ключевой чат фичи (та же доставка, что у карточек снятия паузы). Причина — разбор
// 30.10–02.11: в среднем работал один локальный исполнитель из трёх, треть времени — ни одного,
// а оркестратор просыпается только от доклада своего чата.
//
// Условия карточки: локальные персоны есть (ILocalExecutorRoster); персона свободна не меньше
// IdleMinutes; у открытой фичи без удержания есть её незапущенные step/rework в todo; ключевой чат
// жив и не ведёт ход; по паре (фича, персона) не чаще раза в CooldownMinutes. Состояние (когда
// персона освободилась, когда пинали) — в памяти: рестарт сбрасывает, это допустимо.
// Выключатель — Orchestration:LocalIdleNudge:Enabled (по умолчанию true).
//
// Исключение из проверки не выходит из BackgroundService: .NET по умолчанию гасит весь хост.
public sealed class LocalIdleNudgeService(
    TaskManager tasks,
    IServiceProvider services,
    IConfiguration config,
    ILogger<LocalIdleNudgeService> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    // (владелец, персона) → с какого момента наблюдаем её свободной
    private readonly Dictionary<(string Owner, string Persona), DateTime> _freeSince = new();
    // (фича, персона) → когда пинали в последний раз
    private readonly Dictionary<(string Feature, string Persona), DateTime> _nudged = new();

    // Тестовый шов: подмена источников, которых в тесте нет в DI
    internal ILocalExecutorRoster? Roster;
    internal IKeyChatCardSender? Sender;
    internal ISessionDirectory? Sessions { get; set; }

    private bool Enabled => config.GetValue("Orchestration:LocalIdleNudge:Enabled", true);
    private TimeSpan Idle => TimeSpan.FromMinutes(Math.Max(0, config.GetValue("Orchestration:LocalIdleNudge:IdleMinutes", 10)));
    private TimeSpan Cooldown => TimeSpan.FromMinutes(Math.Max(0, config.GetValue("Orchestration:LocalIdleNudge:CooldownMinutes", 30)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TickAsync(DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Проверка простоя локальных исполнителей не удалась");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal async Task TickAsync(DateTime now)
    {
        if (!Enabled) return;
        var roster = Roster ?? services.GetService(typeof(ILocalExecutorRoster)) as ILocalExecutorRoster;
        var sender = Sender ?? services.GetService(typeof(IKeyChatCardSender)) as IKeyChatCardSender;
        var sessions = Sessions ?? services.GetService(typeof(ISessionDirectory)) as ISessionDirectory;
        if (roster is null || sender is null || sessions is null) return;

        // Учёт простоя только по персонам, увиденным в этом тике: выпавшая из обхода (фич нет,
        // провайдер выключен) теряет отсчёт — иначе потом пришла бы карточка «без работы 3000 мин».
        var seen = new HashSet<(string Owner, string Persona)>();
        var all = tasks.GetAll();
        foreach (var byOwner in all.Where(t => t.OwnerId is not null).GroupBy(t => t.OwnerId!))
        {
            var ownerId = byOwner.Key;
            var ownerTasks = byOwner.ToList();
            var epics = ownerTasks.Where(t => t.Role == TaskRoles.Feature).ToList();
            if (epics.Count == 0) continue;
            foreach (var group in epics.GroupBy(e => e.ProjectId))
                await TickProjectAsync(now, ownerId, group.Key, group.ToList(), ownerTasks, roster, sender, sessions, seen);
        }
        foreach (var stale in _freeSince.Keys.Where(k => !seen.Contains(k)).ToList()) _freeSince.Remove(stale);
    }

    private async Task TickProjectAsync(DateTime now, string ownerId, string? projectId, List<TaskItem> epics,
        List<TaskItem> ownerTasks, ILocalExecutorRoster roster, IKeyChatCardSender sender, ISessionDirectory sessions,
        HashSet<(string Owner, string Persona)> seen)
    {
        var locals = roster.For(ownerId, projectId);
        if (locals.Count == 0) return;
        var busy = LocalExecutorLoad.BusyBy(ownerTasks, locals, sessions);
        // Учёт простоя ведётся по каждому тику, а не только когда есть что пинать: иначе «свободна
        // 10 минут» отсчитывалось бы от появления работы, а не от освобождения.
        foreach (var l in locals)
        {
            seen.Add((ownerId, l.PersonaId));
            if (busy.ContainsKey(l.PersonaId)) _freeSince.Remove((ownerId, l.PersonaId));
            else _freeSince.TryAdd((ownerId, l.PersonaId), now);
        }

        foreach (var epic in epics)
        {
            if (epic.Status == TaskItemStatus.Done || epic.Hold is not null) continue;
            var featureTasks = ownerTasks.Where(t => t.FeatureId == epic.Id).ToList();
            var key = FeatureKeyChain.FirstLive(FeatureKeyChain.Of(epic, featureTasks.Append(epic).ToList()), sessions, ownerId);
            if (key is null || sessions.GetById(key) is not { } keySession) continue;
            // Ключевой чат занят ходом или ждёт человека — карточка встала бы в очередь
            if (keySession.Status is SessionStatus.Working or SessionStatus.Waiting) continue;

            foreach (var l in locals)
            {
                if (busy.ContainsKey(l.PersonaId)) continue;
                if (!_freeSince.TryGetValue((ownerId, l.PersonaId), out var since) || now - since < Idle) continue;
                if (_nudged.TryGetValue((epic.Id, l.PersonaId), out var last) && now - last < Cooldown) continue;

                var steps = featureTasks
                    .Where(t => t.PersonaId == l.PersonaId && t.Status == TaskItemStatus.Todo
                        && t.LinkedSessionId is null && t.Role is TaskRoles.Step or TaskRoles.Rework)
                    .Select(t => t.StepRef ?? t.Title)
                    .Order(StringComparer.Ordinal)
                    .ToList();
                if (steps.Count == 0) continue;

                var minutes = (int)(now - since).TotalMinutes;
                var text = $"Свободен локальный исполнитель: {l.Name} без работы {minutes} мин. " +
                    $"В фиче есть его незапущенные шаги: {string.Join(", ", steps)}. " +
                    "Сверь «После:» и запусти готовые; готовых нет — ответь одной строкой, почему.";
                if (await sender.TrySendAsync(key, text)) _nudged[(epic.Id, l.PersonaId)] = now;
                // Ключевой чат после карточки занят — остальных персон пинать в этом тике незачем
                break;
            }
        }
    }
}
