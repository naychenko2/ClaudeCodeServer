using System.Collections.Concurrent;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Backgrounds;

/// <summary>Итог массового прогона: по проектам владельца или по всему инстансу.</summary>
public sealed record BackfillSummary(int Generated, int Skipped, int Failed)
{
    public int Total => Generated + Skipped + Failed;

    public static BackfillSummary operator +(BackfillSummary a, BackfillSummary b) =>
        new(a.Generated + b.Generated, a.Skipped + b.Skipped, a.Failed + b.Failed);

    public static readonly BackfillSummary Empty = new(0, 0, 0);
}

/// <summary>
/// Разовая генерация фонов существующим проектам (ADR-008 §10).
/// Идемпотентен по построению: кандидат — только проект, которого генерация НИКОГДА не
/// касалась (<c>Background == null</c>), либо протухший Pending (сервер упал в середине).
/// Generated / Standard / Failed не трогаются, поэтому повторный запуск — no-op, а
/// отдельного стора «что уже прогнали» не нужно.
/// </summary>
public sealed class ProjectBackgroundBackfill(
    ProjectManager projects, ProjectBackgroundService backgrounds,
    UserStore users, ILogger<ProjectBackgroundBackfill> log)
{
    // Не больше 2 генераций одновременно на инстанс: 39 залповых вызовов сильной модели
    // упрутся в rate limit, и половина проектов получит Failed по вине очереди, а не модели
    private readonly SemaphoreSlim _slots = new(2, 2);

    // Прогоны владельцев, идущие прямо сейчас. Один владелец = один прогон, а внутри него
    // проекты идут последовательно — это и есть потолок «не больше 1 генерации на владельца»
    private readonly ConcurrentDictionary<string, byte> _running = new();

    // Повторяем только транзиентные отказы: модель не ответила (таймаут, rate limit) или
    // не записался файл. Битый JSON и отсев фигур — причина в модели или в проекте, второй
    // такой же вызов даст ту же кашу и лишний счёт за токены (ADR-008, отклонённая альтернатива)
    private static readonly string[] TransientReasons = ["no-model", "io"];
    private const int MaxAttempts = 2;
    // Пожизненный потолок попыток на проект (ADR-008 §10). Внутрипрогонный MaxAttempts гасит
    // случайный таймаут здесь и сейчас; этот — не даёт мёртвой модели долбиться при каждом
    // рестарте. 3 = один полный прогон (до 2 попыток) + один короткий доводочный.
    private const int MaxTotalAttempts = 3;

    /// <summary>Пауза перед повторной попыткой; в тестах ужимается.</summary>
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Прогон по ВСЕМ владельцам — единственный триггер, работает на старте сервера.</summary>
    public async Task<BackfillSummary> RunAllAsync(CancellationToken ct = default)
    {
        var owners = users.GetAll().Select(u => u.Id).ToList();
        if (owners.Count == 0) return BackfillSummary.Empty;

        // Владельцы идут параллельно, но общий потолок держит _slots
        var results = await Task.WhenAll(owners.Select(id => RunAsync(id, ct)));
        return results.Aggregate(BackfillSummary.Empty, (a, b) => a + b);
    }

    public async Task<BackfillSummary> RunAsync(string ownerId, CancellationToken ct = default)
    {
        if (!_running.TryAdd(ownerId, 0))
        {
            log.LogInformation("Массовый прогон фонов ({Owner}) уже идёт — повторный запуск пропущен", ownerId);
            return BackfillSummary.Empty;
        }

        try
        {
            var all = projects.GetByOwner(ownerId);
            var candidates = all.Where(p => IsCandidate(p.Background)).ToList();
            log.LogInformation("Массовый прогон фонов ({Owner}): кандидатов {Candidates} из {Total} проектов",
                ownerId, candidates.Count, all.Count);
            if (candidates.Count == 0) return BackfillSummary.Empty;

            var summary = BackfillSummary.Empty;
            foreach (var project in candidates)
            {
                ct.ThrowIfCancellationRequested();
                summary += await GenerateOneAsync(project, ct);
            }

            log.LogInformation(
                "Массовый прогон фонов ({Owner}) завершён: сгенерировано {Generated}, пропущено {Skipped}, упало {Failed} из {Total}",
                ownerId, summary.Generated, summary.Skipped, summary.Failed, summary.Total);
            return summary;
        }
        finally
        {
            _running.TryRemove(ownerId, out _);
        }
    }

    // Один проект: попытки с повтором транзиентного отказа. Строка лога — на каждый проект,
    // чтобы по журналу было видно судьбу каждого, а не только итоговые цифры
    private async Task<BackfillSummary> GenerateOneAsync(Project project, CancellationToken ct)
    {
        BackgroundResult? result = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // Пожизненный потолок: транзиентный возврат не должен превысить MaxTotalAttempts.
            // Attempts растёт в TryBeginBackground, поэтому перечитываем стор; достигли предела
            // раньше, чем прошли внутрипрогонные повторы, — стоп, проект остаётся Failed.
            // Проект мог быть удалён между отбором кандидатов и обработкой — тогда GetById
            // вернёт null, паттерн не сматчится, а GenerateAsync ниже корректно вернёт пропуск.
            if (projects.GetById(project.Id)?.Background is { Attempts: >= MaxTotalAttempts })
                break;

            await _slots.WaitAsync(ct);
            try
            {
                // candidateOnly только на первой попытке: после неудачи проект уже наш
                // (Kind = Failed), и кандидатом он быть перестал
                result = await backgrounds.GenerateAsync(project, ct, candidateOnly: attempt == 1);
            }
            finally
            {
                _slots.Release();
            }

            if (result.Kind == ProjectBackgroundKind.Generated && result.FailReason is null) break;
            if (result.Kind != ProjectBackgroundKind.Failed) break;                    // перехвачен кнопкой
            if (!TransientReasons.Contains(result.FailReason)) break;                  // повтор не поможет
            if (attempt == MaxAttempts) break;

            log.LogInformation("Фон проекта {Project} «{Name}»: попытка {Attempt} не удалась ({Reason}), повторяем",
                project.Id, project.Name, attempt, result.FailReason);
            await Task.Delay(RetryDelay, ct);
        }

        switch (result)
        {
            case null:
                // Попали сюда с уже исчерпанным потолком Attempts (гонка: параллельная кнопка
                // или предыдущий тик довели Attempts до MaxTotalAttempts до первой попытки).
                // Модель не звали — считаем пропуском, а не неудачей
                log.LogInformation("Фон проекта {Project} «{Name}»: пропуск — потолок попыток исчерпан",
                    project.Id, project.Name);
                return new BackfillSummary(0, 1, 0);

            case { Kind: ProjectBackgroundKind.Generated, FailReason: null }:
                log.LogInformation("Фон проекта {Project} «{Name}»: сгенерирован ({Tile})",
                    project.Id, project.Name, result.TileVersion);
                return new BackfillSummary(1, 0, 0);

            case { Kind: ProjectBackgroundKind.Failed }:
                log.LogWarning("Фон проекта {Project} «{Name}»: генерация не удалась ({Reason}), остаётся стандартный фон",
                    project.Id, project.Name, result.FailReason);
                return new BackfillSummary(0, 0, 1);

            default:
                // Проект забрали параллельно (кнопка или другой тик) — его состояние чужое
                log.LogInformation("Фон проекта {Project} «{Name}»: пропущен ({State})",
                    project.Id, project.Name, ProjectBackgroundView.Name(result.Kind));
                return new BackfillSummary(0, 1, 0);
        }
    }

    /// <summary>
    /// Кандидат прогона: генерации не было вовсе либо Pending протух (сервер упал).
    /// Failed кандидатом не становится: стартовый прогон идёт при каждом рестарте, и мёртвая
    /// модель (нет строки каталога / пустой слот) долбилась бы в неё бесконечно. Повторную
    /// попытку по конкретному проекту даёт кнопка «Сгенерировать» (ADR-008 §10).
    /// </summary>
    internal static bool IsCandidate(ProjectBackground? background, TimeSpan? staleAfter = null)
    {
        if (background is null) return true;
        if (background.Kind == ProjectBackgroundKind.Pending)
            return background.StartedAt is not { } started
                || DateTime.UtcNow - started >= (staleAfter ?? TimeSpan.FromMinutes(10));

        return false;
    }
}

/// <summary>
/// Единственный триггер прогона: на старте сервера доводим фоны существующим проектам
/// (новый проект получает фон при создании). Прогон идемпотентен, поэтому лишний рестарт
/// ничего не стоит — кандидатов просто нет.
/// </summary>
public sealed class ProjectBackgroundBackfillService(
    ProjectBackgroundBackfill backfill, ILogger<ProjectBackgroundBackfillService> log) : BackgroundService
{
    // Даём серверу подняться: прогон дёргает сильную модель и не должен спорить за старт
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
            await backfill.RunAllAsync(ct);
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
        catch (Exception ex) { log.LogError(ex, "Стартовый прогон фонов проектов сорвался"); }
    }
}
