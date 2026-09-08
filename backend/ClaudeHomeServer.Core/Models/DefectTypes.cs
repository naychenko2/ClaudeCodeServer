namespace ClaudeHomeServer.Models;

// Типы карточек дефектов (DefectRules, MCP tasks-server, фронт lib/tasks.ts). Enum'ы и record
// НИЧЕМ не декорируем: конвенция стора — числа, camelCase на проводе даёт глобальный
// JsonStringEnumConverter(CamelCase) (Program.cs).

// Вид карточки задачи. Task — обычная работа по проекту; Defect — баг, найденный наблюдателем,
// требующий шагов воспроизведения и явного вердикта при закрытии (см. Services/DefectRules).
public enum TaskKind { Task, Defect }

// Итог дефекта. Единственное значение этой волны — внутренний путь закрытия без отдельной
// проверки (галочка заметки, снятие волной штаба): MCP-схема и фронт ограничивают outcome
// ровно этим значением, прочие исходы дефекта планируются следующими волнами.
public enum DefectOutcome { ClosedWithoutCheck }

// Шаги воспроизведения дефекта. Steps обязателен при попадании в review-колонку
// (DefectRules.EnsureReproOnReview); Expected/Actual — свободные поля для контекста.
// Class с пустым конструктором — DefectRulesTests и клиенты (MCP/фронт) шлют объект целиком
// через object initializer (частичное обновление не поддерживается: replace as a whole).
public class DefectRepro
{
    public string? Steps { get; set; }
    public string? Expected { get; set; }
    public string? Actual { get; set; }
}

// Вердикт проверки дефекта. VerifiedAt и PersonaId ставит сервер (PersonaId — из сессии
// вызова X-Caller-Session-Id, заголовок подделываем — это гигиена атрибуции, не защита);
// Notes — комментарий проверяющего, единственное поле, которое присылает клиент.
public class TaskVerification
{
    public DateTime VerifiedAt { get; set; }
    public string? PersonaId { get; set; }
    public string? Notes { get; set; }
}
