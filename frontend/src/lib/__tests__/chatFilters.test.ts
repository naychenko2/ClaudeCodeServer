import { describe, it, expect, beforeEach } from 'vitest';
import {
  loadChatFilters, persistChatFilters, isDefaultFilters, defaultChatFilters,
  defaultChatFiltersKeepingView, matchChatFilter, type ChatFilters,
} from '../chatFilters';
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

beforeEach(() => store.clear());

describe('loadChatFilters: оси вида и миграция cc_chat_view', () => {
  it('дефолт: groupBy=days, sortOrder=newest, hierarchy=false', () => {
    const f = loadChatFilters('p1');
    expect(f.groupBy).toBe('days');
    expect(f.sortOrder).toBe('newest');
    expect(f.hierarchy).toBe(false);
  });

  it('сохранённые оси читаются как есть', () => {
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'tags', sortOrder: 'oldest', hierarchy: true }));
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
    // Пользователь выключил дерево — patch персистит и снимает legacy-ключ
    persistChatFilters('p1', { ...f, hierarchy: false });
    expect(store.has('cc_chat_view:p1')).toBe(false);
    expect(loadChatFilters('p1').hierarchy).toBe(false);
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
    expect(f.hierarchy).toBe(false);
  });

  it('legacy "flat" ⇒ дефолтные оси', () => {
    store.set('cc_chat_view:p1', 'flat');
    const f = loadChatFilters('p1');
    expect(f.groupBy).toBe('days');
    expect(f.hierarchy).toBe(false);
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
    expect(f.hierarchy).toBe(false);
  });

  it('мусор в осях откатывается к дефолту', () => {
    store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: 'zzz', sortOrder: 'zzz', hierarchy: 'да' }));
    const f = loadChatFilters('p1');
    expect(f.groupBy).toBe('days');
    expect(f.sortOrder).toBe('newest');
    expect(f.hierarchy).toBe(false);
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

// === Архивный вид ===
// Архив — не чип фильтра, а развилка вида: обычный список не показывает архивные
// никогда, архивный — только их.
const chat = (over: Partial<Session>): Session => ({
  id: 'c1', mode: 'auto', status: 'finished', messageCount: 1,
  createdAt: '2026-08-01T10:00:00Z', updatedAt: '2026-08-01T10:00:00Z',
  origin: 'manual', ...over,
} as Session);

describe('matchChatFilter: архив', () => {
  it('обычный вид скрывает архивные чаты', () => {
    const ok = matchChatFilter(defaultChatFilters());
    expect(ok(chat({}))).toBe(true);
    expect(ok(chat({ archivedAt: '2026-08-02T10:00:00Z' }))).toBe(false);
  });

  it('архивный вид показывает только архивные', () => {
    const ok = matchChatFilter({ ...defaultChatFilters(), archived: true });
    expect(ok(chat({}))).toBe(false);
    expect(ok(chat({ archivedAt: '2026-08-02T10:00:00Z' }))).toBe(true);
  });

  it('в архиве не применяется фильтр статусов: чат выполненной задачи виден', () => {
    // Дефолт прячет срез «Готово» — иначе архив выглядел бы пустым при лежащих там
    // чатах выполненных задач
    const ok = matchChatFilter({ ...defaultChatFilters(), archived: true });
    expect(ok(chat({ archivedAt: '2026-08-02T10:00:00Z', taskDone: true }))).toBe(true);
  });

  it('сброс фильтров не выкидывает из архива', () => {
    const r = defaultChatFiltersKeepingView({ ...defaultChatFilters(), archived: true, search: 'x' });
    expect(r.archived).toBe(true);
    expect(r.search).toBe('');
  });
});
