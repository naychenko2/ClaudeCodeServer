// Чистая логика экрана редактора шаблонов процесса (C1b): без DOM и React.
// Здесь всё, что в тесте можно проверить без отрисовки: гонки сохранения, разбор отказов
// сервера, форк глобального шаблона в проектный, разбор списков «через запятую».

import { normalizeStages } from './workflowGraphModel';
import { STAGE_ROLES, isNonSkippable, parseStageLabel, stageErrors, stageLabel } from './workflowTemplateModel';
import type { WorkflowEntryMeta, WorkflowStage, WorkflowTemplate, WorkflowTemplateEntry } from './types';

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

// Пустой черновик нового шаблона: без этапов — состав человек собирает сам или берёт готовый
// (takeComposition). version 0 — «ещё не лежал в сторе» (POST).
export function newTemplateDraft(name = 'Новый шаблон'): WorkflowTemplate {
  return { id: '', name, description: '', version: 0, stages: [] };
}

function cloneStages(stages: WorkflowStage[]): WorkflowStage[] {
  return JSON.parse(JSON.stringify(stages)) as WorkflowStage[];
}

// «Взять состав «Полного цикла»»: этапы пресета копируются в черновик целиком (id, «после»,
// настройки), имя и описание черновика остаются. Пресет не меняется.
export function takeComposition(preset: WorkflowTemplate, draft: WorkflowTemplate = newTemplateDraft()): WorkflowTemplate {
  return { ...draft, stages: cloneStages(preset.stages) };
}

// Копия под новым именем — выход из 409: новый ключ (id пустой — генерирует сервер),
// version 0, встроенность и basedOn не тащим.
export function saveAsNew(draft: WorkflowTemplate, name?: string): WorkflowTemplate {
  return {
    ...draft,
    basedOn: null,
    id: '',
    name: (name ?? '').trim() || `${draft.name} (копия)`,
    version: 0,
    builtInKey: null,
    stages: cloneStages(draft.stages),
  };
}

// Откат правок: копия последнего сохранённого шаблона (снимок, с которого форма стала «грязной»).
export function discardChanges(saved: WorkflowTemplate): WorkflowTemplate {
  return { ...saved, stages: cloneStages(saved.stages) };
}

// «Сохранить» доступен, пока нет клиентских ошибок этапов и отказа сервера, который
// правкой не снять без перечитывания (409) или без исправления шаблона (400).
export function canSave(draft: WorkflowTemplate, failure?: SaveFailure | null): boolean {
  if (failure && (failure.kind === 'conflict' || failure.kind === 'invalid')) return false;
  return stageErrors(draft).length === 0;
}

// Отказ после правки черновика: 400 (invalid) снимается — человек исправляет шаблон и
// должен снова мочь сохранить; 409 (conflict) остаётся до перечитывания шаблона.
export function failureAfterEdit(failure: SaveFailure | null): SaveFailure | null {
  return failure?.kind === 'invalid' ? null : failure;
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
      tierWeak: s.tierWeak ?? null, defaultTier: s.defaultTier ?? null, disallowedTools: s.disallowedTools ?? null,
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
  // Те же ошибки с точной меткой этапа (как её строит stageLabel): по ней сводка привязывает
  // ошибку к строке, а ключ stageErrors для этого неоднозначен (Title «qa (b)» и роль qa с id b).
  stageLines?: { label: string; message: string }[];
  generalErrors: string[];
}

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
    const stageLines: { label: string; message: string }[] = [];
    const general: string[] = [];
    for (const line of raw) {
      const m = parseStageLabel(line);
      if (m) {
        (stageErrs[m.key] ??= []).push(m.message);
        stageLines.push({ label: m.label, message: m.message });
      } else general.push(line);
    }
    if (raw.length === 0) general.push(err.body?.error ?? err.message ?? 'Шаблон не прошёл проверку.');
    return { kind: 'invalid', message: 'Шаблон не прошёл проверку.', stageErrors: stageErrs, stageLines, generalErrors: general };
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

// Отправки в полёте по id шаблона: два одновременных save одного шаблона — одна отправка
// (оба вызывающих получают результат первой). Блокировка по id, а не по экземпляру формы.
const inFlight = new Map<string, Promise<GuardedSave>>();

// Сохранение с защитой от устаревшего ответа: запоминаем ключ формы до отправки и сверяем
// после. Ключ — id шаблона на момент отправки (у нового — '' и тот же '' у чистой формы нового).
// Необработанных rejection нет: любой отказ (в том числе синхронный) превращается в значение.
export function saveGuarded(opts: {
  tpl: WorkflowTemplate;
  save: (tpl: WorkflowTemplate) => Promise<WorkflowTemplate>;
  activeKey: () => string;
}): Promise<GuardedSave> {
  const lockKey = opts.tpl.id || '\u0000new';
  const running = inFlight.get(lockKey);
  if (running) return running;
  const run = (async (): Promise<GuardedSave> => {
    try {
      const sentKey = opts.activeKey();
      const saved = await opts.save(opts.tpl);
      return opts.activeKey() === sentKey ? { kind: 'applied', saved } : { kind: 'stale', saved };
    } catch (e) {
      return classifySaveError(e);
    }
  })().finally(() => { inFlight.delete(lockKey); });
  inFlight.set(lockKey, run);
  return run;
}

// Перед отправкой: имя этапа нормализуется (trim, пустое → null) — как это делает сервер
// (WorkflowTemplateStore.Normalize). Порог молчания не клампится: вне [5..240] его ловит
// stageErrors, и до отправки дело не доходит (canSave).
export function normalizeForSave(tpl: WorkflowTemplate): WorkflowTemplate {
  return {
    ...tpl,
    stages: tpl.stages.map(s => ({
      ...s,
      stallMinutes: s.stallMinutes ?? null,
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

// Мета строк списка по ключу «область:id»: и для мета-строки элемента (itemMetaLine),
// и для тулбара редактора.
export function entryMetaMap(entries: WorkflowTemplateEntry[]): Record<string, WorkflowEntryMeta> {
  const map: Record<string, WorkflowEntryMeta> = {};
  for (const e of entries) {
    map[`${e.scope}:${e.template.id}`] = {
      stageCount: e.stageCount, parallelColumns: e.parallelColumns, usageCount: e.usageCount,
      basedOn: e.basedOn ?? null, basedOnLatestVersion: e.basedOnLatestVersion ?? null,
      diffFromPreset: e.diffFromPreset ?? null,
    };
  }
  return map;
}

// Мета-строка элемента списка (T7): «8 этапов · v4 · 14 фич», «2 параллельные колонки»,
// «от «Полного цикла» v3». Считает сервер (B08), фронт не пересчитывает: параллельных
// колонок здесь не меряем вовсе; stageCount старого сервера — длина своего списка
// (этапы уже в модели, это не пересчёт). Части — только из того, что есть:
// фичи при usageCount>0, колонки при >1, «от …» — когда известно имя оригинала
// (meta.basedOn; из WorkflowTemplate.basedOn одно id — его в строку не ставим,
// для происхождения уже есть бейдж «копия»).
export function itemMetaLine(t: WorkflowTemplate, meta: WorkflowEntryMeta | undefined): string | null {
  const parts: string[] = [];
  const stages = meta?.stageCount ?? t.stages.length;
  if (stages > 0) parts.push(`${stages} ${pluralRu(stages, 'этап', 'этапа', 'этапов')}`);
  if (t.version > 0) parts.push(`v${t.version}`);
  const used = meta?.usageCount ?? 0;
  if (used > 0) parts.push(`${used} ${pluralRu(used, 'фича', 'фичи', 'фич')}`);
  const cols = meta?.parallelColumns ?? 0;
  if (cols > 1) parts.push(`${cols} ${pluralRu(cols, 'параллельная колонка', 'параллельные колонки', 'параллельных колонок')}`);
  const b = meta?.basedOn;
  if (b) parts.push(`от «${b.name}» v${b.version}`);
  return parts.length ? parts.join(' · ') : null;
}

// Мета-строка черновика (несохранённого нового шаблона): сервера ещё нет, счёт локальный.
export function draftMetaLine(stages: WorkflowStage[]): string {
  return stages.length === 0
    ? 'этапов пока нет'
    : `${stages.length} ${pluralRu(stages.length, 'этап', 'этапа', 'этапов')}`;
}

// Подпись тулбара: «v5 · от «Полного цикла» v3 — глобальный уже v4» / «v4 · отличается от исходного
// в 3 местах» / «v4 · как исходный». Части — для ссылки на глобальный оригинал.
export interface ToolbarSubtitle { text: string; staleNote: string | null }

export function pluralRu(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10; const m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
  return many;
}

export function toolbarSubtitle(t: WorkflowTemplate, meta: WorkflowEntryMeta | undefined): ToolbarSubtitle | null {
  if (t.version <= 0) return null;
  let text = `v${t.version}`;
  let staleNote: string | null = null;
  if (t.builtInKey && meta?.diffFromPreset != null) {
    text += meta.diffFromPreset === 0
      ? ' · как исходный'
      : ` · отличается от исходного в ${meta.diffFromPreset} ${pluralRu(meta.diffFromPreset, 'месте', 'местах', 'местах')}`;
  }
  const b = meta?.basedOn ?? t.basedOn;
  if (b && 'name' in b) {
    text += ` · от «${b.name}» v${b.version}`;
    if (meta?.basedOnLatestVersion != null && meta.basedOnLatestVersion > b.version) {
      staleNote = `глобальный уже v${meta.basedOnLatestVersion}`;
    }
  }
  return { text, staleNote };
}

export const BUILTIN_NOTICE = 'Встроенный шаблон удалить нельзя — его можно править или сбросить к исходному.';
export const BUILTIN_DELETE_HINT = 'Встроенный шаблон удалить нельзя';

// Текст 409 (§1.4 п.9: «в другом окне» — один раз). Версия сервера неизвестна — без номера.
export function conflictTitle(serverVersion: number | null | undefined): string {
  return serverVersion
    ? `Шаблон изменён в другом окне — теперь это v${serverVersion}. Ваши правки не записаны.`
    : 'Шаблон изменён в другом окне. Ваши правки не записаны.';
}

export const errorsHeading = (n: number): string =>
  `Сохранить нельзя — ${n} ${pluralRu(n, 'ошибка', 'ошибки', 'ошибок')}`;

// Сводка ошибок: каждая — со ссылкой на этап (stageIndex) или без неё (общая).
export interface ErrorItem { text: string; stageIndex: number | null }

export function errorSummaryItems(draft: WorkflowTemplate, failure?: SaveFailure | null): ErrorItem[] {
  const norm = normalizeStages(draft.stages);
  // Привязка — по адресной метке (Title → роль → «роль (id)»): при двух этапах одной роли
  // ошибка попадает в свою строку, а не в первую по роли.
  const indexOf = (label: string): number | null => {
    const i = norm.findIndex((_, idx) => stageLabel(norm, idx) === label);
    return i >= 0 ? i : null;
  };
  const items: ErrorItem[] = [];
  const push = (text: string, stageIndex: number | null) => {
    if (!items.some(i => i.text === text && i.stageIndex === stageIndex)) items.push({ text, stageIndex });
  };
  for (const line of stageErrors(draft)) {
    const m = parseStageLabel(line);
    const num = /^этап #(\d+):/.exec(line);
    if (m) push(line, indexOf(m.label));
    else if (num) push(line, Number(num[1]) - 1 < norm.length ? Number(num[1]) - 1 : null);
    else push(line, null);
  }
  for (const { label, message } of failure?.stageLines ?? []) push(`${label}: ${message}`, indexOf(label));
  for (const g of failure?.generalErrors ?? []) push(g, null);
  return items;
}
