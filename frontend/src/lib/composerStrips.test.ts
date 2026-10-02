import { beforeEach, describe, expect, it } from 'vitest';

// Окружение node — localStorage нет; мокаем минимальную реализацию на Map
const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  length: 0,
} as Storage;

import {
  __resetComposerStrips, forgetComposerSession, forgetComposerOnChatDeleted, getActiveStrip, getPendingFocus,
  getRememberedStrip, getShownStrip, releaseStrip, requestStrip, reportShownStrip,
  resetComposerMemory, resolveStrip, selectStrip, stripStorageKey,
} from './composerStrips';
import { composerModeMemory } from './composerModes';

const ALL = ['git', 'images'];

beforeEach(() => {
  store.clear();
  __resetComposerStrips();
});

describe('resolveStrip — правило старшинства', () => {
  it('фокус картинки важнее запомненной полосы', () => {
    expect(resolveStrip({ focus: 'images', remembered: 'git', available: ALL })).toBe('images');
  });
  it('запомненная полоса важнее Git', () => {
    expect(resolveStrip({ focus: null, remembered: 'images', available: ALL })).toBe('images');
  });
  it('по умолчанию Git', () => {
    expect(resolveStrip({ focus: null, remembered: null, available: ALL })).toBe('git');
  });
  it('недоступная полоса пропускается на любой ступени', () => {
    expect(resolveStrip({ focus: 'video', remembered: 'sound', available: ALL })).toBe('git');
  });
  it('без git — первая доступная', () => {
    expect(resolveStrip({ focus: null, remembered: null, available: ['images'] })).toBe('images');
    expect(resolveStrip({ focus: null, remembered: null, available: [] })).toBeNull();
  });
});

describe('стор полос', () => {
  it('новый чат без выбора — Git', () => {
    expect(getActiveStrip('s1', ALL)).toBe('git');
    expect(getActiveStrip(null, ALL)).toBe('git');
  });

  it('ручной выбор запоминается на чат под ключом cc-composer-strip:{sessionId}', () => {
    selectStrip('s1', 'images');
    expect(stripStorageKey('s1')).toBe('cc-composer-strip:s1');
    expect(store.get('cc-composer-strip:s1')).toBe('images');
    expect(getActiveStrip('s1', ALL)).toBe('images');
  });

  it('ключи разных чатов не пересекаются', () => {
    selectStrip('s1', 'images');
    requestStrip('s2', 'images');
    expect(getActiveStrip('s1', ALL)).toBe('images');
    expect(getActiveStrip('s2', ALL)).toBe('images');
    expect(getActiveStrip('s3', ALL)).toBe('git');
    releaseStrip('s2', 'images');
    expect(getActiveStrip('s2', ALL)).toBe('git');
    expect(getActiveStrip('s1', ALL)).toBe('images');
    expect(store.has('cc-composer-strip:s2')).toBe(false);
  });

  it('запомненная полоса читается из localStorage после перезагрузки', () => {
    store.set('cc-composer-strip:s1', 'images');
    expect(getActiveStrip('s1', ALL)).toBe('images');
  });

  it('фокус картинки перебивает запомненный Git, снятие выбора возвращает прежнюю полосу', () => {
    selectStrip('s1', 'git');
    requestStrip('s1', 'images');
    expect(getActiveStrip('s1', ALL)).toBe('images');
    releaseStrip('s1', 'images');
    expect(getActiveStrip('s1', ALL)).toBe('git');
  });

  it('ручной уход с полосы фокуса не перебивается, пока фокус не запрошен снова', () => {
    requestStrip('s1', 'images');
    selectStrip('s1', 'git');
    expect(getActiveStrip('s1', ALL)).toBe('git');
    expect(getPendingFocus('s1')).toBe('images');
    // Режим «Картинка» повторно запрашивает полосу — она возвращается
    requestStrip('s1', 'images');
    expect(getActiveStrip('s1', ALL)).toBe('images');
    expect(getPendingFocus('s1')).toBeNull();
  });

  it('сам открыл «Картинки» до выбора — после снятия выбора они остаются', () => {
    selectStrip('s1', 'images');
    requestStrip('s1', 'images');
    releaseStrip('s1', 'images');
    expect(getActiveStrip('s1', ALL)).toBe('images');
  });

  it('чужой release не снимает запрос другой полосы', () => {
    requestStrip('s1', 'images');
    releaseStrip('s1', 'video');
    expect(getActiveStrip('s1', ALL)).toBe('images');
  });
});

import {
  collapsedStorageKey, isStripCollapsed, setStripCollapsed,
} from './composerStrips';

describe('свёрнутость полос (прототип полос, вариант C)', () => {
  it('своя у каждой полосы чата', () => {
    expect(collapsedStorageKey('s1', 'images')).not.toBe(collapsedStorageKey('s1', 'git'));
    expect(collapsedStorageKey('s1', 'images')).not.toBe(collapsedStorageKey('s2', 'images'));
  });

  it('по умолчанию — флаг устройства (isMobile) и выбор живёт в localStorage', () => {
    expect(isStripCollapsed('s1', 'images', true)).toBe(true);
    expect(isStripCollapsed('s1', 'images', false)).toBe(false);
    setStripCollapsed('s1', 'images', true);
    expect(isStripCollapsed('s1', 'images', false)).toBe(true);
    expect(isStripCollapsed('s1', 'git', false)).toBe(false);
  });
});

describe('чистка памяти полос и режима', () => {
  beforeEach(() => { localStorage.clear(); __resetComposerStrips(); });

  it('событие chat_deleted чистит память чата, чужие события и чужие чаты — нет', () => {
    for (const id of ['A', 'B']) { selectStrip(id, 'images'); composerModeMemory(id).seen = { image: 'x' }; }
    forgetComposerOnChatDeleted({ type: 'status_changed', sessionId: 'A' });
    expect(getRememberedStrip('A')).toBe('images');
    forgetComposerOnChatDeleted({ type: 'chat_deleted', sessionId: 'A' });
    expect(getRememberedStrip('A')).toBeNull();
    expect(composerModeMemory('A').seen).toEqual({});
    expect(getRememberedStrip('B')).toBe('images');
  });

  it('удаление чата чистит его записи и не трогает соседний чат', () => {
    for (const id of ['A', 'B']) {
      selectStrip(id, 'images');
      requestStrip(id, 'images');
      setStripCollapsed(id, 'images', false);
      reportShownStrip(id, 'images');
      composerModeMemory(id).seen = { image: 'x' };
    }
    forgetComposerSession('A');
    expect(getRememberedStrip('A')).toBeNull();
    expect(getPendingFocus('A')).toBeNull();
    expect(getShownStrip('A')).toBeUndefined();
    expect(localStorage.getItem(stripStorageKey('A'))).toBeNull();
    expect(localStorage.getItem(collapsedStorageKey('A', 'images'))).toBeNull();
    expect(isStripCollapsed('A', 'images', true)).toBe(true);
    expect(composerModeMemory('A').seen).toEqual({});
    expect(getRememberedStrip('B')).toBe('images');
    expect(getShownStrip('B')).toBe('images');
    expect(composerModeMemory('B').seen).toEqual({ image: 'x' });
  });

  it('выход из аккаунта: память прошлого пользователя не видна', () => {
    selectStrip('A', 'images');
    requestStrip('A', 'images');
    reportShownStrip('A', 'images');
    setStripCollapsed('A', 'images', false);
    composerModeMemory('A').seen = { image: 'x' };
    composerModeMemory('A').modeId = 'image';
    resetComposerMemory();
    expect(getActiveStrip('A', ['git', 'images'])).toBe('images'); // из localStorage устройства, фокуса нет
    expect(getShownStrip('A')).toBeUndefined();
    expect(getPendingFocus('A')).toBeNull();
    expect(composerModeMemory('A')).toEqual({ seen: {}, modeId: null });
  });
});
