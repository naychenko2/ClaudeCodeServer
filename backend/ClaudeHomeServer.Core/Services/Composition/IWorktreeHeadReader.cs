namespace ClaudeHomeServer.Services.Composition;

// Шов «состояние git рабочего дерева»: гейт предгейта (TaskManager) сверяет с ним sha зелёного
// результата, TaskPregateService — чистоту дерева и набор изменённых файлов. Реализация в Tasks
// читает git; тесты подставляют заглушку
public interface IWorktreeHeadReader
{
    // Полный sha HEAD дерева по хостовому пути; null — прочитать не удалось (не git-дерево, git упал)
    string? Head(string worktreePath);

    // true — незакоммиченных и неотслеживаемых изменений нет; false — есть; null — прочитать не удалось.
    // Предгейт гоняет проверки по рабочему дереву, а вердикт привязывает к HEAD: пока дерево грязное,
    // зелёный относился бы к коду, которого в коммите нет
    bool? IsClean(string worktreePath);

    // Файлы, изменённые относительно основной ветки (merge-base..HEAD), от корня дерева через «/».
    // null — определить не удалось: фильтр WhenChanged тогда не применяется и идут все проверки
    IReadOnlyList<string>? ChangedFiles(string worktreePath);
}
