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
//   • общая папка и чужая ветка — фон молчит (блокеры консилиума 23.08, вариант «б»):
//     выгрузка идёт только когда история заведомо наша — ветки нет нигде (первая
//     выгрузка на этой машине) либо tip создан нашей же выгрузкой (метка ImportKey).
//     Общую папку нескольких владельцев, чужой tip после git pull и одну только
//     origin-ветку (сирота) оставляем ручной кнопке «Выгрузить» — там человек видит
//     диалог и предупреждение об общей папке;
//   • дебаунс накопления по образцу Dify-синка: серия коммитов подряд сливается в один
//     экспорт по паузе (Dossiers:AutoExportDebounceSeconds, дефолт 90 с), а не по
//     процессу git на каждый коммит;
//   • push — никогда и никак: публикация остаётся только ручной (POST /export/push);
//   • конспекты НЕ снимаются: снятие — вызов модели на каждый чат, дорогое действие
//     бывает только по решению человека (ручная выгрузка/отправка). Автовыгрузка везёт
//     паспорта и уже снятые конспекты из стора, недостающие догонит ручная выгрузка
//     (разбор 23.08).
// Конфликт с ручной кнопкой «Выгрузить» не разрешается отдельно — их сериализует
// лок per-root в GitService.WriteDossiersBranchAsync.
//
// Tip, созданный выгрузкой, помечается своим в DossierCaptureState (MarkOwnTip):
// автоимпорт по нему не запускается — петля «автовыгрузка → автоимпорт» разомкнута
// (разбор 23.08), иначе импортёр завозил бы копии собственных паспортов.
public sealed class DossierAutoExporter : IHostedService
{
    private readonly DossierStore _store;
    private readonly ProjectManager _projects;
    private readonly SessionManager _sessions;
    private readonly GitService _git;
    private readonly InstanceSecretsProvider _secrets;
    private readonly DossierDiscussionService _discussions;
    private readonly FeatureFlagService _flags;
    private readonly DossierCaptureState _state;
    private readonly ILogger<DossierAutoExporter>? _log;
    private readonly MemoryDifyDebouncer _debounce;

    public DossierAutoExporter(DossierStore store, ProjectManager projects, SessionManager sessions,
        GitService git, InstanceSecretsProvider secrets, DossierDiscussionService discussions,
        FeatureFlagService flags, DossierCaptureState state, IConfiguration config,
        ILogger<DossierAutoExporter>? log = null)
    {
        _store = store;
        _projects = projects;
        _sessions = sessions;
        _git = git;
        _secrets = secrets;
        _discussions = discussions;
        _flags = flags;
        _state = state;
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

            // Приватность общей папки (блокер консилиума 23.08): тот же признак, по
            // которому ручной диалог держит фазу confirmShared. Папку делят несколько
            // владельцев — фон не выгружает вовсе: переток паспортов к соседу без
            // единого клика недопустим; ручная выгрузка с предупреждением остаётся.
            if (_projects.GetByRootPath(project.RootPath).Any(x => x.OwnerId != project.OwnerId))
            {
                _log?.LogInformation(
                    "dossiers: автовыгрузка проекта {Project} пропущена: папку делят несколько владельцев — только ручная выгрузка с предупреждением",
                    project.Id);
                return;
            }

            // Гейт «ветка заведомо наша» (вариант «б» вердикта консилиума): снапшот
            // собирается только из своих паспортов, поэтому чужой tip он молча стёр бы,
            // а MarkOwnTip закрыл бы соседу дорогу назад через автоимпорт. Фон трогает
            // ветку лишь когда она точно наша — остальное работа ручной кнопки.
            switch (await ClassifyBranchAsync(ownerId, project))
            {
                case BranchOwnership.Foreign:
                    _log?.LogInformation(
                        "dossiers: автовыгрузка проекта {Project} пропущена: tip {Ref} не создан нашей выгрузкой — ветку трогает только ручная выгрузка",
                        project.Id, GitService.DossiersRef);
                    return;
                case BranchOwnership.Orphan:
                    _log?.LogInformation(
                        "dossiers: автовыгрузка проекта {Project} пропущена: есть только origin-ветка, локальная поверх неё не создаётся",
                        project.Id);
                    return;
            }

            // Тонкий объект над синглтонами — тот же состав, что у DossiersController.
            // ensureDigests=false: снятие конспектов — платное действие модели, фон его
            // не запускает (ручная выгрузка снимет недостающие по кнопке).
            var exporter = new DossierGitExporter(_sessions, _store, _git, _secrets, _discussions);
            var result = await exporter.ExportAsync(ownerId, project, ensureDigests: false);
            // Tip нашей выгрузки — «наш»: автоимпорт его пропустит. CommitSha не-null и при
            // Committed=false (дерево совпало с уже существующим tip — содержимое тоже наше).
            // Узкое окно «update-ref уже виден, пометки ещё нет» закрывают два механизма
            // на стороне автоимпортёра (разбор 23.08): перепроверка tip перед фиксацией
            // партии и compare-and-set курсора — пометку уже не затереть слепой записью.
            _state.MarkOwnTip(ownerId, project.Id, result.CommitSha);
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

    // Классификация ветки паспортов для гейта «ветка заведомо наша»:
    //   • Absent  — ветки нет нигде (ни локальной, ни origin): первая выгрузка на этой
    //               машине, фон создаёт ветку как раньше;
    //   • Ours    — локальный tip совпадает с меткой ImportKey/MarkOwnTip: его создала
    //               наша же выгрузка (авто- или ручная), фон пишет поверх как раньше;
    //   • Foreign — локальная ветка есть, но tip чужой (git pull соседа/второй машины)
    //               либо метки нет: полный снапшот своих паспортов молча стёр бы чужие
    //               записи — фон молчит;
    //   • Orphan  — локальной нет, есть только origin/ccs/dossiers/v1: локальный
    //               корневой коммит без родителя превратил бы ветку в непрошиваемую
    //               сироту (push — non-fast-forward) — фон молчит.
    private enum BranchOwnership { Absent, Ours, Foreign, Orphan }

    private async Task<BranchOwnership> ClassifyBranchAsync(string ownerId, Project project)
    {
        var localTip = await _git.ResolveDossiersLocalTipAsync(ownerId, project.RootPath);
        if (localTip is null)
            return await _git.HasDossiersRemoteAsync(ownerId, project.RootPath)
                ? BranchOwnership.Orphan
                : BranchOwnership.Absent;
        return _state.Get(DossierCaptureState.ImportKey(ownerId, project.Id)) == localTip
            ? BranchOwnership.Ours
            : BranchOwnership.Foreign;
    }
}
