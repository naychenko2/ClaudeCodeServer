import { describe, it, expect } from 'vitest';
import { createSemaphore, withImageLimit, IMAGE_CONCURRENCY } from '../concurrency';

const delay = (ms: number) => new Promise<void>(r => setTimeout(r, ms));

describe('createSemaphore', () => {
  it('ограничивает параллельность заданным лимитом', async () => {
    let active = 0;
    let peak = 0;
    const run = createSemaphore(3);
    const fn = async () => {
      active++;
      peak = Math.max(peak, active);
      await delay(20);
      active--;
    };
    await Promise.all(Array.from({ length: 10 }, () => run(fn)));
    expect(peak).toBeLessThanOrEqual(3);
    expect(peak).toBe(3); // при 10 задачах и лимите 3 пик достигается
  });

  it('освобождает слот при throw — следующая задача из очереди стартует', async () => {
    let active = 0;
    let peak = 0;
    const run = createSemaphore(2);
    // Падает только задача с i === 0 — детерминированно (без гонки на общем счётчике вызовов).
    const makeFn = (i: number) => async () => {
      active++;
      peak = Math.max(peak, active);
      await delay(10);
      active--;
      if (i === 0) throw new Error('boom');
    };
    const results = await Promise.allSettled(Array.from({ length: 4 }, (_, i) => run(makeFn(i))));
    expect(results.filter(r => r.status === 'rejected')).toHaveLength(1);
    expect(peak).toBeLessThanOrEqual(2);
    expect(results).toHaveLength(4); // все 4 завершились — слот не завис, очередь двинулась
  });

  it('освобождает слот при reject промиса', async () => {
    const run = createSemaphore(1);
    let started = 0;
    const makeFn = (i: number) => async () => {
      started++;
      await delay(5);
      if (i === 0) return Promise.reject(new Error('reject'));
    };
    await Promise.allSettled([run(makeFn(0)), run(makeFn(1)), run(makeFn(2))]);
    expect(started).toBe(3); // reject не заблокировал очередь
  });

  it('быстрая серия из 10 — все резолвятся, очередь не дохнет', async () => {
    const run = createSemaphore(3);
    let done = 0;
    await Promise.all(Array.from({ length: 10 }, () => run(async () => { await delay(5); done++; })));
    expect(done).toBe(10);
  });
});

describe('withImageLimit (module-level синглтон)', () => {
  it('делит один лимит между всеми вызовами — глобальность', async () => {
    let active = 0;
    let peak = 0;
    const track = async () => {
      active++;
      peak = Math.max(peak, active);
      await delay(20);
      active--;
    };
    // Два «клиента» зовут withImageLimit параллельно — должны поделить общий IMAGE_CONCURRENCY,
    // а не получить по лимиту каждый (иначе пул снова забит).
    await Promise.all([
      ...Array.from({ length: 5 }, () => withImageLimit(track)),
      ...Array.from({ length: 5 }, () => withImageLimit(track)),
    ]);
    expect(peak).toBeLessThanOrEqual(IMAGE_CONCURRENCY);
  });
});
