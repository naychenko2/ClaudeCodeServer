using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Узкий шов ProjectManager для выноса Notes в отдельный .csproj (Этап 5,
// волна E). Полный ProjectManager в Main содержит десятки методов (жизненный
// цикл проекта, миграции, квоты, права); Notes нужен только проектик по id
// и список проектов владельца — этого хватает для NoteTaskSync, NoteExpiry,
// NotesService.SourcesFor/FindLinked.
//
// Интерфейс лежит рядом с другими Core-интерфейсами (ICheapTextRunner,
// IKnowledgeSyncParticipant), неймспейс ClaudeHomeServer.Services уже
// разрешён в CoreAllowedNamespaces сторожа CoreDll_СодержитТолькоРазрешённыеНеймспейсы.
public interface IProjectManager
{
    // Возвращает проект по id или null, если у пользователя нет такого.
    Project? GetById(string id);

    // Возвращает все проекты указанного владельца (для SourcesFor).
    IReadOnlyCollection<Project> GetByOwner(string userId);

    // Полный список всех проектов в системе (NoteExpiryService обходит всех
    // владельцев при уборке временных заметок). Не разделяем по userId — намеренно,
    // Notes нужно очищать заметки ВСЕХ пользователей независимо от того, чей
    // сейчас ход.
    IReadOnlyCollection<Project> GetAll();

    // Возвращает все проекты с указанным корневым путём (папка может быть
    // общей для нескольких владельцев). Используется ProjectKnowledgeSyncService
    // для BroadcastAsync — уведомить всех владельцев папки.
    IReadOnlyCollection<Project> GetByRootPath(string rootPath);
}
