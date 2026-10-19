// Данные страницы фичи (волна 2 «Обзор фич», шаг Ф2.2).
//
// Источник правды — БЭК: REST `GET /api/tasks/features/{fid}` отдаёт обёртку
// `{ run, events, eventsTotal }` — агрегат `FeatureRun` одной фичи + лента событий.
// Фронт НЕ пересчитывает — только показывает (как `featureRuns.ts`).
//
// Модель: ОДИН запрос, пока страница открыта (fid ≠ null). Пока страница открыта,
// живое обновление по `task_changed` (тот же канал, что в `lib/tasks.ts`/`featureRuns.ts`)
// с ДЕБАУНСОМ 1,5 с и фильтром `task.featureId === fid || task.id === fid`: событие
// чужой фичи страницу не трогает, серия своих в окне — один перезапрос.
//
// Состояния: loading | ok | notFound(404) | disabled(503) | error.
// Урок волны 1 (MAJOR-1): никаких запросов по потребителю сверх одного — первичный
// запрос один, live-перезапрос дебаунсит в один.
//
// Стор — ОДНА страница (fid → state): страница фичи одна на вкладку (маршрут
// #/project/{pid}/feature/{fid}), Map по fid не нужен. Срез — стабильная ссылка
// (тот же объект, пока не пришёл новый fetch), иначе useSyncExternalStore зациклит.

import { useEffect, useSyncExternalStore } from 'react';
import { api } from './api';
import { onMessage } from './signalr';
import { getFlag, FLAGS } from './featureFlags';
import type { FeaturePage } from '../types/featureIndex';
import type { ServerMessage } from '../types';

// Состояние страницы фичи.
//   loading  — первичный запрос в полёте (страница открыта, данных ещё нет);
//   ok       — данные есть (run + events + eventsTotal);
//   notFound — 404: фичи нет / чужая / удалена (бэк не отличает — один 404);
//   disabled — 503: подсистема Tasks выключена на сервере (ADR-014);
//   error    — прочий сбой (сеть, 5xx, таймаут).
export type FeaturePageState =
  | { status: 'loading' }
  | { status: 'ok'; page: FeaturePage }
  | { status: 'notFound' }
  | { status: 'disabled' }
  | { status: 'error' };

// Пустое состояние — стабильная ссылка, чтобы getSnapshot не плодил новые объекты.
const LOADING: FeaturePageState = { status: 'loading' };

let _state: FeaturePageState = LOADING;
// Открытая страница (fid эпика); null — страница закрыта.
let _fid: string | null = null;
const _listeners = new Set<() => void>();
function emit() { _listeners.forEach(fn => fn()); }

// Дебаунс живого перезапроса: серия task_changed в окне → один перезапрос.
const REFRESH_DEBOUNCE_MS = 1500;
let _pending: ReturnType<typeof setTimeout> | null = null;
let _realtimeWired = false;

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

// Смена состояния (новый объект → новая ссылка → useSyncExternalStore видит).
function setState(s: FeaturePageState) {
  _state = s;
  emit();
}

// Первичный/live-запрос страницы. `initial` — первичный (страница открыта):
// сначала loading, чтобы скелет показался; live — не сбрасывает в loading (старые
// данные держим до прихода новых). Ответ: 200 → ok, 404 → notFound, 503 → disabled,
// прочее → error.
function fetchPage(fid: string, initial: boolean, signal?: AbortSignal) {
  if (initial) setState({ status: 'loading' });
  void api.tasks.getFeature(fid, undefined, signal)
    .then(page => { if (_fid === fid) setState({ status: 'ok', page }); })
    .catch((e: Error & { status?: number }) => {
      if (_fid !== fid) return; // страница сменилась/закрыта — поздний ответ не затирает
      if (e.name === 'AbortError') return; // запрос отменён (fid сменился) — не сбой, а отмена
      if (e.status === 404) setState({ status: 'notFound' });
      else if (e.status === 503) setState({ status: 'disabled' });
      else setState({ status: 'error' });
    });
}

// Живой перезапрос по task_changed: дебаунс 1,5 с. Если перезапрос уже в очереди —
// новый событие не плодит (серия → один запрос).
function scheduleRefresh(fid: string) {
  if (_pending) return;
  _pending = setTimeout(() => {
    _pending = null;
    fetchPage(fid, false);
  }, REFRESH_DEBOUNCE_MS);
}

// Свой ли это событие: задача фичи (featureId === fid) или сам эпик (id === fid).
// Удаление (action=deleted) игнорируем, как в featureRuns.ts: агрегат фичи не зависит
// от жизни отдельной задачи, а 404 «фичу удалили» первичный запрос уже обработал.
function isOwnEvent(msg: ServerMessage, fid: string): boolean {
  if (msg.type !== 'task_changed' || msg.action === 'deleted') return false;
  const t = msg.task;
  return t.featureId === fid || t.id === fid;
}

// Обработчик task_changed: флаг выключен или страница закрыта — событие проигнорировано.
function handleTaskChanged(msg: ServerMessage) {
  if (!getFlag(FLAGS.featuresOverview)) return;
  if (!_fid) return;
  if (!isOwnEvent(msg, _fid)) return;
  scheduleRefresh(_fid);
}

// Подписка на task_changed (тот же канал, что в lib/tasks.ts) — один раз.
function wireRealtime() {
  if (_realtimeWired) return;
  _realtimeWired = true;
  onMessage(handleTaskChanged);
}

// Повторный запрос (кнопка «Повторить» в состоянии ошибки, макет §5.7): сброс в
// loading + новый первичный запрос. Публичный шов (не __test) — UI его зовёт.
// Действует, только пока страница открыта (_fid === fid): закрытая страница не
// перезапрашивается (как и live-события).
export function refetchFeaturePage(fid: string) {
  if (_fid !== fid) return;
  fetchPage(fid, true);
}

// «Показать все» (макет §5): перезапрос ленты БЕЗ лимита (eventsLimit='all').
// Не сбрасывает в loading: данные уже есть, просто обновляем events/eventsTotal.
// Действует, только пока страница открыта (_fid === fid).
export function refetchFeaturePageAll(fid: string) {
  if (_fid !== fid) return;
  void api.tasks.getFeature(fid, 'all')
    .then(page => { if (_fid === fid) setState({ status: 'ok', page }); })
    .catch(() => {});
}

// Хук: данные страницы фичи.
//   `fid` — id эпика; null — страница закрыта (сброс в loading, запрос не идёт).
// Один первичный запрос на открытие/смену fid; live-перезапрос по task_changed (дебаунс).
export function useFeaturePage(fid: string | null): FeaturePageState {
  const state = useSyncExternalStore(
    subscribe,
    () => _state,
    () => _state,
  );
  useEffect(() => {
    wireRealtime();
    if (!fid) {
      // Страница закрыта: сбрасываем fid (live-события больше не наши) и в loading.
      _fid = null;
      if (_pending) { clearTimeout(_pending); _pending = null; }
      setState(LOADING);
      return;
    }
    _fid = fid;
    // Первичный запрос: один, пока страница открыта. AbortController отменяет в-полёте
    // запрос при быстрой смене fid (не плодим параллельных GET); cancel-метка _fid не
    // даёт позднему ответу (fid сменился/страница закрыта) затереть свежий срез.
    const controller = new AbortController();
    fetchPage(fid, true, controller.signal);
    return () => {
      if (_pending) { clearTimeout(_pending); _pending = null; }
      controller.abort();
    };
  }, [fid]);
  return state;
}

// Тестовые швы (не публичное API): сброс стора + прямой доступ к внутреннему состоянию.
export const __test = {
  reset() {
    _state = LOADING;
    _fid = null;
    _pending = null;
    _listeners.clear();
    _realtimeWired = false;
  },
  setState,
  state: () => _state,
  fid: () => _fid,
  setFid: (f: string | null) => { _fid = f; },
  handleTaskChanged,
  isOwnEvent,
  pending: () => _pending !== null,
  scheduleRefresh,
  fetchPage,
};
