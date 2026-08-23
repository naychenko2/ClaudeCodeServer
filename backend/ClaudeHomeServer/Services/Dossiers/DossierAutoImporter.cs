using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Git;

namespace ClaudeHomeServer.Services.Dossiers;

// Автоимпорт паспортов из ветки ccs/dossiers/v1 по новому tip (спринт автоматизации
// «Историй решений», задача 3). Сценарий «вторая машина / сосед по общей папке»: ветку
// в репозиторий привозит чужой git pull, сервис её только НАБЛЮДАЕТ — тик 60 с (по
// образцу DossierCaptureService) резолвит tip ветки read-only (rev-parse/git log, без
// fetch/pull/checkout), и если он отличается от сохранённого в state.json (ключ
// DossierCaptureState.ImportKey) — вызывает штатный DossierImporter и фиксирует tip.
//
// Границы те же, что у ручного POST /dossiers/import и автовыгрузки (DossierAutoExporter):
//   • тумблер проекта AutoImportDossiers (дефолт false) + флаг владельца
//     change-dossiers-recall — проверяются в момент импорта, а не включения;
//   • дедуп двух уровней: этот поллер не зовёт импортёр для уже внесённого tip, а сам
//     импортёр не добавляет записей, чей sha уже представлен (imported vs skipped) —
//     повторный тик ничего не меняет;
//   • фиксация tip после импорта при любом составе результата (даже 0 добавленных):
//     битый index.json или полностью знакомые sha не должны перезапускать импорт каждый
//     тик. Исключения: сбой внутри импортёра — state НЕ трогаем, следующий тик
//     переиграет; и гонка с автовыгрузкой — курсор пишется compare-and-set и не
//     затирает MarkOwnTip, успевший приземлиться за долгий импорт (разбор 23.08).
//
// Отдельный hosted-сервис, а не метод DossierCaptureService: захват и импорт — разные
// дела с разными гейтами, и тяжёлый конструктор захватчика не должен расти ради этого.
public sealed class DossierAutoImporter : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    private readonly ProjectManager _projects;
    private readonly DossierStore _store;
    private readonly DossierCaptureState _state;
    private readonly GitService _git;
    private readonly InstanceSecretsProvider _secrets;
    private readonly FeatureFlagService _flags;
    private readonly DossierImporter _importer;
    private readonly ILogger<DossierAutoImporter>? _log;

    public DossierAutoImporter(ProjectManager projects, DossierStore store, DossierCaptureState state,
        GitService git, InstanceSecretsProvider secrets, FeatureFlagService flags,
        ILoggerFactory? logFactory = null, ILogger<DossierAutoImporter>? log = null,
        DossierImporter? importer = null)
    {
        _projects = projects;
        _store = store;
        _state = state;
        _git = git;
        _secrets = secrets;
        _flags = flags;
        _importer = importer ?? new DossierImporter(store, git, secrets, logFactory?.CreateLogger<DossierImporter>());
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await TickAsync(ct); }
                catch (Exception ex) { _log?.LogWarning(ex, "dossiers: ошибка фонового тика автоимпорта"); }
            }
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
    }

    // Публичный для тестов: один проход по всем проектам. Тумблер/флаг/ветка
    // отсеиваются внутри — тик по «пустому» парку проектов не делает ни одного
    // git-вызова.
    public async Task TickAsync(CancellationToken ct = default)
    {
        foreach (var project in _projects.GetAll())
        {
            ct.ThrowIfCancellationRequested();
            await ImportProjectSafeAsync(project);
        }
    }

    private async Task ImportProjectSafeAsync(Project project)
    {
        try { await ImportProjectAsync(project); }
        catch (Exception ex) { _log?.LogWarning(ex, "dossiers: автоимпорт проекта {Project}", project.Id); }
    }

    private async Task ImportProjectAsync(Project project)
    {
        if (!project.AutoImportDossiers || project.OwnerId is not { } ownerId) return;
        if (!_flags.IsEnabled(ownerId, FeatureFlagKeys.ChangeDossiersRecall)) return;
        if (!GitService.IsGitRepo(project.RootPath)) return;

        // Tip того рефа, из которого импортировал бы ручной вызов (локальная ветка, при
        // её отсутствии — remote-tracking после pull): наблюдение и источник импорта не
        // должны разъезжаться. Ветки нет нигде — нечего импортировать, state не трогаем.
        var tip = await _git.GetDossiersTipAsync(ownerId, project.RootPath);
        if (tip is null) return;

        var key = DossierCaptureState.ImportKey(ownerId, project.Id);
        var seen = _state.Get(key);
        if (seen == tip.CommitSha) return;

        var result = await _importer.ImportAsync(ownerId, project,
            // Перепроверка перед фиксацией партии: чтение файлов ветки долгое (десятки
            // секунд на сотни записей), и tip мог стать НАШИМ — параллельная выгрузка
            // успела закоммитить и пометить его (окно update-ref → MarkOwnTip). Такое
            // содержимое уже живёт в сторе own-записями, Imported-копии — дубли.
            stillForeign: t => _state.Get(key) != t.CommitSha);

        // Курсор — compare-and-set: за долгий импорт автовыгрузка могла пометить свой
        // tip в том же ключе (MarkOwnTip). Слепая запись затёрла бы пометку — следующий
        // тик посчитал бы собственную ветку чужой и завёз её второй копией Imported-
        // записей. Отказ CAS не ошибка: пометка новее и точнее, тик по ней молча
        // пропустит ветку (а «опоздавший» чужой контент переиграет следующий tip).
        _state.SetIfUnchanged(key, seen, tip.CommitSha);
        if (result.Added > 0)
            _log?.LogInformation(
                "dossiers: автоимпорт проекта {Project} из {Ref} @{Sha}: добавлено {Added}, пропущено {Skipped}",
                project.Id, tip.Ref, tip.CommitSha[..7], result.Added, result.Skipped);
    }
}
