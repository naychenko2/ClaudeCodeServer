using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Git;

namespace ClaudeHomeServer.Services.Dossiers;

// Гейт автовыгрузки паспортов (сужение 23.08, «фон трогает ветку, только если она
// заведомо наша»), вынесенный из DossierAutoExporter в опрашиваемый вид: логика та же,
// но вместо bool — причина, по которой фон молчит. Потребители: сам автовыгрузчик
// (решение «писать/молчать») и GET /dossiers/export/status (поле autoExport — по нему
// панель истории выбирает честную подсказку вместо общей «выгружается само»).
// Проверка git-репозитория сюда не входит — это предусловие вызывающего: у фона свой
// ранний выход, у статуса отдельное поле isGitRepo.
// Тонкий объект над синглтонами — тот же паттерн, что DossierGitExporter: состояния не
// держит, собирается per-request/per-экспорт.
public sealed class DossierAutoExportGate(ProjectManager projects, GitService git, DossierCaptureState state)
{
    // Причины — wire-строки поля autoExport: сериализуются в JSON как есть, фронт
    // раскладывает по ним тексты подсказки. Переименование = правка фронта.
    public const string Active = "active";
    public const string ForeignTip = "foreignTip";
    public const string OriginOnly = "originOnly";
    public const string SharedFolder = "sharedFolder";

    // Классификация проекта для автовыгрузки:
    //   • SharedFolder — папку делят несколько владельцев: переток паспортов к соседу
    //     без единого клика недопустим, фон молчит (блокер консилиума 23.08); ручная
    //     выгрузка с предупреждением остаётся;
    //   • ForeignTip — локальная ветка есть, но tip не создан нашей выгрузкой (git pull
    //     соседа/второй машины) либо метки ImportKey нет: полный снапшот своих паспортов
    //     молча стёр бы чужие записи;
    //   • OriginOnly — локальной ветки нет, есть только origin/ccs/dossiers/v1: корневой
    //     коммит без родителя сделал бы ветку непрошиваемой сиротой (non-fast-forward);
    //   • Active — ветки нет нигде (первая выгрузка на этой машине) либо tip совпал
    //     с меткой MarkOwnTip: ветка заведомо наша, фон пишет как раньше.
    public async Task<string> ClassifyAsync(string ownerId, Project project, CancellationToken ct = default)
    {
        if (projects.GetByRootPath(project.RootPath).Any(x => x.OwnerId != project.OwnerId))
            return SharedFolder;
        return await ClassifyBranchAsync(ownerId, project, ct) switch
        {
            BranchOwnership.Foreign => ForeignTip,
            BranchOwnership.Orphan => OriginOnly,
            _ => Active,
        };
    }

    // Классификация ветки паспортов: Absent/Ours — «заведомо наша», Foreign/Orphan —
    // работа ручной кнопки «Выгрузить» (там человек видит диалог и предупреждение).
    private enum BranchOwnership { Absent, Ours, Foreign, Orphan }

    private async Task<BranchOwnership> ClassifyBranchAsync(string ownerId, Project project, CancellationToken ct)
    {
        var localTip = await git.ResolveDossiersLocalTipAsync(ownerId, project.RootPath, ct);
        if (localTip is null)
            return await git.HasDossiersRemoteAsync(ownerId, project.RootPath, ct)
                ? BranchOwnership.Orphan
                : BranchOwnership.Absent;
        return state.Get(DossierCaptureState.ImportKey(ownerId, project.Id)) == localTip
            ? BranchOwnership.Ours
            : BranchOwnership.Foreign;
    }
}
