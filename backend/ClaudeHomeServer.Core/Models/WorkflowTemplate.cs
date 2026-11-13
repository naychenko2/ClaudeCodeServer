using ClaudeHomeServer.Services; // ModelTier — примитив спины (по образцу Persona.cs)

namespace ClaudeHomeServer.Models;

// Модели шаблона процесса фичи (разрез docs/research/ai-fabrika-workflow-cut-2026-11.md §1 —
// поля дословно оттуда). Строки, а не enum (тот же урок, что у TaskRoles): откат кода ломает
// чтение enum'а, а незнакомые строки старый код при чтении просто игнорирует.
// Правила и клампы — не здесь: WorkflowRules (вертикаль Tasks).

// Присутствие этапа в маршруте (разрез §1 «Пропуск»): пропуск выражен полем, а не отсутствием
// этапа — состав маршрута остаётся видимым в шаблоне.
// on — этап в маршруте всегда; optional — по умолчанию в маршруте, человек или рекомендатель
// может снять; off — этапа в этом шаблоне нет (фаст-трек: design=off, qa=off).
public static class WorkflowStagePresence
{
    public const string On = "on";
    public const string Optional = "optional";
    public const string Off = "off";

    public static readonly IReadOnlyList<string> All = [On, Optional, Off];
}

// Политика простоя исполнителя этапа (разрез §1, §3 «ClassifyStall», инвариант И3):
// nudge-alert — как сейчас (толкач + уведомление); alert — без автохода;
// stop-alert — остановка исполнителя существующим путём «Стоп» плюс уведомление.
// Строки, а не enum (урок McpCatalogRef). Белый список — WorkflowRules.
public static class WorkflowStallPolicies
{
    public const string NudgeAlert = "nudge-alert";
    public const string Alert = "alert";
    public const string StopAlert = "stop-alert";

    public static readonly IReadOnlyList<string> All = [NudgeAlert, Alert, StopAlert];
}

// Ссылка на версию шаблона (BasedOn): у проектного шаблона — от какого глобального он форкнут.
public class WorkflowTemplateRef
{
    public string TemplateId { get; set; } = "";
    public int Version { get; set; }
}

// Этап шаблона = роль задачи из закрытого набора TaskRoles (разрез §0.3: новых видов работ
// шаблон не вводит; rework берёт этап step). Порядок этапов — порядок маршрута.
public class WorkflowStage
{
    // Ключ из TaskRoles: cut | design | plan | step | review | qa | final | merge.
    public string Role { get; set; } = "";
    // Id этапа (разрез 2 §1.1): slug [a-z0-9-]{1,32}, уникален в шаблоне; null → Role
    // (у линейных шаблонов id этапа равен роли). Роль может повторяться, id — нет.
    public string? Id { get; set; }
    // Имя этапа (G7, решение контракта a5d0c76e по возврату R-C3a): необязательное, 1..40
    // после обрезки по краям, пустая → null. Обязательно и различно в группе этапов с общей
    // парой (Role, Lane) — сторож один: WorkflowRules. В StageSnapshot не входит: показ идёт
    // через проекцию (FeatureStage.Title), исполнителю имя не нужно.
    public string? Title { get; set; }
    // Предшественники: id этапов, стоящих РАНЬШЕ в списке; null → [предыдущий в списке];
    // [] → корень. Порядок списка — топологический, поэтому цикл невозможен по построению.
    public List<string>? After { get; set; }
    // Линия работы, только у step: back | front | other (лексика FeatureProjector.NormalizeLane).
    public string? Lane { get; set; }
    public string Presence { get; set; } = WorkflowStagePresence.On;
    // Кого звать: подсказка оркестратору и предзаполнение personaId (разрез §3 TaskManager.Create).
    // Взаимоисключимо с PersonaId (валидирует WorkflowRules).
    public PersonaSpecialty? Specialty { get; set; }
    public string? PersonaId { get; set; }
    // Доп. текст этапа, только ДОБАВЛЯЕТСЯ к правилам хода (И6), никогда не вытесняет их.
    public string? Prompt { get; set; }
    // Ячейки матрицы моделей в той же лексике ADR-007: id модели | "preset:{id}".
    // "tier:*" в ячейке запрещён (ячейка уже адресована уровнем) — та же проверка,
    // что у ячеек специальности (SpecialtySettingsStore.ValidateModelCell).
    public string? TierStrong { get; set; }
    public string? TierMedium { get; set; }
    public string? TierWeak { get; set; }
    // Уровень этапа: null — не задан (дальше персона → специальность → слоты).
    public ModelTier? DefaultTier { get; set; }
    // Только СУЖЕНИЕ прав поверх персоны (И5), никогда не расширение.
    public List<string>? DisallowedTools { get; set; }
    // Подмножество ключей MCP, уже выданных персоне; null — без сужения.
    public List<string>? McpAllow { get; set; }
    // Порог молчания [5..240] минут; null — Tasks:ExecutorStaleMinutes (дефолт конфига).
    // Обойти таймаут нельзя: NormalizeStall клампит, а не пропускает (И3).
    public int? StallMinutes { get; set; }
    public string? StallPolicy { get; set; }
}

// Шаблон процесса фичи. Он НЕ становится вторым рантаймом (разрез §0.2): конвейер ведёт
// оркестратор карточками TaskRoles; шаблон читают ровно в трёх точках создания/исполнения.
public class WorkflowTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    // Описание читает рекомендатель маршрута (разрез §5).
    public string? Description { get; set; }
    // +1 при каждом сохранении; старые версии НЕ храним (разрез §4).
    public int Version { get; set; }
    // Встроенный пресет ("full-cycle" | "fast-track"): удалить нельзя, сбросить к коду можно.
    // Сами пресеты — шаг B1b.
    public string? BuiltInKey { get; set; }
    // У проектного шаблона: от какого глобального форкнут.
    public WorkflowTemplateRef? BasedOn { get; set; }
    // Порядок списка = порядок маршрута.
    public List<WorkflowStage> Stages { get; set; } = [];
}

// Файл стора data/workflow-templates.json на диске (стор — шаг B1b). Глобальные шаблоны
// пишет только админ, проектные — владелец проекта (И8). Попадает в бэкап по умолчанию.
public class WorkflowTemplateFile
{
    // Версия формата файла; задаёт её стор (B1b), здесь поле только для чтения/записи.
    public int Version { get; set; }
    public List<WorkflowTemplate> Global { get; set; } = [];
    public Dictionary<string, List<WorkflowTemplate>> Projects { get; set; } = new(StringComparer.Ordinal);
}

// Предложение маршрута на эпике (TaskItem.WorkflowProposal, разрез §1): пишет только
// TaskManager.SetWorkflowProposal (рекомендатель, B4a). Скип-лист — только этапы Presence=optional.
public class WorkflowProposal
{
    public string TemplateId { get; set; } = "";
    // "global" | "project"
    public string Scope { get; set; } = "global";
    // Id снятых этапов (у линейных шаблонов id = роль).
    public List<string> Skipped { get; set; } = [];
    public string? Rationale { get; set; }
    public DateTime ProposedAt { get; set; }
    public string? Model { get; set; }
    // M2 (правило Андрея: человека зовём только при сомнении): нужен ли человек для выбора
    // этого маршрута. Дефолт — true: поле отсутствует или разбор не доверен = человек нужен
    // (fail-closed, WorkflowRecommender.ParseAndCut). false — только явный ответ модели
    // без допустимой причины сомнения.
    public bool NeedsHuman { get; set; } = true;
    // Причина сомнения — только из WorkflowDoubtReasons.All; любое другое значение → null.
    public string? DoubtReason { get; set; }
}

// Белый список причин сомнения (M2): почему рекомендатель зовёт человека. Строки, а не enum
// (тот же урок, что у WorkflowStagePresence). Числовой уверенности нет — только флаг и причина.
public static class WorkflowDoubtReasons
{
    // Ни один шаблон каталога не подходит под описание фичи.
    public const string NoFit = "no-fit";
    // Несколько шаблонов сравнимы, выбор неочевиден.
    public const string Ambiguous = "ambiguous";
    // Маршрут требует правок за пределами снятия optional-этапов.
    public const string CustomRoute = "custom-route";
    // Выбор неочевиден — однозначного решения по описанию нет.
    public const string LowConfidence = "low-confidence";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        NoFit, Ambiguous, CustomRoute, LowConfidence,
    };
}

// Подтверждённый человеком маршрут эпика (TaskItem.Workflow, разрез §1, инвариант И7): единственный
// писатель — TaskManager.ConfirmWorkflow. Состав маршрута правкой шаблона не меняется.
public class FeatureWorkflow
{
    public string TemplateId { get; set; } = "";
    public string Scope { get; set; } = "global";
    // Граф маршрута после Resolve: замораживается при подтверждении и дальше от шаблона не зависит
    // (разрез 2 §1.2). Порядок списка — топологический порядок запуска.
    public List<RouteStage> Stages { get; set; } = [];
    // Id снятых этапов (у линейных шаблонов значения те же, что были ролями).
    public List<string> Skipped { get; set; } = [];
    public int ConfirmedVersion { get; set; }
    public DateTime ConfirmedAt { get; set; }
    public bool ConfirmedByUser { get; set; } = true;
    // M2/M3: true ставит только авто-путь подтверждения (M3), когда выбор маршрута был
    // очевиден. ConfirmedByUser не переделываем: подтверждение человеком и авто-путь —
    // разные поля. Миграции нет: старые записи без поля читаются как false.
    public bool AutoConfirmed { get; set; }
    // B03: версия шаблона, обновление до которой человек отклонил («Оставить v1»). Предупреждение
    // «шаблон изменился» не отдаётся, пока версия шаблона не вырастет выше этого значения.
    // Пишет только TaskManager.KeepWorkflowVersion; новое подтверждение поле сбрасывает.
    public int? DismissedUpdateVersion { get; set; }
}

// Этап подтверждённого маршрута (разрез 2 §1.2): After — уже после обхода снятых этапов.
public class RouteStage
{
    public string Id { get; set; } = "";
    public string Role { get; set; } = "";
    public string? Lane { get; set; }
    // Имя, замороженное из шаблона в момент подтверждения (G7, И7): копируется в
    // WorkflowGraph.Resolve рядом с Id/Role/Lane. Из шаблона на лету не читается — иначе
    // правка шаблона после подтверждения меняла бы подтверждённый маршрут.
    public string? Title { get; set; }
    public List<string> After { get; set; } = [];
}

// Слепок этапа на задаче фичи (TaskItem.Stage, разрез §1/§4, инварианты И3/И4): пишет только
// TaskManager.Create из ТЕКУЩЕЙ версии шаблона; исполнитель читает только его, шаблон — никогда.
// Если шаблон удалён после подтверждения, поля этапа пусты (остаются TemplateId/Role).
public class StageSnapshot
{
    public string TemplateId { get; set; } = "";
    public int TemplateVersion { get; set; }
    public string Role { get; set; } = "";
    // Этап маршрута, к которому привязана задача (разрез 2 §1.3); у слепков до графа — пусто.
    public string StageId { get; set; } = "";
    public string? Lane { get; set; }
    // Предшественники из МАРШРУТА эпика на момент создания (не из шаблона); только id.
    public List<string> After { get; set; } = [];
    public PersonaSpecialty? Specialty { get; set; }
    public string? PersonaId { get; set; }
    public string? Prompt { get; set; }
    public string? TierStrong { get; set; }
    public string? TierMedium { get; set; }
    public string? TierWeak { get; set; }
    public ModelTier? DefaultTier { get; set; }
    public List<string>? DisallowedTools { get; set; }
    public List<string>? McpAllow { get; set; }
    // Уже после WorkflowRules.NormalizeStall.
    public int? StallMinutes { get; set; }
    public string? StallPolicy { get; set; }
}
