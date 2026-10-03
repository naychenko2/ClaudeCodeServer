import { beforeEach, describe, expect, it, vi } from 'vitest';

// Окружение node — ни localStorage, ни window нет; мокаем минимально
const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, String(v)); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  length: 0,
} as Storage;
const dispatched: { type: string; detail: unknown }[] = [];
(globalThis as unknown as { window: Pick<Window, 'dispatchEvent'> }).window = {
  dispatchEvent: (e: Event) => { dispatched.push({ type: e.type, detail: (e as CustomEvent).detail }); return true; },
};

import { REVEAL_PANEL_EVENT } from '../../../lib/subsystems/registryCore';
import { __resetAgentPicks, dropAgentPick, getAgentPick } from '../../../lib/genPanelFollow';
import { __applyThreads, __resetAudioStore, handleEvent, soundDraftKey } from '../thread/threadStore';
import { revealSoundPanel } from './reveal';

const reveals = () => dispatched.filter(d => d.type === REVEAL_PANEL_EVENT).map(d => d.detail);

beforeEach(() => {
  store.clear();
  dispatched.length = 0;
  __resetAudioStore();
  __resetAgentPicks();
  vi.restoreAllMocks();
});

describe('показ панели «Контекст» из мест звука', () => {
  it('просьба идёт в панель «Контекст», а не в «Звук»; без чата показывать нечего', () => {
    expect(revealSoundPanel(null)).toBe(false);
    expect(reveals()).toEqual([]);
    expect(revealSoundPanel('s1', soundDraftKey('t1'))).toBe(true);
    expect(reveals()).toEqual([expect.objectContaining({ key: 'chatContext', sessionId: 's1', target: 'sound:t1' })]);
  });

  it('выбор агентом (событие нитей) панель не двигает — запоминается подсказка', () => {
    __applyThreads('s1', 'p1', { focus: null, revision: 1, threads: [] });
    handleEvent({ type: 'audio_thread_changed', sessionId: 's1', scopeKey: 'p1', state: { focus: 't2', revision: 2, threads: [] } } as never);
    expect(reveals()).toEqual([]);
    expect(getAgentPick('s1')).toMatchObject({ panelKey: 'sound', target: 'sound:t2' });
    // Свой клик по тому же звуку подсказку убирает
    dropAgentPick('s1', soundDraftKey('t2'));
    expect(getAgentPick('s1')).toBeNull();
  });
});
