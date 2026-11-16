// Тяжёлый 2-с опрос вкладок «Железо»/«Стенды» (GET /api/local-model/hardware и
// /hardware/farm): только пока вкладка открыта (active) И вкладка браузера видима;
// пауза в скрытой, без утечки таймеров. Паттерн — как useLocalModelStats/
// useLocalModelMedia (чистый контроллер + useSyncExternalStore).
//
// Окно графика (1m/5m/15m/1h) — своё, не общее с LLM (дефолт 15m): сервер отдаёт
// ряды series за это окно, поэтому смена окна — немедленный новый снимок.
// Смена узла — сброс снимка и немедленный снимок: данные старого узла не должны
// показываться под новым выбором (ответ старого узла может прийти позже — см. gen).
// Ручку не-админ не вызывает (вкладка у него нет): active=false.

import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { api } from '../../lib/api';
import type { HardwareFarm, HardwareReport } from './hardwareTypes';

// Окно графика «Температуры»: 1m/5m/15m/1h; дефолт 15m (совпадает с мини-графиками).
export type HardwareWindow = '1m' | '5m' | '15m' | '1h';
export const DEFAULT_HARDWARE_WINDOW: HardwareWindow = '15m';

// === Чистый контроллер опроса: тест проверяет его без React ===
// T — тип снимка: отчёт узла (HardwareReport) или сводка фермы (HardwareFarm).
export interface HardwarePoller<T> {
  setActive(open: boolean, visible: boolean): void;
  getSnapshot(): { data: T | null; loading: boolean };
  subscribe(fn: () => void): () => void;
  // Внеочередной опрос (смена окна); работает, пока вкладка активна.
  refetch(): void;
  // Смена узла: поколение запроса поднимается (летящие ответы старого узла бракуются),
  // data сбрасывается в null и сразу делается снимок.
  restart(): void;
  dispose(): void;
}

export function createHardwarePoller<T>(
  fetch: () => Promise<T>,
  intervalMs: number,
  // Таймеры инъектируются: в node-окружении window.setInterval возвращает
  // NodeJS.Timeout, а не number — тип id поэтому unknown.
  setTimer: (fn: () => void, ms: number) => unknown = (fn, ms) => window.setInterval(fn, ms),
  clearTimer: (id: unknown) => void = (id) => window.clearInterval(id as number),
): HardwarePoller<T> {
  let data: T | null = null;
  let loading = false;
  let active = false;
  let timer: unknown = null;
  let inflight = 0;
  // Поколение запросов. Опрос асинхронный: снимок по таймеру и запрос при смене узла
  // идут параллельно, и ответ старого узла может разрешиться после ответа нового.
  // Каждый doFetch получает номер поколения; ответ принимается, только если его
  // поколение — последнее.
  let gen = 0;
  const subs = new Set<() => void>();
  const emit = () => subs.forEach(fn => fn());

  // Снимок кэшируем: useSyncExternalStore сравнивает по Object.is, а новый объект
  // на каждый вызов getSnapshot — бесконечный ререндер (React #185).
  let snapshot: { data: T | null; loading: boolean } = { data: null, loading: false };
  const refresh = () => {
    const nextLoading = loading || inflight > 0;
    if (snapshot.data !== data || snapshot.loading !== nextLoading) {
      snapshot = { data, loading: nextLoading };
    }
  };

  const doFetch = () => {
    const myGen = ++gen;
    inflight++;
    refresh();
    fetch().then(d => {
      if (myGen !== gen) { inflight--; return; } // начался новый запрос — этот ответ устарел
      data = d;
      loading = false;
      inflight--;
      refresh();
      emit();
    }).catch(() => {
      if (myGen !== gen) { inflight--; return; }
      // Сбой опроса не критичен: показываем последние данные
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
    restart() {
      gen++; // летящие ответы (старого узла) в снимок не попадут
      data = null;
      if (active) loading = true;
      refresh();
      emit();
      if (active) doFetch();
    },
    dispose() { if (timer !== null) clearTimer(timer); timer = null; active = false; },
  };
}

// Хук: 2-с опрос, пока вкладка активна (active) и вкладка видима;
// остановка при неактивности/размонтировании, без утечек таймеров.
// window — выбранное окно графика; node — выбранный узел (ключ из реестра бэкенда);
// без node бэкенд берёт worker — прежний вызов работает как раньше.
// fetch читает window/node через ref (poller живёт дольше хука).
export function useLocalModelHardware(
  active: boolean,
  window: HardwareWindow = DEFAULT_HARDWARE_WINDOW,
  node?: string,
): {
  data: HardwareReport | null;
  loading: boolean;
  refetch: () => void;
} {
  const windowRef = useRef<HardwareWindow>(window);
  windowRef.current = window;
  const nodeRef = useRef<string | undefined>(node);
  nodeRef.current = node;
  const [poller] = useState(() => createHardwarePoller<HardwareReport>(
    () => api.localModel.hardware(windowRef.current, nodeRef.current), 2000));

  // Смена окна — сразу новый снимок: сервер отдаёт ряды за выбранное окно.
  // Первый прогон пропускаем — setActive уже снял снимок.
  const prevWindow = useRef<HardwareWindow | null>(null);
  useEffect(() => {
    if (prevWindow.current === window) return;
    prevWindow.current = window;
    poller.refetch();
  }, [window, poller]);

  // Смена узла — сброс снимка и немедленный снимок (данные старого узла под новым
  // выбором не показываются). Первый прогон без node (прежний вызов) — не трогает
  // ничего; с node с самого начала — сброс безвреден (data уже null).
  const prevNode = useRef<string | undefined>(undefined);
  useEffect(() => {
    if (prevNode.current === node) return;
    prevNode.current = node;
    poller.restart();
  }, [node, poller]);

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

// Хук сводки фермы (карточка «Ферма», макет А): GET /hardware/farm тем же 2-с опросом
// и теми же правилами видимости. Окна нет: сводка — не график; узел не выбирается:
// в сводке все узлы сразу.
export function useLocalModelFarm(active: boolean): {
  data: HardwareFarm | null;
  loading: boolean;
} {
  const [poller] = useState(() => createHardwarePoller<HardwareFarm>(() => api.localModel.hardwareFarm(), 2000));

  const snap = useSyncExternalStore(
    cb => poller.subscribe(cb),
    () => poller.getSnapshot(),
    () => poller.getSnapshot(),
  );

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

  return snap;
}
