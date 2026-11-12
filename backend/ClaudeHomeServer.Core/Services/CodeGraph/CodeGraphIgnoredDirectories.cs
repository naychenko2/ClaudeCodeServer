namespace ClaudeHomeServer.Services.CodeGraph;

// Каталоги, которые граф кода не обходит: кеш/зависимости/артефакты сборки/IDE-мусор.
// Живёт в Core, потому что нужен обеим сторонам границы: вертикали CodeGraph (полное и
// инкрементальное построение — CompilationBuilder.IgnoredDirectories ссылается сюда) и
// FileWatcherService из корня Services (перенос в таких каталогах граф не устаревает).
// Ссылка корня на тип вертикали роняет сторож RootSubsystemBoundaryTests.
public static class CodeGraphIgnoredDirectories
{
    /// <summary>
    /// Сравнение по имени каталога (case-insensitive), на любой глубине. Идея та же, что у
    /// TreeExcludes, но шире: убираем .claude (плагины oh-my-claudecode и их кеш),
    /// packages (NuGet), TestResults — иначе этот мусор раздувает detect и валит граф в
    /// regex-fallback (баг прода: 7372 .cs в .claude/ → 7878 &gt; порога 5000 → Roslyn не звался).
    /// Вложенные <c>.worktrees</c> (копии проекта в корне — их сотни .cs дублируют узлы и валят
    /// граф в fallback) и <c>.cc-attachments</c> (вложения чатов) тоже не обходим. Список сверяется
    /// по пути ОТНОСИТЕЛЬНО корня обхода, поэтому граф самого worktree (корень
    /// <c>&lt;проект&gt;/.worktrees/x</c>, ADR-003) строится полностью.
    /// </summary>
    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        // VCS / IDE / кеш
        ".git", ".claude", ".vs", ".idea", ".cache",
        // Вложенные worktree и вложения чатов
        ".worktrees", ".cc-attachments",
        // Зависимости
        "node_modules", "packages",
        // Артефакты сборки .NET
        "bin", "obj",
        // Результаты тестов (coverage/temp)
        "TestResults",
        // Фронтенд/прочие build-артефакты (совпадает с TreeExcludes)
        "dist", "dev-dist", "publish", ".next", "target",
    };
}
