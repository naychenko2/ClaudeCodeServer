import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mockLocalModelStats } from './localModel.fixtures';
import {
  getLocalModelAdmin,
  getLocalModelConfigured,
  isLocalModelPanelVisible,
  setLocalModelAdmin,
  setLocalModelConfigured,
  shouldPollConfigured,
  startConfiguredPolling,
} from './useLocalModelStats';

describe('shouldPollConfigured: сторож опрашивает только при флаг && admin', () => {
  it('флаг + admin — опрос идёт', () => {
    expect(shouldPollConfigured(true, true)).toBe(true);
  });
  it('без флага — нет', () => {
    expect(shouldPollConfigured(false, true)).toBe(false);
  });
  it('не admin — нет', () => {
    expect(shouldPollConfigured(true, false)).toBe(false);
  });
});

describe('startConfiguredPolling: 60-с опрос configured вне панели', () => {
  beforeEach(() => { vi.useFakeTimers(); setLocalModelConfigured(null); });
  afterEach(() => { vi.useRealTimers(); setLocalModelConfigured(null); });

  const start = (fetch: () => Promise<ReturnType<typeof mockLocalModelStats>>) =>
    startConfiguredPolling(fetch, 60_000, (fn, ms) => setInterval(fn, ms), (id) => clearInterval(id as ReturnType<typeof setInterval>));

  it('первый запрос сразу, затем раз в 60 с; остановка гасит таймер', async () => {
    const fetch = vi.fn(async () => mockLocalModelStats());
    const stop = start(fetch);
    expect(fetch).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(fetch).toHaveBeenCalledTimes(2);
    stop();
    await vi.advanceTimersByTimeAsync(180_000);
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  // Главный сценарий ревью: стартовое null, панель ещё не смонтирована;
  // после ответа configured:true значок становится доступен
  it('configured: null → ответ true → панель/значок видны', async () => {
    const visible = () => isLocalModelPanelVisible({ flag: true, isAdmin: true, configured: getLocalModelConfigured() });
    expect(visible()).toBe(false);
    const stop = start(async () => ({ ...mockLocalModelStats(), configured: true }));
    await vi.advanceTimersByTimeAsync(0);
    expect(getLocalModelConfigured()).toBe(true);
    expect(visible()).toBe(true);
    stop();
  });

  it('сбой запроса не трогает прежнее значение', async () => {
    setLocalModelConfigured(true);
    const stop = start(async () => { throw new Error('сеть'); });
    await vi.advanceTimersByTimeAsync(60_000);
    expect(getLocalModelConfigured()).toBe(true);
    stop();
  });

  // 403 (не admin) / 404 (ручки нет) / сетевой сбой на ПЕРВОМ ответе: configured
  // остаётся null — значок и панель скрыты, падения и бесконечных повторов нет
  // (опрос — по фиксированному интервалу, не retry-шторм). Мутация «считать 404
  // успехом» (configured: true по 404) краснит тест.
  it.each([403, 404])('первый ответ — HTTP %i: configured остаётся null, значок скрыт', async status => {
    const err = new Error('HTTP') as Error & { status?: number };
    err.status = status;
    const stop = start(async () => { throw err; });
    await vi.advanceTimersByTimeAsync(0);
    expect(getLocalModelConfigured()).toBeNull();
    expect(isLocalModelPanelVisible({ flag: true, isAdmin: true, configured: getLocalModelConfigured() })).toBe(false);
    stop();
  });

  it('первый ответ — сетевой сбой: configured остаётся null, значок скрыт', async () => {
    const stop = start(async () => { throw new Error('нет сети'); });
    await vi.advanceTimersByTimeAsync(0);
    expect(getLocalModelConfigured()).toBeNull();
    expect(isLocalModelPanelVisible({ flag: true, isAdmin: true, configured: getLocalModelConfigured() })).toBe(false);
    stop();
  });
});

describe('стор роли: единый источник для панели и isAvailable', () => {
  afterEach(() => setLocalModelAdmin(false));
  it('по умолчанию не админ; сторож публикует auth.role', () => {
    expect(getLocalModelAdmin()).toBe(false);
    setLocalModelAdmin(true);
    expect(getLocalModelAdmin()).toBe(true);
  });
});
