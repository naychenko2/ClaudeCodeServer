import { describe, it, expect, vi } from 'vitest';
import {
  handleStorageCall,
  removePluginStorage,
  collectStorageGarbage,
  KEY_MAX_LEN,
  VALUE_MAX_BYTES,
  KEYS_MAX_PER_PLUGIN,
  PLUGIN_BYTES_MAX,
  ORIGIN_BYTES_MAX,
} from './storage';

// Map-обёртка в форме Storage со шпионом на clear(): модуль никогда не зовёт clear().
class MapStore {
  m = new Map<string, string>();
  clearCalls = 0;
  getItem(key: string): string | null {
    return this.m.has(key) ? this.m.get(key)! : null;
  }
  setItem(key: string, value: string): void {
    this.m.set(key, value);
  }
  removeItem(key: string): void {
    this.m.delete(key);
  }
  key(index: number): string | null {
    const k = [...this.m.keys()][index];
    return k ?? null;
  }
  get length(): number {
    return this.m.size;
  }
  clear(): void {
    this.clearCalls++;
    this.m.clear();
  }
}

const OWNER = 'owner-1';
const PLUGIN = 'pub/slug';
const keyOf = (key: string) => `ccs.plugin.${OWNER}.${PLUGIN}.${key}`;

function storeWith(entries: Record<string, string>): MapStore {
  const s = new MapStore();
  for (const [k, v] of Object.entries(entries)) s.m.set(k, v);
  return s;
}

function call(
  store: MapStore,
  op: string,
  args: Record<string, unknown> = {},
  granted: string[] = ['storage'],
  ownerId: string = OWNER,
  pluginId: string = PLUGIN,
) {
  return handleStorageCall({ op, args, granted, ownerId, pluginId, store });
}

// --- Инвариант 2: право по granted состояния хоста ---

describe('право (инв. 2)', () => {
  it('без права storage → denied с текстом «Нет права: storage», ни одной записи', () => {
    const store = new MapStore();
    const r = call(store, 'get', { key: 'a' }, []);
    expect(r).toEqual({ ok: false, error: { code: 'denied', message: 'Нет права: storage' } });
    expect(store.length).toBe(0);
  });

  it('granted в args панели не влияет: право смотрим только по granted хоста', () => {
    const store = new MapStore();
    const r = call(store, 'set', { key: 'a', value: 1, granted: ['storage'] }, []);
    expect(r.ok).toBe(false);
    expect(r.error.code).toBe('denied');
  });

  it('префикс берётся из host-параметров, а не из полей сообщения панели', () => {
    const store = new MapStore();
    const r = call(store, 'set', { key: 'a', value: 1, pluginId: 'evil/other', ownerId: 'other-owner' });
    expect(r.ok).toBe(true);
    expect(store.getItem(keyOf('a'))).toBe('1');
    expect(store.length).toBe(1);
  });
});

// --- Инвариант 3: ключ и префикс ---

describe('ключ и префикс (инв. 3)', () => {
  it('пустой ключ → invalid, ни одной записи', () => {
    const store = new MapStore();
    expect(call(store, 'set', { key: '', value: 1 })).toEqual({ ok: false, error: { code: 'invalid', message: 'Неверный ключ' } });
    expect(store.length).toBe(0);
  });

  it('ключ с «/» → invalid', () => {
    const store = new MapStore();
    expect(call(store, 'set', { key: 'a/b', value: 1 }).error.code).toBe('invalid');
    expect(store.length).toBe(0);
  });

  it('ключ с «..» → invalid', () => {
    const store = new MapStore();
    expect(call(store, 'set', { key: 'a..b', value: 1 }).error.code).toBe('invalid');
    expect(store.length).toBe(0);
  });

  it('ключ длиннее 64 → invalid', () => {
    const store = new MapStore();
    expect(call(store, 'set', { key: 'a'.repeat(KEY_MAX_LEN + 1), value: 1 }).error.code).toBe('invalid');
    expect(store.length).toBe(0);
  });

  it('ключ не из алфавита (пробел, кириллица) и не строка → invalid', () => {
    const store = new MapStore();
    expect(call(store, 'set', { key: 'ключ', value: 1 }).error.code).toBe('invalid');
    expect(call(store, 'set', { key: 'a b', value: 1 }).error.code).toBe('invalid');
    expect(call(store, 'get', { key: 5 }).error.code).toBe('invalid');
    expect(store.length).toBe(0);
  });

  it('ключ из алфавита до 64 принимается', () => {
    const store = new MapStore();
    const r = call(store, 'set', { key: 'a.b_c-1', value: 1 });
    expect(r.ok).toBe(true);
    expect(store.getItem(keyOf('a.b_c-1'))).toBe('1');
  });

  it('отсутствующий ключ — НЕ ошибка: get → exists:false', () => {
    const store = new MapStore();
    expect(call(store, 'get', { key: 'nope' })).toEqual({ ok: true, data: { exists: false } });
  });
});

// --- Инвариант 4: keys видит только свой префикс ---

describe('изоляция ключей (инв. 4)', () => {
  it('keys отдаёт только свой префикс и без префикса', () => {
    const store = storeWith({
      [keyOf('k1')]: '1',
      [keyOf('k2')]: '22',
      'ccs.plugin.owner-1.other/slug.k1': '333',
      'ccs.plugin.owner-2.pub/slug.k1': '4444',
      'cc_token': 'token',
    });
    const r = call(store, 'keys');
    expect(r.ok).toBe(true);
    expect(r.data.keys).toEqual(['k1', 'k2']);
    // bytes — свои ключи: (len('...k1')+1) + (len('...k2')+2)
    expect(r.data.bytes).toBe(keyOf('k1').length + 1 + keyOf('k2').length + 2);
  });
});

// --- Инвариант 5: нет ownerId ---

describe('нет владельца (инв. 5)', () => {
  it('пустой ownerId → unavailable для любой операции, ноль записей', () => {
    const store = new MapStore();
    for (const op of ['get', 'set', 'remove', 'keys']) {
      const r = call(store, op, { key: 'a', value: 1 }, ['storage'], '');
      expect(r.ok).toBe(false);
      expect(r.error.code).toBe('unavailable');
    }
    expect(store.length).toBe(0);
  });
});

// --- Инвариант 6: пределы, «всё или ничего» ---

describe('пределы (инв. 6)', () => {
  it('значение ровно 8 КиБ проходит, 8193 — limit, store не изменён', () => {
    const store = new MapStore();
    // JSON.stringify добавляет кавычки: 8190 символов → 8192 code units.
    const ok = call(store, 'set', { key: 'a', value: 'a'.repeat(VALUE_MAX_BYTES - 2) });
    expect(ok.ok).toBe(true);
    const r = call(store, 'set', { key: 'b', value: 'a'.repeat(VALUE_MAX_BYTES - 1) });
    expect(r.ok).toBe(false);
    expect(r.error.code).toBe('limit');
    expect(store.length).toBe(1);
    expect(store.getItem(keyOf('b'))).toBe(null);
  });

  it('100 ключей на плагин — предел; 101-й → limit, store не изменён', () => {
    const store = new MapStore();
    for (let i = 0; i < KEYS_MAX_PER_PLUGIN; i++) {
      expect(call(store, 'set', { key: `k${i}`, value: 1 }).ok).toBe(true);
    }
    const r = call(store, 'set', { key: 'k100', value: 1 });
    expect(r.ok).toBe(false);
    expect(r.error.code).toBe('limit');
    expect(store.length).toBe(KEYS_MAX_PER_PLUGIN);
  });

  it('перезапись существующего ключа не увеличивает счётчик ключей', () => {
    const store = new MapStore();
    for (let i = 0; i < KEYS_MAX_PER_PLUGIN; i++) {
      expect(call(store, 'set', { key: `k${i}`, value: 1 }).ok).toBe(true);
    }
    expect(call(store, 'set', { key: 'k0', value: 2 }).ok).toBe(true);
  });

  it('128 КиБ на плагин считаются по keyBytes (ключ + значение): объём ≤ предела, +1 байт → limit', () => {
    const store = new MapStore();
    // Ключи одной длины ('k00'..'k15'): 16 записей, значение подбираем так, чтобы
    // ключ+значение в сумме дали ровно предел. Инвариант: фактический объём по
    // storage.keys → bytes никогда не превышает PLUGIN_BYTES_MAX.
    const n = 16;
    const keyLen = keyOf('k00').length;
    const rawLen = (PLUGIN_BYTES_MAX - n * keyLen) / n;
    const value = 'a'.repeat(rawLen - 2); // JSON — с кавычками
    for (let i = 0; i < n; i++) {
      expect(call(store, 'set', { key: `k${String(i).padStart(2, '0')}`, value }).ok).toBe(true);
    }
    const bytes = (call(store, 'keys').data as { bytes: number }).bytes;
    expect(bytes).toBe(PLUGIN_BYTES_MAX);
    // Любая новая запись (даже ключ 1 символ + значение 1 символ) превысила бы предел.
    const r2 = call(store, 'set', { key: 'x', value: 1 });
    expect(r2.ok).toBe(false);
    expect(r2.error.code).toBe('limit');
    expect(store.getItem(keyOf('x'))).toBe(null);
    // Перезапись на то же значение — объём не растёт, проходит; на большее — limit.
    expect(call(store, 'set', { key: 'k00', value }).ok).toBe(true);
    expect(call(store, 'set', { key: 'k00', value: value + 'b' }).ok).toBe(false);
  });

  it('последняя запись, выходящая за 128 КиБ ровно на 1 байт (из-за длины ключа), отвергается', () => {
    const store = new MapStore();
    const n = 16;
    const keyLen = keyOf('k00').length;
    const rawLen = (PLUGIN_BYTES_MAX - n * keyLen) / n;
    const value = 'a'.repeat(rawLen - 2);
    for (let i = 0; i < n - 1; i++) {
      expect(call(store, 'set', { key: `k${String(i).padStart(2, '0')}`, value }).ok).toBe(true);
    }
    // Значение на 1 символ длиннее: по keyBytes суммарно PLUGIN_BYTES_MAX + 1.
    const r = call(store, 'set', { key: 'k15', value: value + 'b' });
    expect(r.ok).toBe(false);
    expect(r.error.code).toBe('limit');
    expect((call(store, 'keys').data as { bytes: number }).bytes).toBeLessThanOrEqual(PLUGIN_BYTES_MAX);
  });

  it('набивка максимальными записями: объём по keys → bytes не превышает 128 КиБ ни на одном шаге', () => {
    const store = new MapStore();
    const value = 'a'.repeat(VALUE_MAX_BYTES - 2);
    for (let i = 0; i < 30; i++) {
      call(store, 'set', { key: `key-${i}`, value });
      expect((call(store, 'keys').data as { bytes: number }).bytes).toBeLessThanOrEqual(PLUGIN_BYTES_MAX);
    }
    // Предел достигнут: следующая такая запись отвергнута.
    expect(call(store, 'set', { key: 'key-last', value }).ok).toBe(false);
  });

  it('1 МиБ на весь ccs.plugin.* в origin: превышение → limit, store не изменён', () => {
    const store = new MapStore();
    const otherKey = 'ccs.plugin.owner-2.pub/slug.big';
    // Довести весь префикс origin ровно до 1 МиБ другим владельцем.
    store.m.set(otherKey, 'a'.repeat(ORIGIN_BYTES_MAX - otherKey.length));
    const r = call(store, 'set', { key: 'a', value: 'x' });
    expect(r.ok).toBe(false);
    expect(r.error.code).toBe('limit');
    expect(store.getItem(keyOf('a'))).toBe(null);
  });

  it('undefined/NaN/Infinity → invalid, молча не превращаем в null', () => {
    const store = new MapStore();
    for (const value of [undefined, NaN, Infinity, -Infinity]) {
      const r = call(store, 'set', { key: 'a', value });
      expect(r.ok).toBe(false);
      expect(r.error.code).toBe('invalid');
    }
    expect(store.length).toBe(0);
  });

  it('любой JSON проходит: числа, объекты, строки, null', () => {
    const store = new MapStore();
    for (const value of [1, 'текст', { a: [1, 2] }, null, true]) {
      expect(call(store, 'set', { key: 'a', value }).ok).toBe(true);
    }
    const r = call(store, 'get', { key: 'a' });
    expect(r.ok).toBe(true);
    expect(r.data.exists).toBe(true);
    expect(r.data.value).toEqual(true);
  });
});

// --- Несериализуемое значение (F1-д) ---

describe('несериализуемое значение (F1-д)', () => {
  // handleStorageCall синхронный: если бросит — тест падает с исключением,
  // а значит панель ответа не получает. Зелёный тест = ответ есть, промис не висит.
  it('циклический объект → invalid, без исключения, store не изменён', () => {
    const store = new MapStore();
    const cycle: Record<string, unknown> = {};
    cycle.self = cycle;
    const r = call(store, 'set', { key: 'a', value: cycle });
    expect(r.ok).toBe(false);
    expect(r.error.code).toBe('invalid');
    expect(r.error.message).toBe('Значение не выражается в JSON');
    expect(store.length).toBe(0);
  });

  it('BigInt → invalid, без исключения, store не изменён', () => {
    const store = new MapStore();
    const r = call(store, 'set', { key: 'a', value: BigInt(10) });
    expect(r.ok).toBe(false);
    expect(r.error.code).toBe('invalid');
    expect(store.length).toBe(0);
  });

  it('значение-функция → invalid, не TypeError на raw.length', () => {
    const store = new MapStore();
    const r = call(store, 'set', { key: 'a', value: () => 1 });
    expect(r.ok).toBe(false);
    expect(r.error.code).toBe('invalid');
    expect(store.length).toBe(0);
  });

  it('Symbol → invalid, без исключения', () => {
    const store = new MapStore();
    const r = call(store, 'set', { key: 'a', value: Symbol('s') });
    expect(r.ok).toBe(false);
    expect(r.error.code).toBe('invalid');
    expect(store.length).toBe(0);
  });

  it('get не-JSON значения → invalid, а не сырая строка (минор 3)', () => {
    const store = storeWith({ [keyOf('a')]: 'не JSON {' });
    const r = call(store, 'get', { key: 'a' });
    expect(r.ok).toBe(false);
    expect(r.error.code).toBe('invalid');
    expect(r.error.message).toBe('Значение повреждено: не JSON');
  });
});

// --- Инвариант 7: ошибки браузера ---

describe('ошибки хранилища (инв. 7)', () => {
  it('QuotaExceededError от store → limit', () => {
    const store = new MapStore();
    store.setItem = () => {
      const e = new Error('quota');
      e.name = 'QuotaExceededError';
      throw e;
    };
    const r = call(store, 'set', { key: 'a', value: 1 });
    expect(r.ok).toBe(false);
    expect(r.error.code).toBe('limit');
  });

  it('SecurityError от store.setItem → unavailable', () => {
    const store = new MapStore();
    store.setItem = () => {
      throw Object.assign(new Error('sandboxed'), { name: 'SecurityError' });
    };
    const r = call(store, 'set', { key: 'a', value: 1 });
    expect(r.ok).toBe(false);
    expect(r.error.code).toBe('unavailable');
  });

  // Хранилище отключено целиком: бросает любое обращение. Панель обязана получить
  // result unavailable на КАЖДОЙ операции, store при этом не меняется.
  const sec = () => Object.assign(new Error('sandboxed'), { name: 'SecurityError' });
  for (const [op, args] of [
    ['get', { key: 'a' }],
    ['set', { key: 'a', value: 1 }],
    ['remove', { key: 'a' }],
    ['keys', {}],
  ] as const) {
    it(`SecurityError на любом обращении к store → ${op}: unavailable, store не тронут`, () => {
      const store = new MapStore();
      store.m.set(keyOf('keep'), '1');
      const writes: string[] = [];
      store.getItem = () => { throw sec(); };
      store.removeItem = (k: string) => { writes.push(k); throw sec(); };
      store.setItem = (k: string) => { writes.push(k); throw sec(); };
      Object.defineProperty(store, 'length', { get() { throw sec(); } });
      const r = call(store, op, { ...args });
      expect(r.ok).toBe(false);
      expect(r.error.code).toBe('unavailable');
      expect(store.m.get(keyOf('keep'))).toBe('1');
    });
  }

  // F-д4: частичное стирание не молчит.
  it('removePluginStorage: SecurityError посреди цикла — счётчик честный, предупреждение с меткой', () => {
    const store = new MapStore();
    store.m.set(keyOf('a'), '1');
    store.m.set(keyOf('b'), '2');
    const orig = store.removeItem.bind(store);
    let n = 0;
    store.removeItem = (k: string) => { if (n++ >= 1) throw sec(); orig(k); };
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const removed = removePluginStorage({ ownerId: OWNER, pluginId: PLUGIN, store });
    expect(removed).toBe(1);
    expect(warn.mock.calls[0][0]).toContain('[plugin storage]');
    warn.mockRestore();
  });

  it('не-SecurityError пробрасывается как раньше', () => {
    const store = new MapStore();
    store.getItem = () => { throw new Error('boom'); };
    expect(() => call(store, 'get', { key: 'a' })).toThrow('boom');
  });
});

// --- Инвариант 8: отзыв права не трогает данные ---

describe('отзыв права (инв. 8)', () => {
  it('данные на месте: get без права → denied; повторная выдача возвращает те же значения', () => {
    const store = new MapStore();
    expect(call(store, 'set', { key: 'a', value: 'данные' }).ok).toBe(true);

    const denied = call(store, 'get', { key: 'a' }, []);
    expect(denied.ok).toBe(false);
    expect(denied.error.code).toBe('denied');
    expect(store.getItem(keyOf('a'))).toBe('"данные"');

    const again = call(store, 'get', { key: 'a' }, ['storage']);
    expect(again.ok).toBe(true);
    expect(again.data.value).toBe('данные');
  });
});

// --- Инвариант 9: removePluginStorage ---

describe('removePluginStorage (инв. 9)', () => {
  it('стирает ровно свой префикс; чужие плагины, чужие владельцы и cc_token целы; clear() не зовётся', () => {
    const store = storeWith({
      [keyOf('k1')]: '1',
      [keyOf('k2')]: '2',
      'ccs.plugin.owner-1.other/slug.k1': '3',
      'ccs.plugin.owner-2.pub/slug.k1': '4',
      'cc_token': 'token',
    });
    const removed = removePluginStorage({ ownerId: OWNER, pluginId: PLUGIN, store });
    expect(removed).toBe(2);
    expect(store.getItem(keyOf('k1'))).toBe(null);
    expect(store.getItem('ccs.plugin.owner-1.other/slug.k1')).toBe('3');
    expect(store.getItem('ccs.plugin.owner-2.pub/slug.k1')).toBe('4');
    expect(store.getItem('cc_token')).toBe('token');
    expect(store.clearCalls).toBe(0);
  });
});

// --- Инвариант 10: collectStorageGarbage ---

describe('collectStorageGarbage (инв. 10)', () => {
  it('удаляет ключи текущего владельца не-keep-плагинов; keep и чужой owner целы', () => {
    const store = storeWith({
      'ccs.plugin.owner-1.dead/slug.k1': '1',
      'ccs.plugin.owner-1.live/slug.k1': '2',
      'ccs.plugin.owner-2.dead/slug.k1': '3',
      'cc_token': 'token',
    });
    const removed = collectStorageGarbage({ ownerId: OWNER, keepPluginIds: ['live/slug'], store });
    expect(removed).toBe(1);
    expect(store.getItem('ccs.plugin.owner-1.dead/slug.k1')).toBe(null);
    expect(store.getItem('ccs.plugin.owner-1.live/slug.k1')).toBe('2');
    expect(store.getItem('ccs.plugin.owner-2.dead/slug.k1')).toBe('3');
    expect(store.getItem('cc_token')).toBe('token');
  });

  it('пустой ownerId — ничего не удаляет', () => {
    const store = storeWith({ 'ccs.plugin.owner-1.dead/slug.k1': '1' });
    expect(collectStorageGarbage({ ownerId: '', keepPluginIds: [], store })).toBe(0);
    expect(store.length).toBe(1);
  });
});
