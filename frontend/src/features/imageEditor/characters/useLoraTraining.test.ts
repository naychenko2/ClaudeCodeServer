import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { LoraTrainApi, LoraTrainStateDto, LoraVersionDto } from '../api';
import { loraFailure } from './loraErrors';
import { createLoraTrainController, LORA_POLL_MS } from './useLoraTraining';

const baseState: LoraTrainStateDto = {
  enabled: true, available: true, gpu: { busy: false, mine: false }, current: null,
  defaultTrigger: 'ccsandrey', presets: [{ key: 'standard', etaSeconds: 6660 }], photosChanged: false,
};
const withCurrent = (c: LoraTrainStateDto['current']): LoraTrainStateDto => ({ ...baseState, current: c });
const running = (step: number) => withCurrent({
  runId: 'r1', jobId: 'j1', status: 'running', step, totalSteps: 2000, etaSeconds: 4000,
});
const apiError = (status: number, body: object) => Object.assign(new Error('x'), { status, body });

function makeApi(states: (LoraTrainStateDto | Error)[], versions: LoraVersionDto[] = []) {
  let i = 0;
  const api = {
    trainState: vi.fn(async () => {
      const s = states[Math.min(i++, states.length - 1)];
      if (s instanceof Error) throw s;
      return s;
    }),
    versions: vi.fn(async () => ({ items: versions, own: null, limit: 5, used: 0 })),
    startTrain: vi.fn(async () => ({ runId: 'r1', jobId: 'j1', queuePosition: 0, etaSeconds: 6660 })),
    cancelTrain: vi.fn(async () => undefined),
  };
  return api as typeof api & LoraTrainApi;
}

const flush = () => vi.advanceTimersByTimeAsync(0);

beforeEach(() => { vi.useFakeTimers(); });
afterEach(() => { vi.useRealTimers(); });

describe('контроллер обучения LoRA', () => {
  it('нет обучения: idle, опроса по таймеру нет', async () => {
    const api = makeApi([baseState]);
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    expect(ctl.getSnapshot().view).toEqual({ kind: 'idle' });
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS * 3);
    expect(api.trainState).toHaveBeenCalledTimes(1);
  });

  it('очередь → идёт с шагом и ETA; опрос раз в интервал', async () => {
    const queued = withCurrent({ runId: 'r1', jobId: 'j1', status: 'queued', queuePosition: 2 });
    const api = makeApi([queued, running(640)]);
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    expect(ctl.getSnapshot().view).toEqual({ kind: 'queued', position: 2 });
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS);
    expect(ctl.getSnapshot().view).toEqual({ kind: 'running', step: 640, totalSteps: 2000, etaSeconds: 4000 });
    expect(api.trainState).toHaveBeenCalledTimes(2);
  });

  it('прогон исчез и версия ready: терминальный вид done, опрос останавливается', async () => {
    const version: LoraVersionDto = { runId: 'r1', status: 'ready', photosCount: 34, connected: true };
    const api = makeApi([running(1900), baseState], [version]);
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS);
    expect(ctl.getSnapshot().view).toEqual({ kind: 'done', version });
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS * 4);
    expect(api.trainState).toHaveBeenCalledTimes(2);
  });

  it('исход failed несёт ошибку версии', async () => {
    const version: LoraVersionDto = {
      runId: 'r1', status: 'failed', photosCount: 22, connected: false,
      error: { code: 'too_few_faces', message: 'мало лиц' },
    };
    const api = makeApi([running(10), baseState], [version]);
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS);
    expect(ctl.getSnapshot().view).toMatchObject({ kind: 'failed', version: { error: { code: 'too_few_faces' } } });
  });

  it('доводчик ещё не записал исход (версия training): продолжаем опрос', async () => {
    const training: LoraVersionDto = { runId: 'r1', status: 'training', photosCount: 34, connected: false };
    const api = makeApi([running(1999), baseState, baseState], [training]);
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS);
    expect(ctl.getSnapshot().view.kind).toBe('running');
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS);
    expect(api.trainState).toHaveBeenCalledTimes(3);
  });

  it('отписка останавливает опрос', async () => {
    const api = makeApi([running(5)]);
    const ctl = createLoraTrainController('p', 's', api);
    const off = ctl.subscribe(() => {});
    await flush();
    off();
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS * 4);
    expect(api.trainState).toHaveBeenCalledTimes(1);
  });

  it('enabled=false и available=false дают unavailable', async () => {
    const api = makeApi([{ ...baseState, enabled: false }]);
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    expect(ctl.getSnapshot().view).toEqual({ kind: 'unavailable', reason: 'disabled' });
  });

  it('start: отказ gpu_busy даёт причину с busyUntil, запроса состояния после отказа нет', async () => {
    const api = makeApi([baseState]);
    api.startTrain.mockRejectedValueOnce(apiError(409, { code: 'gpu_busy', busyUntil: '2026-10-10T10:05:00Z' }));
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    expect(await ctl.start()).toBe(false);
    expect(ctl.getSnapshot().error).toMatchObject({ reason: 'gpu_busy', busyUntil: '2026-10-10T10:05:00Z' });
    expect(api.trainState).toHaveBeenCalledTimes(1);
  });

  it('start: успех шлёт confirm и перечитывает состояние', async () => {
    const api = makeApi([baseState, running(0)]);
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    expect(await ctl.start('ccsx')).toBe(true);
    expect(api.startTrain).toHaveBeenCalledWith('p', 's', { preset: 'standard', trigger: 'ccsx', confirm: true });
    expect(ctl.getSnapshot().view).toMatchObject({ kind: 'running', step: 0 });
  });
});

describe('loraFailure', () => {
  it.each([
    'disabled', 'owner_busy', 'too_few_photos', 'version_limit', 'no_seam', 'queue_full', 'bad_trigger',
  ])('код %s → одноимённая причина', code => {
    expect(loraFailure(apiError(409, { code })).reason).toBe(code);
  });

  it('неизвестный код и обрыв связи → network', () => {
    expect(loraFailure(apiError(500, { code: 'strange' })).reason).toBe('network');
    expect(loraFailure(new Error('offline')).reason).toBe('network');
  });
});
