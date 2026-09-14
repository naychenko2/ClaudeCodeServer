using System.Text.RegularExpressions;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// СТОРОЖ проводки ContentRootPath в прод-коде: фича BareMode в проде включается
// только если ContentRootPath=AppContext.BaseDirectory доезжает до ClaudeSession.
// Без проводки _serverContentRoot==null → ResolveHostPath уходит в
// SafeJoin(projectRoot, ...) → файла карты нет → флаги снимаются → ход по полной
// CLAUDE.md (фича молча деградирует).
//
// Сканируется ВЕСЬ прод-проект, а не один SessionManager.cs: до круга 8 тест смотрел
// ровно в один файл, а комментарий обещал «во всех точках сборки». Мимо него проходили
// два сценария (ревью 2026-09-06, M-2):
//   1) новая точка `new LlmSessionContext(...)` в другом файле — параметр опциональный
//      (LlmSessionContext.cs, дефолт null), компилируется молча;
//   2) `context with { ContentRootPath = null }` — копия record'а вместо конструктора;
//      сквозные BareArgs-тесты собирают ClaudeSession напрямую, мимо фабрики, и такую
//      мутацию не видел НИ ОДИН тест.
//
// Количество точек не закрепляется (ревью 2026-09-05, L-2: число правили третий круг
// подряд) — проверяется только ФАКТ проводки в каждой найденной точке.
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

    // Все исходники прод-проекта, кроме артефактов сборки. LlmSessionContext объявлен
    // в ClaudeHomeServer (ссылки идут Main → Core), поэтому построить контекст можно
    // только здесь — Core сканировать незачем.
    private static IReadOnlyList<string> ProdSources()
    {
        var root = Path.Combine(BackendRoot, "ClaudeHomeServer");
        Directory.Exists(root).Should().BeTrue($"каталог прод-проекта {root} обязан существовать");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal))
            .ToList();
        // Защита от вакуумного прохода: пустой набор сделал бы оба теста зелёными ни на чём.
        files.Should().NotBeEmpty("скан прод-кода обязан находить файлы, иначе сторож проходит вхолостую");
        return files;
    }

    [Fact]
    public void ВсеТочкиСборкиLlmSessionContext_ПередаютContentRootPath()
    {
        const string ctor = "new LlmSessionContext(";
        var total = 0;
        var missing = new List<string>();

        foreach (var file in ProdSources())
        {
            var text = File.ReadAllText(file);
            var idx = 0;
            while ((idx = text.IndexOf(ctor, idx, StringComparison.Ordinal)) >= 0)
            {
                total++;
                // Окно ~1500 символов вперёд — тело конструктора с именованными параметрами.
                var window = text[idx..Math.Min(text.Length, idx + 1500)];
                if (!window.Contains("ContentRootPath", StringComparison.Ordinal))
                    missing.Add($"{Path.GetFileName(file)} (смещение {idx})");
                idx += ctor.Length;
            }
        }

        // Ни одной точки — сторож проходит вхолостую (переименовали тип, переехал файл).
        total.Should().BeGreaterThan(0,
            "в прод-коде обязана быть хотя бы одна точка new LlmSessionContext(...) — иначе сторож мёртв");
        missing.Should().BeEmpty(
            "каждый вызов new LlmSessionContext(...) должен передавать ContentRootPath — " +
            "иначе BareMode молча деградирует в проде");
    }

    [Fact]
    public void ПрودКод_НеОбнуляетContentRootPath_НиГде()
    {
        // Ловит и `new LlmSessionContext(..., ContentRootPath: null)`, и копию record'а
        // `context with { ContentRootPath = null }` (LlmSessionAdapterFactory) — вторая
        // форма конструктором не является и первым тестом не видна.
        var nulling = new Regex(@"ContentRootPath\s*[:=]\s*null\b", RegexOptions.Compiled);
        // Объявление параметра record'а `string? ContentRootPath = null` — это дефолт
        // контракта, а не обнуление проводки: пропускаем ровно его.
        var declaration = new Regex(@"string\?\s*$", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var file in ProdSources())
        {
            var text = File.ReadAllText(file);
            foreach (Match m in nulling.Matches(text))
            {
                var prefix = text[Math.Max(0, m.Index - 20)..m.Index];
                if (declaration.IsMatch(prefix)) continue;
                var line = text[..m.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetFileName(file)}:{line}");
            }
        }

        offenders.Should().BeEmpty(
            "ContentRootPath = null в прод-коде эквивалентен отсутствию проводки — BareMode " +
            "в проде выключится молча (файл карты не найдётся через SafeJoin(projectRoot, ...))");
    }
}
