// Опрос снимка конвейера (вкладка «Встречи», GET /api/local-model/transcription):
// раз в 5 с, ТОЛЬКО пока active И вкладка браузера видима; пауза в скрытой, без
// утечки таймеров. Паттерн — как useLocalModelMedia / useLocalModelHardware:
// чистый контроллер (тест без React/DOM) + useSyncExternalStore; «память снимка» — при
// сбое опроса остаются прежние данные, «гонка размонтирования» — dispose гасит таймер.
//
// active — «панель "Ферма" открыта и пользователь админ», а не «открыта вкладка
// "Встречи"»: снимок кормит точку тревоги на вкладке, она обязана быть видна с любой
// вкладки (дефект №1 приёмки Ф2/Ф6). Эндпоинт админский ([Authorize(Roles="admin")]),
// не-админ active=false получает вовсе. Сам хук роли не знает.
//
// Ошибка сети или пустой ответ: data остаётся прежним (null до первого успеха) —
// вкладка рисует пустые состояния, исключения в рендере нет. Уровень тревоги (level)
// приходит с бэкенда: здесь его только переносят, сами не выводим.

import { useCallback, useEffect, useState, useSyncExternalStore } from 'react';
import { api } from '../../lib/api';
import type { TranscriptionSnapshot } from './types';

// Интервал опроса — 5 с (значение предложения v1; условие опроса с тех пор изменено:
// пока смонтирована панель «Ферма», а не «пока открыта вкладка» — см. шапку файла).
export const TRANSCRIPTION_POLL_MS = 5000;

// === Чистый контроллер опроса: тест проверяет его без React ===
export interface TranscriptionPoller {
  setActive(open: boolean, visible: boolean): void;
  getSnapshot(): { data: TranscriptionSnapshot | null; loading: boolean };
  subscribe(fn: () => void): () => void;
  // Внеочередной опрос (кнопка «Проверить сейчас»); работает, пока вкладка активна
  refetch(): void;
  dispose(): void;
}

export function createTranscriptionPoller(
  fetch: () => Promise<TranscriptionSnapshot>,
  intervalMs: number,
  // Таймеры инъектируются: в node-окружении window.setInterval возвращает NodeJS.Timeout,
  // а не number — тип id поэтому unknown
  setTimer: (fn: () => void, ms: number) => unknown = (fn, ms) => window.setInterval(fn, ms),
  clearTimer: (id: unknown) => void = (id) => window.clearInterval(id as number),
): TranscriptionPoller {
  let data: TranscriptionSnapshot | null = null;
  let loading = false;
  let active = false;
  let timer: unknown = null;
  let inflight = 0;
  const subs = new Set<() => void>();
  const emit = () => subs.forEach(fn => fn());

  // Снимок кэшируем: useSyncExternalStore сравнивает по Object.is, а новый
  // объект на каждый вызов getSnapshot — бесконечный ререндер (React #185).
  // Пересобираем только при реальном изменении data/loading/inflight.
  let snapshot: { data: TranscriptionSnapshot | null; loading: boolean } = { data: null, loading: false };
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
      // Пустой ответ (null/undefined) — не исключение: снимок остаётся пустым.
      data = d ?? null;
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

// Хук: 5-с опрос, пока active (панель «Ферма» открыта, админ) и вкладка видима;
// остановка при закрытии панели/размонтировании, без утечек таймеров.
export function useTranscription(active: boolean): {
  data: TranscriptionSnapshot | null;
  loading: boolean;
  refetch: () => void;
} {
  const [poller] = useState(() => createTranscriptionPoller(
    () => api.localModel.transcription(), TRANSCRIPTION_POLL_MS));

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
