// Чистая логика экрана редактора шаблонов процесса (C1b): без DOM и React.
// Здесь всё, что в тесте можно проверить без отрисовки: гонки сохранения, разбор отказов
// сервера, форк глобального шаблона в проектный, разбор списков «через запятую».

import { normalizeStages } from './workflowGraphModel';
import { STAGE_ROLES, clampStall, isNonSkippable } from './workflowTemplateModel';
import type { WorkflowStage, WorkflowTemplate, WorkflowTemplateEntry } from './types';

// Список обёрток { scope, template } → области. Разбиение — по метке entry.scope, а не по id:
// без projectId глобальные же отдаются и как «свои» (админская вкладка); с projectId
// глобальные не попадают в «Проектные».
export function splitEntries(
  entries: WorkflowTemplateEntry[], projectMode: boolean,
): { globals: WorkflowTemplate[]; locals: WorkflowTemplate[] } {
  const globals = entries.filter(e => e.scope === 'global').map(e => e.template);
  return { globals, locals: projectMode ? entries.filter(e => e.scope === 'project').map(e => e.template) : globals };
}

export const CONFLICT_MESSAGE = 'Шаблон изменился на сервере — перечитайте его и повторите правку.';

// Список «через запятую» → без пустых и повторов (как parseDisallowed в SpecialtyEditView).
export function parseList(s: string): string[] {
  return Array.from(new Set(s.split(',').map(t => t.trim()).filter(Boolean)));
}

// Пустой массив у сужающих полей = «без сужения» (null): сервер и модель различают их,
// но человеку «ничего не выбрано» и есть «без ограничений».
export function listOrNull(list: string[] | null | undefined): string[] | null {
  return list && list.length > 0 ? list : null;
}

// Новый этап роли: включён, всё остальное — наследование.
export function blankStage(role: string): WorkflowStage {
  return { role, presence: 'on' };
}

// Пустой шаблон: все роли маршрута по порядку. version 0 — «ещё не лежал в сторе» (POST).
export function blankTemplate(name = 'Новый шаблон'): WorkflowTemplate {
  return { id: '', name, description: '', version: 0, stages: STAGE_ROLES.map(blankStage) };
}

// Проектная копия глобального: новый ключ (id пустой — генерирует сервер), version 0,
// basedOn на версию оригинала, встроенность не наследуется (проектный — всегда «свой»).
export function forkToProject(global: WorkflowTemplate): WorkflowTemplate {
  return {
    ...global,
    id: '',
    name: `${global.name} (проект)`,
    version: 0,
    builtInKey: null,
    basedOn: { templateId: global.id, version: global.version },
    stages: global.stages.map(s => ({ ...s })),
  };
}

// Снимок для dirty: тот же шаблон, собранный в одном порядке ключей.
export function templateSnapshot(t: WorkflowTemplate): string {
  return JSON.stringify({
    name: t.name, description: t.description ?? '',
    stages: normalizeStages(t.stages).map(s => ({
      id: s.id, after: s.after, lane: s.lane ?? null,
      // Имя этапа в снимке обязательно: без него правка одного имени не делала бы форму
      // «грязной» и не доходила бы до кнопки «Сохранить».
      title: s.title ?? null,
      role: s.role, presence: s.presence,
      specialty: s.specialty ?? null, personaId: s.personaId ?? null,
      prompt: s.prompt ?? '', tierStrong: s.tierStrong ?? null, tierMedium: s.tierMedium ?? null,
      tierWeak: s.tierWeak ?? null, disallowedTools: s.disallowedTools ?? null,
      mcpAllow: s.mcpAllow ?? null, stallMinutes: s.stallMinutes ?? null,
      stallPolicy: s.stallPolicy ?? null,
    })),
  });
}

// Результат отказа сервера, разложенный для экрана.
export interface SaveFailure {
  kind: 'conflict' | 'invalid' | 'failed';
  message: string;
  // Ошибки валидатора по ролям этапов (для показа у полей) и общие (не привязанные к этапу).
  stageErrors: Record<string, string[]>;
  generalErrors: string[];
}

const STAGE_ERROR_RE = /^этап «([^»]+)»:\s*(.*)$/;

// Раскладывает ошибку request(): 409 → «перечитать», 400 → ошибки валидатора по этапам
// (сервер отдаёт { error, errors: string[] }, тексты те же, что у stageErrors), прочее — как есть.
export function classifySaveError(e: unknown): SaveFailure {
  const err = e as { status?: number; message?: string; body?: { error?: string; errors?: unknown } } | null;
  if (err?.status === 409) {
    return { kind: 'conflict', message: CONFLICT_MESSAGE, stageErrors: {}, generalErrors: [] };
  }
  if (err?.status === 400) {
    const raw = Array.isArray(err.body?.errors) ? (err.body!.errors as unknown[]).map(String) : [];
    const stageErrs: Record<string, string[]> = {};
    const general: string[] = [];
    for (const line of raw) {
      const m = STAGE_ERROR_RE.exec(line);
      if (m) (stageErrs[m[1]] ??= []).push(m[2]);
      else general.push(line);
    }
    if (raw.length === 0) general.push(err.body?.error ?? err.message ?? 'Шаблон не прошёл проверку.');
    return { kind: 'invalid', message: 'Шаблон не прошёл проверку.', stageErrors: stageErrs, generalErrors: general };
  }
  return {
    kind: 'failed', message: err?.message || 'Не удалось сохранить шаблон',
    stageErrors: {}, generalErrors: [],
  };
}

export type GuardedSave =
  | { kind: 'applied'; saved: WorkflowTemplate }
  // Ответ пришёл, но форма уже показывает другой шаблон: список обновить можно, форму — нет.
  | { kind: 'stale'; saved: WorkflowTemplate }
  | ({ kind: 'conflict' | 'invalid' | 'failed' } & SaveFailure);

// Сохранение с защитой от устаревшего ответа: запоминаем ключ формы до отправки и сверяем
// после. Ключ — id шаблона на момент отправки (у нового — '' и тот же '' у чистой формы нового).
// Необработанных rejection нет: любой отказ превращается в значение.
export async function saveGuarded(opts: {
  tpl: WorkflowTemplate;
  save: (tpl: WorkflowTemplate) => Promise<WorkflowTemplate>;
  activeKey: () => string;
}): Promise<GuardedSave> {
  const sentKey = opts.activeKey();
  try {
    const saved = await opts.save(opts.tpl);
    return opts.activeKey() === sentKey ? { kind: 'applied', saved } : { kind: 'stale', saved };
  } catch (e) {
    return classifySaveError(e);
  }
}

// Перед отправкой: порог молчания клампится (И3), имя этапа нормализуется (trim, пустое → null) —
// как это делает сервер (WorkflowTemplateStore.Normalize: WorkflowRules.NormalizeStall и «пустая → null»).
export function normalizeForSave(tpl: WorkflowTemplate): WorkflowTemplate {
  return {
    ...tpl,
    stages: tpl.stages.map(s => ({
      ...s,
      stallMinutes: clampStall(s.stallMinutes),
      title: (s.title ?? '').trim() || null,
    })),
  };
}

// Переключатель присутствия доступен только у съёмных ролей (у step/merge/final — нет).
export function presenceEditable(role: string): boolean {
  return !isNonSkippable(role);
}

// Кнопка записи видна только тому, кому область разрешает писать, и не на чужом (глобальном в проекте) шаблоне.
export function showWriteButton(canWrite: boolean, readOnly: boolean): boolean {
  return canWrite && !readOnly;
}

// Владелец проекта пишет проектные шаблоны (временно, до ответа на вопрос 3 Андрею).
export function canWriteProjectTemplates(ownerId: string | null | undefined, userId: string | null | undefined): boolean {
  return !!ownerId && ownerId === userId;
}
