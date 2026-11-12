using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks;

// B4a (разрез docs/research/ai-fabrika-workflow-cut-2026-11.md §5): рекомендатель маршрута фичи.
// Вход — Title/Description эпика плюс каталог доступных шаблонов (глобальные и проекта) из
// WorkflowTemplateStore; вызов — дешёвый one-shot места feature-workflow-recommend. Паттерн
// тот же, что у TaskDedup и PlanMapService: «модель отвечает JSON, сервер режет по белым
// спискам», наружу — только отрезанное.
//
// Белый список 1: templateId обязан быть id из переданного каталога (урок дедупа: выдуманный
// id ведёт в пустоту). Белый список 2: skipped — только роли этапов ВЫБРАННОГО шаблона с
// Presence=optional: этап «on» снять нельзя, а этапа «off» в маршруте и нет. Всё остальное
// отбрасывается; мусор/сбой модели — предложения нет, без исключения: рекомендация улучшает
// создание фичи, её отсутствие — обычное рабочее состояние (маршрут выбирает человек).
//
// И7: пишет ТОЛЬКО epic.WorkflowProposal и только через TaskManager.SetWorkflowProposal;
// epic.Workflow не трогает никогда. К приходу ответа человек уже подтвердил маршрут —
// предложение не пишется: решение человека старше. Предложение — целый объект под замком
// маршрута, поэтому два параллельных подбора одного эпика оставляют в эпике предложение
// одного из вызовов целиком, а не смесь полей.
public sealed class WorkflowRecommender(
    TaskManager tasks, WorkflowTemplateStore templates, IConfiguration config,
    Llm.ICheapTextRunner cheap, ILogger<WorkflowRecommender>? log = null)
{
    // Длина Description в промпте (контракт места): описание длиннее этого вытесняет из окна
    // каталог шаблонов — его хвост Ollama режет молча, и модель начинает выдумывать id
    public const int MaxDescriptionChars = 8000;

    // Возвращает записанное предложение либо null: эпика нет/он не эпик/не принадлежит
    // владельцу, маршрут уже подтверждён, каталога нет, сбой или мусор ответа. Наружу
    // исключение модели не уходит никогда (кроме осознанной отмены вызывающим).
    public async Task<WorkflowProposal?> RecommendAsync(string epicId, string ownerId, CancellationToken ct)
    {
        var epic = tasks.GetById(epicId);
        if (epic is null || epic.Role != TaskRoles.Feature || epic.OwnerId != ownerId) return null;
        // Устаревшее: маршрут уже подтверждён человеком — предложения не нужно (И7)
        if (epic.Workflow is not null) return null;

        var catalog = templates.List(epic.ProjectId);
        if (catalog.Count == 0) return null;

        string raw;
        try
        {
            raw = await cheap.RunAsync(Llm.LocalActionCatalog.FeatureWorkflowRecommend,
                BuildPrompt(epic, catalog), config["Tasks:AiModel"] ?? "haiku", ownerId,
                jsonFormat: "json", ct);
        }
        // Отмена — не сбой модели, осознанный обрыв вызывающим: наверх, как в PlanMapService
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "feature-workflow-recommend {Epic}: вызов модели не удался — предложения нет", epicId);
            return null;
        }

        var proposal = ParseAndCut(raw, catalog);
        if (proposal is null)
        {
            log?.LogInformation("feature-workflow-recommend {Epic}: ответ модели не разобран или не прошёл белые списки", epicId);
            return null;
        }

        // Пока ждали ответ, человек мог подтвердить маршрут: перечитываем эпик и не пишем
        // поверх решения человека. Запись — целым объектом под замком маршрута (SetWorkflowProposal)
        var current = tasks.GetById(epicId);
        if (current is null || current.Workflow is not null) return null;

        proposal.ProposedAt = DateTime.UtcNow;
        if (tasks.SetWorkflowProposal(epicId, proposal) is null) return null;
        log?.LogInformation("feature-workflow-recommend {Epic}: предложен шаблон {Template} (снято: {Skipped})",
            epicId, proposal.TemplateId, proposal.Skipped.Count);
        return proposal;
    }

    private string BuildPrompt(TaskItem epic, IReadOnlyList<WorkflowTemplateEntry> catalog)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Подбери шаблон процесса (маршрут этапов) для фичи ниже по её названию и описанию.");
        sb.AppendLine("Ответь ТОЛЬКО JSON-объектом без пояснений и без markdown-обёртки:");
        sb.AppendLine("{\"templateId\":\"<id шаблона>\",\"skipped\":[\"<роль, которую снимаем>\"],\"rationale\":\"одно предложение почему\"}");
        sb.AppendLine("templateId — строго один из id списка ниже, не выдумывай его.");
        sb.AppendLine("skipped — только роли помеченные presence=optional у выбранного шаблона, если по описанию");
        sb.AppendLine("фичи они не нужны (маленькая правка — снять design и qa); если снимать нечего — пустой массив.");
        sb.AppendLine("roles с presence=on снять нельзя.");
        sb.AppendLine();
        sb.AppendLine($"Название фичи: «{epic.Title}»");
        var description = epic.Description ?? "";
        if (description.Length > MaxDescriptionChars) description = description[..MaxDescriptionChars] + "…";
        if (description.Trim().Length > 0)
        {
            sb.AppendLine("Описание:");
            sb.AppendLine(description);
        }
        sb.AppendLine();
        sb.AppendLine("Шаблоны (id | название | описание | этапы роль(presence) в порядке маршрута):");
        foreach (var entry in catalog)
        {
            var stages = string.Join(", ", entry.Template.Stages.Select(s => $"{s.Role}({s.Presence})"));
            sb.AppendLine($"- {entry.Template.Id} | {entry.Template.Name} | {entry.Template.Description ?? ""} | {stages}");
        }
        return sb.ToString();
    }

    // Разбор ответа и резание по белым спискам. Любой сбой — null (см. шапку класса).
    internal static WorkflowProposal? ParseAndCut(string raw, IReadOnlyList<WorkflowTemplateEntry> catalog)
    {
        var json = TaskAiService.ExtractJsonObject(raw);
        if (json is null) return null;

        string templateId;
        List<string> skipped;
        string? rationale;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            templateId = root.TryGetProperty("templateId", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()?.Trim() ?? "" : "";
            skipped = root.TryGetProperty("skipped", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()?.Trim() ?? "")
                    .Where(r => r.Length > 0)
                    .ToList()
                : [];
            rationale = root.TryGetProperty("rationale", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()?.Trim() : null;
        }
        catch (JsonException) { return null; }

        // Белый список 1: шаблон из каталога вызова (тот же порядок id, что отдали модели)
        var entry = catalog.FirstOrDefault(e => e.Template.Id == templateId);
        if (entry is null) return null;

        // Белый список 2: снять можно только этап с Presence=optional выбранного шаблона
        var optional = entry.Template.Stages
            .Where(s => s.Presence == WorkflowStagePresence.Optional)
            .Select(s => s.Role)
            .ToHashSet(StringComparer.Ordinal);
        var kept = skipped.Where(optional.Contains).Distinct(StringComparer.Ordinal).ToList();

        return new WorkflowProposal
        {
            TemplateId = entry.Template.Id,
            // Область — та, где живёт шаблон: проектный отличается от глобального id проекта
            Scope = entry.Scope == WorkflowTemplateScope.Project ? "project" : "global",
            Skipped = kept,
            Rationale = string.IsNullOrWhiteSpace(rationale) ? null : rationale,
        };
    }
}
