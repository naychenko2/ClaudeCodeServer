// Режим «Исполнение фичи»: источник данных агрегатов фич чата.
//
// Источник правды — БЭК: REST `GET /api/tasks/features?sessionId=…` возвращает
// готовые агрегаты `FeatureRun` (эпик + конвейер + вердикты + прогресс + готовность).
// Фронт НЕ пересчитывает конвейер — старый проектор `projectFeatureRun` выкинут:
// бэк уже считает `verdicts`/`progress`/`ready`. Здесь только держим полученное
// в сторе (useSyncExternalStore) и раздаём хуком `useFeatureRuns`.

import { useEffect, useSyncExternalStore } from 'react';
import { api } from './api';
import type { FeatureRun } from '../types/featureRun';

// Пустой конвейер — стабильная ссылка, чтобы getSnapshot не плодил новые массивы.
const EMPTY: FeatureRun[] = [];

let _runs: FeatureRun[] = EMPTY;
const _listeners = new Set<() => void>();
function emit() { _listeners.forEach(fn => fn()); }

function setRuns(runs: FeatureRun[]) { _runs = runs; emit(); }
function getRuns(): FeatureRun[] { return _runs; }
function subscribeRuns(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

// Хук: агрегаты фич чата.
//   `enabled` — гейт фич-флага (dark-launch): выключен — источник не зовётся,
//     возвращается `[]` (карточка фичи не показывается, «План хода» — как раньше);
//   `sessionId` — id сессии-чата: по нему бэк выбирает фичи именно этого чата.
// Запрос идемпотентен по (sessionId, enabled): повторный fetch — только при смене
// сессии или включении флага; cancel-метка не даёт позднему ответу затереть свежий.
export function useFeatureRuns(sessionId: string | null, enabled: boolean): FeatureRun[] {
  const runs = useSyncExternalStore(subscribeRuns, getRuns, () => _runs);
  useEffect(() => {
    if (!enabled || !sessionId) {
      if (_runs !== EMPTY) setRuns(EMPTY);
      return;
    }
    let cancelled = false;
    void api.tasks.getFeatures(sessionId)
      .then(list => { if (!cancelled) setRuns(list); })
      .catch(() => { if (!cancelled) setRuns(EMPTY); });
    return () => { cancelled = true; };
  }, [sessionId, enabled]);
  return runs;
}
