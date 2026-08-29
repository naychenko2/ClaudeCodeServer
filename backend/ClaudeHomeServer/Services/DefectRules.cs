using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Чистые правила дефектов: четыре ветки, ни одной зависимости на хранилище или HTTP.
// Без ILogger, JsonFileStore, TaskManager и HttpContext — тестируется unit-тестами
// без подъёма стенда. Вызывающая сторона (TaskManager.Create/Update/SetColumn) ловит
// InvalidOperationException и превращает его в нужный HTTP-статус.
//
// Все четыре метода работают только с Kind == Defect; для обычных Task это no-op.
public static class DefectRules
{
    // Длина Steps порог не проверяет — только наличие. Текст пишет постановщик дефекта.
    private const string ReviewRole = "review";

    // 1) Отказ при попытке создать дефект сразу в Done — по статусу или по колонке.
    // Создание дефекта минуя работу запрещено: иначе пропадает весь смысл бага как
    // «работа, которую нашёл наблюдатель и которую надо проверить». Колонку резолвит
    // вызывающая сторона (контроллер, рядом с targetIsReview) и передаёт сюда — иначе
    // клиент мог бы создать дефект в Todo с columnId="done" и обойти гейт по статусу.
    public static void EnsureNotClosedAtCreate(TaskItem task, BoardColumn? targetColumn = null)
    {
        if (task.Kind != TaskKind.Defect) return;
        if (task.Status == TaskItemStatus.Done)
            throw new InvalidOperationException(
                "Дефект нельзя создавать сразу в Done: путь прошёл проверку мимо работы.");
        if (targetColumn is { Category: TaskItemStatus.Done })
            throw new InvalidOperationException(
                "Дефект нельзя создавать сразу в Done: путь прошёл проверку мимо работы.");
    }

    // 2) Отказ при переводе дефекта в Done без Verification. Инвариант закрытого
    // дефекта — дизъюнкция: Status == Done ⇒ Verification != null ИЛИ
    // Outcome == ClosedWithoutCheck. Если закрываем обычным путём (не ClosedWithoutCheck),
    // Verification обязан содержать осмысленный Notes — пустая или пробельная строка
    // эквивалентна отсутствию вердикта (иначе {notes: ""} проходил бы как валидная
    // проверка и закрывал дефект мимо сути «что проверили»).
    public static void EnsureVerificationOnClose(TaskItem task)
    {
        if (task.Kind != TaskKind.Defect) return;
        if (task.Status != TaskItemStatus.Done) return;
        if (HasMeaningfulNotes(task.Verification)) return;
        if (task.Outcome == DefectOutcome.ClosedWithoutCheck) return;
        throw new InvalidOperationException(
            "Дефект нельзя закрывать без Verification: либо заполните Verification, " +
            "либо используйте внутренний путь ClosedWithoutCheck.");
    }

    // Notes считается содержательным, только если строка непустая и не из одних пробелов.
    // null Notes допустим — Verification может прийти только с VerifiedAt (контроллер
    // проставляет его сам из X-Caller-Session-Id) без явного комментария проверяющего:
    // в этом случае решение о закрытии уже принято человеком/персоной, и пустой комментарий
    // гейт не блокирует.
    private static bool HasMeaningfulNotes(TaskVerification? verification)
    {
        if (verification is null) return false;
        if (verification.Notes is null) return true;
        return !string.IsNullOrWhiteSpace(verification.Notes);
    }

    // 3) Отказ при попадании дефекта в колонку с Role == "review" без заполненных
    // Repro.Steps. Колонка ревью предполагает, что наблюдатель передаёт дефект
    // ревьюеру с описанием «как воспроизвести»; пустые шаги делают ревью бессмысленным.
    // null targetColumn или другая Role — обычное перемещение, правило не действует.
    public static void EnsureReproOnReview(TaskItem task, BoardColumn? targetColumn)
    {
        if (task.Kind != TaskKind.Defect) return;
        if (targetColumn is null) return;
        if (!string.Equals(targetColumn.Role, ReviewRole, StringComparison.Ordinal)) return;
        if (!string.IsNullOrWhiteSpace(task.Repro?.Steps)) return;
        throw new InvalidOperationException(
            "Дефект попадает в ревью без шагов воспроизведения: заполните Repro.Steps.");
    }

    // 4) Вычисление исхода ClosedWithoutCheck для внутренних путей закрытия.
    // Возвращает значение enum, которое вызывающая сторона записывает в TaskItem.Outcome.
    // Отдельный метод вместо прямого DefectOutcome.ClosedWithoutCheck — единая точка
    // для будущих ClosedWithoutCheck-исходов (например, вариантов «снято по политике
    // проекта»), которые заведут следующие волны.
    public static DefectOutcome ComputeClosedWithoutCheck() => DefectOutcome.ClosedWithoutCheck;
}
