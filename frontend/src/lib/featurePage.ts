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
//
// Стор держит и UI-состояние страницы (выбранная вкладка, волна 3 Ф1): оно живёт
// на уровне модуля (не в DOM/компоненте), поэтому выбор вкладки переживает
// скрытие/возврат страницы и перемонтирование.

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
// Лимит ленты событий (GET features/{fid}?eventsLimit=…): undefined → дефолт бэка
// (100), 'all' → вся лента (после «Показать все»). Сбрасывается при смене fid.
let _eventsLimit: string | undefined = undefined;
// Лента «События» раскрыта (показаны все события, не только 8): UI-состояние вкладки.
// Живёт в сторе (уровень модуля, не в компоненте): переживает уход на другую вкладку
// и возврат (локальный useState сбрасывался при перемонтировании вкладки).
let _eventsExpanded = false;
// Якорь этапа (волна 3, Ф-pl-b): клик по точке этапа на плашке чата открывает страницу
// на вкладке «Хронология», ПРОКРУЧЕННОЙ/раскрытой на этом этапе. One-shot сигнал (seq) —
// тот же приём, что focusFeatureStep в featureRuns.ts: повторный фокус на тот же этап
// даёт новую ссылку → вкладка раскрывает его заново. Не сбрасывается в setPage: страница
// монтируется ПОСЛЕ клика, и сброс в setPage стёр бы якорь до того, как вкладка прочитает.
export interface FeatureStageFocus { key: string; seq: number }
let _stageFocus: FeatureStageFocus | null = null;
let _stageFocusSeq = 0;
// Локальная ошибка догрузки: догрузка всей ленты (eventsLimit=all) упала, но страница
// остаётся с уже загруженными событиями (не роняется в error); на ленте —
// «не удалось догрузить · Повторить». Сбрасывается успешной догрузой/перезапросом/сменой fid.
let _expandError = false;
// Флаг УСПЕШНОЙ догрузки всей ленты (eventsLimit=all): true, когда лента уже полностью
// загружена (ответ пришёл), а не только «запрошено». Разводит «запрошено» (_eventsLimit='all',
// ставится ДО запроса) и «загружено»: повторное «Показать все»/«Повторить» по уже
// загруженной ленте — без нового GET (м.2, повтор после ошибки). Сбрасывается в setPage/reset.
let _eventsLoaded = false;
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
// данные держим до прихода новых). `expand` — догрузка всей ленты (eventsLimit=all):
// при сбое страница НЕ роняется в error (данные уже загружены), а получает локальную
// ошибку на ленте (_expandError). Ответ: 200 → ok, 404 → notFound, 503 → disabled,
// прочее → error (или локальная ошибка при догрузке).
function fetchPage(fid: string, initial: boolean, signal?: AbortSignal, expand = false) {
  if (initial) { setState({ status: 'loading' }); _expandError = false; }
  void api.tasks.getFeature(fid, _eventsLimit, signal)
    .then(page => { if (_fid === fid) { _expandError = false; if (expand) _eventsLoaded = true; setState({ status: 'ok', page }); } })
    .catch((e: Error & { status?: number }) => {
      if (_fid !== fid) return; // страница сменилась/закрыта — поздний ответ не затирает
      if (e.name === 'AbortError') return; // запрос отменён (fid сменился) — не сбой, а отмена
      if (e.status === 404) setState({ status: 'notFound' });
      else if (e.status === 503) setState({ status: 'disabled' });
      else if (expand) { _expandError = true; emit(); } // догрузка упала: страница с данными, локальная ошибка
      else setState({ status: 'error' });
    });
}

// Открытие/смена страницы: fid. Эффект хука зовёт её, тесты — напрямую.
// Смена fid (открыли другую фичу / закрыли) — вкладка, лимит ленты, раскрытие и
// локальная ошибка догрузки сбрасываются к дефолту (лента снова с дефолтным лимитом
// бэка, не «all»/раскрытая предыдущей фичи).
function setPage(fid: string | null) {
  if (fid !== _fid) {
    _tab = DEFAULT_TAB;
    _eventsLimit = undefined;
    _eventsExpanded = false;
    _eventsLoaded = false;
    _expandError = false;
  }
  _fid = fid;
}

// === Вкладка страницы (волна 3, Ф1) ===
//
// Выбранная вкладка сохраняется при скрытии/возврате страницы: состояние — в этом
// сторе (уровень модуля), а не в DOM/компоненте. Страница одна на вкладку —
// одно значение; смена fid сбрасывает к дефолту («Хронология»).
export type FeaturePageTab = 'timeline' | 'plans' | 'chats' | 'tasks' | 'events' | 'stats';
const DEFAULT_TAB: FeaturePageTab = 'timeline';
let _tab: FeaturePageTab = DEFAULT_TAB;

// Установить вкладку (no-op при том же значении — лишний emit не плодим).
export function setFeaturePageTab(t: FeaturePageTab) {
  if (_tab === t) return;
  _tab = t;
  emit();
}

// Хук: выбранная вкладка страницы (тот же стор, что и данные).
export function useFeaturePageTab(): FeaturePageTab {
  return useSyncExternalStore(
    subscribe,
    () => _tab,
    () => _tab,
  );
}

// Якорь этапа (Ф-pl-b): поставить one-shot сигнал «раскрыть этап key на Хронологии».
// `seq` — монотонный счётчик: повторный клик по тому же этапу всё равно даёт новую ссылку.
export function focusFeatureStage(key: string) {
  if (!key) return;
  _stageFocusSeq += 1;
  _stageFocus = { key, seq: _stageFocusSeq };
  emit();
}

// Хук: текущий one-shot якорь этапа (null, пока сигнала не было).
export function useFeatureStageFocus(): FeatureStageFocus | null {
  return useSyncExternalStore(
    subscribe,
    () => _stageFocus,
    () => _stageFocus,
  );
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

// «Показать все» (вкладка «События»): догрузить ВСЮ ленту (eventsLimit=all). Бэк
// по умолчанию режет ленту до 100 (eventsLimit), а eventsTotal — общее число до
// обрезки; без догрузки «Показать все · 1000» раскрывало бы только 100. После
// раскрытия лимит остаётся 'all' — live-перезапросы тоже тянут всю ленту (иначе
// лента «схлопнулась бы» обратно к 100 на следующем task_changed). Не сбрасывает в
// loading (initial=false): старые данные держим до прихода новых, без мигания.
// Кэш (м.2): лента уже догружена (УСПЕШНО, _eventsLoaded) — повторное раскрытие/повтор
// без запроса (не плодим лишний GET всей ленты). «Запрошено» (_eventsLimit='all', ставится
// ДО запроса) ≠ «загружено»: после упавшей догрузки _eventsLoaded=false → «Повторить»
// уходит в новый GET. Действует, только пока страница открыта.
export function expandFeaturePageEvents(fid: string) {
  if (_fid !== fid) return;
  if (_eventsLoaded) {
    // Уже догружено: повторное раскрытие/повтор — только UI (раскрыть + снять локальную
    // ошибку), без нового GET.
    _eventsExpanded = true;
    _expandError = false;
    emit();
    return;
  }
  _eventsLimit = 'all';
  _eventsExpanded = true;
  _expandError = false;
  emit();
  fetchPage(fid, false, undefined, true);
}

// «Свернуть» (вкладка «События»): обратно к 8 последним. Данные (лента) не меняются —
// меняется только видимое раскрытие (UI-состояние в сторе, переживает смену вкладки).
export function collapseFeaturePageEvents(fid: string) {
  if (_fid !== fid || !_eventsExpanded) return;
  _eventsExpanded = false;
  emit();
}

// Хуки UI-состояния вкладки «События» (тот же стор, что и данные): раскрытие ленты
// (показаны все события, не только 8) и локальная ошибка догрузки.
export function useFeaturePageEventsExpanded(): boolean {
  return useSyncExternalStore(subscribe, () => _eventsExpanded, () => _eventsExpanded);
}
export function useFeaturePageExpandError(): boolean {
  return useSyncExternalStore(subscribe, () => _expandError, () => _expandError);
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
      setPage(null);
      if (_pending) { clearTimeout(_pending); _pending = null; }
      setState(LOADING);
      return;
    }
    setPage(fid);
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
    _tab = DEFAULT_TAB;
    _eventsLimit = undefined;
    _eventsExpanded = false;
    _eventsLoaded = false;
    _expandError = false;
    _stageFocus = null;
    _stageFocusSeq = 0;
    _listeners.clear();
    _realtimeWired = false;
  },
  focusFeatureStage,
  stageFocus: () => _stageFocus,
  setState,
  state: () => _state,
  fid: () => _fid,
  setFid: (f: string | null) => { _fid = f; },
  setPage,
  handleTaskChanged,
  isOwnEvent,
  pending: () => _pending !== null,
  scheduleRefresh,
  fetchPage,
  // Текущий лимит ленты (undefined → дефолт бэка, 'all' → вся лента).
  eventsLimit: () => _eventsLimit,
  // Успешно догружена ли вся лента (true → повторное раскрытие/повтор без GET).
  eventsLoaded: () => _eventsLoaded,
  expandFeaturePageEvents,
  collapseFeaturePageEvents,
  // Раскрытие ленты (все события, не только 8) и локальная ошибка догрузки.
  eventsExpanded: () => _eventsExpanded,
  setEventsExpanded: (v: boolean) => { _eventsExpanded = v; },
  expandError: () => _expandError,
  setExpandError: (v: boolean) => { _expandError = v; },
};
