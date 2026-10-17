// Режим «Исполнение фичи»: источник данных агрегатов фич чата.
//
// Источник правды — БЭК: REST `GET /api/tasks/features?sessionId=…` возвращает
// готовые агрегаты `FeatureRun` (эпик + конвейер + вердикты + прогресс + готовность).
// Фронт НЕ пересчитывает конвейер — бэк уже считает `verdicts`/`progress`/`ready`.
// Здесь держим полученное в сторе (useSyncExternalStore) и раздаём хуком `useFeatureRuns`.
//
// M1: стор агрегатов РАЗДЕЛЁН по sessionId (Map<sessionId, FeatureRun[]>). Каждый чат
// держит свой срез: «Стена» монтирует ChatPanel по экземпляру на колонку, и последний
// fetch одного чата больше не затирает агрегаты другого. Срез — стабильная ссылка (тот
// же массив, пока по этому sessionId не пришёл новый fetch), иначе useSyncExternalStore
// зациклит ререндер.
//
// M2: живой режим — подписка на `task_changed` (тот же канал, что в lib/tasks.ts). Пока
// исполнители работают, вердикты/прогресс/подпись пилюли меняются: по событию (задача с
// featureId или role=feature) перезапрашиваем агрегат затронутого чата с дебаунсом ~500 мс.
// Событие игнорируем, если фич-флаг выключен или чат не «активен» (никто его не смотрит).

import { useEffect, useSyncExternalStore } from 'react';
import { api } from './api';
import { onMessage } from './signalr';
import { getTaskById } from './tasks';
import { getFlag, FLAGS } from './featureFlags';
import type { FeatureRun } from '../types/featureRun';
import type { ServerMessage } from '../types';

// Пустой конвейер — стабильная ссылка, чтобы getSnapshot не плодил новые массивы.
const EMPTY: FeatureRun[] = [];

// M1: агрегаты по sessionId. Map (не Record) — ключи-строки, копирование дёшевое.
let _runsBySession = new Map<string, FeatureRun[]>();
const _listeners = new Set<() => void>();
function emit() { _listeners.forEach(fn => fn()); }

// M2: «активные» сессии — у них смонтирован useFeatureRuns(enabled=true). Для них
// task_changed перезапрашивает агрегат; чужие (неактивные) не трогаем.
const _active = new Set<string>();
// Дебаунс-таймеры по сессии: серия task_changed → один перезапрос.
const _pending = new Map<string, ReturnType<typeof setTimeout>>();
let _realtimeWired = false;

// M1: обновить ТОЛЬКО срез под sessionId (не весь стор). Копия Map — стабильные ссылки
// на срезы других сессий не меняются (useSyncExternalStore их не «видит» как новые).
function setRuns(sessionId: string, runs: FeatureRun[]) {
  _runsBySession = new Map(_runsBySession);
  _runsBySession.set(sessionId, runs);
  emit();
}

// M1: сброс всех срезов (Map.clear).
function clearRuns() {
  _runsBySession = new Map();
  emit();
}

function subscribeRuns(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

// M1: срез по sessionId — стабильная ссылка (тот же массив, пока не пришёл новый fetch
// для этого sessionId). EMPTY — если сессии ещё нет.
function snapshotFor(sessionId: string | null): FeatureRun[] {
  if (!sessionId) return EMPTY;
  return _runsBySession.get(sessionId) ?? EMPTY;
}

// M2: дебаунс ~500 мс на сессию.
const REFETCH_DEBOUNCE_MS = 500;
function scheduleRefetch(sessionId: string) {
  if (_pending.has(sessionId)) return;
  _pending.set(sessionId, setTimeout(() => {
    _pending.delete(sessionId);
    void api.tasks.getFeatures(sessionId)
      .then(list => setRuns(sessionId, list))
      // Сбой live-перезапроса не затирает свежий срез (в отличие от первичной загрузки).
      .catch(() => {});
  }, REFETCH_DEBOUNCE_MS));
}

// M2: затронутая сессия по task_changed.
//   • задача с role='feature' — это сам эпик → её sourceSessionId;
//   • задача с featureId — дочерняя → эпик (по id) → его sourceSessionId.
// Иначе (обычная задача) → null.
function affectedSessionId(msg: ServerMessage): string | null {
  if (msg.type !== 'task_changed' || msg.action === 'deleted') return null;
  const t = msg.task;
  if (t.role === 'feature') return t.sourceSessionId ?? null;
  if (t.featureId) return getTaskById(t.featureId)?.sourceSessionId ?? null;
  return null;
}

// M2: обработчик task_changed. Фича выключена — событие проигнорировать; затронутая
// сессия не «активна» (никто не смотрит её) — тоже.
function handleTaskChanged(msg: ServerMessage) {
  if (!getFlag(FLAGS.turnPlanFeatureMode)) return;
  const sessionId = affectedSessionId(msg);
  if (!sessionId) return;
  if (!_active.has(sessionId)) return;
  scheduleRefetch(sessionId);
}

// Подписка на task_changed (тот же канал, что в lib/tasks.ts) — один раз.
function wireRealtime() {
  if (_realtimeWired) return;
  _realtimeWired = true;
  onMessage(handleTaskChanged);
}

// Хук: агрегаты фич чата.
//   `enabled` — гейт фич-флага (dark-launch): выключен — источник не зовётся,
//     возвращается `[]` (карточка фичи не показывается, «План хода» — как раньше);
//   `sessionId` — id сессии-чата: по нему бэк выбирает фичи именно этого чата.
// Запрос идемпотентен по (sessionId, enabled): повторный fetch — только при смене
// сессии или включении флага; cancel-метка не даёт позднему ответу затереть свежий.
export function useFeatureRuns(sessionId: string | null, enabled: boolean): FeatureRun[] {
  const runs = useSyncExternalStore(
    subscribeRuns,
    () => snapshotFor(sessionId),
    () => snapshotFor(sessionId),
  );
  useEffect(() => {
    wireRealtime();
    if (!enabled || !sessionId) {
      // выключили/нет сессии — чистим срез этого чата (не весь стор)
      if (sessionId && _runsBySession.get(sessionId) !== EMPTY) setRuns(sessionId, EMPTY);
      return;
    }
    _active.add(sessionId);
    let cancelled = false;
    void api.tasks.getFeatures(sessionId)
      .then(list => { if (!cancelled) setRuns(sessionId, list); })
      .catch(() => { if (!cancelled) setRuns(sessionId, EMPTY); });
    return () => {
      cancelled = true;
      _active.delete(sessionId);
    };
  }, [sessionId, enabled]);
  return runs;
}

// Тестовые швы (не публичное API): сброс стора + прямой доступ к внутреннему состоянию.
export const __test = {
  reset() {
    _runsBySession = new Map();
    _active.clear();
    _pending.forEach(t => clearTimeout(t));
    _pending.clear();
    _listeners.clear();
    _realtimeWired = false;
  },
  setRuns,
  clearRuns,
  snapshotFor,
  handleTaskChanged,
  affectedSessionId,
  setActive: (s: string) => _active.add(s),
  active: () => new Set(_active),
  pending: () => new Set(_pending.keys()),
};
