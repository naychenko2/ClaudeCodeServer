// «Чем» действия картинки (ADR-023 §Д2.1). Источник в итоге один — ответ `quote` с `executors[]` (шаг 2б-3);
// пока строки строит `panel/executorRows.ts` из каталога, функцией (catalog, op).

import type { ChatExecutorSection, ExecutorListModel, ExecutorRow } from 'aihome_shell/kit';
import type { ImageEditCatalog, ImageEditOp } from '../api';
import { quickOf } from './ops';
import { AUTO_EXECUTOR, executorRows, executorSettings, executorValue } from '../panel/executorRows';
import { modeSettings, getPrefs, setModeSettings } from '../thread/prefs';
import { mutate } from '../thread/threadStore';
import { threadsApi, type ImageThread, type ImageThreadSettings } from '../thread/threadsApi';

export { AUTO_EXECUTOR };

// Режим настроек действия: «Нарисовать» — выбор «Создать» проекта, остальное — настройки нити или «Править»
export const settingsMode = (op: ImageEditOp) => (op === 'generate' ? 'create' : 'edit') as 'create' | 'edit';

export const settingsOf = (scope: string, thread: ImageThread, op: ImageEditOp): ImageThreadSettings =>
  modeSettings(settingsMode(op), getPrefs(scope), thread.settings);

// Строки «Чем» под операцию действия; модель, которая не возьмёт задачу, серая с причиной
export function rowsForOp(catalog: ImageEditCatalog, op: ImageEditOp, hasImage: boolean, hasMask: boolean): ExecutorRow[] {
  return executorRows(catalog, { op, hasImage, hasMask, quick: quickOf(op) });
}

// Выбор исполнителя: в префы режима и, у правки, в настройки самой нити (они приоритетнее префов)
export function chooseExecutor(scope: string, sessionId: string, thread: ImageThread, op: ImageEditOp, id: string) {
  const patch = executorSettings(id);
  const mode = settingsMode(op);
  setModeSettings(scope, mode, patch);
  if (mode === 'edit') {
    const next = { ...settingsOf(scope, thread, op), ...patch };
    void mutate(scope, sessionId, rev => threadsApi.settings(scope, sessionId, thread.id, next, rev));
  }
}

// Модель держится по ключу: хост зовёт `executors()` на каждый рендер строки и панели
// Ссылка на каталог входит в ключ: перезагрузка каталога отдаёт новую модель
const _cache = new Map<string, { key: string; catalog: ImageEditCatalog; model: ExecutorListModel }>();

export function executorModel(o: {
  scope: string; sessionId: string; thread: ImageThread; op: ImageEditOp; catalog: ImageEditCatalog;
  hasImage: boolean; hasMask: boolean;
}): ExecutorListModel {
  const value = executorValue(o.catalog, settingsOf(o.scope, o.thread, o.op));
  const key = [o.scope, o.thread.id, o.op, o.hasImage, o.hasMask, value].join('|');
  // Место кэша — чат и режим настроек: в «Чате» строка держит «Править» и «Создать» разом, они не вытесняют друг друга
  const slot = `${o.sessionId}|${settingsMode(o.op)}`;
  const hit = _cache.get(slot);
  if (hit && hit.key === key && hit.catalog === o.catalog) return hit.model;
  const model: ExecutorListModel = {
    rows: rowsForOp(o.catalog, o.op, o.hasImage, o.hasMask),
    value,
    onChange: id => chooseExecutor(o.scope, o.sessionId, o.thread, o.op, id),
  };
  _cache.set(slot, { key, catalog: o.catalog, model });
  return model;
}

// «Чем» в «Чате»: агент правит выбранную картинку настройками «Править», а новую рисует настройками
// «Создать» (ImageProjectPrefs.LaunchSettings на сервере). У черновика правки нет — только «Создать»
const _chat = new Map<string, { edit: ExecutorListModel | null; create: ExecutorListModel; sections: readonly ChatExecutorSection[] }>();

export function chatExecutorSections(o: {
  scope: string; sessionId: string; thread: ImageThread; catalog: ImageEditCatalog; hasImage: boolean;
}): readonly ChatExecutorSection[] {
  const base = { scope: o.scope, sessionId: o.sessionId, thread: o.thread, catalog: o.catalog, hasMask: false };
  const create = executorModel({ ...base, op: 'generate', hasImage: false });
  const edit = o.hasImage ? executorModel({ ...base, op: 'edit', hasImage: true }) : null;
  const hit = _chat.get(o.sessionId);
  if (hit && hit.edit === edit && hit.create === create) return hit.sections;
  const sections: ChatExecutorSection[] = [
    ...(edit ? [{ ...edit, head: 'Править эту картинку', chip: 'Править' }] : []),
    { ...create, head: 'Новая картинка', chip: 'Создать' },
  ];
  _chat.set(o.sessionId, { edit, create, sections });
  return sections;
}

export const __resetExecutorCache = () => { _cache.clear(); _chat.clear(); };
