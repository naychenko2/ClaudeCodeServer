namespace ClaudeHomeServer.Services.Composition;

// Шов файловых операций для вертикали «Документация» (`Services.Docs`):
// DocsIndexService пишет и переименовывает файлы в рабочей папке проекта. Полный
// `FileService` для этого избыточен (там ещё листинги, поиск, диффы и кеш git-статуса),
// а главное — он живёт в Main, и шов держит Docs на расстоянии от Main.
//
// Контракт ровно под методы, которые зовёт DocsIndexService (никаких «на будущее»):
// создать каталог/файл, записать текст или байты, прочитать байты, удалить, переименовать.
// Семантика 1:1 с `FileService.{CreateDirectory,CreateFile,WriteFile,WriteFileBytes,
// ReadFileBytes,Delete,Rename}` — адаптер в Main (`ProjectFileGateway`) тонкий.
//
// Адаптер ОБЯЗАН идти через `FileService`, а не писать файлы сам: `FileService`
// дёргает событие `OnMutated` (`FileService.cs`), на котором висит синк базы знаний
// (ProjectKnowledgeSyncService). Прямой `File.WriteAllText`/`Directory.Move` в адаптере
// обходил бы синк молча — правки документов перестали бы доходить до Dify.
//
// Опциональность (`?`) повторяет исходный контракт `DocsIndexService(FileService?)`:
// чтение корпуса идёт без файлового сервиса, юнит-тесты тоже. Методы записи
// (CreateDoc/WriteProperty/RenameDoc/MoveDoc/DeleteDoc) проверяют null и возвращают
// ошибку, если сервиса нет — это и сохраняется.
public interface IProjectFileGateway
{
    void CreateDirectory(string root, string relativePath);
    void CreateFile(string root, string relativePath);
    void CreateFile(string root, string relativePath, string content);
    void WriteFile(string root, string relativePath, string content);
    void WriteFileBytes(string root, string relativePath, byte[] content);

    // Текстовое чтение — 1:1 с FileService.ReadFile. Используется
    // ProjectKnowledgeSyncService для вычисления SHA-256 содержимого файла.
    string ReadFile(string root, string relativePath);

    byte[] ReadFileBytes(string root, string relativePath);
    void Delete(string root, string relativePath);
    void Rename(string root, string oldRelative, string newRelative);

    // Событие мутаций файлов. Адаптер в Main подписывается на FileService.OnMutated
    // и ретранслирует через это событие. Используется ProjectKnowledgeSyncService
    // для детектирования переноса/удаления файлов.
    event Action<string, string, FileMutationKind, string?>? OnMutated;
}
