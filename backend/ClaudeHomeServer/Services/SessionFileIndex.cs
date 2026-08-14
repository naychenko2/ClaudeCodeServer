using System.Text.Json;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Извлечение путей файлов, изменённых чатом, из его истории — используется
// ProjectFileSessionsIndex для построения обратного индекса «файл → какие ещё
// чаты его меняли» (панель «Изменения»). Отдельный статический класс (а не метод
// ProjectFileSessionsIndex) — чтобы разбирать историю можно было юнит-тестами
// без поднятия SessionManager/ChatHistoryService.
public static class SessionChangedPaths
{
    // Инструменты прямой правки — то же множество, что WRITE_TOOLS на фронте
    // (useSessionArtifacts.ts): write_file/edit_file — легаси-имена старого DeepSeek-адаптера.
    private static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal)
    {
        "Write", "Edit", "MultiEdit", "NotebookEdit", "write_file", "edit_file",
    };

    // Инструмент из белого списка прямой правки — общая точка для Extract и снятия
    // пометки CommittedFilePaths (SessionManager.OnMessageAsync).
    public static bool IsWriteTool(string name) => WriteTools.Contains(name);

    // Относительные пути (прямые слэши, lowercase), изменённые чатом, — по его истории.
    // Значение — External: true, если файл менялся ТОЛЬКО вне заявленного хода (Bash/скрипты
    // модели либо человек в IDE во время хода); правка через Edit/Write или file_changed
    // с External=false побеждает (false сильнее true при слиянии). Потребители:
    // бейдж «Также меняли» берёт лишь External=false (внешняя правка к чату не привязана),
    // фильтр «только файлы чата» — все записи активного чата.
    //
    // [общий с фронтом] StoredFileChangedMessage / StoredToolUseMessage из WriteTools —
    // то же множество источников, что у фронтового фильтра (ранее computeChangedPaths).
    // [намеренное расхождение] пути хода, ушедшего в чужое git worktree (TurnWorktree != null),
    // исключаются — они относятся к другому дереву, а не к rootPath проекта, и
    // отношения к «этот файл в ЭТОМ проекте менял чат X» не имеют.
    //
    // Зеркало значимого подмножества кейсов во фронтовом тесте —
    // frontend/src/lib/__tests__/git.changedBy.test.ts (там же обратная ссылка сюда).
    public static Dictionary<string, bool> Extract(IReadOnlyList<StoredMessage> messages, string rootPath)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        // Пропуск хода в чужом worktree: включается session_started с TurnWorktree,
        // сбрасывается либо следующим session_started БЕЗ TurnWorktree, либо любым
        // сообщением пользователя (начало нового хода в основном дереве)
        var skippingWorktreeTurn = false;
        foreach (var m in messages)
        {
            switch (m)
            {
                case StoredSessionStartedMessage started:
                    skippingWorktreeTurn = started.TurnWorktree != null;
                    break;
                case StoredUserMessage:
                    skippingWorktreeTurn = false;
                    break;
                case StoredFileChangedMessage fc when !skippingWorktreeTurn:
                    Add(result, Normalize(fc.Path), fc.External);
                    break;
                case StoredToolUseMessage tu when !skippingWorktreeTurn && WriteTools.Contains(tu.Name):
                    if (NormalizedToolPath(tu.Input, rootPath) is { } rel)
                        Add(result, rel, external: false);
                    break;
            }
        }
        return result;
    }

    // Слияние по пути: false (правка своим ходом) побеждает true (только внешняя)
    private static void Add(Dictionary<string, bool> result, string path, bool external) =>
        result[path] = result.TryGetValue(path, out var wasExternal) ? wasExternal && external : external;

    // Прямые слэши, срез ведущего "./" (как фронтовый экстрактор — см.
    // path.replace(/^\.\//, '') в useSessionArtifacts.ts) и lowercase
    public static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.ToLowerInvariant();
    }

    // Путь write-инструмента → нормализованный относительный путь проекта (null — вне
    // корня / не извлекается). Общая точка для Extract и снятия пометки CommittedFilePaths.
    public static string? NormalizedToolPath(object? input, string rootPath) =>
        ExtractToolPath(input) is { } raw && ToRelative(raw, rootPath) is { } rel ? Normalize(rel) : null;

    // file_path ?? notebook_path ?? path — как extractToolPath на фронте. Input после
    // десериализации истории — JsonElement; null/другой тип/без нужных свойств — тихо null.
    private static string? ExtractToolPath(object? input)
    {
        if (input is not JsonElement el || el.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in new[] { "file_path", "notebook_path", "path" })
        {
            if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String) continue;
            var s = v.GetString();
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return null;
    }

    // Абсолютный путь → относительный от rootPath (вне корня — null); уже относительный —
    // нормализуется как есть. Портирует frontend/src/lib/paths.ts toRelative 1:1.
    private static string? ToRelative(string raw, string rootPath)
    {
        var p = raw.Replace('\\', '/');
        // Не абсолютный Windows (C:/…) и не unix (/…) — уже относительный
        if (!(p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':' && p.Length >= 3 && p[2] == '/')
            && !p.StartsWith('/'))
        {
            var rel = p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p;
            if (rel.StartsWith("../", StringComparison.Ordinal) || rel.Contains("/../", StringComparison.Ordinal))
                return null;
            return rel;
        }
        var root = rootPath.Replace('\\', '/').TrimEnd('/');
        if (root.Length == 0) return null;
        var lp = p.ToLowerInvariant();
        var lr = root.ToLowerInvariant();
        if (lp == lr) return null;
        if (lp.StartsWith(lr + "/", StringComparison.Ordinal)) return p[(root.Length + 1)..];
        return null;
    }
}
