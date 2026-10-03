import { beforeEach, describe, expect, it, vi } from 'vitest';

const ls = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => ls.get(k) ?? null,
  setItem: (k: string, v: string) => { ls.set(k, String(v)); },
  removeItem: (k: string) => { ls.delete(k); },
  clear: () => ls.clear(),
  key: () => null,
  length: 0,
} as Storage;
const dispatched: { type: string; detail: unknown }[] = [];
(globalThis as unknown as { window: Pick<Window, 'dispatchEvent'> }).window = {
  dispatchEvent: (e: Event) => { dispatched.push({ type: e.type, detail: (e as CustomEvent).detail }); return true; },
};

import { __resetComposerStrips, __resetStripHolds, getActiveStrip, requestStrip } from '../../../lib/composerStrips';
import { REVEAL_PANEL_EVENT } from '../../../lib/subsystems/registryCore';
import { videoApi } from '../api';
import { film } from '../mocks';
import { VIDEO_STRIP } from '../store/videoStore';
import { composeForFilm } from './compose';

const AVAIL = ['git', VIDEO_STRIP, 'sound'];

beforeEach(() => {
  ls.clear();
  dispatched.length = 0;
  __resetComposerStrips();
  __resetStripHolds();
  vi.restoreAllMocks();
});

describe('«Сочинить под фильм…»: решение v7 №9', () => {
  it('полоса остаётся на «Видео», «Звук» открывается рядом со ссылкой «К фильму»', async () => {
    requestStrip('c1', VIDEO_STRIP);
    vi.spyOn(videoApi, 'composeMusic').mockResolvedValue({ threadId: 't1' });
    expect(await composeForFilm('p1', 'c1', 'Мой', film())).toBe(true);
    // стор «Звука» при смене фокуса нитей просит свою полосу — запрос не должен сработать
    requestStrip('c1', 'sound');
    expect(getActiveStrip('c1', AVAIL)).toBe(VIDEO_STRIP);
    const reveal = dispatched.find(d => d.type === REVEAL_PANEL_EVENT)?.detail as { key: string; opts?: { returnTo?: { key: string; label: string } } } | undefined;
    expect(reveal?.key).toBe('sound');
    expect(JSON.stringify(reveal)).toContain('К фильму «Мой»');
    expect(JSON.stringify(reveal)).toContain('"strip":"video"');
  });

  it('отказ сервера снимает удержание: «Звук» снова может просить полосу', async () => {
    requestStrip('c1', VIDEO_STRIP);
    vi.spyOn(videoApi, 'composeMusic').mockRejectedValue(new Error('нет'));
    expect(await composeForFilm('p1', 'c1', 'Мой', film())).toBe(false);
    requestStrip('c1', 'sound');
    expect(getActiveStrip('c1', AVAIL)).toBe('sound');
  });
});
