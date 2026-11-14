namespace ClaudeHomeServer.Services.Composition;

// Шов «коммиты задачи в git-дереве»: экран этапа (D1a) показывает артефакты шага — коммиты с
// трейлером «CCS-Task: <id>» — и дифф одного из них. Реализация в Tasks читает git на хосте;
// тесты подставляют заглушку. Отдельный интерфейс рядом с IWorktreeHeadReader, а не его
// расширение: тому достаточно трёх членов состояния дерева, а этот ходит по истории.
public interface IWorktreeCommitReader
{
    // Коммиты ветки дерева (от HEAD), в сообщении которых есть строка-трейлер «CCS-Task: <taskId>»
    // (точное совпадение id, не префикс). Порядок — от старого к новому, не больше limit самых
    // новых. null — прочитать не удалось (нет git, не репозиторий, таймаут)
    IReadOnlyList<WorktreeCommit>? CommitsWithTaskTrailer(string worktreePath, string taskId, int limit);

    // Дифф ОДНОГО коммита по ПОЛНОМУ sha (вызывающий берёт его из списка выше, а не из запроса).
    // Текст режется по maxChars: Truncated = true — дифф длиннее потолка. null — не удалось
    WorktreeDiff? CommitDiff(string worktreePath, string fullSha, int maxChars);

    // Дифф ОДНОГО файла между двумя состояниями дерева. Ссылки — полный sha коммита из списка этапа,
    // fromRef может быть «sha^» (состояние до коммита). Режется по maxChars, как CommitDiff. null — не удалось
    WorktreeDiff? FileDiff(string worktreePath, string fromRef, string toRef, string file, int maxChars);
}

// Files — пути изменённых коммитом файлов от корня дерева через «/»
public sealed record WorktreeCommit(string Sha, string Subject, DateTime? At, IReadOnlyList<string> Files);

public sealed record WorktreeDiff(string Text, bool Truncated);
