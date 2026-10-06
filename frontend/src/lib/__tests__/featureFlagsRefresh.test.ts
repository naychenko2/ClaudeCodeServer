import { afterEach, describe, expect, it, vi } from 'vitest';
import { createFlagsRefresher, getFlag, setAllFlags } from '../featureFlags';

describe('освежение фич-флагов', () => {
  afterEach(() => setAllFlags({}));

  it('после интервала перечитывает и меняет значение, в пределах интервала — нет', async () => {
    setAllFlags({ spheres: true });
    let t = 1_000_000;
    const fetchFlags = vi.fn().mockResolvedValue({ spheres: false });
    const refresh = createFlagsRefresher(fetchFlags, () => t, 45_000);

    expect(await refresh()).toBe(true);
    expect(getFlag('spheres')).toBe(false);

    setAllFlags({ spheres: true });
    t += 10_000;
    expect(await refresh()).toBe(false);
    expect(fetchFlags).toHaveBeenCalledTimes(1);
    expect(getFlag('spheres')).toBe(true);

    t += 40_000;
    expect(await refresh()).toBe(true);
    expect(fetchFlags).toHaveBeenCalledTimes(2);
    expect(getFlag('spheres')).toBe(false);
  });

  it('частичный, пустой ответ и сбой не затирают известные ключи', async () => {
    setAllFlags({ spheres: true, 'audio-editor': true });
    let t = 0;
    const answers: Array<() => Promise<Record<string, boolean> | undefined>> = [
      async () => ({ 'audio-editor': false }),
      async () => undefined,
      async () => { throw new Error('offline'); },
    ];
    const refresh = createFlagsRefresher(() => answers.shift()!(), () => t, 10);

    await refresh(true);
    expect(getFlag('spheres')).toBe(true);
    expect(getFlag('audio-editor')).toBe(false);
    t += 100; await refresh(); t += 100; await refresh();
    expect(getFlag('spheres')).toBe(true);
  });

  it('markFresh: после стартового запроса освежение запроса не делает, по истечении интервала — второй', async () => {
    let t = 0;
    const fetchFlags = vi.fn().mockResolvedValue({ spheres: true });
    const refresh = createFlagsRefresher(fetchFlags, () => t, 45_000);

    refresh.markFresh();
    expect(await refresh()).toBe(false);
    expect(fetchFlags).not.toHaveBeenCalled();

    t += 46_000;
    expect(await refresh()).toBe(true);
    expect(fetchFlags).toHaveBeenCalledTimes(1);
  });

  it('cancel: ответ, пришедший после отмены (выход, смена пользователя), не меняет стор', async () => {
    setAllFlags({ spheres: false });
    let resolve!: (v: Record<string, boolean>) => void;
    const pending = new Promise<Record<string, boolean>>(r => { resolve = r; });
    const refresh = createFlagsRefresher(() => pending, () => 0, 10);

    const run = refresh(true);
    refresh.cancel();
    resolve({ spheres: true });
    await run;

    expect(getFlag('spheres')).toBe(false);
  });
});
