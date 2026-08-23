using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Memory;

namespace ClaudeHomeServer.Services.Dossiers;

// Автовыгрузка паспортов в ЛОКАЛЬНУЮ ветку ccs/dossiers/v1 после захвата (спринт
// автоматизации «Истории решений»). Подписана на DossierStore.OnOwnDossierChanged:
// событие поднимают только собственный захват и переякорение при squash, поэтому
// opt-out чаты (захват для них не происходит вовсе) и импорт сюда не попадают; каждый
// паспорт дополнительно перепроверяется предохранителями выгрузки
// (DossierGitExporter.ShouldExportDossier — opt-out могли включить и позже захвата).
//
// Границы:
//   • флаг change-dossiers-recall ВЛАДЕЛЬЦА — тот же гейт, что у ручного POST /export;
//   • дебаунс накопления по образцу Dify-синка: серия коммитов подряд сливается в один
//     экспорт по паузе (Dossiers:AutoExportDebounceSeconds, дефолт 90 с), а не по
//     процессу git на каждый коммит;
//   • push — никогда и никак: публикация остаётся только ручной (POST /export/push).
// Конфликт с ручной кнопкой «Выгрузить» не разрешается отдельно — их сериализует
// лок per-root в GitService.WriteDossiersBranchAsync.
public sealed class DossierAutoExporter : IHostedService
{
    private readonly DossierStore _store;
    private readonly ProjectManager _projects;
    private readonly SessionManager _sessions;
    private readonly GitService _git;
    private readonly InstanceSecretsProvider _secrets;
    private readonly DossierDiscussionService _discussions;
    private readonly FeatureFlagService _flags;
    private readonly ILogger<DossierAutoExporter>? _log;
    private readonly MemoryDifyDebouncer _debounce;

    public DossierAutoExporter(DossierStore store, ProjectManager projects, SessionManager sessions,
        GitService git, InstanceSecretsProvider secrets, DossierDiscussionService discussions,
        FeatureFlagService flags, IConfiguration config, ILogger<DossierAutoExporter>? log = null)
    {
        _store = store;
        _projects = projects;
        _sessions = sessions;
        _git = git;
        _secrets = secrets;
        _discussions = discussions;
        _flags = flags;
        _log = log;
        // Окно батча: всплеск коммитов одного рабочего дня не должен крутить git-процессы
        // без остановки, но и затягивать сверх пары минут незачем. Минимум 1 с — осмысленная
        // граница для тестов и локальных стендов.
        var seconds = config.GetValue("Dossiers:AutoExportDebounceSeconds", 90);
        _debounce = new MemoryDifyDebouncer(TimeSpan.FromSeconds(Math.Max(1, seconds)));
    }

    public Task StartAsync(CancellationToken ct)
    {
        _store.OnOwnDossierChanged += Schedule;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _store.OnOwnDossierChanged -= Schedule;
        return Task.CompletedTask;
    }

    private void Schedule(string ownerId, string projectId) =>
        _debounce.Schedule($"{ownerId}:{projectId}", () => _ = ExportSafeAsync(ownerId, projectId));

    // Немедленная выгрузка без дебаунса — та же работа, что по таймеру. Публичная для
    // тестов; прод использует её только через дебаунс (ручной экспорт идёт своим путём
    // через DossiersController.Exporter и эту точку не задевает).
    public async Task ExportSafeAsync(string ownerId, string projectId)
    {
        try
        {
            // Граница та же, что у ручного экспорта: без флага владельца автовыгрузки нет.
            // Проверяется В МОМЕНТ выгрузки, а не захвата — тумблер могли успеть выключить.
            if (!_flags.IsEnabled(ownerId, FeatureFlagKeys.ChangeDossiersRecall)) return;
            var project = _projects.GetById(projectId);
            if (project is null || project.OwnerId != ownerId) return;
            if (!GitService.IsGitRepo(project.RootPath)) return;

            // Тонкий объект над синглтонами — тот же состав, что у DossiersController
            var exporter = new DossierGitExporter(_sessions, _store, _git, _secrets, _discussions);
            var result = await exporter.ExportAsync(ownerId, project);
            if (result.Committed)
                _log?.LogInformation(
                    "dossiers: автовыгрузка проекта {Project} в {Ref}: {Count} паспортов, коммит {Sha}",
                    project.Id, GitService.DossiersRef, result.Exported, result.CommitSha);
        }
        catch (Exception ex)
        {
            // Фоновая механика не имеет права ронять что-либо: не вышло — остаётся ручная
            // кнопка «Выгрузить», следующий захват переиграет попытку.
            _log?.LogWarning(ex, "dossiers: автовыгрузка проекта {Project} не удалась", projectId);
        }
    }
}
