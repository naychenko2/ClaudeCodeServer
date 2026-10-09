// Статус обучения LoRA персонажа: опрос раз в 15 с, пока обучение идёт (ADR-026 §8).
// Логика — в контроллере без React (тестируется в node), хук — тонкая обёртка.

import { useMemo, useSyncExternalStore } from 'react';
import { loraTrainApi, type LoraTrainApi, type LoraTrainStateDto, type LoraVersionDto } from '../api';
import { loraFailure, type LoraFailure } from './loraErrors';

export const LORA_POLL_MS = 15_000;

export type LoraTrainView =
  | { kind: 'loading' }
  // Обучение выключено, нет шва или ComfyUI не отвечает: reason из кода или флагов состояния
  | { kind: 'unavailable'; reason: 'disabled' | 'no_seam' | 'unavailable' }
  | { kind: 'idle' }
  | { kind: 'queued'; position: number | null }
  | { kind: 'running'; step: number; totalSteps: number | null; etaSeconds: number | null }
  | { kind: 'done'; version: LoraVersionDto }
  | { kind: 'failed'; version: LoraVersionDto }
  | { kind: 'cancelled'; version: LoraVersionDto };

export interface LoraTrainSnapshot {
  view: LoraTrainView;
  state: LoraTrainStateDto | null;
  // Последний отказ действия (старт/отмена) или опроса
  error: LoraFailure | null;
  busy: boolean;
}

export interface LoraTrainController {
  getSnapshot(): LoraTrainSnapshot;
  subscribe(fn: () => void): () => void;
  refresh(): Promise<void>;
  start(trigger?: string): Promise<boolean>;
  cancel(): Promise<boolean>;
}

const TERMINAL = new Set<LoraTrainView['kind']>(['done', 'failed', 'cancelled']);

function viewOf(s: LoraTrainStateDto): LoraTrainView {
  if (!s.enabled) return { kind: 'unavailable', reason: 'disabled' };
  if (!s.available) return { kind: 'unavailable', reason: 'unavailable' };
  const c = s.current;
  if (!c) return { kind: 'idle' };
  if (c.status === 'queued') return { kind: 'queued', position: c.queuePosition ?? null };
  return { kind: 'running', step: c.step ?? 0, totalSteps: c.totalSteps ?? null, etaSeconds: c.etaSeconds ?? null };
}

export function createLoraTrainController(
  projectId: string, slug: string, api: LoraTrainApi = loraTrainApi, intervalMs = LORA_POLL_MS,
): LoraTrainController {
  let snap: LoraTrainSnapshot = { view: { kind: 'loading' }, state: null, error: null, busy: false };
  const listeners = new Set<() => void>();
  let timer: ReturnType<typeof setTimeout> | null = null;
  // Прогон, за которым следим: нужен, чтобы после исчезновения current узнать исход
  let watchedRunId: string | null = null;
  let epoch = 0;

  const set = (patch: Partial<LoraTrainSnapshot>) => {
    snap = { ...snap, ...patch };
    listeners.forEach(fn => fn());
  };
  const stopTimer = () => { if (timer) { clearTimeout(timer); timer = null; } };
  const schedule = () => {
    stopTimer();
    if (listeners.size === 0 || !(snap.view.kind === 'queued' || snap.view.kind === 'running')) return;
    timer = setTimeout(() => { void refresh(); }, intervalMs);
  };

  async function refresh(): Promise<void> {
    const my = ++epoch;
    stopTimer();
    let state: LoraTrainStateDto;
    try {
      state = await api.trainState(projectId, slug);
    } catch (e) {
      if (my !== epoch) return;
      const f = loraFailure(e);
      // Сбой опроса не стирает известный статус: идущее обучение продолжаем опрашивать
      const view: LoraTrainView = f.reason === 'no_seam' || f.reason === 'disabled' || f.reason === 'unavailable'
        ? { kind: 'unavailable', reason: f.reason }
        : snap.view.kind === 'loading' ? { kind: 'idle' } : snap.view;
      set({ view, error: f });
      schedule();
      return;
    }
    if (my !== epoch) return;
    const next = viewOf(state);
    if (state.current) watchedRunId = state.current.runId;

    // current пропал, а мы за прогоном следили → узнаём исход из списка версий
    if (!state.current && watchedRunId) {
      const runId = watchedRunId;
      let version: LoraVersionDto | undefined;
      try {
        version = (await api.versions(projectId, slug)).items.find(v => v.runId === runId);
      } catch { /* исход узнаем следующим кругом */ }
      if (my !== epoch) return;
      if (version && version.status !== 'training') {
        watchedRunId = null;
        const kind = version.status === 'ready' ? 'done' : version.status === 'failed' ? 'failed' : 'cancelled';
        set({ state, view: { kind, version } as LoraTrainView, error: null });
        return;
      }
      // Доводчик ещё не записал исход — считаем, что идёт, и спрашиваем снова
      set({ state, view: snap.view.kind === 'running' || snap.view.kind === 'queued' ? snap.view : next, error: null });
      schedule();
      return;
    }
    set({ state, view: next, error: null });
    schedule();
  }

  async function act(run: () => Promise<unknown>): Promise<boolean> {
    set({ busy: true, error: null });
    try {
      await run();
    } catch (e) {
      set({ busy: false, error: loraFailure(e) });
      return false;
    }
    set({ busy: false });
    await refresh();
    return true;
  }

  return {
    getSnapshot: () => snap,
    subscribe(fn) {
      listeners.add(fn);
      if (listeners.size === 1) void refresh();
      return () => {
        listeners.delete(fn);
        if (listeners.size === 0) { epoch++; stopTimer(); }
      };
    },
    refresh,
    start: trigger => act(() => {
      // Новый запуск начинается с чистого терминального вида
      if (TERMINAL.has(snap.view.kind)) set({ view: { kind: 'idle' } });
      return api.startTrain(projectId, slug, { preset: 'standard', ...(trigger ? { trigger } : {}), confirm: true });
    }),
    cancel: () => act(() => api.cancelTrain(projectId, slug)),
  };
}

// projectId/slug = null — персонаж ещё не сохранён: контроллера нет
export function useLoraTraining(projectId: string | null, slug: string | null) {
  const ctl = useMemo(
    () => (projectId && slug ? createLoraTrainController(projectId, slug) : null), [projectId, slug]);
  const none: LoraTrainSnapshot = useMemo(
    () => ({ view: { kind: 'idle' }, state: null, error: null, busy: false }), []);
  const snap = useSyncExternalStore(
    fn => (ctl ? ctl.subscribe(fn) : () => {}),
    () => (ctl ? ctl.getSnapshot() : none),
    () => (ctl ? ctl.getSnapshot() : none),
  );
  return { ...snap, start: ctl?.start, cancel: ctl?.cancel, refresh: ctl?.refresh };
}
