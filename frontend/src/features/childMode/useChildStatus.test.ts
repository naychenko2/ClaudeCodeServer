import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { startChildStatusPoll } from './useChildStatus';
import type { ChildLimitsStateKind, ChildStatus } from './types';

// Окружение vitest — node (vitest.config.ts): jsdom и testing-library в проекте нет, поэтому
// поведение проверяется на чистой модели опроса (useChildStatus ниже — тонкая обёртка
// стор+эффект по ключу space), а childApi и document подменяются своими — по образцу
// hooks/useContainerWidth.test.ts.

type Call = { kind: 'status' | 'heartbeat'; space: string; signal?: AbortSignal };

let calls: Call[] = [];
let received: ChildStatus[] = [];
let offlineFlags: boolean[] = [];
let unhandled: unknown[] = [];
// Ответ status по индексу его вызова: нет записи — promise держится in-flight (на этом
// проверяется гонка ответов), Error — отказ сети.
let answers: (ChildStatus | Error)[] = [];
let pending = new Map<number, { resolve: (s: ChildStatus) => void; reject: (e: unknown) => void }>();
let statusCalls = 0;
let failHeartbeat = false;

vi.mock('./api', () => ({
  childApi: {
    status: (space: string, opts?: { signal?: AbortSignal }) => {
      const i = statusCalls++;
      calls.push({ kind: 'status', space, signal: opts?.signal });
      const a = answers[i];
      if (a instanceof Error) return Promise.reject(a);
      if (a) return Promise.resolve(a);
      return new Promise<ChildStatus>((res, rej) => { pending.set(i, { resolve: res, reject: rej }); });
    },
    heartbeat: (space: string) => {
      calls.push({ kind: 'heartbeat', space });
      return failHeartbeat ? Promise.reject(new Error('нет сети')) : Promise.resolve({} as ChildStatus);
    },
  },
}));

// document только под наблюдением visiblePoll (isTabHidden + подписка visibilitychange).
class FakeDocument {
  visibilityState = 'visible';
  private handlers = new Map<string, (() => void)[]>();
  addEventListener(type: string, h: () => void) {
    this.handlers.set(type, [...(this.handlers.get(type) ?? []), h]);
  }
  removeEventListener(type: string, h: () => void) {
    this.handlers.set(type, (this.handlers.get(type) ?? []).filter((x) => x !== h));
  }
  listenerCount(type: string) { return (this.handlers.get(type) ?? []).length; }
  change(state: 'visible' | 'hidden') {
    this.visibilityState = state;
    for (const h of [...(this.handlers.get('visibilitychange') ?? [])]) h();
  }
}

let doc: FakeDocument;

function statusCount() { return calls.filter((c) => c.kind === 'status').length; }
function heartbeatCount() { return calls.filter((c) => c.kind === 'heartbeat').length; }

function status(state: ChildLimitsStateKind, minutesLeft: number): ChildStatus {
  return {
    state, space: 'lessons', minutesLeft, windowEndsAt: null, nextWindowAt: null,
    warnMinutes: 5, maxAttachmentBytes: 10_485_760, attachmentTypes: ['image/png'], rulesAgreedAt: null,
  };
}

// Выгнать цепочки then/catch in-flight ответов: depth hops микротасков с запасом
// (heartbeat → catch → then → status → then → sink).
async function flush() {
  for (let i = 0; i < 12; i++) await Promise.resolve();
}

const sink = {
  onStatus: (s: ChildStatus) => { received.push(s); },
  onOffline: (o: boolean) => { offlineFlags.push(o); },
};

beforeEach(() => {
  calls = []; received = []; offlineFlags = []; unhandled = [];
  answers = []; pending = new Map(); statusCalls = 0; failHeartbeat = false;
  doc = new FakeDocument();
  (globalThis as { document?: unknown }).document = doc;
  vi.useFakeTimers();
  const onUnhandled = (e: unknown) => { unhandled.push(e); };
  process.on('unhandledRejection', onUnhandled);
  (globalThis as { __onUnhandled?: unknown }).__onUnhandled = onUnhandled;
});

afterEach(() => {
  const onUnhandled = (globalThis as { __onUnhandled?: unknown }).__onUnhandled as
    ((e: unknown) => void) | undefined;
  if (onUnhandled) process.off('unhandledRejection', onUnhandled);
  delete (globalThis as { document?: unknown }).document;
  vi.useRealTimers();
});

describe('опрос состояния детского режима', () => {
  it('видимая вкладка: ровно 3 heartbeat за 180 с; статус — при монтировании, каждые 30 с и после каждого heartbeat', async () => {
    const stop = startChildStatusPoll('lessons', sink);
    expect(statusCount()).toBe(1); // при монтировании
    expect(heartbeatCount()).toBe(0); // первый heartbeat — через 60 с, не сразу

    await vi.advanceTimersByTimeAsync(180_000);
    await flush();

    // 1 при монтировании + 6 по таймеру статуса (30..180) + 3 после heartbeat
    expect(heartbeatCount()).toBe(3);
    expect(statusCount()).toBe(10);
    expect(calls.every((c) => c.space === 'lessons')).toBe(true);
    stop();
  });

  it('скрытая вкладка: за 180 с ни heartbeat, ни статуса', async () => {
    doc.visibilityState = 'hidden';
    const stop = startChildStatusPoll('lessons', sink);
    await vi.advanceTimersByTimeAsync(180_000);
    await flush();
    expect(calls).toEqual([]);
    stop();
  });

  it('вернули вкладку — сразу heartbeat и статус', async () => {
    const stop = startChildStatusPoll('lessons', sink);
    await vi.advanceTimersByTimeAsync(10_000);
    doc.change('hidden');
    await vi.advanceTimersByTimeAsync(50_000);
    await flush();
    expect(heartbeatCount()).toBe(0); // на скрытой таймеры стоят
    expect(statusCount()).toBe(1); // только начальный

    doc.change('visible');
    await flush();
    expect(heartbeatCount()).toBe(1);
    // начальный + догон статуса при возврате + статус после heartbeat-догона
    expect(statusCount()).toBe(3);
    stop();
  });

  it('stale status response is dropped', async () => {
    const stop = startChildStatusPoll('lessons', sink); // status #0 держится in-flight
    await vi.advanceTimersByTimeAsync(30_000); // status #1 — тоже in-flight
    expect(statusCount()).toBe(2);

    const late = status('ok', 12); // первый запущен, ответил позже
    const fresh = status('warn', 4); // второй запущен, ответил раньше
    pending.get(1)!.resolve(fresh);
    await flush();
    expect(received).toEqual([fresh]);

    pending.get(0)!.resolve(late);
    await flush();
    expect(received).toEqual([fresh]); // ранний ответ отброшен
    stop();
  });

  it('ошибка сети: offline=true, последний status остаётся, необработанных rejection нет', async () => {
    const keep = status('ok', 20);
    answers[0] = keep;
    answers[1] = new Error('нет сети');
    const stop = startChildStatusPoll('lessons', sink);
    await flush();
    expect(received).toEqual([keep]);
    expect(offlineFlags).toEqual([]);

    await vi.advanceTimersByTimeAsync(30_000);
    await flush();
    expect(offlineFlags).toEqual([true]);
    expect(received).toEqual([keep]); // прежний снимок не сброшен
    expect(unhandled).toEqual([]);
    stop();
  });

  it('отказ heartbeat: offline=true, статус прежний, статус после него всё равно запрашивается', async () => {
    answers[0] = status('ok', 20);
    failHeartbeat = true;
    const stop = startChildStatusPoll('lessons', sink);
    await flush();
    expect(offlineFlags).toEqual([]);

    await vi.advanceTimersByTimeAsync(60_000);
    await flush();
    expect(heartbeatCount()).toBe(1);
    expect(offlineFlags).toEqual([true]);
    // начальный + тик статуса 30 с + тик статуса 60 с + после heartbeat
    expect(statusCount()).toBe(4);
    expect(received).toEqual([answers[0]]);
    expect(unhandled).toEqual([]);
    stop();
  });

  it('снятие: in-flight статус отменён, за 120 с ни одного вызова, поздний ответ не применяется', async () => {
    const stop = startChildStatusPoll('lessons', sink);
    const signal = calls[0].signal!;
    expect(doc.listenerCount('visibilitychange')).toBe(2); // подписка обоих опросов

    stop();
    expect(signal.aborted).toBe(true);
    expect(doc.listenerCount('visibilitychange')).toBe(0);

    await vi.advanceTimersByTimeAsync(120_000);
    expect(calls.length).toBe(1);

    pending.get(0)!.resolve(status('ok', 7));
    await flush();
    expect(received).toEqual([]);
    expect(offlineFlags).toEqual([]);
  });
});
