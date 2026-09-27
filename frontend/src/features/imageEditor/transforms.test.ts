import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ImageTransformBase, ImageTransformRequest, ImageTransformResponse } from './api';
import { dropStepsFrom, pushStep, stepSaveSource, type History } from './editorInputs';
import { maskExportSize } from './marks';
import {
  chainTransform, fitCropRatio, formatBytes, initialCrop, moveCrop, presetDims, resizeCrop, sizeFormChanged, sizeFormOps,
  transformedSize, weightEstimator, WEIGHT_DEBOUNCE_MS, type SizeForm,
} from './transforms';

// Сервер-заглушка: отвечает, когда тест скажет, и помнит, с какой базой звали
function fakeServer() {
  const calls: { req: ImageTransformRequest; resolve: () => void; reject: (e: Error) => void }[] = [];
  let seq = 0;
  const run = (req: ImageTransformRequest) => new Promise<ImageTransformResponse>((resolve, reject) => {
    const stepId = `s${++seq}`;
    calls.push({ req, resolve: () => resolve({ stepId, width: 10, height: 10, bytes: 100 }), reject });
  });
  return { calls, run };
}

const tick = () => new Promise(r => setTimeout(r, 0));

describe('цепочка правок', () => {
  it('две операции подряд без ожидания обе фиксируются, вторая — поверх первой', async () => {
    const srv = fakeServer();
    const first = chainTransform(Promise.resolve({ path: 'a.png' }), [{ type: 'rotate', degrees: 90 }], null, srv.run);
    // Вторая ставится сразу, пока первая в полёте
    const second = chainTransform(first.ready, [{ type: 'flip', axis: 'horizontal' }], null, srv.run);
    await tick();
    // Сервер пишет шаги по очереди: вторая уходит, только когда у первой есть stepId
    expect(srv.calls).toHaveLength(1);
    expect(srv.calls[0].req.base).toEqual({ path: 'a.png' });
    srv.calls[0].resolve();
    await tick();
    expect(srv.calls).toHaveLength(2);
    expect(srv.calls[1].req.base).toEqual({ stepId: 's1' });
    expect(srv.calls[1].req.ops).toEqual([{ type: 'flip', axis: 'horizontal' }]);
    srv.calls[1].resolve();
    await expect(first.result).resolves.toMatchObject({ stepId: 's1' });
    await expect(second.result).resolves.toMatchObject({ stepId: 's2' });
  });

  it('упал шаг — падают и построенные поверх него, сервер их не зовёт', async () => {
    const srv = fakeServer();
    const first = chainTransform(Promise.resolve({ path: 'a.png' }), [{ type: 'rotate', degrees: 90 }], null, srv.run);
    const second = chainTransform(first.ready, [{ type: 'rotate', degrees: 90 }], null, srv.run);
    await tick();
    srv.calls[0].reject(new Error('картинка слишком большая'));
    await expect(first.result).rejects.toThrow('картинка слишком большая');
    await expect(second.result).rejects.toThrow('картинка слишком большая');
    expect(srv.calls).toHaveLength(1);
  });

  it('кодирование уходит вместе с операциями', async () => {
    const srv = fakeServer();
    chainTransform(Promise.resolve({ stepId: 'x' }), [], { format: 'webp', quality: 60 }, srv.run);
    await tick();
    expect(srv.calls[0].req).toEqual({ base: { stepId: 'x' }, ops: [], encode: { format: 'webp', quality: 60 } });
  });

  it('не записанный шаг убирается вместе с шагами после него', () => {
    let h: History = { steps: [{ id: 'o', original: true, title: 'Оригинал', src: 'o' }], cur: 0 };
    h = pushStep(h, { id: 't1', original: false, title: 'Поворот', src: 'o', pending: true });
    h = pushStep(h, { id: 't2', original: false, title: 'Отражение', src: 'o', pending: true });
    const back = dropStepsFrom(h, 't1');
    expect(back.steps.map(s => s.id)).toEqual(['o']);
    expect(back.cur).toBe(0);
  });

  it('сохранить можно записанный шаг или вариант, но не оригинал и не шаг в полёте', () => {
    const base: ImageTransformBase = { stepId: 's1' };
    expect(stepSaveSource({ id: 'a', original: false, title: '', src: '', base })).toEqual({ stepId: 's1' });
    expect(stepSaveSource({ id: 'a', original: false, title: '', src: '', base: { jobId: 'j', variant: 2 } })).toEqual({ jobId: 'j', variant: 2 });
    expect(stepSaveSource({ id: 'a', original: false, title: '', src: '', base, pending: true })).toBeNull();
    expect(stepSaveSource({ id: 'a', original: true, title: '', src: '', base: { path: 'a.png' } })).toBeNull();
  });
});

describe('размеры после правок', () => {
  it('поворот меняет стороны, обрезка берёт доли, ресайз с замком досчитывает сторону', () => {
    expect(transformedSize({ w: 1600, h: 1200 }, [{ type: 'rotate', degrees: 90 }])).toEqual({ w: 1200, h: 1600 });
    expect(transformedSize({ w: 1600, h: 1200 }, [{ type: 'rotate', degrees: 180 }])).toEqual({ w: 1600, h: 1200 });
    expect(transformedSize({ w: 1000, h: 500 }, [{ type: 'crop', rect: { x: 0.1, y: 0, width: 0.5, height: 0.5 } }])).toEqual({ w: 500, h: 250 });
    expect(transformedSize({ w: 1600, h: 1200 }, [{ type: 'resize', width: 800, lockAspect: true }])).toEqual({ w: 800, h: 600 });
    expect(transformedSize({ w: 1600, h: 1200 }, [{ type: 'resize', percent: 50 }])).toEqual({ w: 800, h: 600 });
  });

  it('пресет — по длинной стороне', () => {
    expect(presetDims({ w: 4000, h: 3000 }, 1920)).toEqual({ w: 1920, h: 1440 });
    expect(presetDims({ w: 3000, h: 4000 }, 1080)).toEqual({ w: 810, h: 1080 });
  });

  it('форма размера: без изменения размера ресайза нет, кодирование есть всегда', () => {
    const f: SizeForm = { unit: 'px', w: 1600, h: 1200, percent: 100, format: 'webp', quality: 70 };
    expect(sizeFormOps({ w: 1600, h: 1200 }, f)).toEqual({ ops: [], encode: { format: 'webp', quality: 70 }, target: { w: 1600, h: 1200 } });
    expect(sizeFormOps({ w: 1600, h: 1200 }, { ...f, unit: '%', percent: 50 }).ops).toEqual([{ type: 'resize', percent: 50 }]);
    expect(sizeFormOps({ w: 1600, h: 1200 }, { ...f, format: 'png' }).encode).toEqual({ format: 'png' });
  });

  it('форма без изменений применять нечего', () => {
    const f: SizeForm = { unit: 'px', w: 100, h: 50, percent: 100, format: 'jpeg', quality: 85 };
    expect(sizeFormChanged({ w: 100, h: 50 }, f, 'jpeg')).toBe(false);
    expect(sizeFormChanged({ w: 100, h: 50 }, { ...f, quality: 60 }, 'jpeg')).toBe(true);
    expect(sizeFormChanged({ w: 100, h: 50 }, f, 'png')).toBe(true);
    expect(sizeFormChanged({ w: 100, h: 50 }, { ...f, w: 50 }, 'jpeg')).toBe(true);
  });

  it('вес по-человечески', () => {
    expect(formatBytes(2.4 * 1024 * 1024)).toBe('2,4 МБ');
    expect(formatBytes(310 * 1024)).toBe('310 КБ');
    expect(formatBytes(12.6 * 1024 * 1024)).toBe('13 МБ');
    expect(formatBytes(900)).toBe('900 Б');
  });

  it('маска едет не больше 2048 по длинной стороне', () => {
    expect(maskExportSize(8064, 6048)).toEqual({ w: 2048, h: 1536, k: 2048 / 8064 });
    expect(maskExportSize(1600, 1200)).toEqual({ w: 1600, h: 1200, k: 1 });
  });
});

describe('рамка обрезки', () => {
  const size = { w: 1600, h: 900 };
  const px = (r: { width: number; height: number }) => (r.width * size.w) / (r.height * size.h);

  it('с пропорциями стартовая рамка — самая большая по центру и держит пропорции в пикселях', () => {
    const sq = initialCrop('1:1', size);
    expect(px(sq)).toBeCloseTo(1);
    expect(sq.height).toBeCloseTo(1);
    expect(sq.x).toBeCloseTo((1 - sq.width) / 2);
    expect(px(initialCrop('9:16', size))).toBeCloseTo(9 / 16);
    expect(initialCrop('16:9', size)).toEqual({ x: 0, y: 0, width: 1, height: 1 });
  });

  it('угол тянется, противоположный стоит; с пропорциями они сохраняются', () => {
    const start = { x: 0.2, y: 0.2, width: 0.4, height: 0.4 };
    const free = resizeCrop(start, 'se', 0.9, 0.7, 'free', size);
    expect(free).toMatchObject({ x: 0.2, y: 0.2 });
    expect(free.width).toBeCloseTo(0.7);
    expect(free.height).toBeCloseTo(0.5);
    const sq = resizeCrop(start, 'nw', 0.1, 0.05, '1:1', size);
    expect(px(sq)).toBeCloseTo(1);
    expect(sq.x + sq.width).toBeCloseTo(0.6);
    expect(sq.y + sq.height).toBeCloseTo(0.6);
  });

  it('рамка не выходит за картинку', () => {
    const r = moveCrop({ x: 0.5, y: 0.5, width: 0.4, height: 0.4 }, 0.5, -0.9);
    expect(r).toEqual({ x: 0.6, y: 0, width: 0.4, height: 0.4 });
    const big = resizeCrop({ x: 0.5, y: 0.5, width: 0.2, height: 0.2 }, 'se', 2, 2, '16:9', size);
    expect(big.x + big.width).toBeLessThanOrEqual(1 + 1e-9);
    expect(big.y + big.height).toBeLessThanOrEqual(1 + 1e-9);
    expect(px(big)).toBeCloseTo(16 / 9);
  });

  it('смена пропорций вписывает рамку в прежнюю', () => {
    const r = fitCropRatio({ x: 0.1, y: 0.1, width: 0.8, height: 0.8 }, '1:1', size);
    expect(px(r)).toBeCloseTo(1);
    expect(r.height).toBeCloseTo(0.8);
  });
});

describe('вес через dryRun', () => {
  afterEach(() => { vi.useRealTimers(); });

  it('правки подряд — один запрос по последней, через 300 мс тишины', async () => {
    vi.useFakeTimers();
    const run = vi.fn(async () => ({ stepId: null, width: 1, height: 1, bytes: 310 * 1024 }));
    const results: [string, number | null][] = [];
    const est = weightEstimator(run, (k, b) => results.push([k, b]));
    const base = Promise.resolve({ path: 'a.png' } as ImageTransformBase);
    for (let q = 40; q <= 90; q += 10) {
      est.request(base, `q${q}`, [], { format: 'webp', quality: q });
      await vi.advanceTimersByTimeAsync(100);
    }
    expect(run).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(WEIGHT_DEBOUNCE_MS);
    expect(run).toHaveBeenCalledTimes(1);
    expect(run).toHaveBeenCalledWith({ path: 'a.png' }, [], { format: 'webp', quality: 90 });
    expect(results).toEqual([['q90', 310 * 1024]]);
  });

  it('вес пересчитывается не чаще раза в 300 мс', async () => {
    vi.useFakeTimers();
    const at: number[] = [];
    const run = vi.fn(async () => { at.push(Date.now()); return { stepId: null, width: 1, height: 1, bytes: 1 }; });
    const est = weightEstimator(run, () => {});
    const base = Promise.resolve({ path: 'a.png' } as ImageTransformBase);
    // Ползунок качества: событие каждые 50 мс в течение 2 секунд, с паузами
    for (let i = 0; i < 40; i++) {
      est.request(base, `k${i}`, [], { format: 'jpeg', quality: 40 + i });
      await vi.advanceTimersByTimeAsync(i % 10 === 9 ? 400 : 50);
    }
    await vi.advanceTimersByTimeAsync(1000);
    expect(run.mock.calls.length).toBeGreaterThan(0);
    for (let i = 1; i < at.length; i++) expect(at[i] - at[i - 1]).toBeGreaterThanOrEqual(WEIGHT_DEBOUNCE_MS);
  });

  it('ответ на устаревший запрос не показывается', async () => {
    vi.useFakeTimers();
    let release: (() => void) | null = null;
    const run = vi.fn(() => new Promise<ImageTransformResponse>(r => { release = () => r({ stepId: null, width: 1, height: 1, bytes: 1 }); }));
    const results: string[] = [];
    const est = weightEstimator(run, k => results.push(k));
    const base = Promise.resolve({ path: 'a.png' } as ImageTransformBase);
    est.request(base, 'old', [], { format: 'png' });
    await vi.advanceTimersByTimeAsync(WEIGHT_DEBOUNCE_MS);
    est.request(base, 'new', [], { format: 'jpeg', quality: 50 });
    release!();
    await vi.advanceTimersByTimeAsync(0);
    expect(results).toEqual([]);
  });
});
