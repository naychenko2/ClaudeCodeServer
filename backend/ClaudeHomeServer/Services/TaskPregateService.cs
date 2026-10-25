using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Tasks;

namespace ClaudeHomeServer.Services;

public enum TaskPregateStatus
{
    // Прогон состоялся (зелёный или красный), результат записан в задачу
    Recorded,
    NotFound,
    // Подсистема test-runs выключена — шва нет, прогонять нечем
    Disabled,
    // Не шаг/доработка или нет собственного дерева: гейт задачу не касается
    NotApplicable,
    // Прогон не состоялся (дерево занято, потолок, «Стоп», нет git): результат не записан
    NotRun,
}

public sealed record TaskPregateOutcome(TaskPregateStatus Status, string Text, PregateRecord? Record = null);

// Запуск предгейта шага (NC-P1b): общий для ручки POST /api/tasks/{id}/pregate и инструмента pregate.
// Шов ITaskPregate опциональный (вертикаль TestRuns отключаемая), сам прогон ничего не знает о задачах:
// здесь вердикт превращается в запись трекера, которую видит ревьюер; закрытие шага запись не блокирует.
public sealed class TaskPregateService(TaskManager tasks, ProjectManager projects, IWorktreeHeadReader heads,
    ITaskPregate? pregate = null)
{
    public async Task<TaskPregateOutcome> RunAsync(string ownerId, string taskId, string sessionId, CancellationToken ct)
    {
        var task = tasks.GetById(taskId);
        if (task is null || task.OwnerId != ownerId)
            return new(TaskPregateStatus.NotFound, "Задача не найдена.");
        if (pregate is null)
            return new(TaskPregateStatus.Disabled, "Предгейт выключен на этом сервере (подсистема test-runs): предгейт не запускается.");
        if (!PregateRules.Applies(task.Role, task.WorktreePath))
            return new(TaskPregateStatus.NotApplicable, "Предгейт нужен только шагу или доработке с собственным деревом (role=step|rework и worktreePath).");
        var project = task.ProjectId is null ? null : projects.GetById(task.ProjectId);
        if (project is null)
            return new(TaskPregateStatus.NotRun, "Проект задачи не найден — предгейт не запущен.");
        var sha = heads.Head(task.WorktreePath!);
        if (sha is null)
            return new(TaskPregateStatus.NotRun, "Не удалось определить текущий коммит дерева шага — предгейт не запущен.");

        // Проверки идут по рабочему дереву, а вердикт привязывается к HEAD: грязное дерево дало бы зелёный
        // коду, которого в коммите нет. Чистота читается ДО прогона, HEAD сверяется ПОСЛЕ: дерево занято
        // блокировкой PregateService на время прогона, а правка файлов между проверками без коммита
        // сдвинула бы только чистоту, не HEAD — для вердикта по коммиту это не меняет привязки
        var clean = heads.IsClean(task.WorktreePath!);
        if (clean is null)
            return new(TaskPregateStatus.NotRun, "Не удалось прочитать состояние дерева шага (git status) — предгейт не запущен.");
        if (!clean.Value)
            return new(TaskPregateStatus.NotRun, "В дереве шага есть незакоммиченные или неотслеживаемые изменения: "
                + "результат привязывается к коммиту, а проверки идут по файлам. Закоммить правки и прогони предгейт снова.");

        var verdict = await pregate.RunAsync(new PregateRequest(project, task.WorktreePath!, sessionId,
            heads.ChangedFiles(task.WorktreePath!), PregateRules.UiRoutes(task.Description)), ct);
        if (verdict.Refusal is { } refusal)
            return new(TaskPregateStatus.NotRun, "Предгейт не состоялся: " + refusal);
        if (!string.Equals(heads.Head(task.WorktreePath!), sha, StringComparison.OrdinalIgnoreCase))
            return new(TaskPregateStatus.NotRun, "Коммит дерева сменился во время прогона — результат не записан, прогони предгейт снова.");

        if (verdict.Unmeasured is { } unmeasured)
            return new(TaskPregateStatus.NotRun, "Предгейт не дал вердикта, результат не записан — " + unmeasured + ".");

        var red = verdict.Checks.Where(c => c.Status == PregateStatus.Red).ToList();
        var summary = string.Join("\n", red.Select(c => $"✗ {c.Name}: {c.Detail}"));
        var redKey = red.Count == 0 ? null : string.Join("|", red.Select(c => c.Name).Order(StringComparer.Ordinal));
        var nothing = verdict.NothingApplicable || verdict.Checks.Count == 0;
        if (nothing) summary = "NotRun: изменения не касаются ни одной проверки";
        var record = tasks.RecordPregate(task.Id, sha, verdict.IsGreen && !nothing, summary, redKey, nothing);
        return new(TaskPregateStatus.Recorded, Format(verdict, sha, record, nothing), record);
    }

    private static string Format(PregateVerdict verdict, string sha, PregateRecord? record, bool nothing)
    {
        var text = new StringBuilder();
        text.Append(nothing ? "Предгейт NotRun (проверять нечего)"
                : verdict.IsGreen ? "Предгейт ЗЕЛЁНЫЙ" : "Предгейт КРАСНЫЙ").Append(" по коммиту ")
            .Append(sha.Length > 8 ? sha[..8] : sha).Append('\n');
        foreach (var c in verdict.Checks)
        {
            var mark = c.Status switch { PregateStatus.Green => "✓", PregateStatus.Red => "✗", _ => "–" };
            text.Append(mark).Append(' ').Append(c.Name);
            if (c.Detail.Length > 0) text.Append(": ").Append(c.Detail);
            text.Append('\n');
        }
        if (!verdict.IsGreen)
            text.Append(record is { RedStreak: >= PregateRules.MaxRedStreak }
                ? "Второй красный подряд по той же причине: доложи постановщику (chats_report_up, blocker: true)."
                : "Исправь причину, закоммить и прогони предгейт снова.");
        return text.ToString().TrimEnd();
    }
}
