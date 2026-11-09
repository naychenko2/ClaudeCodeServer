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
const gemma = (configured: boolean): LocalModelEngine => ({ name: 'gemma', title: 'Gemma 4', configured });

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
    expect(pickDefaultEngine([main(true), gemma(true)])).toBe('main');
  });
  it('настроен только main — main', () => {
    expect(pickDefaultEngine([main(true), gemma(false)])).toBe('main');
  });
  it('настроен только gemma — gemma', () => {
    expect(pickDefaultEngine([main(false), gemma(true)])).toBe('gemma');
  });
  it('список пуст или ничего не настроено — main', () => {
    expect(pickDefaultEngine([])).toBe('main');
    expect(pickDefaultEngine([main(false), gemma(false)])).toBe('main');
  });
});

describe('resolveEngine: сохранённый выбор против умолчания', () => {
  it('сохранённый настроенный движок побеждает умолчание', () => {
    expect(resolveEngine([main(true), gemma(true)], 'gemma')).toBe('gemma');
  });
  it('сохранённый, но ненастроенный движок — умолчание', () => {
    expect(resolveEngine([main(false), gemma(true)], 'main')).toBe('gemma');
  });
  // Ревью Ф1 (MAJOR 3): пустой список — «ещё не загружен», а не «ничего не
  // настроено»: сохранённый выбор остаётся, пока сервер не ответил
  it('список не загружен (пустой) — сохранённый выбор, не умолчание', () => {
    expect(resolveEngine([], 'gemma')).toBe('gemma');
    expect(resolveEngine([], 'main')).toBe('main');
  });
  it('список не загружен и выбора нет — main', () => {
    expect(resolveEngine([], null)).toBe('main');
  });
});

describe('createEnginesStore: выбор переживает пересоздание стора', () => {
  it('setEngine пишет в хранилище, новый стор поверх него читает выбор', () => {
    const storage = fakeStorage();
    const a = createEnginesStore(storage);
    a.setEngines([main(true), gemma(true)]);
    expect(a.getSnapshot().engine).toBe('main');
    a.setEngine('gemma');
    expect(storage.raw.get(ENGINE_STORAGE_KEY)).toBe('gemma');

    const b = createEnginesStore(storage); // «перезагрузка страницы»
    b.setEngines([main(true), gemma(true)]);
    expect(b.getSnapshot().engine).toBe('gemma');
  });

  // Ревью Ф1 (MAJOR 3): до первого ответа сервера (список пуст) стор отдаёт
  // сохранённый выбор, а не умолчание main — иначе панель на мгновение показала
  // бы цифры другого стенда
  it('стор поверх хранилища с «gemma» до загрузки списка отдаёт сохранённый выбор', () => {
    const store = createEnginesStore(fakeStorage({ [ENGINE_STORAGE_KEY]: 'gemma' }));
    expect(store.getSnapshot().engine).toBe('gemma'); // setEngines ещё не было
    store.setEngines([main(true), gemma(false)]); // список пришёл: gemma не настроен
    expect(store.getSnapshot().engine).toBe('main'); // → умолчание
  });

  it('мусор в хранилище игнорируется — умолчание', () => {
    const store = createEnginesStore(fakeStorage({ [ENGINE_STORAGE_KEY]: 'gpt' }));
    store.setEngines([main(true), gemma(true)]);
    expect(store.getSnapshot().engine).toBe('main');
  });

  it('подписчик видит смену; снимок стабилен без событий', () => {
    const store = createEnginesStore(fakeStorage());
    store.setEngines([main(true), gemma(true)]);
    const s1 = store.getSnapshot();
    expect(store.getSnapshot()).toBe(s1);
    const fn = vi.fn();
    const unsub = store.subscribe(fn);
    store.setEngine('gemma');
    store.setEngine('gemma'); // повтор — без события
    expect(fn).toHaveBeenCalledTimes(1);
    unsub();
    store.setEngine('main');
    expect(fn).toHaveBeenCalledTimes(1);
  });
});
