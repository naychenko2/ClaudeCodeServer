import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { LoraTrainApi, LoraTrainStateDto, LoraVersionDto } from '../api';
import { OfflineError, RequestTimeoutError } from '../../../lib/offline';
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

  it('enabled=false без current → unavailable/disabled', async () => {
    const api = makeApi([{ ...baseState, enabled: false }]);
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    expect(ctl.getSnapshot().view).toEqual({ kind: 'unavailable', reason: 'disabled' });
  });

  it('available=false при current=null → unavailable/unavailable', async () => {
    const api = makeApi([{ ...baseState, available: false }]);
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    expect(ctl.getSnapshot().view).toEqual({ kind: 'unavailable', reason: 'unavailable' });
  });

  it('available=false при живом current: идёт обучение, а не unavailable', async () => {
    const api = makeApi([{ ...running(300), available: false }]);
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    expect(ctl.getSnapshot().view).toMatchObject({ kind: 'running', step: 300 });
  });

  it('ComfyUI один раз не ответил при живом current: вид сохраняется и опрос идёт дальше', async () => {
    const api = makeApi([running(100), apiError(503, { code: 'unavailable' }), running(900)]);
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS);
    expect(ctl.getSnapshot().view).toMatchObject({ kind: 'running', step: 100 });
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS);
    expect(ctl.getSnapshot().view).toMatchObject({ kind: 'running', step: 900 });
  });

  it('сетевой сбой при живом current не стирает вид, опрос продолжается', async () => {
    const api = makeApi([running(100), new TypeError('fetch failed'), running(900)]);
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS);
    expect(ctl.getSnapshot().view).toMatchObject({ kind: 'running', step: 100 });
    expect(ctl.getSnapshot().error?.reason).toBe('network');
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS);
    expect(ctl.getSnapshot().view).toMatchObject({ kind: 'running', step: 900 });
    expect(ctl.getSnapshot().error).toBeNull();
  });

  it('сетевой сбой на первой загрузке: остаёмся в loading, ошибка видна, опрос продолжается', async () => {
    const api = makeApi([new TypeError('fetch failed'), baseState]);
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    expect(ctl.getSnapshot().view).toEqual({ kind: 'loading' });
    expect(ctl.getSnapshot().error?.reason).toBe('network');
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS);
    expect(ctl.getSnapshot().view).toEqual({ kind: 'idle' });
  });

  it('версии нет в списке (удалена в другой вкладке): вид сбрасывается в idle с причиной', async () => {
    const api = makeApi([running(1900), baseState], []);
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS);
    expect(ctl.getSnapshot().view).toEqual({ kind: 'idle' });
    expect(ctl.getSnapshot().error?.reason).toBe('version_not_found');
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS * 3);
    expect(api.trainState).toHaveBeenCalledTimes(2);
  });

  it('versions() ответил 404: тоже idle; прочая ошибка versions() — прежний вид и повтор', async () => {
    const api = makeApi([running(1900), baseState]);
    api.versions.mockRejectedValueOnce(apiError(404, { code: 'version_not_found' }));
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS);
    expect(ctl.getSnapshot().view).toEqual({ kind: 'idle' });

    const api2 = makeApi([running(1900), baseState, baseState]);
    api2.versions.mockRejectedValueOnce(new TypeError('fetch failed'));
    const ctl2 = createLoraTrainController('p', 's', api2);
    ctl2.subscribe(() => {});
    await flush();
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS);
    expect(ctl2.getSnapshot().view.kind).toBe('running');
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS);
    expect(api2.trainState).toHaveBeenCalledTimes(3);
  });

  it('cancel: успех перечитывает состояние; 404 not_training даёт причину', async () => {
    const api = makeApi([running(5), baseState]);
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await flush();
    expect(await ctl.cancel()).toBe(true);
    expect(api.cancelTrain).toHaveBeenCalledWith('p', 's');
    expect(api.trainState).toHaveBeenCalledTimes(2);

    const api2 = makeApi([baseState]);
    api2.cancelTrain.mockRejectedValueOnce(apiError(404, { code: 'not_training' }));
    const ctl2 = createLoraTrainController('p', 's', api2);
    ctl2.subscribe(() => {});
    await flush();
    expect(await ctl2.cancel()).toBe(false);
    expect(ctl2.getSnapshot().error?.reason).toBe('not_training');
    expect(ctl2.getSnapshot().busy).toBe(false);
  });

  it('ответ после отписки (StrictMode) не теряется, но опрос без подписчиков не заводится', async () => {
    let release!: (s: LoraTrainStateDto) => void;
    const api = makeApi([baseState]);
    api.trainState.mockImplementationOnce(() => new Promise<LoraTrainStateDto>(r => { release = r; }));
    const ctl = createLoraTrainController('p', 's', api);
    const off = ctl.subscribe(() => {});
    off();
    release(running(7));
    await flush();
    expect(ctl.getSnapshot().view.kind).toBe('running');
    await vi.advanceTimersByTimeAsync(LORA_POLL_MS * 3);
    expect(api.trainState).toHaveBeenCalledTimes(1);
  });

  it('более новый refresh вытесняет медленный старый', async () => {
    let release!: (s: LoraTrainStateDto) => void;
    const api = makeApi([baseState]);
    api.trainState.mockImplementationOnce(() => new Promise<LoraTrainStateDto>(r => { release = r; }));
    const ctl = createLoraTrainController('p', 's', api);
    ctl.subscribe(() => {});
    await ctl.refresh();
    expect(ctl.getSnapshot().view).toEqual({ kind: 'idle' });
    release(running(7));
    await flush();
    expect(ctl.getSnapshot().view).toEqual({ kind: 'idle' });
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

  it('неизвестный код и прочие ошибки → unknown; network только для обрыва и таймаута', () => {
    expect(loraFailure(apiError(500, { code: 'strange' })).reason).toBe('unknown');
    expect(loraFailure(apiError(403, { code: 'forbidden' })).reason).toBe('unknown');
    expect(loraFailure(new Error('x')).reason).toBe('unknown');
    expect(loraFailure(new OfflineError()).reason).toBe('network');
    expect(loraFailure(new RequestTimeoutError(30000)).reason).toBe('network');
    expect(loraFailure(new TypeError('fetch failed')).reason).toBe('network');
  });
});

describe('шторм запросов (критическое: переподписка не грузит заново)', () => {
  it('N переподписок и getSnapshot → один запрос, снимок стабилен по ссылке', async () => {
    const api = makeApi([baseState]);
    const ctl = createLoraTrainController('p', 's', api);
    for (let i = 0; i < 20; i++) { ctl.subscribe(() => {})(); }
    ctl.subscribe(() => {});
    await flush();
    expect(api.trainState).toHaveBeenCalledTimes(1);
    const a = ctl.getSnapshot();
    expect(ctl.getSnapshot()).toBe(a);
  });
});
