using ClaudeHomeServer.Services.Knowledge;

namespace ClaudeHomeServer.Services.Composition;

// Реализация IWorkspaceDatasetLookup: тонкая обёртка над WorkspaceKnowledgeStore,
// достаёт только два поля записи. Сама `WorkspaceKnowledge` (корень, карта отслеживаемых
// файлов `Docs` с хешами, момент обновления) через шов не утекает, как и методы записи
// стора и снимок всех рабочих деревьев.
public sealed class WorkspaceDatasetLookup(WorkspaceKnowledgeStore store) : IWorkspaceDatasetLookup
{
    public WorkspaceDatasetInfo? ForRoot(string rootPath) =>
        store.GetByPath(rootPath) is { } wk
            ? new WorkspaceDatasetInfo(wk.DifyDatasetId, wk.DocumentTags)
            : null;
}
