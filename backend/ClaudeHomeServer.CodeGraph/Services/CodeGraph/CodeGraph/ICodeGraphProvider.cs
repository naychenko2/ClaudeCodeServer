using ClaudeHomeServer.Services.CodeGraph.Core;

namespace ClaudeHomeServer.Services.CodeGraph;

/// <summary>
/// Провайдер построения графа зависимостей для языка/экстension.
/// Строит граф из файлов проекта: узлы — типы (классы/интерфейсы), рёбра — Calls/Implements/References.
/// </summary>
public interface ICodeGraphProvider
{
    /// <summary>
    /// Построить полный граф для всех файлов проекта.
    /// </summary>
    Task<Core.CodeGraph> BuildAsync(string rootPath, CancellationToken ct);

    /// <summary>
    /// Обновить граф по изменившимся файлам (инкрементально).
    /// Если провайдер не поддерживает инкремент — вызывает BuildAsync.
    /// </summary>
    Task<Core.CodeGraph> UpdateAsync(string rootPath, IEnumerable<string> changedFiles, CancellationToken ct);
}
