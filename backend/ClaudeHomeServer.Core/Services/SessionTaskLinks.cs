using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Единая точка вычисления «связей с задачей» сессии поверх шва ITaskLookup.
// Выносит из модели Session вычисляемые признаки ParentSessionId/TaskDone,
// которые раньше читали СТАТИЧЕСКИЕ Func-резолверы, поставленные конструктором
// TaskManager (вертикаль Tasks): спин-модель фактически зависела от вертикали, а
// сторож границ (рефлексия по типам) статического присваивания не видел.
// ITaskLookup — единственный шов (новый не заводим). null-safe: lookup=null → задача
// не резолвится — ровно та же семантика, что у прежней «резолвер не установлен»
// (ParentSessionId=null, TaskDone=false), без исключений.
//
// Живёт в СПИНЕ (Core), а не в Main: обе функции — чистые проекции над Core-типами
// (Models.Session + контракт Core/Services/ITaskLookup.cs), зависимостей от Main нет
// вовсе. В Main этот же файл заставлял вертикали ходить в обход (см. комментарий
// TeamWaveService: «SessionTaskLinks напрямую нельзя — он в namespace Main-root»),
// хотя никакой новой поверхности зависимостей вызов не открывает: сторож границ
// пропускает Core-типы раньше проверки допусков (IsCoreAssembly). Namespace оставлен
// прежним — call-site'ы не меняются, как при любом переносе спины в Core.
public static class SessionTaskLinks
{
    // TaskDone: чат-исполнитель выполненной задачи (TaskId → Status == Done).
    // true только для чатов выполненных задач (бывший «архив»); false — обычные чаты
    // и живые задачи.
    public static bool IsTaskDone(Session s, ITaskLookup? tasks) =>
        s.TaskId is { } tid && tasks?.GetById(tid)?.Status == TaskItemStatus.Done;

    // ParentSessionId: ручная группировка (drag-and-drop) побеждает, иначе чат, в котором
    // была создана задача чата-исполнителя (TaskId → Task.SourceSessionId). null — корневой
    // чат (задачи нет/удалена — чат всплывает в корень, принято осознанно) либо чат,
    // вынесенный в корень пользователем (ParentDetached).
    public static string? ParentSessionId(Session s, ITaskLookup? tasks) =>
        s.ParentOverrideId is not null ? s.ParentOverrideId
        : s.ParentDetached ? null
        : s.TaskId is { } tid ? tasks?.GetById(tid)?.SourceSessionId : null;
}
