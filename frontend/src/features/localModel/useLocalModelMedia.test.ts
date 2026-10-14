// Тяжёлый 2-с опрос вкладки «Медиа»: чистый контроллер (createMediaPoller) —
// тест без React/DOM. Паттерн — как useLocalModelStats.test.ts.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mockLocalModelMedia } from './localModel.fixtures';
import { createMediaPoller } from './useLocalModelMedia';

describe('createMediaPoller: 2-с опрос вкладки «Медиа»', () => {
  const mock = mockLocalModelMedia('run');

  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  const make = () => {
    const fetch = vi.fn(async () => mock);
    const p = createMediaPoller(fetch, 2000, (fn, ms) => setInterval(fn, ms), (id) => clearInterval(id));
    return { fetch, p };
  };

  it('закрытая вкладка — таймера нет, опроса нет', async () => {
    const { fetch, p } = make();
    p.setActive(false, true);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(fetch).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('открытая вкладка и видимый браузер — опрос сразу и каждые 2 с', async () => {
    const { fetch, p } = make();
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    expect(fetch).toHaveBeenCalledTimes(1); // первый снимок сразу
    await vi.advanceTimersByTimeAsync(2000);
    expect(fetch).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(4000);
    expect(fetch).toHaveBeenCalledTimes(4);
    p.dispose();
    expect(vi.getTimerCount()).toBe(0); // dispose — без утечки таймера
  });

  it('вкладка браузера скрыта — опрос стоит; снова видима — продолжается', async () => {
    const { fetch, p } = make();
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    p.setActive(true, false); // скрыли вкладку браузера
    await vi.advanceTimersByTimeAsync(10_000);
    expect(fetch).toHaveBeenCalledTimes(1);
    p.setActive(true, true); // вернули: немедленный снимок + таймер
    await vi.advanceTimersByTimeAsync(0);
    expect(fetch).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(2000);
    expect(fetch).toHaveBeenCalledTimes(3);
  });

  it('снапшот: данные после первого ответа, loading гаснет', async () => {
    const { p } = make();
    p.setActive(true, true);
    expect(p.getSnapshot().data).toBeNull();
    await vi.advanceTimersByTimeAsync(0);
    const snap = p.getSnapshot();
    expect(snap.data).toEqual(mock);
    expect(snap.loading).toBe(false);
  });

  it('сбой опроса не роняет: прежние данные, loading гаснет', async () => {
    const fetch = vi.fn(async () => mock);
    const p = createMediaPoller(fetch, 2000, (fn, ms) => setInterval(fn, ms), (id) => clearInterval(id));
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    expect(p.getSnapshot().data).toEqual(mock);
    fetch.mockImplementationOnce(async () => { throw new Error('offline'); });
    await vi.advanceTimersByTimeAsync(2000);
    expect(p.getSnapshot().data).toEqual(mock); // прежние данные
    expect(p.getSnapshot().loading).toBe(false);
  });
});
