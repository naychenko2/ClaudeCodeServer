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
import { REVEAL_PANEL_EVENT, revealContextPanel } from './subsystems/registryCore';

const P = 'chatContext';

beforeEach(() => {
  store.clear();
  dispatched.length = 0;
  vi.restoreAllMocks();
});

describe('genPanelDismissed', () => {
  it('без признака панель открывается', () => {
    expect(autoRevealGenerationPanel(P, 's1')).toBe(true);
    expect(dispatched).toEqual([{ type: REVEAL_PANEL_EVENT, detail: { key: P, sessionId: 's1' } }]);
  });

  it('при признаке «закрыта» в этом чате — отказ', () => {
    markGenPanelDismissed('s1', P);
    expect(autoRevealGenerationPanel(P, 's1')).toBe(false);
    expect(dispatched).toHaveLength(0);
  });

  it('признак — по паре чат + панель: другой чат открывается', () => {
    markGenPanelDismissed('s1', P);
    expect(autoRevealGenerationPanel(P, 's2')).toBe(true);
  });

  it('без чата и для чужих панелей ничего не делается', () => {
    expect(autoRevealGenerationPanel(P, null)).toBe(false);
    expect(autoRevealGenerationPanel('files', 's1')).toBe(false);
    markGenPanelDismissed('s1', 'files');
    markGenPanelDismissed(null, P);
    expect(store.size).toBe(0);
  });

  it(`держит не больше ${MAX_KEYS} последних ключей: ${MAX_KEYS + 1}-й вытесняет самый старый`, () => {
    for (let i = 0; i <= MAX_KEYS; i++) markGenPanelDismissed(`s${i}`, P);
    expect(isGenPanelDismissed('s0', P)).toBe(false);
    expect(isGenPanelDismissed('s1', P)).toBe(true);
    expect(isGenPanelDismissed(`s${MAX_KEYS}`, P)).toBe(true);
    expect(JSON.parse(store.get('cc_gen_panel_dismissed')!)).toHaveLength(MAX_KEYS);
  });

  it('повторное закрытие освежает ключ — он не вытесняется первым', () => {
    markGenPanelDismissed('s0', P);
    for (let i = 1; i < MAX_KEYS; i++) markGenPanelDismissed(`s${i}`, P);
    markGenPanelDismissed('s0', P);
    markGenPanelDismissed('new', P);
    expect(isGenPanelDismissed('s0', P)).toBe(true);
    expect(isGenPanelDismissed('s1', P)).toBe(false);
  });

  it('битое значение в localStorage не роняет стор', () => {
    store.set('cc_gen_panel_dismissed', '{не json');
    expect(autoRevealGenerationPanel(P, 's1')).toBe(true);
  });
});

describe('вся генерация живёт в одной панели chatContext', () => {
  it('признак «закрыта» ключуется {сессия}:chatContext', () => {
    markGenPanelDismissed('s1', 'chatContext');
    expect(JSON.parse(store.get('cc_gen_panel_dismissed')!)).toEqual(['s1:chatContext']);
    expect(isGenPanelDismissed('s1', 'chatContext')).toBe(true);
  });

  it('вызов с ключом упразднённой панели (images, sound, videoEditor) попадает в chatContext: и признак, и показ', () => {
    markGenPanelDismissed('s1', 'images');
    expect(JSON.parse(store.get('cc_gen_panel_dismissed')!)).toEqual(['s1:chatContext']);
    expect(autoRevealGenerationPanel('sound', 's1', 'music')).toBe(false);
    expect(autoRevealGenerationPanel('videoEditor', 's1')).toBe(false);
    expect(autoRevealGenerationPanel('sound', 's2', 'music')).toBe(true);
    expect(dispatched).toEqual([{ type: REVEAL_PANEL_EVENT, detail: { key: 'chatContext', sessionId: 's2' } }]);
  });

  it('revealContextPanel — обёртка над показом chatContext без вкладки', () => {
    revealContextPanel('s1', { target: 'image:t1' });
    expect(dispatched).toEqual([{ type: REVEAL_PANEL_EVENT, detail: { key: 'chatContext', sessionId: 's1', target: 'image:t1' } }]);
  });
});
