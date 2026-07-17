using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Services;

// Достаёт блок `export const meta = { … }` из скрипта именованного workflow.
//
// Зачем: механики команды («панель экспертов» и др.) запускаются по ИМЕНИ
// (Workflow({ name, args }) без inline-script). Фронт умеет доставать meta.phases
// только из input.script, поэтому индикация этапов (дотики фаз, счётчик N/M в тулбаре
// и в карточке workflow) для таких запусков пропадала. Обогащаем input tool_use
// вырезанным meta-блоком того же скрипта, что реально исполнил CLI.
public static class WorkflowMetaResolver
{
    public static ILogger Log { get; set; } = NullLogger.Instance;

    // ~/.claude/workflows — каталог workflow-скриптов основного профиля (подписка)
    public static readonly string GlobalWorkflowsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "workflows");

    // Кэш: полный путь файла → вырезанный meta-блок. Кэшируем только УСПЕХИ —
    // промахи не кэшируем, чтобы лениво засеянный профиль провайдера подхватился позже.
    // Скрипты в рантайме не меняются, инвалидация по mtime не нужна.
    private static readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Репо-source claude-defaults/workflows — фолбэк для dev-хоста, где ~/.claude/workflows
    // засеян не полностью (там встроенные механики — source of truth)
    private static readonly Lazy<string?> _defaultsDir = new(FindDefaultsWorkflowsDir);

    // dirs — каталоги-кандидаты в порядке приоритета (профиль сессии); в конец
    // добавляется claude-defaults. Возвращает текст блока `export const meta {…}` или null.
    public static string? TryGetMetaBlock(IEnumerable<string> dirs, string name)
    {
        if (!IsSafeName(name)) return null;

        var all = new List<string>(dirs);
        if (_defaultsDir.Value is { } def) all.Add(def);

        foreach (var dir in all)
        {
            var path = Path.Combine(dir, name + ".js");
            if (_cache.TryGetValue(path, out var cached)) return cached;
            var block = ExtractMeta(path);
            if (block is not null)
            {
                _cache[path] = block;
                return block;
            }
        }
        return null;
    }

    // Имя workflow приходит от модели — пускаем только безопасный slug (без разделителей
    // пути и точек: защита от traversal, файлы всегда {name}.js)
    private static bool IsSafeName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    // Вырезает `export const meta = { … }` по балансу фигурных скобок (тот же приём, что
    // frontend/src/lib/workflowMeta.ts). Скобки внутри строк не учитываем — meta-блоки
    // простые (как и на фронте), для phases/description/name этого достаточно.
    private static string? ExtractMeta(string path)
    {
        if (!File.Exists(path)) return null;
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Не удалось прочитать workflow-скрипт: {Path}", path);
            return null;
        }

        var metaStart = text.IndexOf("export const meta", StringComparison.Ordinal);
        if (metaStart < 0) return null;
        var braceStart = text.IndexOf('{', metaStart);
        if (braceStart < 0) return null;

        var depth = 0;
        for (var i = braceStart; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return text[metaStart..(i + 1)];
            }
        }
        return null;
    }

    // Ищет claude-defaults/workflows вверх от каталога сборки (dev-хост) + типовой путь
    // образа (/app/claude-defaults). Один раз за процесс (Lazy).
    private static string? FindDefaultsWorkflowsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "claude-defaults", "workflows");
            if (Directory.Exists(candidate)) return candidate;
        }
        const string container = "/app/claude-defaults/workflows";
        return Directory.Exists(container) ? container : null;
    }
}
