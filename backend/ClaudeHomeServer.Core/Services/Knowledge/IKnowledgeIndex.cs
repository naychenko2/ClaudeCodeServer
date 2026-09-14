namespace ClaudeHomeServer.Services.Knowledge;

// Узкий контракт Dify-клиента. На него переведены Notes и Dossiers — они зовут ровно
// это подмножество. Memory (PersonaMemoryService/TeamMemoryService) НА КОНТРАКТ НЕ
// ПЕРЕВЕДЕНА: держит конкретный KnowledgeService ради RenameDatasetAsync, которого
// здесь нет. Понадобится сузить и Memory — контракту нужен седьмой метод (уточнено
// ревью 2026-09-08: прежняя формулировка «для Notes/Memory/Dossiers» вводила в
// заблуждение).
// Шесть методов — те, что реально зовут потребители по факту разбора (Этап 5, волна 5):
//   - IsConfigured — гейт graceful degradation в NotesKnowledgeService;
//   - CreateDatasetAsync — `{username}:notes` у Notes;
//   - DeleteDocumentAsync — синк заметок и memory по одной записи;
//   - DeleteDatasetAsync — каскад Dossiers (один Dify-датасет на проект);
//   - IndexFileByTextAsync — индексация заметки/записи памяти;
//   - RetrieveAsync — семантический поиск для подсказок Notes.
//
// Метод EnsureDatasetAsync(Project, string) намеренно НЕ включён: это знание
// вертикали Knowledge (ProjectKnowledgeSyncService использует его при первом
// обращении к БЗ проекта) и в Core-контракт не попадает. Project и DifyOptions
// остаются деталями конструктора Main-класса KnowledgeService.
//
// Сигнатуры — префиксы реализации в Main (KnowledgeService): потребители
// передают первые N позиционных/named аргументов, остальные опциональные
// параметры реализации (searchMethod/indexingTechnique/scoreThreshold/etc.)
// доступны только Main-вызывающим (контроллеры, ProjectKnowledgeSyncService,
// DifyToolset) и в Core-контракт не попадают. KnowledgeSubsystem.Register
// ниже добавляет forwarder `IKnowledgeIndex → KnowledgeService`.
public interface IKnowledgeIndex
{
    // True, если Dify-секция конфигурации непустая (ApiUrl + ApiKey). Иначе
    // все методы ниже бросают InvalidOperationException — грейсful degradation
    // зовущих сводится к проверке этого гейта.
    bool IsConfigured { get; }

    // Создаёт датасет с указанным именем; возвращает id созданного датасета.
    // indexingTechnique — null/не задан = настройка Dify-инстанса (дефолт).
    Task<string> CreateDatasetAsync(string name, string permission = "only_me", string? description = null,
        string? indexingTechnique = null);

    // Удаляет документ по id в датасете. Потребители глотают исключения (best-effort).
    Task DeleteDocumentAsync(string datasetId, string documentId);

    // Удаляет весь датасет Dify. Используется Dossiers-каскадом при удалении проекта.
    Task DeleteDatasetAsync(string datasetId);

    // Переименовать датасет (Dify: PATCH /datasets /d). Используется при смене хендла
    // персоны (PersonaMemoryService.RenameDatasetSafeAsync) и при переименовании
    // проекта (TeamMemoryService.RenameProjectDatasetAsync). Сбой — лог: id остаётся
    // валидным, retrieve работает, и стухшее имя функциональность не ломает.
    // Этап 5, волна 2: добавлен в Core-контракт ради Memory↔Knowledge (раньше Memory
    // держала прямую ссылку на KnowledgeService именно ради этого метода).
    Task RenameDatasetAsync(string datasetId, string newName);

    // Индексирует текстовый документ: создаёт запись Dify, возвращает её описание.
    // Используется заметками и памятью.
    Task<DifyDocumentInfo> IndexFileByTextAsync(string datasetId, string fileName, string content,
        List<string>? tags = null);

    // Семантический поиск по датасету: возвращает topK чанков с метаданными.
    // Notes использует для подсказок связей; memory — для recall.
    Task<IReadOnlyList<DifyRetrieveChunk>> RetrieveAsync(string datasetId, string query, int topK = 8,
        IReadOnlyList<KnowledgeMetadataFilter>? filters = null);
}
