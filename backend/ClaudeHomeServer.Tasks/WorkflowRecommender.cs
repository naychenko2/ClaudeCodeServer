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
// id ведёт в пустоту). Белый список 2: skipped — только id этапов ВЫБРАННОГО шаблона с
// Presence=optional (ключ этапа = s.Id ?? s.Role, R-B4a Major 1: у линейных шаблонов id этапа
// равен роли, у графовых — свой slug; Skipped читается дальше именно по id — WorkflowGraph.States):
// этап «on» снять нельзя, а этапа «off» в маршруте и нет. Всё остальное
// отбрасывается; мусор/сбой модели — предложения нет, без исключения: рекомендация улучшает
// создание фичи, её отсутствие — обычное рабочее состояние (маршрут выбирает человек).
//
// M2 (правило Андрея): предложение несёт «нужен ли человек» (NeedsHuman) и причину сомнения
// (DoubtReason, белый список 3 — WorkflowDoubtReasons). Разбор fail-closed: false — только
// явный ответ модели без допустимой причины; мусорный или неполный ответ = «нужен человек».
//
// Каталог в промпте — id/роль/presence/«после» каждого этапа после WorkflowGraph.Normalize
// (G5): модель видит граф, а не список ролей, и отвечает id этапа. Белые списки проверяются по
// каталогу, перечитанному ПОСЛЕ ответа: правка или удаление шаблона, пока модель думала,
// снимают и его id, и весь чужой шаблон, а не оставляют в предложении несуществующий этап.
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

        // Каталог перечитываем ПОСЛЕ ответа: шаблон могли править или удалить, пока модель думала.
        // Белый список тогда строится по теперешним id этапов — id, которого в шаблоне уже нет,
        // в предложение не попадает (предложение, ссылающееся на снятый этап, подтверждается
        // отказом на ручке B4b, а не тихим пропуском).
        var proposal = ParseAndCut(raw, templates.List(epic.ProjectId));
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

    // M3 (правило Андрея: очевидный выбор модель подтверждает сама): авто-подтверждение предложения.
    // «Очевидно» = человек не нужен, причины сомнения нет и ни один этап не снят. Шаблон берётся из
    // каталога заново — его могли удалить, пока модель думала (тогда предложение остаётся человеку).
    // Запись — тем же единственным писателем ConfirmWorkflow, с проверкой «маршрута нет» под его замком:
    // маршрут, подтверждённый человеком, не затирается никогда. Возвращает эпик либо null.
    public TaskItem? TryAutoConfirm(string epicId, WorkflowProposal proposal)
    {
        if (proposal.NeedsHuman || proposal.DoubtReason is not null || proposal.Skipped.Count != 0) return null;
        var epic = tasks.GetById(epicId);
        if (epic is null) return null;

        var scope = proposal.Scope == "project" ? WorkflowTemplateScope.Project : WorkflowTemplateScope.Global;
        var entry = templates.List(epic.ProjectId)
            .FirstOrDefault(e => e.Template.Id == proposal.TemplateId && e.Scope == scope);
        if (entry is null) return null;

        var workflow = new FeatureWorkflow
        {
            TemplateId = entry.Template.Id,
            Scope = proposal.Scope,
            Stages = WorkflowGraph.Resolve(entry.Template, []),
            Skipped = [],
            ConfirmedVersion = entry.Template.Version,
            ConfirmedAt = DateTime.UtcNow,
            ConfirmedByUser = false,
            AutoConfirmed = true,
        };
        var written = tasks.ConfirmWorkflow(epicId, workflow, onlyIfUnconfirmed: true);
        if (written is not null)
            log?.LogInformation("feature-workflow-recommend {Epic}: маршрут подтверждён автоматически, шаблон {Template}",
                epicId, entry.Template.Id);
        return written;
    }

    private string BuildPrompt(TaskItem epic, IReadOnlyList<WorkflowTemplateEntry> catalog)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Подбери шаблон процесса (маршрут этапов) для фичи ниже по её названию и описанию.");
        sb.AppendLine("Ответь ТОЛЬКО JSON-объектом без пояснений и без markdown-обёртки:");
        sb.AppendLine("{\"templateId\":\"<id шаблона>\",\"skipped\":[\"<id этапа, который снимаем>\"],\"rationale\":\"одно предложение почему\",");
        sb.AppendLine("\"needsHuman\":true|false,\"doubtReason\":\"<одно из no-fit|ambiguous|custom-route|low-confidence>\"}");
        sb.AppendLine("templateId — строго один из id списка ниже, не выдумывай его.");
        sb.AppendLine("skipped — только id этапов, помеченные presence=optional у выбранного шаблона (id — первое поле в");
        sb.AppendLine("скобках этапов), если по описанию фичи они не нужны (маленькая правка — снять design и qa);");
        sb.AppendLine("если снимать нечего — пустой массив.");
        sb.AppendLine("этапы с presence=on снять нельзя.");
        sb.AppendLine("«после=[…]» — id предшественников этапа: несколько этапов с одним «после» идут параллельно,");
        sb.AppendLine("снять такой этап — значит убрать одну из веток, остальные останутся связны.");
        sb.AppendLine("needsHuman=false — только если ровно один шаблон из списка явно подходит по описанию фичи");
        sb.AppendLine("и снимать этапы не нужно; иначе needsHuman=true и doubtReason — причина сомнения,");
        sb.AppendLine("ровно одна из: no-fit — ни один шаблон не подходит; ambiguous — несколько сравнимы, выбор");
        sb.AppendLine("неочевиден; custom-route — маршрут требует правок за пределами снятия optional-этапов;");
        sb.AppendLine("low-confidence — однозначного решения по описанию нет.");
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
        sb.AppendLine("Шаблоны (id | название | описание | этапы id(роль, presence, после=[id предшественников]) в порядке маршрута):");
        foreach (var entry in catalog)
        {
            // After берётся из Normalize: у линейного шаблона это «предыдущий в списке», у графового —
            // явные рёбра. Модель видит ровно тот граф, который построит подтверждение маршрута (B4b).
            var stages = string.Join(", ", WorkflowGraph.Normalize(entry.Template.Stages)
                .Select(n => $"{n.Id}({n.Role}, {n.Presence}, после=[{string.Join(",", n.After)}])"));
            sb.AppendLine($"- {entry.Template.Id} | {entry.Template.Name} | {entry.Template.Description ?? ""} | {stages}");
        }
        return sb.ToString();
    }

    // Ключ этапа в промпте и в Skipped — его id (WorkflowStage.Id, разрез 2 §1.1); у линейных
    // шаблонов Id=null и ключ равен роли — те же токены, что ждёт ConfirmWorkflow/WorkflowGraph.
    private static string StageKey(WorkflowStage s) => string.IsNullOrWhiteSpace(s.Id) ? s.Role : s.Id!;

    // Разбор ответа и резание по белым спискам. Любой сбой — null (см. шапку класса).
    internal static WorkflowProposal? ParseAndCut(string raw, IReadOnlyList<WorkflowTemplateEntry> catalog)
    {
        var json = TaskAiService.ExtractJsonObject(raw);
        if (json is null) return null;

        string templateId;
        List<string> skipped;
        string? rationale;
        bool? saysNoHuman; // null — поле отсутствует или не bool (M2, см. правило ниже)
        string? doubtReason;
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
            // M2: допустимы ровно JSON-true/false; строка, число, null или отсутствие поля —
            // то же самое, что «не доверено» (fail-closed ниже).
            saysNoHuman = root.TryGetProperty("needsHuman", out var nh)
                && nh.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? nh.ValueKind == JsonValueKind.False : null;
            // Белый список 3: причина сомнения — только из WorkflowDoubtReasons.All;
            // вне списка — null и на NeedsHuman не влияет.
            var rawDoubt = root.TryGetProperty("doubtReason", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()?.Trim() : null;
            doubtReason = rawDoubt is not null && WorkflowDoubtReasons.All.Contains(rawDoubt) ? rawDoubt : null;
        }
        catch (JsonException) { return null; }

        // Белый список 1: шаблон из каталога вызова (тот же порядок id, что отдали модели)
        var entry = catalog.FirstOrDefault(e => e.Template.Id == templateId);
        if (entry is null) return null;

        // Белый список 2: снять можно только этап с Presence=optional выбранного шаблона,
        // ключ этапа — его id (у линейных шаблонов id = роль, у графовых — свой slug)
        var optional = entry.Template.Stages
            .Where(s => s.Presence == WorkflowStagePresence.Optional)
            .Select(StageKey)
            .ToHashSet(StringComparer.Ordinal);
        var kept = skipped.Where(optional.Contains).Distinct(StringComparer.Ordinal).ToList();

        // M2, fail-closed: NeedsHuman=false только если модель явно ответила false И причины
        // сомнения нет; поле отсутствует, не bool или пришла допустимая причина — человек нужен.
        var needsHuman = saysNoHuman != true || doubtReason is not null;

        return new WorkflowProposal
        {
            TemplateId = entry.Template.Id,
            // Область — та, где живёт шаблон: проектный отличается от глобального id проекта
            Scope = entry.Scope == WorkflowTemplateScope.Project ? "project" : "global",
            Skipped = kept,
            Rationale = string.IsNullOrWhiteSpace(rationale) ? null : rationale,
            NeedsHuman = needsHuman,
            DoubtReason = doubtReason,
        };
    }
}
