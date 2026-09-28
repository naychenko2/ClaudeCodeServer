import { afterEach, describe, expect, it, vi } from 'vitest';
import { createMockApi, type ImageEditEvent, type ImageEditJobInput } from '../api';
import { jobPercent, progressPercent, type JobStatus } from './useJobStatus';

describe('progressPercent', () => {
  it('в очереди полоса стоит на готовых прогонах', () => {
    expect(progressPercent({ run: 2, runs: 3, etaSeconds: 40, elapsed: 30, queued: true })).toBeCloseTo(100 / 3);
  });

  it('середина второго из трёх прогонов', () => {
    expect(progressPercent({ run: 2, runs: 3, etaSeconds: 40, elapsed: 20 })).toBeCloseTo(50);
  });

  it('внутри прогона не выше 95 %, пока он не кончился', () => {
    expect(progressPercent({ run: 1, runs: 1, etaSeconds: 40, elapsed: 400 })).toBeCloseTo(95);
    expect(progressPercent({ run: 3, runs: 3, etaSeconds: 40, elapsed: 400 })).toBeCloseTo((2 + 0.95) / 3 * 100);
  });

  it('без ETA прогона — ETA котировки, без неё — 30 секунд', () => {
    expect(progressPercent({ run: null, runs: null, etaSeconds: null, elapsed: 10, fallbackEta: 20 })).toBeCloseTo(50);
    expect(progressPercent({ run: null, runs: null, etaSeconds: null, elapsed: 15 })).toBeCloseTo(50);
  });
});

describe('jobPercent', () => {
  const t0 = 1_000_000;
  const base: JobStatus = {
    phase: 'run', variants: [], count: 0, cost: null, charged: null, error: null, model: null, createdAt: t0,
    run: null, runs: null, eta: null, runStartedAt: null, queuePosition: null, fallbackEta: 20,
  };

  it('без прогонов (fal, Higgsfield) переход queued→running не откатывает полосу', () => {
    const queued = jobPercent(base, t0 + 6000, t0);
    // Событие running ставит начало прогона на момент перехода — у fal без номера прогона
    const running = jobPercent({ ...base, runStartedAt: t0 + 6000 }, t0 + 6500, t0);
    expect(queued).toBeCloseTo(30);
    expect(running).toBeGreaterThanOrEqual(queued);
  });

  it('с прогонами (local) отсчёт — от начала текущего прогона', () => {
    const status = { ...base, run: 2, runs: 2, eta: 10, runStartedAt: t0 + 20_000 };
    expect(jobPercent(status, t0 + 25_000, t0)).toBeCloseTo(75);
  });
});

describe('мок прогресса', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('локальные модели шлют номер прогона, их число и ETA', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('window', globalThis);
    const api = createMockApi('all');
    const events: ImageEditEvent[] = [];
    api.subscribe(e => events.push(e));

    const quote = api.quote('p1', { provider: 'local', model: 'auto', mode: 'auto', op: 'edit', count: 2 } as never);
    await vi.advanceTimersByTimeAsync(500);
    const input = { quoteId: (await quote).quoteId, prompt: 'вечер' } as ImageEditJobInput;
    const started = api.startJob('p1', input);
    await vi.advanceTimersByTimeAsync(60_000);
    const { jobId } = await started;

    const running = events.filter(e => e.type === 'image_edit_progress' && e.jobId === jobId && e.stage === 'running');
    expect(running.map(e => e.type === 'image_edit_progress' && [e.run, e.runs])).toEqual([[1, 2], [2, 2]]);
    expect(running.every(e => e.type === 'image_edit_progress' && e.etaSeconds === 15)).toBe(true);
  });
});
