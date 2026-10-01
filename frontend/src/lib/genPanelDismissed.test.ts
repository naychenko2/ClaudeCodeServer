import { beforeEach, describe, expect, it, vi } from 'vitest';

// Окружение node — ни localStorage, ни window нет; мокаем минимально
const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Pick<Storage, 'getItem' | 'setItem' | 'clear'> }).localStorage = {
  getItem: k => store.get(k) ?? null,
  setItem: (k, v) => { store.set(k, String(v)); },
  clear: () => store.clear(),
};
const dispatched: { type: string; detail: unknown }[] = [];
(globalThis as unknown as { window: Pick<Window, 'dispatchEvent'> }).window = {
  dispatchEvent: (e: Event) => { dispatched.push({ type: e.type, detail: (e as CustomEvent).detail }); return true; },
};

import {
  MAX_KEYS, autoRevealGenerationPanel, isGenPanelDismissed, markGenPanelDismissed,
} from './genPanelDismissed';
import { REVEAL_PANEL_EVENT } from './subsystems/registryCore';

beforeEach(() => {
  store.clear();
  dispatched.length = 0;
  vi.restoreAllMocks();
});

describe('genPanelDismissed', () => {
  it('без признака панель открывается', () => {
    expect(autoRevealGenerationPanel('images', 's1')).toBe(true);
    expect(dispatched).toEqual([{ type: REVEAL_PANEL_EVENT, detail: { key: 'images' } }]);
  });

  it('вкладка едет в detail события', () => {
    autoRevealGenerationPanel('sound', 's1', 'music');
    expect(dispatched[0].detail).toEqual({ key: 'sound', tab: 'music' });
  });

  it('при признаке «закрыта» в этом чате — отказ', () => {
    markGenPanelDismissed('s1', 'images');
    expect(autoRevealGenerationPanel('images', 's1')).toBe(false);
    expect(dispatched).toHaveLength(0);
  });

  it('признак — по паре чат + панель: другой чат и другая панель открываются', () => {
    markGenPanelDismissed('s1', 'images');
    expect(autoRevealGenerationPanel('images', 's2')).toBe(true);
    expect(autoRevealGenerationPanel('sound', 's1')).toBe(true);
  });

  it('без чата и для чужих панелей ничего не делается', () => {
    expect(autoRevealGenerationPanel('images', null)).toBe(false);
    expect(autoRevealGenerationPanel('files', 's1')).toBe(false);
    markGenPanelDismissed('s1', 'files');
    markGenPanelDismissed(null, 'images');
    expect(store.size).toBe(0);
  });

  it(`держит не больше ${MAX_KEYS} последних ключей: ${MAX_KEYS + 1}-й вытесняет самый старый`, () => {
    for (let i = 0; i <= MAX_KEYS; i++) markGenPanelDismissed(`s${i}`, 'images');
    expect(isGenPanelDismissed('s0', 'images')).toBe(false);
    expect(isGenPanelDismissed('s1', 'images')).toBe(true);
    expect(isGenPanelDismissed(`s${MAX_KEYS}`, 'images')).toBe(true);
    expect(JSON.parse(store.get('cc_gen_panel_dismissed')!)).toHaveLength(MAX_KEYS);
  });

  it('повторное закрытие освежает ключ — он не вытесняется первым', () => {
    markGenPanelDismissed('s0', 'images');
    for (let i = 1; i < MAX_KEYS; i++) markGenPanelDismissed(`s${i}`, 'images');
    markGenPanelDismissed('s0', 'images');
    markGenPanelDismissed('new', 'images');
    expect(isGenPanelDismissed('s0', 'images')).toBe(true);
    expect(isGenPanelDismissed('s1', 'images')).toBe(false);
  });

  it('битое значение в localStorage не роняет стор', () => {
    store.set('cc_gen_panel_dismissed', '{не json');
    expect(autoRevealGenerationPanel('images', 's1')).toBe(true);
  });
});
