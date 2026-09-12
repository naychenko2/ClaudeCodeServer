using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Notes;

// Узкий шов доступа к заметкам (Этап 5): вертикаль Notes живёт в отдельной сборке
// `ClaudeHomeServer.Notes`, а потребители в спине держат этот Core-контракт.
//
// Состав собран ПО ФАКТИЧЕСКИМ вызовам спины (разведка 2026-09-12), не «на вырост»:
//  - Create/GetDetail — запись памяти в заметки (PersonaMemoryService), заметка-итог
//    сессии (SessionSummaryService), текст карточки архива (ChatDigestService);
//  - GetSources/GetSummaries — каталог источников и списки заметок (привязки персон,
//    каталог целей автоматизаций, единый поиск, утренний бриф);
//  - GetOrCreateDaily/Update — дневниковая заметка брифа и её правка;
//  - RewriteAnnotationTargets — перепись привязок комментариев при переименовании
//    документа (FilesController/DocsController/WorkspaceToolset).
// Полного API заметок (граф, backlinks, треды комментариев, move/delete) здесь НЕТ:
// единственный его потребитель — MCP-тулсет `NotesToolset`, и тот живёт уже в
// вертикали `ClaudeHomeServer.Notes`, оставаясь на `NotesService` как продуктовая
// граница сервера заметок.
// Запись и перепись аннотаций (`Update`/`RewriteAnnotationTargets`/`GetOrCreateDaily`) —
// для спины; вертикаль с read-only нуждами должна просить расщепление шва, а не тянуть
// его целиком.
//
// Реализация — сам `NotesService` в вертикали: интерфейс объявлен под его сигнатуры,
// отдельного класса-адаптера нет.
// Типы `CreateNoteRequest`/`UpdateNoteRequest`/`NoteDetail`/`NoteSummary`/`NoteSourceDto`
// уже в Core (Models/Note.cs) — новых DTO шов не заводит.
public interface INoteAccessor
{
    // Создать заметку с минимальным набором полей. Возвращает полную запись
    // (NoteDetail) с проставленными сервером id/timestamps.
    NoteDetail Create(string ownerId, CreateNoteRequest request);

    // Получить заметку по id. null — не найдена (или чужой владелец).
    NoteDetail? GetDetail(string ownerId, string noteId);

    // Источники заметок владельца (личный vault + его проекты) — для выбора «куда
    // создать» и каталога целей автоматизаций/привязок персон.
    IReadOnlyList<NoteSourceDto> GetSources(string ownerId);

    // Сводки заметок владельца: source — фильтр по источнику (null — все),
    // query — ключевой поиск (null — без него).
    IReadOnlyList<NoteSummary> GetSummaries(string ownerId, string? source, string? query);

    // Дневниковая заметка за день (дата YYYY-MM-DD, null — сегодня): создаётся, если её нет.
    NoteDetail GetOrCreateDaily(string ownerId, string? date);

    // Правка заметки. null — заметка не найдена (файла нет). НО источник резолвится
    // первым (`ResolveRoot`): несуществующий источник — `KeyNotFoundException`, чужой
    // владельцу проекта — `UnauthorizedAccessException`. То есть сигнатура обещает null
    // только для «нет такой заметки», промах по источнику летит исключением.
    NoteDetail? Update(string ownerId, string noteId, UpdateNoteRequest request);

    // Перепись привязок комментариев к документу при его переименовании/переносе
    // (точечно или по префиксу для папки). Возвращает число изменённых комментариев.
    int RewriteAnnotationTargets(string ownerId, string oldScope, string oldPath,
        string newScope, string newPath, bool prefix = false);
}
