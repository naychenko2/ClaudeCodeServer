namespace ClaudeHomeServer.Services.ChatSubjects;

// Вид записи темы чата в SessionManager.ApplySubjects — единственной точке записи полей
// Session.Subject*.
public enum SubjectWriteKind
{
    // Назначение моделью. CAS: принимается, только если чат не закреплён человеком и его
    // SubjectId всё ещё равен ExpectedSubjectId из снимка прохода, а тема есть в реестре.
    Ai,
    // Модель видела чат, но темы не дала (null, мусор, повтор алиаса, потолок): только отметка
    // попытки, чтобы чат не возвращался в каждый тик без новой активности
    Tried,
    // Ручной перенос (SubjectId) или «убрать из темы» (SubjectId == null) — закрепление
    Human,
    // «Вернуть автоматике»: тема, источник и отметка попытки сброшены — чат снова кандидат
    Release,
    // «Разложить заново»: снять назначение МОДЕЛИ и отметку попытки; закреплённые не трогает
    ResetAi,
}

public sealed record SubjectWrite(string SessionId, SubjectWriteKind Kind, string? SubjectId = null,
    string? ExpectedSubjectId = null);
