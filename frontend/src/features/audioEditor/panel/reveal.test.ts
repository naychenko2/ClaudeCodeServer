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

import { markGenPanelDismissed } from '../../../lib/genPanelDismissed';
import { REVEAL_PANEL_EVENT } from '../../../lib/subsystems/registryCore';
import { audioApi } from '../api';
import { openSoundShortcut, selectThreadByHuman } from '../thread/actions';
import { __applyThreads, __resetAudioStore, handleEvent, soundDraftKey } from '../thread/threadStore';
import { __resetGenPanelOpen, holdGenPanelOpen } from '../../../lib/genPanelOpen';
import { __resetAgentPicks, dropAgentPick, getAgentPick } from '../../../lib/genPanelFollow';

const reveals = () => dispatched.filter(d => d.type === REVEAL_PANEL_EVENT).map(d => d.detail);

beforeEach(() => {
  store.clear();
  dispatched.length = 0;
  __resetAudioStore();
  __resetGenPanelOpen();
  __resetAgentPicks();
  vi.restoreAllMocks();
});

describe('автооткрытие панели «Звук» по действию человека', () => {
  it('ярлык «Звук» открывает панель на «Настройках», пока её в чате не закрывали', () => {
    openSoundShortcut('s1');
    expect(reveals()).toEqual([{ key: 'sound', tab: 'settings', sessionId: 's1' }]);
  });

  it('закрыл панель в чате — ярлык её больше сам не открывает', () => {
    markGenPanelDismissed('s1', 'sound');
    openSoundShortcut('s1');
    expect(reveals()).toEqual([]);
    openSoundShortcut('s2');
    expect(reveals()).toEqual([{ key: 'sound', tab: 'settings', sessionId: 's2' }]);
  });

  it('клик по карточке закрытую панель не открывает, открытую переключает на «Звук»', async () => {
    __applyThreads('s1', 'p1', { focus: null, revision: 1, threads: [] });
    vi.spyOn(audioApi, 'focus').mockResolvedValue({ focus: 't1', revision: 2, threads: [] });
    expect(await selectThreadByHuman('p1', 's1', 't1')).toBe(true);
    expect(reveals()).toEqual([]);
    const off = holdGenPanelOpen('images', 'column');
    expect(await selectThreadByHuman('p1', 's1', 't1', true)).toBe(true);
    off();
    expect(reveals()).toEqual([{ key: 'sound', tab: 'settings', sessionId: 's1', target: 'sound:t1', follow: true }]);
    dispatched.length = 0;
    vi.spyOn(audioApi, 'focus').mockRejectedValueOnce(new Error('нет'));
    expect(await selectThreadByHuman('p1', 's1', 't1')).toBe(false);
    expect(reveals()).toEqual([]);
  });

  it('выбор агентом (событие нитей) панель не двигает — подсказка в панели другого раздела', () => {
    __applyThreads('s1', 'p1', { focus: null, revision: 1, threads: [] });
    handleEvent({ type: 'audio_thread_changed', sessionId: 's1', scopeKey: 'p1', state: { focus: 't2', revision: 2, threads: [] } } as never);
    expect(reveals()).toEqual([]);
    expect(getAgentPick('s1')).toMatchObject({ panelKey: 'sound', target: 'sound:t2' });
    // Свой клик по тому же звуку подсказку убирает
    dropAgentPick('s1', soundDraftKey('t2'));
    expect(getAgentPick('s1')).toBeNull();
  });
});
