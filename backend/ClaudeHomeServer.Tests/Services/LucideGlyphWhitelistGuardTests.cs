using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.ProjectIcons;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Тест-сторож белого списка значков (ADR-009 §5.4, ревизия 17.08.2026). Рукописных списков
// больше нет: LucideGlyphs.All — производная генерируемой копии lucide-icon-names.g.txt, и
// единственное возможное расхождение — копию забыли перегенерировать после апгрейда
// lucide-react. Симптом тот же тихий, что и раньше: сервер принимает имя, которого фронт
// не нарисует (или отбрасывает годное) — значок молча не появляется, в логах ничего.
// Сверяем ключи карты loader'ов dynamicIconImports.mjs: рисуется ровно то, что есть в карте,
// и валидировать надо то же множество. В бэковом CI node_modules нет — сторож пропускается
// явно, дрейф там ловит зеркальный vitest-сторож фронта (блокирующий шаг CI).
public class LucideGlyphWhitelistGuardTests
{
    // Корень решения ищем снизу вверх от папки тестовой сборки: тесты стартуют из bin/…,
    // а Windows-литерал пути на Linux-раннере CI считался бы относительным — только
    // Path.Combine от найденного каталога (платформонезависимость тестов, CLAUDE.md)
    private static string? FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent!;
        }
        return null;
    }

    // Ключи карты loader'ов: "имя": () => import('./icons/имя.mjs'). Компоненты справа не
    // разбираем. Звёздочка, а не плюс, после первого символа: в наборе есть однобуквенное
    // имя «x», строгая версия теряла бы его и сторож краснел навсегда (ADR-009 §5.4)
    private static HashSet<string> ParseLoaderKeys(string mjs)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(mjs, @"""([a-z][a-z0-9-]*)"":\s*\(\)\s*=>"))
            keys.Add(m.Groups[1].Value);
        Assert.NotEmpty(keys);
        return keys;
    }

    // Длинный список в сообщении отказа нечитаем: первые 20 имён + счётчик
    private static string Describe(IReadOnlyList<string> names) =>
        $"[{string.Join(", ", names.Take(20))}]" + (names.Count > 20 ? $" … всего {names.Count}" : "");

    [SkippableFact]
    public void БелыйСписокБэка_СовпадаетСКлючамиКартыУстановленногоПакета()
    {
        var mjsPath = FindRepoFile("frontend", "node_modules", "lucide-react",
            "dist", "esm", "dynamicIconImports.mjs");
        Skip.If(mjsPath is null,
            "lucide-react не установлен — дрейф ловит vitest-сторож фронта");

        var package = ParseLoaderKeys(File.ReadAllText(mjsPath!));
        var backend = LucideGlyphs.All.ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(backend);   // пустой список = ресурс не загрузился, а не «всё совпало»

        var backendOnly = backend.Except(package).Order().ToList();
        var packageOnly = package.Except(backend).Order().ToList();

        Assert.True(backendOnly.Count == 0 && packageOnly.Count == 0,
            "Белый список значков разошёлся с установленным lucide-react (ADR-009 §5.4) — " +
            "перезапусти генератор имён: node scripts/gen-glyph-names.mjs из frontend/. " +
            $"Только на бэке (LucideGlyphs.All): {Describe(backendOnly)} — сервер примет имя, " +
            "которого нет в пакете, значок молча не отрисуется у пользователя. " +
            $"Только в пакете (dynamicIconImports): {Describe(packageOnly)} — сервер это имя не принимает.");
    }
}
