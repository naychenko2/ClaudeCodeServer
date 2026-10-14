// Редьюсер живых данных локальной модели: фазы, переход в idle по таймауту (фейковые
// таймеры), итог хода, сброс при смене сессии. Мутация «не сбрасывать при смене сессии»
// должна покрасить тест на reset.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  IDLE_TIMEOUT_MS, initialLiveState, liveReducer,
  type LocalModelLiveState,
} from './useLocalModelLive';

const live = (phase: string, genTps: number | null, waitingTotal: number | null, now: number) =>
  ({ type: 'live', phase, genTps, waitingTotal, now } as const);

describe('liveReducer: фазы', () => {
  it('live-событие выставляет фазу, скорость и очередь', () => {
    const s = liveReducer(initialLiveState(), live('generating', 38, 0, 1000));
    expect(s.seen).toBe(true);
    expect(s.phase).toBe('generating');
    expect(s.genTps).toBe(38);
    expect(s.waitingTotal).toBe(0);
    expect(s.lastActivityAt).toBe(1000);
  });

  it('queued → queued, prefill → prefill, незнакомая фаза → prefill (читает)', () => {
    expect(liveReducer(initialLiveState(), live('queued', null, 3, 1000)).phase).toBe('queued');
    expect(liveReducer(initialLiveState(), live('prefill', null, 0, 1000)).phase).toBe('prefill');
    expect(liveReducer(initialLiveState(), live('weird-phase', null, 0, 1000)).phase).toBe('prefill');
  });
});

describe('liveReducer: переход в idle по таймауту (фейковые таймеры)', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  it('до истечения таймаута фаза не меняется, после — idle', () => {
    vi.setSystemTime(1000);
    let s: LocalModelLiveState = liveReducer(initialLiveState(), live('generating', 38, 0, Date.now()));
    expect(s.phase).toBe('generating');

    // 5 с без событий — ещё не таймаут (10 с)
    vi.setSystemTime(1000 + 5000);
    s = liveReducer(s, { type: 'idle-check', now: Date.now() });
    expect(s.phase).toBe('generating');

    // истёк (10 с + 1 мс) — idle
    vi.setSystemTime(1000 + IDLE_TIMEOUT_MS + 1);
    s = liveReducer(s, { type: 'idle-check', now: Date.now() });
    expect(s.phase).toBe('idle');
  });

  it('idle-check без активности (lastActivityAt=null) не меняет состояние', () => {
    const s = liveReducer(initialLiveState(), { type: 'idle-check', now: 999999 });
    expect(s).toEqual(initialLiveState());
  });
});

describe('liveReducer: итог хода', () => {
  it('turn-событие запоминает итог и переводит в idle', () => {
    const s = liveReducer(initialLiveState(), {
      type: 'turn',
      stats: { genTps: 30, prefillTps: 1200, ttftMs: 800, queueMsApprox: 4000, cacheHitPerc: 0.87, outTokens: 500 },
      now: 2000,
    });
    expect(s.seen).toBe(true);
    expect(s.phase).toBe('idle');
    expect(s.lastTurn).toEqual({ genTps: 30, prefillTps: 1200, ttftMs: 800, queueMsApprox: 4000, cacheHitPerc: 0.87, outTokens: 500 });
  });

  it('restore с null не меняет состояние (данных о ходе нет)', () => {
    const s = liveReducer(initialLiveState(), { type: 'restore', stats: null, now: 1000 });
    expect(s).toEqual(initialLiveState());
  });

  it('restore с итогом восстанавливает последний ход (после перезагрузки)', () => {
    const s = liveReducer(initialLiveState(), {
      type: 'restore',
      stats: { genTps: 25, prefillTps: 900, ttftMs: 600, queueMsApprox: 1000, cacheHitPerc: 0.5, outTokens: 300 },
      now: 1000,
    });
    expect(s.seen).toBe(true);
    expect(s.phase).toBe('idle');
    expect(s.lastTurn?.genTps).toBe(25);
  });
});

describe('liveReducer: сброс при смене сессии', () => {
  it('reset возвращает начальное состояние (фаза/скорость/итог уходят)', () => {
    let s: LocalModelLiveState = liveReducer(initialLiveState(), live('generating', 38, 2, 1000));
    s = liveReducer(s, {
      type: 'turn',
      stats: { genTps: 30, prefillTps: 1200, ttftMs: 800, queueMsApprox: 4000, cacheHitPerc: 0.87, outTokens: 500 },
      now: 2000,
    });
    expect(s.seen).toBe(true);
    expect(s.lastTurn?.genTps).toBe(30);

    // смена сессии
    s = liveReducer(s, { type: 'reset' });
    expect(s).toEqual(initialLiveState());
    expect(s.seen).toBe(false);
    expect(s.lastTurn).toBeNull();
  });
});
