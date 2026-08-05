namespace ClaudeHomeServer.Models;

// Триггеры остановок режима «Командная реализация» (Э4). В автономном режиме худший
// сценарий — практика встала, а человек не знает, ждёт она его или умерла: поэтому любая
// остановка = карточка в ленте с причиной и кнопками + уведомление и web push.
// Молчаливых пауз не бывает. См. docs/architecture/team-implement-mode.md, «Эскалация и остановки».
public enum TeamEscalationKind
{
    // Исполнитель доложил о блокере (chats_report_up с пометкой «нужна помощь»)
    Blocker,
    // Под-задача провалилась дважды: одна перевыдача уже была
    TaskFailed,
    // Расхождение с планом: нужны файлы вне владения, объём вырос (маркер координатора)
    PlanDeviation,
    // Проверка (сборка/тесты) красная после попыток починки (маркер координатора)
    CheckFailed,
    // Нужно продуктовое решение: два рабочих варианта. Живой вопрос координатора идёт
    // ASK-карточкой (маркер <escalate:decision> из протокола ушёл — парсер терпит его как
    // фолбэк); карточки этого вида публикуют сторожи вне хода координатора (молчаливый тупик,
    // прерванный ход) — у них остаётся поле свободного ответа.
    ProductDecision,
    // Исчерпан бюджет итерации: следующая волна не стартует
    BudgetExhausted,
    // Волна не двигается дольше таймаута — страховка от молчаливого зависания
    WaveStalled,
    // Не эскалация, а гейт: авто-волны сняты, волна закрыта — «Запустить следующую?»
    WaveGate,
    // Человек нажал «Остановить»: текущие исполнители дорабатывают, новые не стартуют
    Stopped,
    // Не остановка, а информация (Э5): по новой вводной развёрнута добавочная волна.
    // При авто-волнах она не ждёт клика — сама вводная человека и есть точка контроля,
    // поэтому карточка лишь показывает состав и даёт кнопку «Остановить».
    WaveAdded,
    // Тупик в волне (Э8): координатор не знает, как действовать дальше — требования неясны.
    // Волны встают на паузу, практика уходит в интервью, а после ответов человек получает
    // план vN на подтверждение. Кнопок нет: ответы идут ASK-карточками интервью.
    NeedsClarification,
}

public static class TeamEscalationKindExtensions
{
    // Wire-токен для фронта (camelCase) — по нему Кира выбирает иконку и текст карточки
    public static string ToWireToken(this TeamEscalationKind kind) => kind switch
    {
        TeamEscalationKind.Blocker => "blocker",
        TeamEscalationKind.TaskFailed => "taskFailed",
        TeamEscalationKind.PlanDeviation => "planDeviation",
        TeamEscalationKind.CheckFailed => "checkFailed",
        TeamEscalationKind.ProductDecision => "productDecision",
        TeamEscalationKind.BudgetExhausted => "budgetExhausted",
        TeamEscalationKind.WaveStalled => "waveStalled",
        TeamEscalationKind.WaveGate => "waveGate",
        TeamEscalationKind.Stopped => "stopped",
        TeamEscalationKind.WaveAdded => "waveAdded",
        TeamEscalationKind.NeedsClarification => "needsClarification",
        _ => "blocker",
    };

    // Информационная карточка (Э5): практика не остановлена, работа идёт — такая карточка
    // не переводит режим в «ждёт решения» и не гасит отсечку таймаута волны.
    public static bool IsInformational(this TeamEscalationKind kind) =>
        kind == TeamEscalationKind.WaveAdded;
}

// Кнопка карточки эскалации. Id — стабильный wire-токен (по нему бэкенд узнаёт решение),
// Label — подпись для человека. Ответ можно дать и обычным сообщением в чат: кнопки лишь
// избавляют от набора текста, особого протокола за ними нет.
public sealed record TeamEscalationAction(string Id, string Label);

// Карточка остановки в ленте штаба: причина, детали и кнопки. Живёт в истории чата
// (StoredTeamEscalationMessage) — переживает рестарт, как карточка плана.
public class TeamEscalation
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public TeamEscalationKind Kind { get; init; }
    // Заголовок карточки: «Исполнитель застрял: …», «Задача „…" не удалась дважды»
    public string Title { get; init; } = "";
    // Развёрнутая причина: текст блокера, вывод проверки, сводка расхода бюджета
    public string Details { get; init; } = "";
    // Задача трекера, к которой относится остановка (блокер, провал, зависшая волна)
    public string? TaskId { get; init; }
    // Номер волны на момент остановки — для текста «Волна 2 закрыта»
    public int Wave { get; init; }
    public IReadOnlyList<TeamEscalationAction> Actions { get; init; } = [];
    // Персона-автор карточки (Э8): всё, что штаб говорит человеку, идёт от лица координатора —
    // аватар и имя в шапке. Пишется В МОМЕНТ публикации, поэтому смена координатора не
    // переписывает историю: старые карточки сохраняют исходного автора. null — персоны у
    // штаба нет, карточка и уведомление деградируют до обезличенного текста.
    public string? PersonaId { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    // Решение человека: карточка гаснет, id выбранной кнопки уходит координатору ходом
    public bool Resolved { get; set; }
    public string? ChosenActionId { get; set; }
}

// Кнопки по типу остановки — ровно те, что в таблице продуктового плана.
// Продуктовое решение кнопок не имеет: варианты формулирует координатор текстом,
// человек отвечает сообщением в чат.
public static class TeamEscalationActions
{
    public static IReadOnlyList<TeamEscalationAction> For(TeamEscalationKind kind) => kind switch
    {
        TeamEscalationKind.Blocker =>
        [
            new("answer", "Ответить"),
            new("reassign", "Отдать другому"),
            new("drop", "Снять задачу"),
        ],
        TeamEscalationKind.TaskFailed =>
        [
            new("reassign", "Отдать другому"),
            new("skip", "Пропустить"),
            new("finish", "Завершить"),
        ],
        TeamEscalationKind.PlanDeviation =>
        [
            new("allow", "Разрешить"),
            new("keepPlan", "Оставить в плане"),
            new("rebuildWave", "Пересобрать волну"),
        ],
        TeamEscalationKind.CheckFailed =>
        [
            new("keepFixing", "Чинить дальше"),
            new("finishWithIssues", "Завершить с замечаниями"),
        ],
        TeamEscalationKind.BudgetExhausted =>
        [
            new("addBudget", "Добавить бюджет"),
            new("finish", "Завершить итерацию"),
        ],
        TeamEscalationKind.WaveStalled =>
        [
            new("restart", "Перезапустить"),
            new("drop", "Снять"),
            new("finish", "Завершить"),
        ],
        TeamEscalationKind.WaveGate =>
        [
            new("runNext", "Запустить"),
            new("editRest", "Изменить остаток плана"),
            new("finish", "Завершить итерацию"),
        ],
        TeamEscalationKind.Stopped =>
        [
            new("resume", "Продолжить"),
            new("finish", "Завершить итерацию"),
        ],
        // Добавочная волна уже идёт: единственное действие — передумать и остановить её
        TeamEscalationKind.WaveAdded => [new("stop", "Остановить")],
        _ => [],
    };
}
