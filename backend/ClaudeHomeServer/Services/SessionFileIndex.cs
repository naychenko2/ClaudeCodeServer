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

    // Относительные пути (прямые слэши, lowercase), изменённые чатом, — по его истории.
    //
    // [общий с фронтом] StoredFileChangedMessage / StoredToolUseMessage из WriteTools —
    // то же множество источников, что computeChangedPaths (frontend/src/hooks/useSessionArtifacts.ts).
    // [намеренное расхождение] External=true (правка вне заявленного хода — Bash/скрипты
    // модели либо человек в IDE во время хода) здесь ИСКЛЮЧАЕТСЯ, фронт её включает: индекс
    // отвечает на вопрос «что менял именно этот ЧАТ своим ходом», внешняя правка к чату не привязана.
    // [намеренное расхождение] пути хода, ушедшего в чужое git worktree (TurnWorktree != null),
    // тоже исключаются — они относятся к другому дереву, а не к rootPath проекта, и
    // отношения к «этот файл в ЭТОМ проекте менял чат X» не имеют.
    //
    // Зеркало значимого подмножества кейсов во фронтовом тесте —
    // frontend/src/lib/__tests__/git.changedBy.test.ts (там же обратная ссылка сюда).
    public static HashSet<string> Extract(IReadOnlyList<StoredMessage> messages, string rootPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                case StoredFileChangedMessage { External: false } fc when !skippingWorktreeTurn:
                    result.Add(Normalize(fc.Path));
                    break;
                case StoredToolUseMessage tu when !skippingWorktreeTurn && WriteTools.Contains(tu.Name):
                    if (ExtractToolPath(tu.Input) is { } raw && ToRelative(raw, rootPath) is { } rel)
                        result.Add(Normalize(rel));
                    break;
            }
        }
        return result;
    }

    // Прямые слэши, срез ведущего "./" (как фронтовый computeChangedPaths — см.
    // path.replace(/^\.\//, '') в useSessionArtifacts.ts) и lowercase
    private static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.ToLowerInvariant();
    }

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
