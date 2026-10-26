import { afterAll, afterEach, describe, it, expect, beforeEach } from 'vitest';
import { act, createElement } from 'react';
import {
  loadChatFilters, persistChatFilters, isDefaultFilters, defaultChatFilters,
  defaultChatFiltersKeepingView, matchChatFilter, buildHiddenReason, sortKeyOf,
  useChatFilters, isDefaultFeatureKinds, ALL_SORT_ORDERS, ALL_FEATURE_KINDS, type ChatFilters,
} from '../chatFilters';
import { installFakeDom, mount, uninstallFakeDom } from '../../components/chat/__tests__/fakeDom';
import type { Session } from '../../types';

// Окружение node — localStorage нет; мокаем минимальную реализацию на Map
const store = new Map<string, string>();
globalThis.localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, String(v)); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  get length() { return store.size; },
} as Storage;

beforeEach(() => { store.clear(); installFakeDom(); });
afterEach(() => { for (const r of roots.splice(0)) act(() => r.unmount()); });
afterAll(() => { uninstallFakeDom(); });

// Пробник для тестов пути записи через useChatFilters (R-Ф1.2): хук монтируется
// через фейк-DOM (createRoot + act) — паттерн тестов плагинов; latest — результат
// последнего рендера, через него тест зовёт patch и читает filters.
const roots: { unmount: () => void }[] = [];
let latest: ReturnType<typeof useChatFilters>;
function Probe() {
  latest = useChatFilters('p1');
  return null;
}

describe('loadChatFilters: groupBy=subjects (темы чатов)', () => {
  it('значение переживает запись и чтение — оно в белом списке normalize', () => {
    persistChatFilters('p-subj', { ...loadChatFilters('p-subj'), groupBy: 'subjects' });
    expect(loadChatFilters('p-subj').groupBy).toBe('subjects');
  });
});

describe('loadChatFilters: groupBy=features (фичи)', () => {
  it('значение переживает запись и чтение — оно в белом списке normalize', () => {
    persistChatFilters('p-feat', { ...loadChatFilters('p-feat'), groupBy: 'features' });
    expect(loadChatFilters('p-feat').groupBy).toBe('features');
  });

  it('старое значение из localStorage не ломается: features читается, а не откатывается в days', () => {
    store.set('cc_chat_filters:p-feat', JSON.stringify({ groupBy: 'features', sortOrder: 'newest' }));
    expect(loadChatFilters('p-feat').groupBy).toBe('features');
  });
});

describe('loadChatFilters: оси вида и миграция cc_chat_view', () => {
  it('дефолт: groupBy=days, sortOrder=newest, hierarchy=true (список всегда деревом)', () => {
    const f = loadChatFilters('p1');
    expect(f.groupBy).toBe('days');
    expect(f.sortOrder).toBe('newest');
    expect(f.hierarchy).toBe(true);
  });

  it('сохранённые оси читаются как есть, выключенная иерархия игнорируется', () => {
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'tags', sortOrder: 'oldest', hierarchy: false }));
    const f = loadChatFilters('p1');
    expect(f.groupBy).toBe('tags');
    expect(f.sortOrder).toBe('oldest');
    expect(f.hierarchy).toBe(true);
  });

  it('legacy "tree" ⇒ hierarchy=true; ключ живёт до первой записи нового формата', () => {
    store.set('cc_chat_view:p1', 'tree');
    const f = loadChatFilters('p1');
    expect(f.hierarchy).toBe(true);
    expect(f.groupBy).toBe('days');
    // При чтении ключ НЕ удаляется: это единственная копия выбора до первого persist
    expect(store.has('cc_chat_view:p1')).toBe(true);
    persistChatFilters('p1', f);
    expect(store.has('cc_chat_view:p1')).toBe(false);
  });

  it('миграция не теряет выбор: «открыли → ушли → вернулись» без patch', () => {
    store.set('cc_chat_view:projA', 'tree');
    // Открыли проект A — миграция применилась
    expect(loadChatFilters('projA').hierarchy).toBe(true);
    // Ушли в проект B и вернулись — выбор на месте (ключ не был удалён при чтении)
    loadChatFilters('projB');
    expect(loadChatFilters('projA').hierarchy).toBe(true);
  });

  it('после persist выбор читается из нового формата, legacy-ключ удалён', () => {
    store.set('cc_chat_view:p1', 'tree');
    const f = loadChatFilters('p1');
    // Запись с выключенным деревом (сохранённая до снятия переключателя) — legacy-ключ
    // снимается, а сама ось при чтении всё равно дерево
    persistChatFilters('p1', { ...f, hierarchy: false });
    expect(store.has('cc_chat_view:p1')).toBe(false);
    expect(loadChatFilters('p1').hierarchy).toBe(true);
  });

  it('запись старого формата (без осей): повторный load не теряет мигрированные оси', () => {
    store.set('cc_chat_filters:p1', JSON.stringify({ origins: ['task'] }));
    store.set('cc_chat_view:p1', 'tags');
    expect(loadChatFilters('p1').groupBy).toBe('tags');
    // «Ушли-вернулись»: legacy-ключ на месте, оси мигрируют повторно
    expect(loadChatFilters('p1').groupBy).toBe('tags');
    expect(loadChatFilters('p1').origins).toEqual(['task']);
  });

  it('legacy "tags" ⇒ groupBy=tags', () => {
    store.set('cc_chat_view:p1', 'tags');
    const f = loadChatFilters('p1');
    expect(f.groupBy).toBe('tags');
    expect(f.hierarchy).toBe(true);
  });

  it('legacy "flat" ⇒ дефолтные оси', () => {
    store.set('cc_chat_view:p1', 'flat');
    const f = loadChatFilters('p1');
    expect(f.groupBy).toBe('days');
    expect(f.hierarchy).toBe(true);
  });

  it('legacy-ключ применяется и к записи фильтров старого формата (без осей)', () => {
    store.set('cc_chat_filters:p1', JSON.stringify({ origins: ['task'], search: 'ревью' }));
    store.set('cc_chat_view:p1', 'tree');
    const f = loadChatFilters('p1');
    expect(f.origins).toEqual(['task']);
    expect(f.search).toBe('ревью');
    expect(f.hierarchy).toBe(true);
  });

  it('новые оси в записи сильнее legacy-ключа (миграция не затирает свежий выбор)', () => {
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'none', sortOrder: 'oldest', hierarchy: false }));
    store.set('cc_chat_view:p1', 'tree');
    const f = loadChatFilters('p1');
    expect(f.groupBy).toBe('none');
    // Ось иерархии больше не переключается — из записи не читается
    expect(f.hierarchy).toBe(true);
  });

  it('значения оси по созданию читаются как есть', () => {
    store.set('cc_chat_filters:p1', JSON.stringify({ sortOrder: 'created-oldest' }));
    expect(loadChatFilters('p1').sortOrder).toBe('created-oldest');
    persistChatFilters('p1', { ...defaultChatFilters(), sortOrder: 'created-newest' });
    expect(loadChatFilters('p1').sortOrder).toBe('created-newest');
  });

  it('незнакомое значение sortOrder вырождается в newest, остальные поля целы', () => {
    store.set('cc_chat_filters:p1', JSON.stringify({ sortOrder: 'by-name', groupBy: 'none', search: 'ревью' }));
    const f = loadChatFilters('p1');
    expect(f.sortOrder).toBe('newest');
    expect(f.groupBy).toBe('none');
    expect(f.search).toBe('ревью');
  });

  it('мусор в осях откатывается к дефолту', () => {
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'zzz', sortOrder: 'zzz', hierarchy: 'да' }));
    const f = loadChatFilters('p1');
    expect(f.groupBy).toBe('days');
    expect(f.sortOrder).toBe('newest');
    expect(f.hierarchy).toBe(true);
  });
});

// Единая точка разбора оси: по ней сортируют и секционируют chatGroups и chatTree
describe('sortKeyOf', () => {
  it('поле даты и направление для всех четырёх значений', () => {
    expect(sortKeyOf('newest')).toEqual({ field: 'updatedAt', dir: -1 });
    expect(sortKeyOf('oldest')).toEqual({ field: 'updatedAt', dir: 1 });
    expect(sortKeyOf('created-newest')).toEqual({ field: 'createdAt', dir: -1 });
    expect(sortKeyOf('created-oldest')).toEqual({ field: 'createdAt', dir: 1 });
  });

  it('белый список оси — он же порядок цикла кнопки', () => {
    expect(ALL_SORT_ORDERS).toEqual(['newest', 'oldest', 'created-newest', 'created-oldest']);
  });
});

describe('isDefaultFilters', () => {
  it('оси вида не влияют: только фильтрующие поля определяют «дефолт»', () => {
    const f: ChatFilters = { ...defaultChatFilters(), groupBy: 'tags', sortOrder: 'oldest', hierarchy: true };
    expect(isDefaultFilters(f)).toBe(true);
  });

  it('фильтрующее поле делает состояние не-дефолтным', () => {
    expect(isDefaultFilters({ ...defaultChatFilters(), search: 'х' })).toBe(false);
  });
});

describe('defaultChatFiltersKeepingView', () => {
  it('сбрасывает фильтры, сохраняя оси', () => {
    const f: ChatFilters = {
      ...defaultChatFilters(),
      search: 'ревью', only: ['pinned'],
      groupBy: 'tags', sortOrder: 'oldest', hierarchy: true,
    };
    const r = defaultChatFiltersKeepingView(f);
    expect(r.search).toBe('');
    expect(r.only).toEqual([]);
    expect(r.groupBy).toBe('tags');
    expect(r.sortOrder).toBe('oldest');
    expect(r.hierarchy).toBe(true);
  });
});

// matchChatFilter: архив (поле archived с бэка, готовое bool) прячет чат из обычного
// списка. Не вычисляем из updatedAt/archivedAt — это вторая копия правила,
// плюс мигание на равных таймстемпах. Шаг 4 плана v4.
function mkSession(over: Partial<Session> & { archived?: boolean }): Session {
  return {
    id: 'c', mode: 'auto', status: 'finished', messageCount: 0,
    createdAt: '2026-08-22T10:00:00Z', updatedAt: '2026-08-22T10:00:00Z',
    origin: 'manual', ...over,
  } as Session;
}

describe('matchChatFilter: архив', () => {
  it('archived=true прячет чат из обычного списка, archived=false показывает', () => {
    const f = defaultChatFilters();
    const pred = matchChatFilter(f);
    expect(pred(mkSession({ id: 'a', archived: true }))).toBe(false);
    expect(pred(mkSession({ id: 'b', archived: false }))).toBe(true);
    expect(pred(mkSession({ id: 'c' }))).toBe(true);
  });

  it('читается поле isArchived (Session с бэка), archived остаётся у сводки главной', () => {
    const pred = matchChatFilter(defaultChatFilters());
    expect(pred(mkSession({ id: 'a', isArchived: true }))).toBe(false);
    expect(pred(mkSession({ id: 'b', isArchived: false }))).toBe(true);
  });

  it('архив прячется независимо от остальных осей (origin/status/persona/search)', () => {
    const pred = matchChatFilter({ ...defaultChatFilters(), search: 'ревью' });
    const arch = mkSession({ id: 'a', archived: true, name: 'ревью' });
    expect(pred(arch)).toBe(false);
  });

  it('архив прячется даже при выбранном чипе «Завершён» (статус «done»)', () => {
    // Чат выполненной задачи (taskDone) — отдельная сущность от архива; оба
    // механизма могут пересекаться, и архив всё равно главнее
    const pred = matchChatFilter({ ...defaultChatFilters(), statuses: ['active', 'waiting', 'done', 'error'] });
    const arch = mkSession({ id: 'a', archived: true, taskDone: true });
    expect(pred(arch)).toBe(false);
  });
});

// Ось archivedOnly: переключатель «Архивные» переводит список В РЕЖИМ архива —
// видны ТОЛЬКО архивные чаты, обычные скрыты (смешения нет ни в одну сторону).
describe('matchChatFilter: режим архива (archivedOnly)', () => {
  it('archivedOnly=true показывает только архивные, обычные прячет', () => {
    const pred = matchChatFilter({ ...defaultChatFilters(), archivedOnly: true });
    expect(pred(mkSession({ id: 'a', archived: true }))).toBe(true);
    expect(pred(mkSession({ id: 'b', archived: false }))).toBe(false);
    expect(pred(mkSession({ id: 'c' }))).toBe(false);
  });

  it('остальные фильтры в режиме архива продолжают работать: поиск', () => {
    const pred = matchChatFilter({ ...defaultChatFilters(), archivedOnly: true, search: 'ревью' });
    expect(pred(mkSession({ id: 'a', archived: true, name: 'Ревью вёрстки' }))).toBe(true);
    expect(pred(mkSession({ id: 'b', archived: true, name: 'Планёрка' }))).toBe(false);
  });

  it('ЛОВУШКА: архивный чат выполненной задачи виден только с чипом «Завершён»', () => {
    // Дефолтный набор статусов не содержит 'done' — архивный taskDone-чат в режиме
    // архива остаётся скрыт. Это не баг фильтра: чипы статуса действуют и здесь,
    // и пустой список в этом случае — «скрыты фильтрами», а не «архива нет».
    const arch = mkSession({ id: 'a', archived: true, taskDone: true });
    expect(matchChatFilter({ ...defaultChatFilters(), archivedOnly: true })(arch)).toBe(false);
    const withDone = matchChatFilter({
      ...defaultChatFilters(), archivedOnly: true, statuses: ['active', 'waiting', 'done', 'error'],
    });
    expect(withDone(arch)).toBe(true);
  });
});

describe('archivedOnly: ось, а не фильтр', () => {
  it('isDefaultFilters не реагирует на режим архива (триггер фильтров не красится)', () => {
    expect(isDefaultFilters({ ...defaultChatFilters(), archivedOnly: true })).toBe(true);
  });

  it('«Сбросить всё» сохраняет режим архива', () => {
    const f: ChatFilters = { ...defaultChatFilters(), archivedOnly: true, search: 'ревью', only: ['pinned'] };
    const r = defaultChatFiltersKeepingView(f);
    expect(r.archivedOnly).toBe(true);
    expect(r.search).toBe('');
    expect(r.only).toEqual([]);
  });

  it('запись старого формата (без оси) нормализуется в обычный список', () => {
    store.set('cc_chat_filters:p1', JSON.stringify({ origins: ['task'] }));
    expect(loadChatFilters('p1').archivedOnly).toBe(false);
  });

  it('режим архива персистится и читается обратно', () => {
    persistChatFilters('p1', { ...defaultChatFilters(), archivedOnly: true });
    expect(loadChatFilters('p1').archivedOnly).toBe(true);
  });
});

// Старый одиночный featureFilter (записи, сохранённые до мультивыбора): цепочка
// featureFilter → featureChips/showClosed → featureKinds живёт только внутри миграции,
// сами поля не экспортируются (Ф-3) — тест проверяет итог цепочки.
describe('featureFilter: миграция старого одиночного значения в featureKinds', () => {
  const read = (raw: unknown): ChatFilters => {
    store.set('cc_chat_filters:p1', JSON.stringify(raw));
    return loadChatFilters('p1');
  };

  it('дефолт (нет записи) — []', () => {
    expect(loadChatFilters('p1').featureKinds).toEqual([]);
  });

  it("'open' → [] (пустой выбор)", () => {
    expect(read({ featureFilter: 'open' }).featureKinds).toEqual([]);
  });

  it("'waiting' → ['waiting_you']", () => {
    expect(read({ featureFilter: 'waiting' }).featureKinds).toEqual(['waiting_you']);
  });

  it("'stopped' → ['error']", () => {
    expect(read({ featureFilter: 'stopped' }).featureKinds).toEqual(['error']);
  });

  it("'merge' → ['waiting_you'] (схлопывается, разрез п. 3)", () => {
    expect(read({ featureFilter: 'merge' }).featureKinds).toEqual(['waiting_you']);
  });

  it('мусор → [], остальные поля целы', () => {
    const f = read({ featureFilter: 'zzz', search: 'ревью' });
    expect(f.featureKinds).toEqual([]);
    expect(f.search).toBe('ревью');
  });
});

// featureKinds — новая ось фильтра фич (Ф-1.2): значения FeatureBrief.kind. Миграция
// сохранённых фильтров — таблица разреза п. 5; выбор, равный умолчанию, хранится как [].
describe('ALL_FEATURE_KINDS', () => {
  it('девять значений без дублей, порядок чипов — по срочности, merged последним из открытых, closed последним', () => {
    expect(ALL_FEATURE_KINDS).toEqual([
      'waiting_you', 'error', 'running', 'queued', 'paused', 'draft', 'idle', 'merged', 'closed',
    ]);
    expect(new Set(ALL_FEATURE_KINDS).size).toBe(9);
  });
});

describe('featureKinds: миграция сохранённых фильтров (разрез п. 5)', () => {
  const read = (raw: unknown): ChatFilters => {
    store.set('cc_chat_filters:p1', JSON.stringify(raw));
    return loadChatFilters('p1');
  };

  it('дефолт (нет записи) — []', () => {
    expect(loadChatFilters('p1').featureKinds).toEqual([]);
  });

  it('строка 1: featureChips=[], showClosed=false → [] (умолчание)', () => {
    expect(read({ featureChips: [], showClosed: false }).featureKinds).toEqual([]);
  });

  it('строка 2: featureChips=[], showClosed=true → восемь открытых + closed явно', () => {
    expect(read({ featureChips: [], showClosed: true }).featureKinds).toEqual([
      'waiting_you', 'error', 'running', 'queued', 'paused', 'draft', 'idle', 'merged', 'closed',
    ]);
  });

  it("строка 3: ['waiting','merge'] → один 'waiting_you' (дубли схлопнуть, разрез п. 3)", () => {
    expect(read({ featureChips: ['waiting', 'merge'] }).featureKinds).toEqual(['waiting_you']);
  });

  it("строка 4: ['stopped'] → ['error']", () => {
    expect(read({ featureChips: ['stopped'] }).featureKinds).toEqual(['error']);
  });

  it("строка 5: ['running'] → ['running']", () => {
    expect(read({ featureChips: ['running'] }).featureKinds).toEqual(['running']);
  });

  it("строка 6: ['merged'] → ['merged'] (значение точное, не 'closed')", () => {
    expect(read({ featureChips: ['merged'] }).featureKinds).toEqual(['merged']);
  });

  it("строка 7: непустые чипы + showClosed=true → перевод чипов + 'closed'", () => {
    expect(read({ featureChips: ['waiting', 'stopped'], showClosed: true }).featureKinds)
      .toEqual(['waiting_you', 'error', 'closed']);
  });

  it("одиночный featureFilter='closed' через существующую цепочку → восемь открытых + closed", () => {
    expect(read({ featureFilter: 'closed' }).featureKinds).toEqual([
      'waiting_you', 'error', 'running', 'queued', 'paused', 'draft', 'idle', 'merged', 'closed',
    ]);
  });

  it('мусор в чипах и в featureKinds → [], остальные поля целы', () => {
    const f = read({ featureChips: ['zzz', 'bogus'], featureKinds: ['неткого'], search: 'ревью' });
    expect(f.featureKinds).toEqual([]);
    expect(f.search).toBe('ревью');
  });

  // R-Ф3 мин. 1: ключи Object.prototype ('toString'/'constructor'/'__proto__') — не собственные
  // ключи таблицы перевода и не должны проходить как чипы (прежний белый список ALL_FEATURE_CHIPS
  // их отсекал). Без этого ['toString'] + showClosed=true мигрировал бы в ['closed'] вместо девяти.
  it("чипы-наследники ('toString'/'constructor'/'__proto__') не чипы: + showClosed=true → девять значений", () => {
    expect(read({ featureChips: ['toString'], showClosed: true }).featureKinds).toEqual([
      'waiting_you', 'error', 'running', 'queued', 'paused', 'draft', 'idle', 'merged', 'closed',
    ]);
    expect(read({ featureChips: ['constructor', '__proto__'], showClosed: true }).featureKinds).toEqual([
      'waiting_you', 'error', 'running', 'queued', 'paused', 'draft', 'idle', 'merged', 'closed',
    ]);
  });

  it('идемпотентность: normalize(normalize(x)) = normalize(x)', () => {
    read({ featureChips: ['waiting', 'merge'], showClosed: true });
    const first = loadChatFilters('p1');
    persistChatFilters('p1', first);
    const second = loadChatFilters('p1');
    expect(second.featureKinds).toEqual(first.featureKinds);
    expect(second.featureKinds).toEqual(['waiting_you', 'closed']);
  });

  it('featureKinds в записи — белый список и эталонный порядок (дубли схлопнуть)', () => {
    expect(read({ featureKinds: ['closed', 'error', 'error'] }).featureKinds).toEqual(['error', 'closed']);
  });

  it('featureKinds в записи НЕ массив (строка/объект) → умолчание [], запись не отбрасывается', () => {
    const f = read({ featureKinds: 'error', search: 'ревью' });
    expect(f.featureKinds).toEqual([]);
    expect(f.search).toBe('ревью');
    // объект — не итерируемый массив: normalize обязан пропустить его как отсутствующий,
    // а не упасть и потерять всю запись (проверяется сохранением search)
    const g = read({ featureKinds: { '0': 'error' }, search: 'ревью' });
    expect(g.featureKinds).toEqual([]);
    expect(g.search).toBe('ревью');
  });

  it('featureKinds персистится и читается обратно', () => {
    persistChatFilters('p1', { ...defaultChatFilters(), featureKinds: ['error'] });
    expect(loadChatFilters('p1').featureKinds).toEqual(['error']);
  });

  it('выбор, равный умолчанию (восемь открытых без closed), хранится как [] (разрез п. 4)', () => {
    const eightOpen = ALL_FEATURE_KINDS.filter(k => k !== 'closed');
    expect(read({ featureKinds: eightOpen }).featureKinds).toEqual([]);
  });

  it('featureKinds в записи сильнее legacy-полей (миграция не затирает новый формат)', () => {
    const f = read({ featureKinds: ['error'], featureChips: ['waiting'], showClosed: true });
    expect(f.featureKinds).toEqual(['error']);
  });
});

// Путь записи через useChatFilters: featureKinds — единственная ось фич, патч пишет её
// напрямую. Пересчёт из legacy-полей (R-Ф1.2) удалён в Ф-3: согласованность держит
// миграция при ЧТЕНИИ — первый load legacy-записи приносит выбор в featureKinds,
// первая же запись сохраняет его в новом формате (старый UI больше ничего не пишет).
describe('useChatFilters.patch: запись featureKinds (единственная ось фич)', () => {
  const saved = (): Record<string, unknown> => JSON.parse(store.get('cc_chat_filters:p1')!);

  it('legacy-запись: мигрированный выбор не теряется при первом патче', () => {
    // Запись старого формата: featureChips:['waiting'], featureKinds нет
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'features', featureChips: ['waiting'], showClosed: false }));
    const { root } = mount(createElement(Probe));
    roots.push(root);
    // Чтение: миграция применилась, выбор 'waiting_you'
    expect(latest.filters.featureKinds).toEqual(['waiting_you']);
    // Патч постороннего поля: мигрированный выбор записывается в новом формате
    act(() => latest.patch({ search: 'ревью' }));
    expect(saved().featureKinds).toEqual(['waiting_you']);
    // Повторное чтение: featureKinds читается напрямую (legacy уже не нужен) — триггер горит
    const f = loadChatFilters('p1');
    expect(f.featureKinds).toEqual(['waiting_you']);
    expect(isDefaultFilters(f)).toBe(false);
  });

  it('патч других полей: featureKinds не трогается', () => {
    persistChatFilters('p1', { ...defaultChatFilters(), groupBy: 'features', featureKinds: ['error'] });
    const { root } = mount(createElement(Probe));
    roots.push(root);
    expect(latest.filters.featureKinds).toEqual(['error']);
    act(() => latest.patch({ search: 'ревью' }));
    expect(saved().featureKinds).toEqual(['error']);
  });

  it('патч пишет featureKinds напрямую (новый UI): значение сохраняется', () => {
    const { root } = mount(createElement(Probe));
    roots.push(root);
    act(() => latest.patch({ groupBy: 'features', featureKinds: ['error'] }));
    expect(saved().featureKinds).toEqual(['error']);
  });
});

describe('featureKinds: isDefaultFilters в обоих режимах', () => {
  it('режим фич: выбранный featureKinds → не-дефолт (триггер красится)', () => {
    expect(isDefaultFilters({ ...defaultChatFilters(), groupBy: 'features', featureKinds: ['error'] })).toBe(false);
  });

  it('режим фич: пустой выбор → дефолт', () => {
    expect(isDefaultFilters({ ...defaultChatFilters(), groupBy: 'features' })).toBe(true);
  });

  it('другой режим: featureKinds спит → дефолт (триггер не красится)', () => {
    expect(isDefaultFilters({ ...defaultChatFilters(), groupBy: 'days', featureKinds: ['error'] })).toBe(true);
  });
});

// Р-Ф1.2 мин. 2: «равен умолчанию» определяется МНОЖЕСТВОМ с эталоном, а не СЧЁТОМ:
// восемь элементов с дублем или мусором равными не считаются. Пустой выбор здесь false
// (он означает умолчание сам по себе, вне этой функции).
describe('isDefaultFeatureKinds: сравнение по множеству с эталоном', () => {
  const eightOpen = ALL_FEATURE_KINDS.filter(k => k !== 'closed');

  it('восемь открытых в любом порядке — true', () => {
    expect(isDefaultFeatureKinds([...eightOpen])).toBe(true);
    expect(isDefaultFeatureKinds([...eightOpen].reverse())).toBe(true);
  });

  it('восемь элементов с дублем и пропущенным kind — false (счёт не признак)', () => {
    const dup = [...eightOpen.slice(0, 7), eightOpen[0]];
    expect(isDefaultFeatureKinds(dup)).toBe(false);
  });

  it('с «closed» или частичный выбор — false', () => {
    expect(isDefaultFeatureKinds([...ALL_FEATURE_KINDS])).toBe(false);
    expect(isDefaultFeatureKinds(['error'])).toBe(false);
    expect(isDefaultFeatureKinds([])).toBe(false);
  });
});

describe('featureKinds: «Сбросить всё» в обоих режимах', () => {
  it('режим фич: сброс очищает featureKinds, сохраняет groupBy', () => {
    const f: ChatFilters = { ...defaultChatFilters(), groupBy: 'features', featureKinds: ['error'], search: 'ревью' };
    const r = defaultChatFiltersKeepingView(f);
    expect(r.featureKinds).toEqual([]);
    expect(r.search).toBe('');
    expect(r.groupBy).toBe('features');
  });

  it('другой режим: сброс сохраняет featureKinds (спит)', () => {
    const f: ChatFilters = { ...defaultChatFilters(), groupBy: 'days', featureKinds: ['error'], search: 'ревью' };
    const r = defaultChatFiltersKeepingView(f);
    expect(r.featureKinds).toEqual(['error']);
    expect(r.search).toBe('');
    expect(r.groupBy).toBe('days');
  });
});

// Причина пустого списка: число, существительное и глагол согласованы между собой
describe('buildHiddenReason: согласование числа', () => {
  it('один чат — «Единственный чат скрыт»', () => {
    expect(buildHiddenReason(1, '')).toBe(
      'Единственный чат скрыт фильтрами. Ослабьте условия или сбросьте их целиком.');
  });

  it('2–4 — «Все N чата скрыты»', () => {
    expect(buildHiddenReason(3, '')).toBe(
      'Все 3 чата скрыты фильтрами. Ослабьте условия или сбросьте их целиком.');
  });

  it('5+ — «Все N чатов скрыты»', () => {
    expect(buildHiddenReason(12, '')).toBe(
      'Все 12 чатов скрыты фильтрами. Ослабьте условия или сбросьте их целиком.');
  });

  it('число на 1 (кроме 11) — единственное число глагола', () => {
    expect(buildHiddenReason(21, '')).toBe(
      'Все 21 чат скрыт фильтрами. Ослабьте условия или сбросьте их целиком.');
    expect(buildHiddenReason(11, '')).toBe(
      'Все 11 чатов скрыты фильтрами. Ослабьте условия или сбросьте их целиком.');
  });

  it('с поиском — та же форма плюс запрос', () => {
    expect(buildHiddenReason(1, ' ревью ')).toBe(
      'Единственный чат скрыт фильтрами и поиском «ревью». Ослабьте условия или сбросьте их целиком.');
    expect(buildHiddenReason(12, 'ревью')).toBe(
      'Все 12 чатов скрыты фильтрами и поиском «ревью». Ослабьте условия или сбросьте их целиком.');
  });
});
