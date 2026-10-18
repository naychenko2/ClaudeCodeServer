// Тяжёлый 2-с опрос вкладки «Железо» (GET /api/local-model/hardware): только пока
// вкладка открыта (active) И вкладка браузера видима; пауза в скрытой, без утечки
// таймеров. Паттерн — как useLocalModelStats/useLocalModelMedia (чистый контроллер
// + useSyncExternalStore).
//
// Окно графика (1m/5m/15m/1h) — своё, не общее с LLM (дефолт 15m): сервер отдаёт
// ряды series за это окно, поэтому смена окна — немедленный новый снимок.
// Ручку не-админ не вызывает (вкладка у него нет): active=false.

import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { api } from '../../lib/api';
import type { HardwareReport } from './hardwareTypes';

// Окно графика «Температуры»: 1m/5m/15m/1h; дефолт 15m (совпадает с мини-графиками).
export type HardwareWindow = '1m' | '5m' | '15m' | '1h';
export const DEFAULT_HARDWARE_WINDOW: HardwareWindow = '15m';

// === Чистый контроллер опроса: тест проверяет его без React ===
export interface HardwarePoller {
  setActive(open: boolean, visible: boolean): void;
  getSnapshot(): { data: HardwareReport | null; loading: boolean };
  subscribe(fn: () => void): () => void;
  // Внеочередной опрос (смена окна); работает, пока вкладка активна.
  refetch(): void;
  dispose(): void;
}

export function createHardwarePoller(
  fetch: () => Promise<HardwareReport>,
  intervalMs: number,
  // Таймеры инъектируются: в node-окружении window.setInterval возвращает
  // NodeJS.Timeout, а не number — тип id поэтому unknown.
  setTimer: (fn: () => void, ms: number) => unknown = (fn, ms) => window.setInterval(fn, ms),
  clearTimer: (id: unknown) => void = (id) => window.clearInterval(id as number),
): HardwarePoller {
  let data: HardwareReport | null = null;
  let loading = false;
  let active = false;
  let timer: unknown = null;
  let inflight = 0;
  const subs = new Set<() => void>();
  const emit = () => subs.forEach(fn => fn());

  // Снимок кэшируем: useSyncExternalStore сравнивает по Object.is, а новый объект
  // на каждый вызов getSnapshot — бесконечный ререндер (React #185).
  let snapshot: { data: HardwareReport | null; loading: boolean } = { data: null, loading: false };
  const refresh = () => {
    const nextLoading = loading || inflight > 0;
    if (snapshot.data !== data || snapshot.loading !== nextLoading) {
      snapshot = { data, loading: nextLoading };
    }
  };

  const doFetch = () => {
    inflight++;
    refresh();
    fetch().then(d => {
      data = d;
      loading = false;
      inflight--;
      refresh();
      emit();
    }).catch(() => {
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
    dispose() { if (timer !== null) clearTimer(timer); timer = null; active = false; },
  };
}

// Хук: 2-с опрос, пока вкладка «Железо» активна (active) и вкладка видима;
// остановка при неактивности/размонтировании, без утечек таймеров.
// window — выбранное окно графика; fetch читает его через ref (poller живёт дольше).
export function useLocalModelHardware(active: boolean, window: HardwareWindow = DEFAULT_HARDWARE_WINDOW): {
  data: HardwareReport | null;
  loading: boolean;
  refetch: () => void;
} {
  const windowRef = useRef<HardwareWindow>(window);
  windowRef.current = window;
  const [poller] = useState(() => createHardwarePoller(() => api.localModel.hardware(windowRef.current), 2000));

  // Смена окна — сразу новый снимок: сервер отдаёт ряды за выбранное окно.
  // Первый прогон пропускаем — setActive уже снял снимок.
  const prevWindow = useRef<HardwareWindow | null>(null);
  useEffect(() => {
    if (prevWindow.current === window) return;
    prevWindow.current = window;
    poller.refetch();
  }, [window, poller]);

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
