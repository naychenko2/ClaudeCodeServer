import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mockLocalModelStats } from './localModel.fixtures';
import {
  createStatsPoller,
  getLocalModelConfigured,
  isLocalModelPanelVisible,
  setLocalModelConfigured,
  subscribeLocalModelConfigured,
} from './useLocalModelStats';

describe('isLocalModelPanelVisible: значок — только когда все три условия', () => {
  it('флаг + admin + configured — показывается', () => {
    expect(isLocalModelPanelVisible({ flag: true, isAdmin: true, configured: true })).toBe(true);
  });
  it('без флага — не показывается', () => {
    expect(isLocalModelPanelVisible({ flag: false, isAdmin: true, configured: true })).toBe(false);
  });
  it('не admin — не показывается', () => {
    expect(isLocalModelPanelVisible({ flag: true, isAdmin: false, configured: true })).toBe(false);
  });
  it('configured: false — локальных моделей нет, значка нет', () => {
    expect(isLocalModelPanelVisible({ flag: true, isAdmin: true, configured: false })).toBe(false);
  });
  it('configured: null — сервер ещё не ответил, значка нет', () => {
    expect(isLocalModelPanelVisible({ flag: true, isAdmin: true, configured: null })).toBe(false);
  });
});

describe('createStatsPoller: 2-с опрос', () => {
  const mock = mockLocalModelStats();

  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  const make = () => {
    const fetch = vi.fn(async () => mock);
    const p = createStatsPoller(fetch, 2000, (fn, ms) => setInterval(fn, ms), (id) => clearInterval(id));
    return { fetch, p };
  };

  it('закрытая панель — таймера нет, опроса нет', async () => {
    const { fetch, p } = make();
    p.setActive(false, true);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(fetch).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('открытая панель и видимая вкладка — опрос сразу и каждые 2 с', async () => {
    const { fetch, p } = make();
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    expect(fetch).toHaveBeenCalledTimes(1); // первый ответ сразу
    await vi.advanceTimersByTimeAsync(2000);
    expect(fetch).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(4000);
    expect(fetch).toHaveBeenCalledTimes(4);
    p.dispose();
    expect(vi.getTimerCount()).toBe(0); // dispose — без утечки таймера
  });

  it('вкладка не видима — опрос стоит; снова видима — продолжается', async () => {
    const { fetch, p } = make();
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    p.setActive(true, false); // скрыли вкладку
    await vi.advanceTimersByTimeAsync(10_000);
    expect(fetch).toHaveBeenCalledTimes(1);
    p.setActive(true, true); // вернули: немедленный fetch (данные могли устать) + таймер
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
    const p = createStatsPoller(fetch, 2000, (fn, ms) => setInterval(fn, ms), (id) => clearInterval(id));
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    expect(p.getSnapshot().data).toEqual(mock);
    fetch.mockImplementationOnce(async () => { throw new Error('offline'); });
    await vi.advanceTimersByTimeAsync(2000);
    expect(p.getSnapshot().data).toEqual(mock); // прежние данные
    expect(p.getSnapshot().loading).toBe(false);
  });

  it('getSnapshot: два подряд вызова без событий — один объект; после fetch — новый', async () => {
    const { p } = make();
    const s1 = p.getSnapshot();
    const s2 = p.getSnapshot();
    expect(s1).toBe(s2); // === : кэш, а не новый объект на каждый вызов
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    const s3 = p.getSnapshot();
    expect(s3).not.toBe(s1); // после завершения fetch — пересобранный снимок
  });
});

describe('стор configured: лёгкий опрос для значка', () => {
  it('null до первого ответа, значение обновляется и подписчики видят смену', () => {
    expect(getLocalModelConfigured()).toBeNull();
    const seen: (boolean | null)[] = [];
    const unsub = subscribeLocalModelConfigured(() => { seen.push(getLocalModelConfigured()); });
    setLocalModelConfigured(true);
    setLocalModelConfigured(true); // повторное значение — без дубля
    setLocalModelConfigured(false);
    unsub();
    setLocalModelConfigured(null);
    expect(seen).toEqual([true, false]);
    expect(getLocalModelConfigured()).toBeNull();
  });
});
