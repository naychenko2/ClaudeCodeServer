// Панель «Локальные модели»: условие показа значка, лёгкий 60-с опрос состояния
// провайдера (для значка) и тяжёлый 2-с опрос — только пока панель открыта и
// вкладка видима. Паттерн стора — как lib/featureFlags.ts: модульное состояние,
// подписки, useSyncExternalStore.

import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { fetchLocalModelMedia, fetchLocalModelStats, type LocalModelEngineName, type LocalModelStats } from './api';
import { DEFAULT_WINDOW, type Window as StatsWindow } from './ThroughputChart';

// Дефолтные таймеры: в браузере — window.setInterval; в тестах инъектируются свои
// (в node-окружении window.setInterval возвращает NodeJS.Timeout, а не number —
// тип id поэтому unknown). Названы, чтобы вызывающий мог передать их рядом с
// engineRef, не повторяя тело.
const defaultSetTimer = (fn: () => void, ms: number): unknown => window.setInterval(fn, ms);
const defaultClearTimer = (id: unknown): void => window.clearInterval(id as number);

// Захват текущего окна для fetch: poller живёт дольше выбора окна, поэтому fetch
// читает windowRef.current на каждый вызов, а не захваченное значение. Смена
// окна (без пересоздания poller) меняет следующий запрос — тест ниже.
// engineRef — выбранный движок (тот же приём): смена меняет URL со следующего тика.
// Без engineRef или с 'main' — прежний вызов и URL без параметра engine (основной стенд).
export function makeStatsFetch(
  windowRef: { current: StatsWindow },
  engineRef?: { current: LocalModelEngineName },
): () => Promise<LocalModelStats> {
  return () => {
    const engine = engineRef?.current;
    return engine && engine !== 'main'
      ? fetchLocalModelStats(windowRef.current, undefined, engine)
      : fetchLocalModelStats(windowRef.current);
  };
}

// === Условие показа значка и панели ===
// Админ: флаг фичи + configured (как раньше). Не-админ: только configured === true —
// флаг local-model-stats его не гейтит, панель доступна всем, когда настроен ComfyUI.
// configured === null — сервер ещё не ответил: значка нет, но и «не настроена» ещё не значит.
export function isLocalModelPanelVisible(p: { flag: boolean; isAdmin: boolean; configured: boolean | null }): boolean {
  if (p.isAdmin) return p.flag && p.configured === true;
  return p.configured === true;
}

// === Стор configured: лёгкий редкий опрос для значка ===
// null — сервер ещё не ответил; true/false — последний ответ.
let _configured: boolean | null = null;
const _configuredSubs = new Set<() => void>();
export function getLocalModelConfigured(): boolean | null { return _configured; }
export function setLocalModelConfigured(v: boolean | null): void {
  if (_configured === v) return;
  _configured = v;
  _configuredSubs.forEach(fn => fn());
}
export function subscribeLocalModelConfigured(fn: () => void): () => void {
  _configuredSubs.add(fn);
  return () => { _configuredSubs.delete(fn); };
}
// Подписка компонента: значок в рельсе и панель читают одно и то же значение
export function useLocalModelConfigured(): boolean | null {
  return useSyncExternalStore(subscribeLocalModelConfigured, getLocalModelConfigured, getLocalModelConfigured);
}

// === Стор «пользователь — админ» ===
// Источник один: auth.role страницы (как в HubHeader). Сторож
// (LocalModelWatcher) публикует его сюда, а панель и isAvailable (вне React,
// без доступа к auth) читают отсюда — третьего источника роли нет.
let _isAdmin = false;
const _adminSubs = new Set<() => void>();
export function getLocalModelAdmin(): boolean { return _isAdmin; }
export function setLocalModelAdmin(v: boolean): void {
  if (_isAdmin === v) return;
  _isAdmin = v;
  _adminSubs.forEach(fn => fn());
}
export function useLocalModelAdmin(): boolean {
  return useSyncExternalStore(
    fn => { _adminSubs.add(fn); return () => { _adminSubs.delete(fn); }; },
    getLocalModelAdmin,
    getLocalModelAdmin,
  );
}

// Источник значения configured для значка: админ — /stats (только при включённом
// флаге), не-админ — /stats/media (флаг панель для него не гейтит). null — ни одного
// запроса к /api/local-model/* (только админ без флага).
export type ConfiguredSource = 'stats' | 'media';
export function configuredSource(flag: boolean, isAdmin: boolean): ConfiguredSource | null {
  if (isAdmin) return flag ? 'stats' : null;
  return 'media';
}

// Чистый запуск лёгкого опроса (60 с): первый запрос сразу, затем по таймеру.
// Возвращает остановку. Таймеры и запрос инъектируются — тест без React/DOM.
// Сбой не критичен: прежнее значение остаётся. fetch — любой ответ с configured
// (и /stats, и /stats/media).
export function startConfiguredPolling(
  fetch: () => Promise<{ configured: boolean }>,
  intervalMs = 60_000,
  setTimer: (fn: () => void, ms: number) => unknown = (fn, ms) => window.setInterval(fn, ms),
  clearTimer: (id: unknown) => void = (id) => window.clearInterval(id as number),
): () => void {
  let cancelled = false;
  const tick = () => {
    void fetch()
      .then(d => { if (!cancelled) setLocalModelConfigured(d.configured); })
      .catch(() => { /* прежнее значение */ });
  };
  tick();
  const id = setTimer(tick, intervalMs);
  return () => { cancelled = true; clearTimer(id); };
}

// Какой fetch для configured: 'stats' → /stats (админ), 'media' → /stats/media (не-админ).
// Чистая функция — тест проверяет, что не-админ (/stats/media) /stats не трогает.
export function configuredFetch(source: ConfiguredSource): () => Promise<{ configured: boolean }> {
  return source === 'stats' ? () => fetchLocalModelStats() : () => fetchLocalModelMedia();
}

// Хук поверх startConfiguredPolling. Вызывается ТОЛЬКО из сторожа, который
// смонтирован на странице всегда: панель монтируется лишь при configured=true,
// опрос внутри неё замкнул бы круг (значок не появился бы никогда).
// source — какой эндпоинт опрашивать: 'stats' (админ) или 'media' (не-админ).
export function useLocalModelConfiguredPolling(source: ConfiguredSource | null, intervalMs = 60_000): void {
  useEffect(() => {
    if (source == null) return;
    return startConfiguredPolling(configuredFetch(source), intervalMs);
  }, [source, intervalMs]);
}

// === Тяжёлый опрос (2 с) — только пока панель открыта и вкладка видима ===
// Чистый контроллер: тест проверяет его без React. open — панель смонтирована,
// visible — document.visibilityState === 'visible'; опрос идёт, только когда оба.
export interface StatsPoller {
  setActive(open: boolean, visible: boolean): void;
  getSnapshot(): { data: LocalModelStats | null; loading: boolean };
  subscribe(fn: () => void): () => void;
  // Внеочередной опрос (кнопка «Проверить сейчас»): работает, пока панель активна
  refetch(): void;
  dispose(): void;
}

export function createStatsPoller(
  fetch: () => Promise<LocalModelStats>,
  intervalMs: number,
  // Таймеры инъектируются: в node-окружении тестов window.setInterval возвращает
  // NodeJS.Timeout, а не number — тип id поэтому unknown
  setTimer: (fn: () => void, ms: number) => unknown = defaultSetTimer,
  clearTimer: (id: unknown) => void = defaultClearTimer,
  // Движок, к которому относятся запросы (тот же ref, что у makeStatsFetch): без
  // него поллер про движок не знает — поведение как раньше. С ним: ответ
  // относится к движку, актуальному на момент запроса, поздний ответ другого
  // движка отбрасывается, а смена движка сбрасывает чужой снимок.
  engineRef?: { current: LocalModelEngineName },
): StatsPoller {
  let data: LocalModelStats | null = null;
  // Движок, к которому относится data (тот же, что был в engineRef при запросе).
  // Рядом с data — чтобы поздний ответ старого движка не затёр данные нового.
  let dataEngine: LocalModelEngineName | null = null;
  let loading = false;
  let active = false;
  let timer: unknown = null;
  let inflight = 0;
  const subs = new Set<() => void>();
  const emit = () => subs.forEach(fn => fn());

  // Снимок кэшируем: useSyncExternalStore сравнивает по Object.is, а новый
  // объект на каждый вызов getSnapshot — бесконечный ререндер (React #185).
  // Пересобираем только при реальном изменении data/loading/inflight.
  let snapshot: { data: LocalModelStats | null; loading: boolean } = { data: null, loading: false };
  const refresh = () => {
    const nextLoading = loading || inflight > 0;
    if (snapshot.data !== data || snapshot.loading !== nextLoading) {
      snapshot = { data, loading: nextLoading };
    }
  };

  const doFetch = () => {
    // Движок этого запроса — тот, что выбран сейчас; без engineRef — основной стенд.
    const reqEngine: LocalModelEngineName = engineRef?.current ?? 'main';
    if (dataEngine !== null && dataEngine !== reqEngine) {
      // Сменили движок: цифры другого стенда под новым выбором быть не должно —
      // снимок сбрасывается сразу, до ответа нового (отказ нового движка не
      // оставляет чужих данных).
      data = null;
      dataEngine = null;
      loading = true;
    }
    inflight++;
    refresh();
    fetch().then(d => {
      if (engineRef && reqEngine !== engineRef.current) {
        // Поздний ответ движка, который уже не выбран: он не относится к
        // текущему снимку — отбрасываем, данные нового остаются.
        inflight--;
        refresh();
        emit();
        return;
      }
      data = d;
      dataEngine = reqEngine;
      loading = false;
      inflight--;
      refresh();
      emit();
    }).catch(() => {
      // Сбой опроса не критичен: показываем последние данные; после смены движка
      // их нет — так и остаёмся без данных, а не с чужими
      inflight--;
      loading = false;
      refresh();
      emit();
    });
  };

  return {
    setActive(open, visible) {
      const want = open && visible;
      if (want === active) return;
      active = want;
      if (timer !== null) { clearTimer(timer); timer = null; }
      if (want) {
        if (data === null) loading = true;
        refresh();
        doFetch();
        timer = setTimer(() => doFetch(), intervalMs);
      }
    },
    getSnapshot() { return snapshot; },
    subscribe(fn) { subs.add(fn); return () => { subs.delete(fn); }; },
    refetch() { if (active) doFetch(); },
    dispose() { if (timer !== null) clearTimer(timer); timer = null; active = false; },
  };
}

// Хук: 2-с опрос, пока панель открыта (active) и вкладка видима;
// остановка при закрытии/размонтировании, без утечек таймеров.
// Контроллер — ленивым useState (паттерн ChatSwitcherSheet): стабильен между
// рендерами, без чтения ref в рендере. refetch — внеочередной опрос (кнопка
// «Проверить сейчас»); стабильная обёртка, чтобы панель не перерисовывалась.
// window — выбранное окно графика (5m/15m/1h): сервер отдаёт средние
// speed.avg* за это окно, поэтому fetch читает текущее окно через ref.
// engine — выбранный стенд ('main' по умолчанию): читается через ref, как окно.
export function useLocalModelStats(
  active: boolean,
  window: StatsWindow = DEFAULT_WINDOW,
  engine: LocalModelEngineName = 'main',
): {
  data: LocalModelStats | null;
  loading: boolean;
  refetch: () => void;
} {
  const windowRef = useRef<StatsWindow>(window);
  windowRef.current = window;
  const engineRef = useRef<LocalModelEngineName>(engine);
  engineRef.current = engine;
  const [poller] = useState(() => createStatsPoller(
    makeStatsFetch(windowRef, engineRef), 2000, defaultSetTimer, defaultClearTimer, engineRef,
  ));

  // Смена окна — сразу новый снимок: сервер отдаёт средние за выбранное окно,
  // поэтому данные за 15m не подходят под график 1h. Первый прогон пропускаем —
  // setActive уже снял снимок, повторный fetch был бы дублем.
  // Смена движка — то же: немедленный запрос нового; поллер видит несовпадение
  // движков и сбрасывает чужой снимок — цифры другого стенда не показываем.
  const prevWindow = useRef<StatsWindow | null>(null);
  const prevEngine = useRef<LocalModelEngineName | null>(null);
  useEffect(() => {
    if (prevWindow.current === window && prevEngine.current === engine) return;
    prevWindow.current = window;
    prevEngine.current = engine;
    poller.refetch();
  }, [window, engine, poller]);

  const snap = useSyncExternalStore(
    cb => poller.subscribe(cb),
    () => poller.getSnapshot(),
    () => poller.getSnapshot(),
  );

  const refetch = useCallback(() => poller.refetch(), [poller]);

  useEffect(() => {
    const visible = () => document.visibilityState === 'visible';
    poller.setActive(active, visible());
    if (!active) return;
    const onVis = () => poller.setActive(true, visible());
    document.addEventListener('visibilitychange', onVis);
    return () => {
      document.removeEventListener('visibilitychange', onVis);
      poller.setActive(false, false);
      poller.dispose();
    };
  }, [active, poller]);

  return { ...snap, refetch };
}
