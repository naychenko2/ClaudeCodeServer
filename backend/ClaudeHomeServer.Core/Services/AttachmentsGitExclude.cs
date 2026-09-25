using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services;

// Примитив спины: гарантировать, что вложения чата (FileService.AttachmentsDir)
// не светятся в git-статусе проекта и не уезжают в историю по `git add -A`.
// Вынесен из GitService (задача `4d044b22`) — самодостаточная статика без
// состояния, троица приватных помощников больше нигде в GitService не
// используется. Прецедент — TranscriptRoots/ExecutableResolver: реестр
// «вертикаль → спина» живёт в корне Services, а вертикаль (Git) его не знает.
//
// Размещение правила — $GIT_COMMON_DIR/info/exclude, а не .gitignore:
//   - файл репозитория пользователя не трогаем — нет чужой правки в статусе,
//     в его коммитах и конфликтов при merge/rebase;
//   - common dir один на главное дерево и все worktree, а .gitignore главного
//     дерева на ветку worktree не действует — вложения же кладутся именно в
//     рабочую папку сессии (worktree);
//   - локальность правила не мешает: папку вложений создаёт только наш сервер,
//     в чужом клоне репозитория её не бывает.
// Best effort: не-репозиторий (чат вне проекта) и нерезолвимый git-dir —
// просто выходим.
public static class AttachmentsGitExclude
{
    public static void Ensure(string root)
    {
        var commonDir = ResolveGitCommonDir(root);
        if (commonDir is null) return;

        var excludeFile = Path.Combine(commonDir, "info", "exclude");
        var exclude = File.Exists(excludeFile) ? File.ReadAllText(excludeFile) : "";
        var gitignoreFile = Path.Combine(root, ".gitignore");
        var gitignore = File.Exists(gitignoreFile) ? File.ReadAllText(gitignoreFile) : "";
        if (HasAttachmentsRule(exclude) || HasAttachmentsRule(gitignore)) return;

        Directory.CreateDirectory(Path.Combine(commonDir, "info"));
        File.AppendAllText(excludeFile, RuleAppendix(exclude));
    }

    // Та же гарантия через шов файлов проекта — у локального проекта (ADR-016) файлы на
    // устройстве, и агент пишет в них только через IProjectFiles со сверкой дескриптора.
    // Только главное дерево (.git — папка внутри проекта): git-dir linked worktree лежит
    // вне корня проекта, туда шов не пустит, и правило просто не пишется.
    public static async Task EnsureAsync(IProjectFiles files, Project project, CancellationToken ct = default)
    {
        // .git листинг корня не показывает (служебный каталог) — спрашиваем его самого:
        // папка — главное дерево, файл или нет — не наш случай
        try { await files.ListAsync(project, ".git", showHidden: true, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        const string excludeRel = ".git/info/exclude";
        var exclude = await ReadOrEmptyAsync(files, project, excludeRel, ct);
        var gitignore = await ReadOrEmptyAsync(files, project, ".gitignore", ct);
        if (HasAttachmentsRule(exclude) || HasAttachmentsRule(gitignore)) return;

        await files.CreateDirectoryAsync(project, ".git/info", ct);
        await files.WriteFileAsync(project, excludeRel, exclude + RuleAppendix(exclude), ct);
    }

    private static async Task<string> ReadOrEmptyAsync(IProjectFiles files, Project project, string rel, CancellationToken ct)
    {
        try { return await files.ReadFileAsync(project, rel, ct); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return ""; }
    }

    private static string RuleAppendix(string exclude)
    {
        var lead = exclude.Length == 0 || exclude.EndsWith('\n') ? "" : "\n";
        return $"{lead}# Вложения чата (TreeExcludes.AttachmentsDir) — файлы сообщений, не история проекта\n" +
               $"{TreeExcludes.AttachmentsDir}/\n";
    }

    // Путь нового вложения чата относительно рабочей папки: {AttachmentsDir}/{guid}/{имя}.
    // Уникальность — подпапкой с GUID, чтобы сохранить оригинальное имя файла (на плашке в
    // чате показывается basename, и Claude видит его же). null — имени нет или оно не имя
    // файла: Path.GetFileName срезает path-сегменты (../evil). Одно правило на сервер и агента.
    public static string? AttachmentPath(string? fileName)
    {
        var safeName = Path.GetFileName(fileName ?? "");
        if (string.IsNullOrEmpty(safeName) || safeName is "." or "..") return null;
        return $"{TreeExcludes.AttachmentsDir}/{Guid.NewGuid():N}/{safeName}";
    }

    // Идемпотентность: правило уже записано (в любом из вариантов написания) — не дублируем
    private static bool HasAttachmentsRule(string text) =>
        text.Split('\n').Any(l => l.Trim().Trim('/') == TreeExcludes.AttachmentsDir);

    // Общий git-dir рабочего дерева. Главное дерево: .git — папка. Linked worktree: .git — файл
    // «gitdir: <.git/worktrees/{id}>», а рядом с ним файл commondir с путём к общему .git.
    // Путь в .git-файле — той среды, где создавался worktree, поэтому у container-пользователей
    // он может не резолвиться на хосте: тогда возвращаем null и правило не пишем.
    private static string? ResolveGitCommonDir(string root)
    {
        var dotGit = Path.Combine(root, ".git");
        if (Directory.Exists(dotGit)) return dotGit;
        if (!File.Exists(dotGit)) return null;

        var line = File.ReadLines(dotGit).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
        var gitDir = line?["gitdir:".Length..].Trim();
        if (string.IsNullOrEmpty(gitDir)) return null;
        gitDir = Path.GetFullPath(gitDir, root);
        if (!Directory.Exists(gitDir)) return null;

        var commonFile = Path.Combine(gitDir, "commondir");
        if (!File.Exists(commonFile)) return gitDir;
        var common = File.ReadAllText(commonFile).Trim();
        if (common.Length == 0) return gitDir;
        var resolved = Path.GetFullPath(common, gitDir);
        return Directory.Exists(resolved) ? resolved : null;
    }
}