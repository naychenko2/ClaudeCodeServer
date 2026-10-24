namespace ClaudeHomeServer.Services.Composition;

// Шов «текущий коммит рабочего дерева»: гейт предгейта (TaskManager) сверяет с ним sha зелёного
// результата. Реализация в Tasks читает git; тесты подставляют заглушку
public interface IWorktreeHeadReader
{
    // Полный sha HEAD дерева по хостовому пути; null — прочитать не удалось (не git-дерево, git упал)
    string? Head(string worktreePath);
}
