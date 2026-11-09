// Число фото для обучения персонажа: карточке оно нужно и при закрытой шторке «Фото».
// Один запрос на контроллер; set снаружи — шторка сообщает свежее число после загрузки/удаления.

import { useCallback, useMemo, useSyncExternalStore } from 'react';
import { loraTrainApi, type LoraTrainApi } from '../api';

export interface TrainPhotoCountController {
  // null — ещё не узнали или запрос не удался
  getSnapshot(): number | null;
  subscribe(fn: () => void): () => void;
  set(n: number): void;
}

export function createTrainPhotoCountController(
  projectId: string, slug: string, api: Pick<LoraTrainApi, 'photos'> = loraTrainApi,
): TrainPhotoCountController {
  let count: number | null = null;
  let started = false;
  // set снаружи свежее ответа, который ещё летит
  let fresh = false;
  const listeners = new Set<() => void>();
  const apply = (n: number | null) => {
    if (n === count) return;
    count = n;
    listeners.forEach(fn => fn());
  };
  return {
    getSnapshot: () => count,
    subscribe(fn) {
      listeners.add(fn);
      if (!started) {
        started = true;
        api.photos(projectId, slug)
          .then(d => { if (!fresh) apply(d.count); })
          .catch(() => { if (!fresh) apply(null); });
      }
      return () => { listeners.delete(fn); };
    },
    set(n) { fresh = true; apply(n); },
  };
}

export function useTrainPhotoCount(projectId: string | null, slug: string | null) {
  const ctl = useMemo(
    () => (projectId && slug ? createTrainPhotoCountController(projectId, slug) : null), [projectId, slug]);
  const subscribe = useCallback((fn: () => void) => (ctl ? ctl.subscribe(fn) : () => {}), [ctl]);
  const getSnapshot = useCallback(() => (ctl ? ctl.getSnapshot() : null), [ctl]);
  const count = useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
  const set = useCallback((n: number) => ctl?.set(n), [ctl]);
  return { count, set };
}
