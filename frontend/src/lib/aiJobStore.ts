// Долгие вспомогательные ИИ-операции (подбор/генерация по кнопке «✨ …»):
// состояние живёт в памяти вкладки по стабильному ключу, а не в useState
// компонента — переживает переход между разделами/вкладками (компонент
// размонтировался и смонтировался заново — job на месте), но не F5.

import { useSyncExternalStore } from 'react';

export type AiJobStatus = 'idle' | 'running' | 'done' | 'error';

export interface AiJobState<T> {
  status: AiJobStatus;
  result?: T;
  error?: string;
}

const IDLE: AiJobState<unknown> = { status: 'idle' };

const jobs = new Map<string, AiJobState<unknown>>();
const listeners = new Map<string, Set<() => void>>();

function notify(key: string) {
  listeners.get(key)?.forEach(fn => fn());
}

function subscribe(key: string, cb: () => void) {
  let set = listeners.get(key);
  if (!set) { set = new Set(); listeners.set(key, set); }
  set.add(cb);
  return () => {
    set!.delete(cb);
    if (set!.size === 0) listeners.delete(key);
  };
}

export function getAiJob<T>(key: string): AiJobState<T> {
  return (jobs.get(key) as AiJobState<T> | undefined) ?? (IDLE as AiJobState<T>);
}

export function useAiJob<T>(key: string): AiJobState<T> {
  return useSyncExternalStore(
    cb => subscribe(key, cb),
    () => getAiJob<T>(key),
  );
}

// Запуск операции; если по этому ключу уже что-то выполняется — не дублируем запрос
export function runAiJob<T>(key: string, fn: () => Promise<T>): void {
  const cur = jobs.get(key);
  if (cur?.status === 'running') return;
  jobs.set(key, { status: 'running' });
  notify(key);
  fn().then(
    result => { jobs.set(key, { status: 'done', result }); notify(key); },
    err => { jobs.set(key, { status: 'error', error: err instanceof Error ? err.message : String(err) }); notify(key); },
  );
}

// Точечная правка готового результата (чекбоксы кандидатов и т.п.) — без нового запроса
export function patchAiJobResult<T>(key: string, updater: (prev: T) => T): void {
  const cur = jobs.get(key);
  if (!cur || cur.status !== 'done') return;
  jobs.set(key, { status: 'done', result: updater(cur.result as T) });
  notify(key);
}

// Сброс после применения результата или явной отмены
export function resetAiJob(key: string): void {
  jobs.delete(key);
  notify(key);
}
