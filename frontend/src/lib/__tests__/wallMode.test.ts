// Тесты навигационной памяти режима «Стены»: флаг активности и точка возврата
// в зону проектов (куда вернуть клик «Проекты» — на стену, в воркспейс или список).
import { describe, it, expect, beforeEach, vi } from 'vitest';
import {
  isWallActive, setWallActive, getWallReturn, setWallReturn, type WallReturn,
} from '../wallMode';

// Стаб localStorage для node-окружения vitest (jsdom в проекте не подключён)
function fakeStorage(): Storage {
  const m = new Map<string, string>();
  return {
    get length() { return m.size; },
    key: (i: number) => [...m.keys()][i] ?? null,
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, String(v)); },
    removeItem: (k: string) => { m.delete(k); },
    clear: () => { m.clear(); },
  } as Storage;
}

beforeEach(() => {
  vi.stubGlobal('localStorage', fakeStorage());
});

describe('флаг режима стены', () => {
  it('выключен по умолчанию и включается/выключается', () => {
    expect(isWallActive()).toBe(false);
    setWallActive(true);
    expect(isWallActive()).toBe(true);
    setWallActive(false);
    expect(isWallActive()).toBe(false);
  });

  it('мусорное значение ключа читается как «выключен»', () => {
    localStorage.setItem('cc_wall_active', 'да');
    expect(isWallActive()).toBe(false);
  });
});

describe('точка возврата в зону проектов', () => {
  it('не задана по умолчанию и записывается/читается', () => {
    expect(getWallReturn()).toBeNull();
    for (const v of ['wall', 'workspace', 'list'] as WallReturn[]) {
      setWallReturn(v);
      expect(getWallReturn()).toBe(v);
    }
  });

  it('мусорное значение ключа читается как «не задана»', () => {
    localStorage.setItem('cc_wall_return', 'moon');
    expect(getWallReturn()).toBeNull();
  });

  it('явный выход из режима стены стирает и точку возврата', () => {
    setWallReturn('wall');
    setWallActive(true);
    expect(getWallReturn()).toBe('wall');
    setWallActive(false);
    expect(getWallReturn()).toBeNull();
  });
});
