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
    //
    // 2026-10-08: секция едет ХВОСТОМ ХОДА (InTurnTail), а не системным блоком. Трейлер
    // содержит id сессии и задачи — единственные строки системного блока, различающиеся
    // между сессиями одной персоны в одном проекте. Стоя в середине блока, они обрывали
    // общий префикс prefix cache vLLM на 12,5k токенов из 15k (замер 08.10.2026: первый
    // ответ исполнителя 3,5 с при попадании против 8–10 с при промахе). В хвосте хода
    // подсказка остаётся в истории с первого хода, а системный блок делится между сессиями.
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
            new PromptSectionContribution([new PromptSection(Key, text, InTurnTail: true)]));
    }
}