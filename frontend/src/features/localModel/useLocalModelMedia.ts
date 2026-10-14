// Тяжёлый 2-с опрос вкладки «Медиа» (ComfyUI): только пока вкладка открыта И вкладка
// браузера видима; пауза в скрытой вкладке, без утечки таймеров. Паттерн — как
// useLocalModelStats (чистый контроллер + useSyncExternalStore).
//
// since НЕ передаём: «последний час» — скользящее окно, хвост (since) его бы обрезал,
// а список мал (показываем до 8 + «и ещё N»), поэтому каждый опрос берёт полный час.

import { useCallback, useEffect, useState, useSyncExternalStore } from 'react';
import { fetchLocalModelMedia, type LocalModelMedia } from './api';

// === Чистый контроллер опроса: тест проверяет его без React ===
export interface MediaPoller {
  setActive(open: boolean, visible: boolean): void;
  getSnapshot(): { data: LocalModelMedia | null; loading: boolean };
  subscribe(fn: () => void): () => void;
  // Внеочередной опрос (кнопка «Проверить сейчас»); работает, пока вкладка активна
  refetch(): void;
  dispose(): void;
}

export function createMediaPoller(
  fetch: () => Promise<LocalModelMedia>,
  intervalMs: number,
  // Таймеры инъектируются: в node-окружении window.setInterval возвращает NodeJS.Timeout,
  // а не number — тип id поэтому unknown
  setTimer: (fn: () => void, ms: number) => unknown = (fn, ms) => window.setInterval(fn, ms),
  clearTimer: (id: unknown) => void = (id) => window.clearInterval(id as number),
): MediaPoller {
  let data: LocalModelMedia | null = null;
  let loading = false;
  let active = false;
  let timer: unknown = null;
  let inflight = 0;
  const subs = new Set<() => void>();
  const emit = () => subs.forEach(fn => fn());

  // Снимок кэшируем: useSyncExternalStore сравнивает по Object.is, а новый
  // объект на каждый вызов getSnapshot — бесконечный ререндер (React #185).
  // Пересобираем только при реальном изменении data/loading/inflight.
  let snapshot: { data: LocalModelMedia | null; loading: boolean } = { data: null, loading: false };
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

// Хук: 2-с опрос, пока вкладка «Медиа» открыта (active) и вкладка видима;
// остановка при закрытии/размонтировании, без утечек таймеров.
export function useLocalModelMedia(active: boolean): {
  data: LocalModelMedia | null;
  loading: boolean;
  refetch: () => void;
} {
  const [poller] = useState(() => createMediaPoller(() => fetchLocalModelMedia(), 2000));

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
