// Стор области панелей вики (W8): у каждой панели своя область (follow | all | section),
// переживает перезагрузку, две вкладки синхронизируются событием 'storage', отказный
// вход (битый JSON, чужой mode, бросок localStorage) даёт follow без исключения наружу.
// Хук монтируется фейк-DOM'ом (тот же путь, что useRestoreWikiStatement.test); «перезагрузка
// страницы» — __resetWikiPanelScope: память обнулена, хранилище нет.
//
// Мутации этого прогона (вывод — в отчёте задачи):
//   (1) запись из снимка, взятого вне set (const snap = readStored() на уровне модуля) →
//       «гонка: два set в одном тике» краснеет;
//   (2) убрать нормализацию all→follow для wikiHealth в parseScopeValue → «Здоровье» краснеет;
//   (3) убрать фильтр e.key в bindStorage → «чужой ключ игнорируется» краснеет.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { mount, installFakeDom, uninstallFakeDom } from '../../../../components/chat/__tests__/fakeDom';
import {
  WIKI_SCOPE_KEY, __resetWikiPanelScope, resolveWikiScope, useWikiPanelScope,
  type WikiPanelKey, type WikiPanelScopeValue,
} from '../wikiPanelScope';

const roots: Root[] = [];
// Хранилище теста: та же форма, что у настоящего localStorage, но под контролем теста;
// throwGet — «приватный режим»: getItem бросает, как браузер при недоступном хранилище
let store = new Map<string, string>();
let throwGet = false;

beforeEach(() => {
  installFakeDom();
  store = new Map();
  throwGet = false;
  vi.stubGlobal('localStorage', {
    getItem: (k: string) => {
      if (throwGet) throw new Error('private mode');
      return store.get(k) ?? null;
    },
    setItem: (k: string, v: string) => { store.set(k, v); },
    removeItem: (k: string) => { store.delete(k); },
  });
  // следующая читка снова идёт из хранилища — как свежая страница
  __resetWikiPanelScope();
});

afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  __resetWikiPanelScope();
  vi.unstubAllGlobals();
});

type ScopeApi = [WikiPanelScopeValue, (v: WikiPanelScopeValue) => void];

function mountScope(key: WikiPanelKey): { box: { current: ScopeApi } } {
  const box = { current: null as unknown as ScopeApi };
  const Harness = () => { box.current = useWikiPanelScope(key); return null; };
  const { root } = mount(createElement(Harness));
  roots.push(root);
  return { box };
}

function unmountAll() {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
}

// «Другая вкладка»: значение в хранилище уже чужое, событие идет по нашему ключу
function fireStorage(key: string) {
  const ev = { type: 'storage', key, newValue: store.get(key) ?? null } as unknown as StorageEvent;
  act(() => { window.dispatchEvent(ev); });
}

const saved = () => JSON.parse(store.get(WIKI_SCOPE_KEY) ?? '{}') as Record<string, unknown>;

describe('умолчание и резолв', () => {
  it('пустое хранилище — у всех панелей follow; follow резолвится в открытую область, без неё — null', () => {
    const inbox = mountScope('wikiInbox');
    const journal = mountScope('wikiJournal');
    const health = mountScope('wikiHealth');
    expect(inbox.box.current[0]).toEqual({ mode: 'follow' });
    expect(journal.box.current[0]).toEqual({ mode: 'follow' });
    expect(health.box.current[0]).toEqual({ mode: 'follow' });
    expect(saved()).toEqual({}); // пустое хранилище стор не перезаписывает
    expect(resolveWikiScope({ mode: 'follow' }, { followKey: 'projects/p1', sectionKeys: ['projects/p1'] })).toBe('projects/p1');
    expect(resolveWikiScope({ mode: 'follow' }, { followKey: null, sectionKeys: [] })).toBeNull();
  });

  it('резолв: раздел есть — его ключ; раздел удалён — followKey; all — "all"', () => {
    const ctx = { followKey: 'open/section', sectionKeys: ['a/b', 'c/d'] };
    expect(resolveWikiScope({ mode: 'section', key: 'c/d' }, ctx)).toBe('c/d');
    expect(resolveWikiScope({ mode: 'section', key: 'gone' }, ctx)).toBe('open/section');
    expect(resolveWikiScope({ mode: 'all' }, ctx)).toBe('all');
  });
});

describe('значение переживает перезагрузку', () => {
  it('set → значение в хранилище; после обнуления памяти читается обратно', () => {
    const inbox = mountScope('wikiInbox');
    act(() => { inbox.box.current[1]({ mode: 'section', key: 'a/b' }); });
    expect(inbox.box.current[0]).toEqual({ mode: 'section', key: 'a/b' });
    expect(saved().wikiInbox).toEqual({ mode: 'section', key: 'a/b' });
    unmountAll();
    __resetWikiPanelScope();
    const fresh = mountScope('wikiInbox');
    expect(fresh.box.current[0]).toEqual({ mode: 'section', key: 'a/b' });
    // чужие панели при этом не затронуты
    const journal = mountScope('wikiJournal');
    expect(journal.box.current[0]).toEqual({ mode: 'follow' });
  });
});

describe('конкурентность', () => {
  it('гонка: два set в одном тике по разным панелям — в хранилище и в памяти оба', () => {
    const inbox = mountScope('wikiInbox');
    const journal = mountScope('wikiJournal');
    act(() => {
      inbox.box.current[1]({ mode: 'section', key: 'a/b' });
      journal.box.current[1]({ mode: 'all' });
    });
    expect(saved().wikiInbox).toEqual({ mode: 'section', key: 'a/b' });
    expect(saved().wikiJournal).toEqual({ mode: 'all' });
    expect(inbox.box.current[0]).toEqual({ mode: 'section', key: 'a/b' });
    expect(journal.box.current[0]).toEqual({ mode: 'all' });
  });

  it('вторая вкладка: storage-событие по нашему ключу перечитывает; чужой ключ игнорируется', () => {
    const inbox = mountScope('wikiInbox');
    store.set(WIKI_SCOPE_KEY, JSON.stringify({ wikiInbox: { mode: 'section', key: 'from/tab2' } }));
    fireStorage(WIKI_SCOPE_KEY);
    expect(inbox.box.current[0]).toEqual({ mode: 'section', key: 'from/tab2' });
    // чужая вкладка меняет чужой ключ — наше значение не трогается
    store.set('cc_notes_zones', '{"any":"other"}');
    fireStorage('cc_notes_zones');
    expect(inbox.box.current[0]).toEqual({ mode: 'section', key: 'from/tab2' });
  });
});

describe('отказный вход → follow без исключения наружу', () => {
  it('битый JSON, неизвестный mode, section без ключа — все панели follow', () => {
    store.set(WIKI_SCOPE_KEY, 'не json {');
    const a = mountScope('wikiInbox');
    expect(a.box.current[0]).toEqual({ mode: 'follow' });
    unmountAll();
    store.set(WIKI_SCOPE_KEY, JSON.stringify({
      wikiJournal: { mode: 'bogus' },
      wikiInbox: { mode: 'section' },
      wikiHealth: 'не объект',
    }));
    __resetWikiPanelScope();
    const b = mountScope('wikiInbox');
    const c = mountScope('wikiJournal');
    const d = mountScope('wikiHealth');
    expect(b.box.current[0]).toEqual({ mode: 'follow' });
    expect(c.box.current[0]).toEqual({ mode: 'follow' });
    expect(d.box.current[0]).toEqual({ mode: 'follow' });
  });

  it('localStorage бросает (приватный режим): follow, set не бросает', () => {
    throwGet = true;
    const inbox = mountScope('wikiInbox');
    expect(inbox.box.current[0]).toEqual({ mode: 'follow' });
    expect(() => act(() => { inbox.box.current[1]({ mode: 'all' }); })).not.toThrow();
  });
});

describe('«Здоровье»: all не бывает', () => {
  it("all в хранилище и set('all') резолвятся в follow; у «Входящих» all — значение", () => {
    store.set(WIKI_SCOPE_KEY, JSON.stringify({ wikiHealth: { mode: 'all' } }));
    const health = mountScope('wikiHealth');
    expect(health.box.current[0]).toEqual({ mode: 'follow' });
    expect(resolveWikiScope(health.box.current[0], { followKey: 'sec/x', sectionKeys: ['sec/x'] })).toBe('sec/x');
    // и запись all для «Здоровья» не проходит: было section → стало follow, в хранилище — follow
    unmountAll();
    store.set(WIKI_SCOPE_KEY, JSON.stringify({ wikiHealth: { mode: 'section', key: 'k' } }));
    __resetWikiPanelScope();
    const fresh = mountScope('wikiHealth');
    act(() => { fresh.box.current[1]({ mode: 'all' }); });
    expect(fresh.box.current[0]).toEqual({ mode: 'follow' });
    expect(saved().wikiHealth).toEqual({ mode: 'follow' });
    // для двух других панелей all — обычное значение
    const inbox = mountScope('wikiInbox');
    act(() => { inbox.box.current[1]({ mode: 'all' }); });
    expect(inbox.box.current[0]).toEqual({ mode: 'all' });
    expect(saved().wikiInbox).toEqual({ mode: 'all' });
  });
});
