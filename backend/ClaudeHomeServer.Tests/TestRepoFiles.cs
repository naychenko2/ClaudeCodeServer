namespace ClaudeHomeServer.Tests;

/// <summary>
/// Файл репозитория по пути от корня — для тестов-сторожей, читающих исходники дерева
/// (конфиги, фронтовые файлы), а не артефакты сборки. Корень — подъём от bin-каталога
/// тестов до .git: в основном дереве это каталог, в worktree — файл со ссылкой, поэтому
/// проверяются обе формы. Подъём, а не относительный путь в коде: тесты стартуют из
/// bin/…, а машинный абсолютный путь был бы платформенным (платформонезависимость тестов,
/// CLAUDE.md). Работает одинаково в основном дереве, worktree задачи и копии мутации.
///
/// Вынесено из двух дословных копий — MemoryToolsetParityTests.RepoFile и
/// SubsystemDisableMatrixTests.RepoFile (п. 5 хвоста R-S2b-2a). Тот же подъём остаётся в
/// TasksControllerTests, PlansControllerTests, SsrfSafeHandlerTests, PromptBenchTests,
/// WidgetsToolsetParityTests — они переезжают сюда при касании их строк, не этого шага.
/// </summary>
internal static class TestRepoFiles
{
    public static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
               && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        var path = dir is null ? null : Path.Combine([dir.FullName, .. parts]);
        if (path is null || !File.Exists(path))
            throw new InvalidOperationException(
                $"{Path.Combine(parts)} не найден — сторож не может читать файл репозитория");
        return path;
    }
}
