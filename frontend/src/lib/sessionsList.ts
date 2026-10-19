// Единая точка чтения списка чатов проекта (GET /api/projects/{id}/sessions).
//
// Зачем. Список тяжёлый (сотни чатов), а читали его десять мест независимо: опрос
// SessionList, догон после реконнекта, восстановление открытого чата, сторожа, шторка
// фичи, карточка задачи… При входе в проект и при переходах между чатами они стреляли
// пачкой — каждый своим запросом. Теперь:
//   • запросы одного проекта, идущие одновременно, делят один fetch (дедуп in-flight);
//   • читатели, которым не нужна секундная свежесть, берут недавний результат
//     (maxAgeMs), но только если в нём есть нужный им чат (mustContain): устаревший
//     список не должен превращаться в ложное «чата нет».
// Опрос SessionList всегда ходит на сервер (maxAgeMs не задан) — он и держит кэш свежим,
// поэтому updatedAt, на котором стоят непрочитанность и пропуск повторной загрузки
// истории, не залеживается дольше интервала опроса.

import { api } from './api';
import type { Session } from '../types';

interface Cached { at: number; list: Session[] }

const _inflight = new Map<string, Promise<Session[]>>();
const _cache = new Map<string, Cached>();

export interface ListOptions {
  // Годен результат не старше N мс. Не задан — всегда спрашиваем сервер (с дедупом in-flight)
  maxAgeMs?: number;
  // Кэш годен, только если в нём есть чат с этим id; иначе идём на сервер
  mustContain?: string;
}

export function listProjectSessions(projectId: string, opts: ListOptions = {}): Promise<Session[]> {
  const { maxAgeMs, mustContain } = opts;
  if (maxAgeMs !== undefined && maxAgeMs > 0) {
    const hit = _cache.get(projectId);
    if (hit && Date.now() - hit.at <= maxAgeMs
      && (mustContain === undefined || hit.list.some(s => s.id === mustContain))) {
      return Promise.resolve(hit.list);
    }
  }
  const running = _inflight.get(projectId);
  if (running) return running;
  const p = api.sessions.list(projectId)
    .then(list => { _cache.set(projectId, { at: Date.now(), list }); return list; })
    .finally(() => { _inflight.delete(projectId); });
  _inflight.set(projectId, p);
  return p;
}

// Тестовые швы (не публичное API)
export const __test = {
  reset() { _inflight.clear(); _cache.clear(); },
  cached: (projectId: string) => _cache.get(projectId)?.list,
};
