using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Tasks;

// Серверный пинок при простое локального движка: раз в минуту проверяет, не стоят ли свободными
// слоты локальной модели при том, что в открытой фиче ждут незапущенные шаги локальных
// исполнителей, и тогда кладёт карточку в ключевой чат фичи (та же доставка, что у карточек
// снятия паузы). Причина — разбор 30.10–02.11: в среднем работал один локальный исполнитель из
// трёх, треть времени — ни одного, а оркестратор просыпается только от доклада своего чата.
//
// Простой считается по слотам движка (ILocalExecutorRoster.SlotsFor — тот же счётчик, что
// пускает ходы), а не по персонам: одну персону можно запустить на несколько шагов
// параллельно, упор — в слоты. Поэтому карточка называет число свободных слотов и все
// незапущенные локальные шаги фичи, а не одного «свободного» исполнителя.
//
// Условия карточки: локальные исполнители есть; свободный слот держится не меньше IdleMinutes;
// у открытой фичи без удержания есть незапущенные step/rework локальных исполнителей в todo;
// ключевой чат жив и не ведёт ход; по фиче не чаще раза в CooldownMinutes. Состояние (когда
// освободился слот, когда пинали) — в памяти: рестарт сбрасывает, это допустимо.
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

    // (владелец, проект) → с какого момента у движков его исполнителей есть свободный слот
    private readonly Dictionary<(string Owner, string? Project), DateTime> _freeSince = new();
    // фича → когда пинали в последний раз
    private readonly Dictionary<string, DateTime> _nudged = new();

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
                    log.LogWarning(ex, "Проверка простоя локального движка не удалась");
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

        // Учёт простоя только по контекстам, увиденным в этом тике со свободным слотом: выпавший
        // из обхода (фич нет, провайдер выключен) теряет отсчёт — иначе потом пришла бы карточка
        // «простаивают 3000 мин».
        var seen = new HashSet<(string Owner, string? Project)>();
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
        HashSet<(string Owner, string? Project)> seen)
    {
        var locals = roster.For(ownerId, projectId);
        if (locals.Count == 0 || roster.SlotsFor(ownerId, projectId) is not { Free: > 0 } slots) return;
        // Учёт простоя ведётся по каждому тику, а не только когда есть что пинать: иначе «свободно
        // 10 минут» отсчитывалось бы от появления работы, а не от освобождения слота.
        var ctx = (ownerId, projectId);
        seen.Add(ctx);
        _freeSince.TryAdd(ctx, now);
        var since = _freeSince[ctx];
        if (now - since < Idle) return;

        var names = locals.ToDictionary(l => l.PersonaId, l => l.Name);
        foreach (var epic in epics)
        {
            if (epic.Status == TaskItemStatus.Done || epic.Hold is not null) continue;
            if (_nudged.TryGetValue(epic.Id, out var last) && now - last < Cooldown) continue;
            var featureTasks = ownerTasks.Where(t => t.FeatureId == epic.Id).ToList();
            var key = FeatureKeyChain.FirstLive(FeatureKeyChain.Of(epic, featureTasks.Append(epic).ToList()), sessions, ownerId);
            if (key is null || sessions.GetById(key) is not { } keySession) continue;
            // Ключевой чат занят ходом или ждёт человека — карточка встала бы в очередь
            if (keySession.Status is SessionStatus.Working or SessionStatus.Waiting) continue;

            var steps = featureTasks
                .Where(t => t.PersonaId is { } pid && names.ContainsKey(pid) && t.Status == TaskItemStatus.Todo
                    && t.LinkedSessionId is null && t.Role is TaskRoles.Step or TaskRoles.Rework)
                .GroupBy(t => names[t.PersonaId!])
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key + ": " + string.Join(", ", g.Select(t => t.StepRef ?? t.Title).Order(StringComparer.Ordinal)))
                .ToList();
            if (steps.Count == 0) continue;

            var minutes = (int)(now - since).TotalMinutes;
            var text = $"Свободно слотов локальной модели: {slots.Free} из {slots.Limit}, простаивают {minutes} мин. " +
                $"В фиче есть незапущенные шаги локальных исполнителей — {string.Join("; ", steps)}. " +
                "Одного исполнителя можно запускать на несколько шагов параллельно. " +
                $"Сверь «После:» и запусти готовые, до {slots.Free}; готовых нет — ответь одной строкой, почему.";
            if (!await sender.TrySendAsync(key, text)) continue;
            _nudged[epic.Id] = now;
            // Слоты общие для всех фич контекста: вторая карточка в том же тике раздала бы те же
            // слоты дважды. Следующая фича получит свою через минуту — уже по свежему счёту.
            break;
        }
    }
}
