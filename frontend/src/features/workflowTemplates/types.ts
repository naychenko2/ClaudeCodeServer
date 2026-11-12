// Типы домена «Шаблоны процесса фичи» (фича feature-workflows, разрез
// docs/research/ai-fabrika-workflow-cut-2026-11.md §1). Зеркалят серверные
// WorkflowTemplate/WorkflowStage (backend/ClaudeHomeServer.Core/Models/WorkflowTemplate.cs;
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
// шаблон не вводит; rework берёт этап step). Порядок этапов — порядок маршрута.
export interface WorkflowStage {
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

// Предложение маршрута на эпике (серверный WorkflowProposal, разрез §1): пишет только
// рекомендатель (B4a/B4b). Скип-лист — только этапы Presence=optional.
export interface WorkflowProposal {
  templateId: string;
  scope: WorkflowTemplateScope;
  skipped: string[];
  rationale?: string | null;
  proposedAt?: string | null;
  model?: string | null;
}

// Тело POST /api/tasks/{id}/workflow/confirm (B4b): человек может прислать шаблон и пропуски,
// отличные от предложения. Пишет маршрут только эта ручка (И7).
export interface ConfirmWorkflowBody {
  templateId: string;
  scope: WorkflowTemplateScope;
  skipped: string[];
}
