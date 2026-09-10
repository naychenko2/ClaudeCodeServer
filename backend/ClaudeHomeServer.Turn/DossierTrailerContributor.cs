namespace ClaudeHomeServer.Services.Turn;

// Подсказка про трейлер CCS-Session/CCS-Task (ADR-004, «Паспорта изменений»): одна
// строка рядом с Co-Authored-By, по ней DossierCaptureService захватит коммит с этим
// трейлером. Только проектные сессии владельца — вне проекта git-канала нет, а
// подсказка про коммит стала бы шумом.
//
// Гейт IsEnabled повторяет прежний BuildDossierTrailerHint: ownerId != null
// && session.ProjectId != null. Без ownerId владельца у DossierCaptureService нет
// ключа; без ProjectId захватывать нечего.
public sealed class DossierTrailerContributor : IPromptSectionContributor
{
    public string Key => "dossier-trailer";
    public string Title => "Трейлер истории решений";
    // Трейлер истории решений идёт ДО блоков recall — порядок, который зафиксирован
    // golden-фикстурой 1 («dossier-trailer → recall-notes»).
    public int Order => 100;
    public string Group => "project";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.OwnerId is not null && sessionContext.Session.ProjectId is not null;

    public Task<PromptSectionContribution?> BuildAsync(
        PromptSessionContext sessionContext, string? turnText)
    {
        var session = sessionContext.Session;
        var taskLine = session.TaskId is null ? "" : $"\nCCS-Task: {session.TaskId}";
        var text = "Если делаешь `git commit` в этом проекте — добавь в сообщение коммита трейлер " +
            $"отдельной строкой (рядом с Co-Authored-By):\nCCS-Session: {session.Id}{taskLine}\n" +
            "Он привязывает коммит к этому чату/задаче для фичи «История решений» (паспорт изменения " +
            "с выжимкой «зачем/решения/отказы/грабли») — без него автоматическая выжимка не соберётся. " +
            "Не убирай и не меняй значение при amend/squash.";
        return Task.FromResult<PromptSectionContribution?>(
            new PromptSectionContribution([new PromptSection(Key, text)]));
    }
}