// Типы домена «Шаблоны процесса фичи» (фича feature-workflows, разрез
// docs/research/ai-fabrika-workflow-cut-2026-11.md §1; граф маршрута — разрез 2
// docs/research/ai-fabrika-workflow-2-cut-2026-11.md §1.1–1.2). Зеркалят серверные
// WorkflowTemplate/WorkflowStage/RouteStage (backend/ClaudeHomeServer.Core/Models/WorkflowTemplate.cs;
// контракт ручки — WorkflowTemplatesController.cs, B1c): поля — camelCase, как их отдаёт сервер.
// Правила и клампы — не здесь, а в workflowTemplateModel.ts (то же деление, что на бэке:
// правила в WorkflowRules, не в моделях).
// role/presence/stallPolicy — строки, а не union полей: тот же выбор, что на бэке
// («строки, а не enum» — урок TaskRoles/McpCatalogRef): незнакомая строка при чтении
// просто игнорируется, а не ломает чтение. Белые списки — STAGE_ROLES/… в модели,
// их проверяет stageErrors.

import type { ModelTierValue, PersonaSpecialty } from '../../types';

// Область шаблона: без projectId — глобальная, с projectId — проектная (ScopeOf в контроллере).
export type WorkflowTemplateScope = 'global' | 'project';

// Значения белых списков (серверные WorkflowRules.StageRoles / WorkflowStagePresence.All /
// WorkflowStallPolicies.All). Поля этапов типизированы строкой — см. комментарий в начале файла.
export type WorkflowStageRole = 'cut' | 'design' | 'plan' | 'step' | 'review' | 'qa' | 'final' | 'merge';
export type WorkflowStagePresence = 'on' | 'optional' | 'off';
export type WorkflowStallPolicy = 'nudge-alert' | 'alert' | 'stop-alert';

// Ссылка на версию шаблона: у проектного шаблона — от какого глобального он форкнут (BasedOn).
export interface WorkflowTemplateRef {
  templateId: string;
  version: number;
}

// Этап шаблона = роль задачи из закрытого набора TaskRoles (разрез §0.3: новых видов работ
// шаблон не вводит; rework берёт этап step). Порядок этапов — порядок маршрута (топологический:
// after ссылается только на стоящие раньше).
export interface WorkflowStage {
  // Id этапа (разрез 2 §1.1): slug [a-z0-9-]{1,32}, уникален в шаблоне; null → роль.
  // Ключ всех ссылок графа: после normalizeStages этапы опознаются по id, не по индексу и не по роли.
  id?: string | null;
  // Имя этапа (G7, решение контракта a5d0c76e по возврату R-C3a): необязательное, 1..40 после
  // обрезки, пустая → null. Обязательно и различно в группе этапов с общей парой (Role, Lane) —
  // без него граф показал бы две неотличимые «Проверки» (qa-plan и qa). Проверяет сервер
  // (WorkflowRules.ValidateTitleGroups); здесь — показ и правка, необходимость подсказывает
  // titleRequired. В подтверждённом маршруте имя заморожено (серверный RouteStage.Title).
  title?: string | null;
  // Id этапов, стоящих раньше в списке; null → [предыдущий в списке], для первого — [].
  // Ссылка на себя или вперёд — ошибка сервера (разрез 2 §1.6), здесь она не проверяется.
  after?: string[] | null;
  // Только у role=step: 'back' | 'front' | 'other' (лексика FeatureProjector.NormalizeLane);
  // у прочих ролей и неизвестное значение — отказ сервера.
  lane?: string | null;
  // Значение из STAGE_ROLES; незнакомая строка возможная (младший/старший сервер) —
  // модель назовёт её в stageErrors, сервер откажет при сохранении.
  role: string;
  // 'on' | 'optional' | 'off' (разрез §1 «Пропуск»): пропуск выражен полем, а не
  // отсутствием этапа — состав маршрута остаётся видимым в шаблоне.
  presence: string;
  // Кого звать: подсказка оркестратору и предзаполнение personaId. Взаимоисключимо с
  // personaId (валидирует сервер; stageErrors подсказывает до отправки). 'none' — не задана.
  specialty?: PersonaSpecialty | null;
  personaId?: string | null;
  // Доп. текст этапа, ≤4000 символов, только ДОБАВЛЯЕТСЯ к правилам хода (И6), никогда не вытесняет.
  prompt?: string | null;
  // Ячейки матрицы моделей в той же лексике ADR-007: id модели | "preset:{id}".
  // "tier:*" в ячейке запрещён (ячейка уже адресована уровнем).
  tierStrong?: string | null;
  tierMedium?: string | null;
  tierWeak?: string | null;
  // Уровень этапа: null — не задан (дальше персона → специальность → слоты).
  defaultTier?: ModelTierValue | null;
  // Только СУЖЕНИЕ прав поверх персоны (И5), никогда не расширение.
  disallowedTools?: string[] | null;
  // Подмножество ключей MCP, уже выданных персоне; null — без сужения.
  mcpAllow?: string[] | null;
  // Порог молчания [5..240] минут; null — дефолт конфига (Tasks:ExecutorStaleMinutes).
  // Обойти таймаут нельзя: clampStall клампит, а не пропускает (И3).
  stallMinutes?: number | null;
  // null — политика не задана (дефолт берёт исполнение).
  stallPolicy?: string | null;
}

// Шаблон процесса фичи. Ключ — (scope, projectId, id); version — +1 при каждом сохранении,
// старые версии НЕ хранятся (разрез §4). Он не становится вторым рантаймом (разрез §0.2).
export interface WorkflowTemplate {
  id: string;
  name: string;
  // Описание читает рекомендатель маршрута (разрез §5).
  description?: string | null;
  version: number;
  // Встроенный пресет ("full-cycle" | "fast-track"): удалить нельзя, сбросить к коду можно
  // (сброс — только admin, ручка {id}/reset).
  builtInKey?: string | null;
  // У проектного шаблона: от какого глобального форкнут.
  basedOn?: WorkflowTemplateRef | null;
  // Порядок списка = порядок маршрута.
  stages: WorkflowStage[];
}

// Этап подтверждённого маршрута (разрез 2 §1.2, серверный RouteStage): замораживается при
// подтверждении и дальше не зависит от шаблона. after — уже после обхода снятых этапов
// (серверный WorkflowGraph.Resolve). Членство и порядок маршрута — это stages.map(s => s.id).
export interface RouteStage {
  id: string;
  role: string;
  lane?: string | null;
  after: string[];
}

// Предложение маршрута на эпике (серверный WorkflowProposal, разрез §1): пишет только
// рекомендатель (B4a/B4b). Скип-лист — только этапы Presence=optional; skipped — id этапов
// (разрез 2 §1.2; у линейных шаблонов значения те же, что были ролями: там id = роль).
export interface WorkflowProposal {
  templateId: string;
  scope: WorkflowTemplateScope;
  skipped: string[];
  rationale?: string | null;
  proposedAt?: string | null;
  model?: string | null;
}

// Тело POST /api/tasks/{id}/workflow/confirm (B4b): человек может прислать шаблон и пропуски,
// отличные от предложения. Пишет маршрут только эта ручка (И7). skipped — id этапов
// (разрез 2 §1.2), как в WorkflowProposal.
export interface ConfirmWorkflowBody {
  templateId: string;
  scope: WorkflowTemplateScope;
  skipped: string[];
}

// Строка списка GET /api/workflow-templates (WorkflowTemplateEntry на бэке): шаблон с областью.
export interface WorkflowTemplateEntry {
  scope: 'global' | 'project';
  projectId: string | null;
  template: WorkflowTemplate;
}
