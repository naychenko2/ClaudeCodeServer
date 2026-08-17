using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Backup;

namespace ClaudeHomeServer.Services.ProjectIcons;

/// <summary>Итог миграции значков (ADR-009 §10): сколько подобрано, сколько осталось на инициалах.</summary>
public sealed record IconMigrationSummary(int Migrated, int Failed)
{
    public int Total => Migrated + Failed;
    public static readonly IconMigrationSummary Empty = new(0, 0);

    public static IconMigrationSummary operator +(IconMigrationSummary a, IconMigrationSummary b) =>
        new(a.Migrated + b.Migrated, a.Failed + b.Failed);
}

/// <summary>
/// Разовая миграция значков существующих проектов (ADR-009 §10): каждому проекту без
/// значка подбирается один, папка растровых иконок <c>data/project-icons</c> удаляется
/// целиком. Идемпотентность — состоянием самой записи, отдельного стора «что уже прогнали»
/// нет: кандидат только проект с <c>Icon.Glyph == null</c>, поэтому второй запуск — no-op,
/// а рестарт доводит лишь неполучившихся (по одной попытке, без повторов — у пользователя
/// есть кнопка «Подобрать значок»).
///
/// Порядок прогона обязателен: сперва бэкап каталога data штатным механизмом — после
/// прогона растровые иконки не восстанавливаются ничем, кроме архива. Бэкап инициирует
/// сам сервис; не снялся — миграция не стартует вовсе.
/// </summary>
public sealed class ProjectIconMigration(
    ProjectManager projects,
    ProjectIconGlyphService glyphs,
    IConfiguration config,
    ILogger<ProjectIconMigration> log)
{
    private const string IconsDirName = "project-icons";

    // Один прогон на инстанс: стартовый сервис плюс возможный будущий триггер не должны
    // гнать миграцию параллельно — каждая попытка это ход модели
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>Кандидат прогона — только проект, у которого значка нет (ADR-009 §10).</summary>
    internal static bool IsCandidate(Project project) => project.Icon.Glyph is null;

    // Каталог растровых иконок — тот же вывод DataDir, что у BackupContext.FromConfiguration
    // и ProjectManager.BackgroundsDir: все сторы живут рядом с projects.json
    internal static string IconsDirOf(IConfiguration configuration)
    {
        var dataPath = configuration["DataPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(dataPath)) ?? AppContext.BaseDirectory,
            IconsDirName);
    }

    public async Task<IconMigrationSummary> RunAsync(CancellationToken ct = default)
    {
        if (!await _runGate.WaitAsync(TimeSpan.Zero, ct))
        {
            log.LogInformation("Миграция значков уже идёт — повторный запуск пропущен");
            return IconMigrationSummary.Empty;
        }
        try
        {
            var all = projects.GetAll();
            var candidates = all.Where(IsCandidate).ToList();
            var iconsDir = IconsDirOf(config);
            // Всё сделано: значки у всех, растры уже ушли — рестарт чистый no-op,
            // без бэкапа и без единого касания стора
            if (candidates.Count == 0 && !Directory.Exists(iconsDir))
                return IconMigrationSummary.Empty;

            // Порядок обязателен (ADR-009 §10): замена необратима, прогон не начинается,
            // пока не снят свежий бэкап штатным механизмом
            var backup = BackupCore.Snapshot(BackupContext.FromConfiguration(config), log);
            if (!backup.Ok)
            {
                log.LogError("Миграция значков НЕ стартовала: бэкап каталога data не снялся ({Error}). " +
                             "Проекты остаются как есть, растровые иконки не тронуты", backup.Error);
                return IconMigrationSummary.Empty;
            }

            // Растровый механизм упразднён (ADR-009 §6): файлы больше не читаются никем,
            // копия уже в архиве — папка уходит целиком, даже если прогон ниже оборвётся
            DeleteIconsDir(iconsDir);

            log.LogInformation("Миграция значков: кандидатов {Candidates} из {Total} проектов",
                candidates.Count, all.Count);
            var summary = IconMigrationSummary.Empty;
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                summary += await MigrateOneAsync(candidate, ct);
            }
            log.LogInformation("Миграция значков завершена: подобрано {Migrated}, " +
                               "осталось на инициалах {Failed} из {Total}",
                summary.Migrated, summary.Failed, summary.Total);
            return summary;
        }
        finally
        {
            _runGate.Release();
        }
    }

    // Один проект — один ход модели, без повторов при отказе (ADR-009 §10): не подобралось —
    // проект остаётся на инициалах, это не ошибка, а фолбэк
    private async Task<IconMigrationSummary> MigrateOneAsync(Project candidate, CancellationToken ct)
    {
        // Проект могли удалить между отбором кандидатов и обработкой — GetById вернёт null
        if (projects.GetById(candidate.Id) is not { } project)
        {
            log.LogInformation("Значок проекта {Project}: проект удалён среди прогона — пропущен", candidate.Id);
            return new IconMigrationSummary(0, 0);
        }

        try
        {
            var result = await glyphs.SuggestAsync(project.Name, null, project.OwnerId!, ct);
            if (result.Ok)
            {
                // Вид «имя» предпочтительнее (ADR-009 §1): готовая иконка lucide узнаётся
                // всегда; нарисованный значок берём, только если имён модель не вернула
                var pick = result.Candidates.FirstOrDefault(c => c.IsNamed) ?? result.Candidates[0];
                var glyph = pick.IsNamed
                    ? new ProjectGlyph { Name = pick.Name, SetAt = DateTime.UtcNow }
                    : new ProjectGlyph { Paths = [.. pick.Paths!], SetAt = DateTime.UtcNow };
                if (!projects.TrySetIconGlyphMigrated(project.Id, glyph))
                {
                    // Значок успел выбрать пользователь — его выбор главнее
                    log.LogInformation("Значок проекта «{Name}»: пропущен, значок уже стоит", project.Name);
                    return new IconMigrationSummary(0, 0);
                }
                log.LogInformation("Значок проекта «{Name}»: подобран {Glyph}",
                    project.Name, pick.IsNamed ? pick.Name : "рисованный путь");
                return new IconMigrationSummary(1, 0);
            }

            log.LogWarning("Значок проекта «{Name}»: не подобрался ({Reason}), остаётся на инициалах",
                project.Name, result.FailReason);
            return new IconMigrationSummary(0, 1);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Неожиданный сбой одного проекта не должен ронять прогон: остальные тоже ждут значков
            log.LogWarning(ex, "Значок проекта «{Name}»: неожиданный сбой, остаётся на инициалах", project.Name);
            return new IconMigrationSummary(0, 1);
        }
    }

    private void DeleteIconsDir(string iconsDir)
    {
        try
        {
            if (Directory.Exists(iconsDir)) Directory.Delete(iconsDir, recursive: true);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Не удалось удалить папку растровых иконок {Dir}", iconsDir);
        }
    }
}

/// <summary>
/// Стартовый запуск миграции значков (ADR-009 §10): прогон идёт один раз после старта
/// сервера и идемпотентен — при полностью мигрированном сторе это чистый no-op.
/// </summary>
public sealed class ProjectIconMigrationService(
    ProjectIconMigration migration, ILogger<ProjectIconMigrationService> log) : BackgroundService
{
    // Даём серверу подняться: миграция снимает бэкап и дёргает модель, ей нечего спорить
    // со стартом за ресурсы (та же пауза, что у прогона фонов ADR-008)
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
            await migration.RunAsync(ct);
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
        catch (Exception ex) { log.LogError(ex, "Миграция значков проектов сорвалась"); }
    }
}
