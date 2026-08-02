using System.Text;
using ClaudeHomeServer.Models;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services;

// Рендер полного плана «Командной реализации» в markdown-файл (решение владельца 2026-08-02,
// docs/architecture/team-implement-mode.md, раздел «Замысел в карточке и полный план файлом»).
// Файл пишет СЕРВЕР из структуры плана, а не модель: координатору запрещено писать файлы
// (CoordinatorWriteGuard, правило «любая работа — через задачу»), а рендер из структуры
// гарантирует, что файл соответствует тому, что человек утвердит, а не тому, что модель решила
// написать. Версия плана = отдельный файл — перепланирование (Э8) не перезаписывает предыдущий.
public static class TeamPlanFileRenderer
{
    // Папка планов команды относительно корня проекта (или worktree чата)
    public const string PlansDir = "docs/plans/team";

    // Слаг папки чата: нормализованное имя + короткий суффикс id сессии.
    // Суффикс обязателен по двум причинам: (1) имя чата приходит от человека и после
    // нормализации может совпасть у двух разных чатов — суффикс разводит коллизию;
    // (2) чат можно переименовать между версиями плана, а все версии одной итерации обязаны
    // лежать в одной папке («версия = отдельный файл, РЯДОМ») — суффикс держит папку стабильной
    // независимо от переименования. Сам слаг — только буквы/цифры/дефис, тот же семейный риск,
    // что у resumeSessionId в TranscriptMigrator.IsSafeSessionId: имя человека в путь напрямую
    // не годится (traversal, разделители пути).
    internal static string ChatSlug(string? chatName, string sessionId)
    {
        var suffix = sessionId.Length > 8 ? sessionId[..8] : sessionId;
        var sb = new StringBuilder();
        var prevDash = false;
        foreach (var ch in (chatName ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); prevDash = false; }
            else if (!prevDash && sb.Length > 0) { sb.Append('-'); prevDash = true; }
        }
        var slug = sb.ToString().TrimEnd('-');
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        return slug.Length == 0 ? suffix : $"{slug}-{suffix}";
    }

    // Относительный путь файла версии плана. SafeJoin в TryWrite — вторая линия защиты:
    // ChatSlug уже безопасен по построению, но проверка остаётся на случай будущих правок.
    public static string RelativePath(string? chatName, string sessionId, int version) =>
        $"{PlansDir}/{ChatSlug(chatName, sessionId)}/plan-v{version}.md";

    // Markdown файла: замысел, под-задачи по волнам (Goal/DoneCriteria/файлы/исполнитель
    // с обоснованием), допущения, а с v2 — «Что изменилось».
    public static string Render(TeamImplementPlan plan, Func<string?, string> executorLabel)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# План командной реализации v{plan.Version}");
        sb.AppendLine();
        sb.AppendLine($"**Вводная:** {plan.Request.Trim()}");
        if (!string.IsNullOrWhiteSpace(plan.Summary))
            sb.AppendLine($"**Подход:** {plan.Summary.Trim()}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(plan.Intent))
        {
            sb.AppendLine("## Замысел");
            sb.AppendLine(plan.Intent.Trim());
            sb.AppendLine();
        }

        if (plan.Changes.Count > 0)
        {
            sb.AppendLine("## Что изменилось");
            foreach (var c in plan.Changes) sb.AppendLine($"- {c}");
            sb.AppendLine();
        }

        sb.AppendLine("## Под-задачи");
        foreach (var wave in plan.Subtasks.Select(s => s.Wave).Distinct().OrderBy(w => w))
        {
            sb.AppendLine();
            sb.AppendLine($"### Волна {wave}");
            foreach (var s in plan.Subtasks.Where(s => s.Wave == wave))
            {
                sb.AppendLine();
                sb.AppendLine($"#### {s.Title}");
                if (!string.IsNullOrWhiteSpace(s.Goal)) sb.AppendLine($"- **Что сделать:** {s.Goal.Trim()}");
                sb.AppendLine($"- **Исполнитель:** {executorLabel(s.ExecutorPersonaId)} — {s.ExecutorRationale}");
                sb.AppendLine(s.Files.Count > 0
                    ? $"- **Файлы:** {string.Join(", ", s.Files.Select(f => $"`{f}`"))}"
                    : "- **Файлы:** не заданы планом");
                if (!string.IsNullOrWhiteSpace(s.DoneCriteria))
                    sb.AppendLine($"- **Критерий готовности:** {s.DoneCriteria.Trim()}");
            }
        }

        if (plan.Assumptions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Допущения");
            foreach (var a in plan.Assumptions) sb.AppendLine($"- {a}");
        }

        return sb.ToString();
    }

    // Записать файл версии плана. Никогда не бросает наружу: ошибка записи (нет прав, путь
    // занят) не должна ронять публикацию карточки — план публикуется, ссылки просто нет,
    // а в лог уходит предупреждение (см. краевые случаи продуктового плана).
    public static string? TryWrite(string rootPath, string? chatName, string sessionId,
        TeamImplementPlan plan, Func<string?, string> executorLabel, ILogger? log = null)
    {
        var rel = RelativePath(chatName, sessionId, plan.Version);
        try
        {
            var full = FileService.SafeJoinPublic(rootPath, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, Render(plan, executorLabel));
            return rel;
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Запись файла плана командной реализации (чат {SessionId}, v{Version}) не удалась",
                sessionId, plan.Version);
            return null;
        }
    }
}
