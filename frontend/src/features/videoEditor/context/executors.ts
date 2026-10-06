// «Чем» действий «Видео» (ADR-023 §Д2.1). «Снять» — модели каталога поставщиков: выбор пишется в настройки
// сцены, как в панели «Видео», и идёт в котировку явным поставщиком и моделью. «Собрать» идёт без ИИ,
// у него одна строка. Модель держится по ключу: хост зовёт `executors()` на каждый рендер строки и панели.

import type { ExecutorListModel, ExecutorRow } from 'aihome_shell/kit';
import type { VideoCatalog } from '../api';
import { changeSettings } from '../scene/actions';
import { executorRows, pickRow, rowId, type ResolvedScene } from '../scene/model';

export const NO_AI_ROW: ExecutorRow = {
  id: 'noAi', group: 'auto', name: 'Без ИИ', sub: 'ffmpeg на сервере', price: 'бесплатно', free: true,
};

export const NO_AI_MODEL: ExecutorListModel = { rows: [NO_AI_ROW], value: NO_AI_ROW.id, onChange: () => {} };

const _cache = new Map<string, { key: string; catalog: VideoCatalog; model: ExecutorListModel }>();

export function shootExecutors(o: {
  scope: string; sessionId: string; catalog: VideoCatalog | null; personal: boolean; r: ResolvedScene;
}): ExecutorListModel | null {
  if (!o.catalog) return null;
  const value = rowId(o.r);
  const key = [o.personal, o.r.aspect, o.r.auto, value].join('|');
  const hit = _cache.get(o.sessionId);
  if (hit && hit.key === key && hit.catalog === o.catalog) return hit.model;
  const model: ExecutorListModel = {
    rows: executorRows(o.catalog, o.personal, o.r.aspect, o.r.auto ? 'выбирает сервер' : ''),
    value,
    onChange: id => { const p = pickRow(id); changeSettings(o.scope, o.sessionId, { provider: p.provider, model: p.model }); },
  };
  _cache.set(o.sessionId, { key, catalog: o.catalog, model });
  return model;
}

export const __resetVideoExecutors = () => { _cache.clear(); };
