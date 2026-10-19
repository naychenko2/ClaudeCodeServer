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
//
// F1: в памяти ВКЛАДКИ (не localStorage — постоянное хранение режима вынесено за волну)
// живёт ещё три среза на чат, все в том же сторе и на тех же подписках:
//   • режим «План хода»/«Исполнение фичи» (getTurnPlanMode/setTurnPlanMode) — один выбор
//     видят и пилюля, и карточка; умолчание: есть хотя бы одна фича → feature, нет → plan;
//   • выбранная фича (getSelectedFeatureId/setSelectedFeatureId) — влияет только на тело
//     в меню/карточке; пилюля её НЕ слушает (всегда runs[0], порядок отсортировал бэк);
//   • сигнал фокуса (focusFeatureStep/useFeatureStepFocus) — чип «Шаг N фичи» (Ф9)
//     открывает нужную фичу и шаг.
// Все три — per-session (Map<sessionId, …>): смена чата показывает срез того чата,
// и ни режим, ни выбор, ни фокус не текут между чатами.

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

// F1: режим «План хода»/«Исполнение фичи» на чат, в памяти вкладки.
// Явный выбор пользователя (setTurnPlanMode); без выбора — умолчание по наличию фич.
export type TurnPlanMode = 'plan' | 'feature';
let _modeBySession = new Map<string, TurnPlanMode>();

// F1: выбранная фича на чат (id эпика), в памяти вкладки, живёт до смены чата.
let _selectedFeatureBySession = new Map<string, string>();

// F1: сигнал фокуса шага на чат: по нему чип «Шаг N фичи» (Ф9) открывает фичу и шаг.
// `seq` — монотонный счётчик: повторный фокус на тот же (featureId, taskId) всё равно
// даёт новую ссылку → подписчик срабатывает заново.
export interface FeatureStepFocus {
  featureId: string;
  taskId: string;
  seq: number;
}
let _focusBySession = new Map<string, FeatureStepFocus>();
let _focusSeq = 0;

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

// F1: режим чата. Явный выбор (setTurnPlanMode) — в приоритете; иначе умолчание:
// есть хотя бы одна фича у этого чата → feature, нет → plan (макет v1, решение 1).
// Умолчание пересчитывается, когда меняется срез фич (setRuns → emit → re-snapshot).
function modeFor(sessionId: string | null): TurnPlanMode {
  if (!sessionId) return 'plan';
  const explicit = _modeBySession.get(sessionId);
  if (explicit) return explicit;
  return snapshotFor(sessionId).length > 0 ? 'feature' : 'plan';
}

export function getTurnPlanMode(sessionId: string | null): TurnPlanMode {
  return modeFor(sessionId);
}

export function setTurnPlanMode(sessionId: string, mode: TurnPlanMode) {
  if (!sessionId) return;
  _modeBySession = new Map(_modeBySession);
  _modeBySession.set(sessionId, mode);
  emit();
}

// Хук: режим чата. Пилюля и карточка видят один выбор (тот же getSnapshot).
export function useTurnPlanMode(sessionId: string | null): TurnPlanMode {
  return useSyncExternalStore(
    subscribeRuns,
    () => modeFor(sessionId),
    () => modeFor(sessionId),
  );
}

// F1: выбранная фича (id эпика) на чат. Влияет только на тело в меню/карточке;
// пилюля её не слушает (всегда runs[0]).
function selectedFeatureIdFor(sessionId: string | null): string | undefined {
  if (!sessionId) return undefined;
  return _selectedFeatureBySession.get(sessionId);
}

export function getSelectedFeatureId(sessionId: string | null): string | undefined {
  return selectedFeatureIdFor(sessionId);
}

export function setSelectedFeatureId(sessionId: string, featureId: string) {
  if (!sessionId) return;
  _selectedFeatureBySession = new Map(_selectedFeatureBySession);
  _selectedFeatureBySession.set(sessionId, featureId);
  emit();
}

// Хук: выбранная фича чата (id эпика) или undefined, если выбор ещё не сделан.
export function useSelectedFeatureId(sessionId: string | null): string | undefined {
  return useSyncExternalStore(
    subscribeRuns,
    () => selectedFeatureIdFor(sessionId),
    () => selectedFeatureIdFor(sessionId),
  );
}

// F1: сигнал фокуса шага. focusFeatureStep ставит новый сигнал (с новым seq) и будит
// подписчиков; useFeatureStepFocus возвращает текущий сигнал чата (стабильная ссылка,
// пока не пришёл новый) или null.
function focusFor(sessionId: string | null): FeatureStepFocus | null {
  if (!sessionId) return null;
  return _focusBySession.get(sessionId) ?? null;
}

export function focusFeatureStep(sessionId: string, featureId: string, taskId: string) {
  if (!sessionId) return;
  _focusSeq += 1;
  _focusBySession = new Map(_focusBySession);
  _focusBySession.set(sessionId, { featureId, taskId, seq: _focusSeq });
  emit();
}

// Хук: сигнал фокуса шага чата (null, пока фокуса не было).
export function useFeatureStepFocus(sessionId: string | null): FeatureStepFocus | null {
  return useSyncExternalStore(
    subscribeRuns,
    () => focusFor(sessionId),
    () => focusFor(sessionId),
  );
}

// F9: «в ленте есть живая карточка плана» (per chat): ChatPanel пишет (карточка живая
// только на последней пачке и только когда есть фичи), чип «Шаг N фичи» (TaskCreatedView)
// читает: есть — сигнал фокуса потребляется в ленте, нет — чип открывает меню пилюли.
let _planCardBySession = new Map<string, boolean>();

function planCardFor(sessionId: string | null): boolean {
  if (!sessionId) return false;
  return _planCardBySession.get(sessionId) ?? false;
}

export function setHasPlanCard(sessionId: string, has: boolean) {
  if (!sessionId) return;
  _planCardBySession = new Map(_planCardBySession);
  _planCardBySession.set(sessionId, has);
  emit();
}

// Хук: есть ли в ленте чата живая карточка плана (false, пока ChatPanel не написал).
export function useHasPlanCard(sessionId: string | null): boolean {
  return useSyncExternalStore(
    subscribeRuns,
    () => planCardFor(sessionId),
    () => planCardFor(sessionId),
  );
}

// F9: прочитать флаг «живая карточка плана» императивно (не из stale-значения хука):
// чип «Шаг N фичи» (TaskCreatedView) решает, открывать ли меню пилюли, на следующем кадре
// ПОСЛЕ клика — когда эффект ChatPanel уже отработал (Глеб: гонка ремоунта).
export function getHasPlanCard(sessionId: string | null): boolean {
  return planCardFor(sessionId);
}

// F9: одноразовый сигнал «открыть меню пилюли» (per chat): чип «Шаг N фичи» открывает
// меню, когда живой карточки плана в ленте нет. `seq` — монотонный счётчик: повторное
// открытие — новая ссылка → подписчик (TurnPlanPill) срабатывает заново.
export interface PillMenuOpen { seq: number }
let _pillMenuBySession = new Map<string, PillMenuOpen>();
let _pillMenuSeq = 0;

function pillMenuFor(sessionId: string | null): PillMenuOpen | null {
  if (!sessionId) return null;
  return _pillMenuBySession.get(sessionId) ?? null;
}

export function openPillMenu(sessionId: string) {
  if (!sessionId) return;
  _pillMenuSeq += 1;
  _pillMenuBySession = new Map(_pillMenuBySession);
  _pillMenuBySession.set(sessionId, { seq: _pillMenuSeq });
  emit();
}

// Хук: сигнал «открыть меню пилюли» чата (null, пока сигнала не было).
export function usePillMenuOpen(sessionId: string | null): PillMenuOpen | null {
  return useSyncExternalStore(
    subscribeRuns,
    () => pillMenuFor(sessionId),
    () => pillMenuFor(sessionId),
  );
}

// F1: ТОЛЬКО ЧИТАЕТ стор агрегатов, без fetch (запрос уже делает ChatPanel через
// useFeatureRuns). Нужен карточке плана в ленте: она видит те же агрегаты, что и
// пилюля, но не дублирует сетевой запрос.
export function useFeatureRunsSnapshot(sessionId: string | null): FeatureRun[] {
  return useSyncExternalStore(
    subscribeRuns,
    () => snapshotFor(sessionId),
    () => snapshotFor(sessionId),
  );
}

// M2: дебаунс ~500 мс на сессию + троттлинг: не чаще одного запроса на сессию за
// MIN_FETCH_GAP_MS. Пока идут исполнители, task_changed летят непрерывно, и запрос по 400+ КБ
// уходил десятки раз в минуту (HAR прода: 24 за 1,5 мин) — теперь серия событий схлопывается
// в один запрос на окно, а последнее изменение всё равно попадает в хвостовой перезапрос.
const REFETCH_DEBOUNCE_MS = 500;
const MIN_FETCH_GAP_MS = 4000;

// Дедуп: параллельные запросы одной сессии (ChatPanel и FeatureBanner монтируют
// useFeatureRuns для одного чата) делят один fetch.
const _inflight = new Map<string, Promise<FeatureRun[]>>();
// Момент старта последнего запроса по сессии (для троттлинга).
const _lastFetchAt = new Map<string, number>();

function fetchRuns(sessionId: string): Promise<FeatureRun[]> {
  const running = _inflight.get(sessionId);
  if (running) return running;
  _lastFetchAt.set(sessionId, Date.now());
  const p = api.tasks.getFeatures(sessionId).finally(() => { _inflight.delete(sessionId); });
  _inflight.set(sessionId, p);
  return p;
}

function scheduleRefetch(sessionId: string) {
  if (_pending.has(sessionId)) return;
  const sinceLast = Date.now() - (_lastFetchAt.get(sessionId) ?? -Infinity);
  const delay = Math.max(REFETCH_DEBOUNCE_MS, MIN_FETCH_GAP_MS - sinceLast);
  _pending.set(sessionId, setTimeout(() => {
    _pending.delete(sessionId);
    void fetchRuns(sessionId)
      .then(list => setRuns(sessionId, list))
      // Сбой live-перезапроса не затирает свежий срез (в отличие от первичной загрузки).
      .catch(() => {});
  }, delay));
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
    void fetchRuns(sessionId)
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
    _modeBySession = new Map();
    _selectedFeatureBySession = new Map();
    _focusBySession = new Map();
    _focusSeq = 0;
    _planCardBySession = new Map();
    _pillMenuBySession = new Map();
    _pillMenuSeq = 0;
    _active.clear();
    _pending.forEach(t => clearTimeout(t));
    _pending.clear();
    _inflight.clear();
    _lastFetchAt.clear();
    _listeners.clear();
    _realtimeWired = false;
  },
  setRuns,
  clearRuns,
  snapshotFor,
  handleTaskChanged,
  fetchRuns,
  affectedSessionId,
  setActive: (s: string) => _active.add(s),
  active: () => new Set(_active),
  pending: () => new Set(_pending.keys()),
  // F1: прямой доступ к срезам режима/выбора/фокуса для тестов.
  modeFor,
  selectedFeatureIdFor,
  focusFor,
  // F9: прямой доступ к срезам «живая карточка плана» и «открыть меню пилюли».
  setHasPlanCard,
  planCardFor,
  openPillMenu,
  pillMenuFor,
};
