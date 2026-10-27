import { describe, expect, it, vi } from 'vitest';
import {
  createEnginesStore,
  ENGINE_STORAGE_KEY,
  pickDefaultEngine,
  resolveEngine,
} from './useLocalModelEngines';
import type { LocalModelEngine } from './api';

vi.mock('./api', () => ({ fetchLocalModelEngines: vi.fn() }));

const main = (configured: boolean): LocalModelEngine => ({ name: 'main', title: 'Основная', configured });
const bonsai = (configured: boolean): LocalModelEngine => ({ name: 'bonsai', title: 'Bonsai', configured });

// Подмена localStorage: Map под интерфейсом Storage (окружение node)
function fakeStorage(init: Record<string, string> = {}) {
  const m = new Map(Object.entries(init));
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    raw: m,
  };
}

describe('pickDefaultEngine: правило умолчания', () => {
  it('настроены оба — main', () => {
    expect(pickDefaultEngine([main(true), bonsai(true)])).toBe('main');
  });
  it('настроен только main — main', () => {
    expect(pickDefaultEngine([main(true), bonsai(false)])).toBe('main');
  });
  it('настроен только bonsai — bonsai', () => {
    expect(pickDefaultEngine([main(false), bonsai(true)])).toBe('bonsai');
  });
  it('список пуст или ничего не настроено — main', () => {
    expect(pickDefaultEngine([])).toBe('main');
    expect(pickDefaultEngine([main(false), bonsai(false)])).toBe('main');
  });
});

describe('resolveEngine: сохранённый выбор против умолчания', () => {
  it('сохранённый настроенный движок побеждает умолчание', () => {
    expect(resolveEngine([main(true), bonsai(true)], 'bonsai')).toBe('bonsai');
  });
  it('сохранённый, но ненастроенный движок — умолчание', () => {
    expect(resolveEngine([main(false), bonsai(true)], 'main')).toBe('bonsai');
  });
});

describe('createEnginesStore: выбор переживает пересоздание стора', () => {
  it('setEngine пишет в хранилище, новый стор поверх него читает выбор', () => {
    const storage = fakeStorage();
    const a = createEnginesStore(storage);
    a.setEngines([main(true), bonsai(true)]);
    expect(a.getSnapshot().engine).toBe('main');
    a.setEngine('bonsai');
    expect(storage.raw.get(ENGINE_STORAGE_KEY)).toBe('bonsai');

    const b = createEnginesStore(storage); // «перезагрузка страницы»
    b.setEngines([main(true), bonsai(true)]);
    expect(b.getSnapshot().engine).toBe('bonsai');
  });

  it('мусор в хранилище игнорируется — умолчание', () => {
    const store = createEnginesStore(fakeStorage({ [ENGINE_STORAGE_KEY]: 'gpt' }));
    store.setEngines([main(true), bonsai(true)]);
    expect(store.getSnapshot().engine).toBe('main');
  });

  it('подписчик видит смену; снимок стабилен без событий', () => {
    const store = createEnginesStore(fakeStorage());
    store.setEngines([main(true), bonsai(true)]);
    const s1 = store.getSnapshot();
    expect(store.getSnapshot()).toBe(s1);
    const fn = vi.fn();
    const unsub = store.subscribe(fn);
    store.setEngine('bonsai');
    store.setEngine('bonsai'); // повтор — без события
    expect(fn).toHaveBeenCalledTimes(1);
    unsub();
    store.setEngine('main');
    expect(fn).toHaveBeenCalledTimes(1);
  });
});
