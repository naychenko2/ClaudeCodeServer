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
    /// </summary>
    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        // VCS / IDE / кеш
        ".git", ".claude", ".vs", ".idea", ".cache",
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
