// «Чем» действия звука (ADR-023 §Д2.1). Источник в итоге один — ответ `quote` с `executors[]` (2б-4);
// пока строки строит `panel/executorRows.ts` из каталога по операции. «Свести» и «Склеить» идут без ИИ,
// у них одна строка. Выбор исполнителя помним на чат и операцию: настройки нити описывают операцию
// панели, и подставлять их чужому действию нельзя.

import type { ExecutorListModel, ExecutorRow } from 'aihome_shell/kit';
import type { AudioCatalog, AudioOp, AudioStemSet } from '../api';
import { isStemSet } from '../panel/stems';
import { isNoAi } from '../ops';
import { AUTO_EXECUTOR, executorPatch, executorRows, executorValue } from '../panel/executorRows';

export const NO_AI_ROW: ExecutorRow = {
  id: 'noAi', group: 'auto', name: 'Без ИИ', sub: 'на сервере', price: 'бесплатно', free: true,
};

const NO_AI_MODEL: ExecutorListModel = { rows: [NO_AI_ROW], value: NO_AI_ROW.id, onChange: () => {} };

export interface ExecutorChoice { provider: string; model: string }

const _choices = new Map<string, ExecutorChoice>();
const choiceKey = (sessionId: string, op: AudioOp) => `${sessionId}|${op}`;

export const getChoice = (sessionId: string, op: AudioOp): ExecutorChoice | null => _choices.get(choiceKey(sessionId, op)) ?? null;

export function chooseExecutor(sessionId: string, op: AudioOp, id: string, notify: () => void) {
  const patch = executorPatch(id);
  if (id === AUTO_EXECUTOR || !patch.provider || !patch.model) _choices.delete(choiceKey(sessionId, op));
  else _choices.set(choiceKey(sessionId, op), { provider: patch.provider, model: patch.model });
  notify();
}

// Модель держится по ключу: хост зовёт `executors()` на каждый рендер строки и панели.
// Ссылка на каталог входит в ключ: перезагрузка каталога отдаёт новую модель
const _cache = new Map<string, { key: string; catalog: AudioCatalog | null; model: ExecutorListModel }>();

export function executorModel(o: {
  sessionId: string; op: AudioOp; catalog: AudioCatalog | null; personal: boolean; notify: () => void;
  // Набор «Стемов» из ответа вопроса: «Авто» и список показывают только модели, что умеют его
  stemSet?: string | null;
}): ExecutorListModel | null {
  if (isNoAi(o.op)) return NO_AI_MODEL;
  if (!o.catalog) return null;
  const choice = getChoice(o.sessionId, o.op);
  const value = choice
    ? executorValue(o.catalog, { providerKey: choice.provider, modelId: choice.model, provider: null, model: null })
    : AUTO_EXECUTOR;
  const stemSet: AudioStemSet | null = o.op === 'separate' && isStemSet(o.stemSet) ? o.stemSet : null;
  const key = [o.op, o.personal, value, stemSet].join('|');
  const hit = _cache.get(o.sessionId);
  if (hit && hit.key === key && hit.catalog === o.catalog) return hit.model;
  const model: ExecutorListModel = {
    rows: executorRows(o.catalog, o.op, o.personal, stemSet),
    value,
    onChange: id => chooseExecutor(o.sessionId, o.op, id, o.notify),
  };
  _cache.set(o.sessionId, { key, catalog: o.catalog, model });
  return model;
}

export const __resetExecutorState = () => { _cache.clear(); _choices.clear(); };
