namespace ClaudeHomeServer.Models;

// Статус рабочего дерева: ветка, upstream, ahead/behind и списки изменений
// по группам (staged/unstaged/untracked). Парсится из `git status --porcelain=v2 --branch -z`.
public record GitStatusDto(
    bool IsRepo,
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    bool Detached,
    IReadOnlyList<GitFileChange> Staged,
    IReadOnlyList<GitFileChange> Unstaged,
    IReadOnlyList<GitFileChange> Untracked);

// Одно изменение файла. Status — односимвольный код git (M/A/D/R/C/?);
// OldPath заполняется только для переименований (R).
public record GitFileChange(string Path, string Status, string? OldPath = null);

// Ветка репозитория. Current — текущая (HEAD); Upstream — отслеживаемая ветка, если есть.
public record GitBranchInfo(string Name, bool Current, string? Upstream);

// Запись истории коммитов для UI (короткий sha + метаданные автора).
public record GitLogEntry(
    string Sha,
    string ShortSha,
    string Author,
    string Email,
    DateTimeOffset Date,
    string Subject);

// Запись stash: index — позиция в stash@{N} на момент листинга.
public record GitStashEntry(int Index, string Message, DateTimeOffset Date);

// Строка blame: кто и в каком коммите последним менял строку файла.
public record GitBlameLine(
    int Line,
    string Sha,
    string ShortSha,
    string Author,
    DateTimeOffset Date,
    string Content);

// Детали коммита для просмотра в контентной области: метаданные + список файлов.
public record GitCommitDetail(
    string Sha,
    string ShortSha,
    string Author,
    string Email,
    DateTimeOffset Date,
    string Subject,
    string Body,
    IReadOnlyList<GitFileChange> Files);
