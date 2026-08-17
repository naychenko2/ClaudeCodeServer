using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.ProjectIcons;

namespace ClaudeHomeServer.Tests.Services;

// Тест-сторож равенства белых списков значков (ADR-009 §5, инвариант §11.5): ключи карты
// GLYPHS фронта (frontend/src/lib/projectGlyphs.ts) обязаны совпадать с LucideGlyphs.All
// бэка в обе стороны. Дублирование — осознанное (компилятор фронта проверяет существование
// каждой иконки lucide, бэк не может тянуть фронтовый файл), а расхождение тихое: бэк
// вернёт имя, которого нет в карте GLYPHS, и значок молча не отрисуется — без ошибки в
// логах. Пополнение — обычный коммит в оба места, сторож держит их ровными.
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

    // Ключи карты GLYPHS: 'kebab-case': Компонент, — из тела объекта между
    // "export const GLYPHS = {" и "} as const". Компоненты справа не разбираем: имя
    // проверяет компилятор фронта, здесь важен только состав ключей
    private static HashSet<string> ParseGlyphMapKeys(string ts)
    {
        var start = ts.IndexOf("export const GLYPHS", StringComparison.Ordinal);
        Assert.True(start >= 0, "в projectGlyphs.ts не найден «export const GLYPHS» — карта переехала?");
        var end = ts.IndexOf("} as const", start, StringComparison.Ordinal);
        Assert.True(end > start, "в projectGlyphs.ts не найден закрывающий «} as const» карты GLYPHS");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(ts[start..end], @"'([a-z][a-z0-9-]*)'\s*:"))
            keys.Add(m.Groups[1].Value);
        Assert.NotEmpty(keys);
        return keys;
    }

    [Fact]
    public void БелыеСписки_ФронтаИБэка_Совпадают()
    {
        var tsPath = FindRepoFile("frontend", "src", "lib", "projectGlyphs.ts");
        Assert.True(tsPath is not null,
            $"frontend/src/lib/projectGlyphs.ts не найден ни одним каталогом вверх от " +
            $"{AppContext.BaseDirectory} — сторож не может проверить списки");
        var frontend = ParseGlyphMapKeys(File.ReadAllText(tsPath!));
        var backend = LucideGlyphs.All.ToHashSet(StringComparer.Ordinal);

        var backendOnly = backend.Except(frontend).Order().ToList();
        var frontendOnly = frontend.Except(backend).Order().ToList();

        Assert.True(backendOnly.Count == 0 && frontendOnly.Count == 0,
            "Белые списки значков разошлись (ADR-009 §5) — правь одним коммитом карту GLYPHS " +
            "и LucideGlyphs.Domains. " +
            $"Только на бэке (LucideGlyphs.All): [{string.Join(", ", backendOnly)}] — бэк примет имя, " +
            "которого нет в карте GLYPHS, значок молча не отрисуется у пользователя. " +
            $"Только на фронте (GLYPHS): [{string.Join(", ", frontendOnly)}] — сервер это имя не принимает.");
    }
}
