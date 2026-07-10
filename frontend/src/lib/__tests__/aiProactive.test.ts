import { describe, it, expect, beforeEach, vi } from 'vitest';

// proactive тянет api — мокаем (computeSuggestion тут не проверяем, только дозировку)
vi.mock('../api', () => ({ api: { notes: { get: vi.fn() }, tasks: { get: vi.fn() } } }));

import {
  canShow, markShown, markDismissed, isProactiveEnabled, setProactiveEnabled,
} from '../ai/proactive';

// Минимальный localStorage для окружения node
beforeEach(() => {
  const store: Record<string, string> = {};
  (globalThis as unknown as { localStorage: Storage }).localStorage = {
    getItem: (k: string) => (k in store ? store[k] : null),
    setItem: (k: string, v: string) => { store[k] = String(v); },
    removeItem: (k: string) => { delete store[k]; },
    clear: () => { for (const k in store) delete store[k]; },
    key: () => null,
    length: 0,
  } as Storage;
});

describe('push-дозировка', () => {
  it('по умолчанию проактивность включена', () => {
    expect(isProactiveEnabled()).toBe(true);
  });

  it('тумблер выключает подсказки — canShow даёт false', () => {
    setProactiveEnabled(false);
    expect(isProactiveEnabled()).toBe(false);
    expect(canShow('k')).toBe(false);
    setProactiveEnabled(true);
    expect(canShow('k')).toBe(true);
  });

  it('дневной лимит — не более 3 показов', () => {
    expect(canShow('a')).toBe(true);
    markShown(); markShown(); markShown();
    expect(canShow('a')).toBe(false); // лимит исчерпан
  });

  it('дедуп — отклонённая подсказка не повторяется, другие доступны', () => {
    markDismissed('k');
    expect(canShow('k')).toBe(false);
    expect(canShow('other')).toBe(true);
  });
});
