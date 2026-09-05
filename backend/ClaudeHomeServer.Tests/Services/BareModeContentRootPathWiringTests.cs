using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// СТОРОЖ проводки ContentRootPath в прод-коде: фича BareMode в проде включается
// только если SessionManager передаёт ContentRootPath=AppContext.BaseDirectory
// во всех точках сборки LlmSessionContext. Без проводки _serverContentRoot==null
// → ResolveHostPath уходит в SafeJoin(projectRoot, ...) → файла карты нет →
// флаги снимаются → ход по полной CLAUDE.md (фича молча деградирует).
//
// Мутация «удалить ContentRootPath=…» в любой из точек ломает этот тест.
// Подсчёт вхождений `new LlmSessionContext(` синхронизирован с SessionManager
// (на 2026-09-05 — три: :3729, :5097, :5147). Добавление нового места без
// ContentRootPath краснеет по количеству; удаление точки без правки теста —
// тоже красное (тест откажется пропустить меньшее число). Тест НЕ зависит от
// значения ContentRootPath (AppContext.BaseDirectory, Guid, что угодно) —
// проверяет только ФАКТ проводки.
public class BareModeContentRootPathWiringTests
{
    // Каталог решения (.slnx лежит рядом с ClaudeHomeServer/, т.е. в backend/, а не в корне
    // репозитория) — имя BackendRoot точнее: это та папка, где живёт ClaudeHomeServer.csproj.
    private static string BackendRoot
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "ClaudeHomeServer.slnx"))
                    || File.Exists(Path.Combine(dir, "ClaudeHomeServer.sln")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException(
                "Не найден каталог решения (ClaudeHomeServer.sln[x])");
        }
    }

    [Fact]
    public void SessionManager_ВсеНовыеLlmSessionContext_ПередаютContentRootPath()
    {
        // BackendRoot указывает на каталог, где лежит ClaudeHomeServer.slnx — рядом с
        // ClaudeHomeServer/Services/SessionManager.cs. Имя RepoRoot было неточным:
        // .slnx живёт в backend/, а не в корне репо, поэтому SessionManager.cs —
        // сосед, а не «внук».
        var file = Path.Combine(BackendRoot, "ClaudeHomeServer", "Services", "SessionManager.cs");
        File.Exists(file).Should().BeTrue($"файл {file} обязан существовать");

        var text = File.ReadAllText(file);
        var occurrences = CountOccurrences(text, "new LlmSessionContext(");

        occurrences.Should().Be(3,
            "три точки сборки контекста в SessionManager; добавление новой обязано сопровождаться проводкой ContentRootPath");

        // Для каждого вхождения смотрим на ~1500 символов вперёд — тело конструктора
        // с именованными параметрами; проверяем, что ContentRootPath задан НЕНУЛЁВЫМ значением.
        // `ContentRootPath: null` — мутация проводки: компилируется, BareMode молча деградирует.
        var idx = 0;
        var foundAny = 0;
        var nullWiringCount = 0;
        while ((idx = text.IndexOf("new LlmSessionContext(", idx, StringComparison.Ordinal)) >= 0)
        {
            var windowEnd = Math.Min(text.Length, idx + 1500);
            var window = text[idx..windowEnd];
            if (!window.Contains("ContentRootPath", StringComparison.Ordinal))
            {
                idx += "new LlmSessionContext(".Length;
                continue;
            }
            foundAny++;
            // Отлавливаем мутацию «ContentRootPath: null» или «ContentRootPath = null».
            if (System.Text.RegularExpressions.Regex.IsMatch(window,
                @"ContentRootPath\s*[:=]\s*null\b"))
                nullWiringCount++;
            idx += "new LlmSessionContext(".Length;
        }

        foundAny.Should().Be(occurrences,
            "каждый вызов new LlmSessionContext(...) должен передавать ContentRootPath — иначе BareMode молча деградирует в проде");
        nullWiringCount.Should().Be(0,
            "ContentRootPath: null в прод-коде эквивалентен отсутствию проводки — BareMode в проде " +
            "выключится молча (файл карты не найдётся через SafeJoin(projectRoot, ...))");
    }

    private static int CountOccurrences(string text, string needle)
    {
        var n = 0;
        var i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += needle.Length;
        }
        return n;
    }
}
