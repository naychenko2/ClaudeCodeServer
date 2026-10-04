import { beforeEach, describe, expect, it, vi } from 'vitest';

// Черновик «Новый звук» сразу попадает в контекст чата: перечитываем его, не дожидаясь события рассылки.
const fakeStorage = (m: Map<string, string>) => ({
  getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v); },
  removeItem: (k: string) => { m.delete(k); }, clear: () => m.clear(), key: () => null, length: 0,
}) as Storage;
vi.stubGlobal('localStorage', fakeStorage(new Map()));
vi.stubGlobal('window', Object.assign(new EventTarget(), {
  setTimeout, clearTimeout, setInterval, clearInterval, innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));

const { chatContextApi } = await import('../../../lib/chatContext/api');
const { __resetChatContextStore, getChatContextState } = await import('../../../lib/chatContext/store');
const { __resetAudioStore } = await import('./threadStore');
const { audioApi } = await import('../api');
const { createDraft } = await import('./actions');

const draft = {
  id: 'd1', file: null, lineage: [], draftFolder: '', stacks: [], currentStackId: null, currentStepId: null,
  settings: null, pendingJobId: null, createdAt: '2026-10-03T00:00:00Z',
};

beforeEach(() => {
  vi.restoreAllMocks();
  __resetAudioStore();
  __resetChatContextStore();
});

describe('createDraft: контекст чата', () => {
  it('после заведения черновика контекст перечитывается и черновик — основной объект', async () => {
    vi.spyOn(audioApi, 'open').mockResolvedValue({ focus: 'd1', revision: 2, threads: [draft] } as never);
    const get = vi.spyOn(chatContextApi, 'get').mockResolvedValue({
      revision: 1, refs: [], primary: { id: 'ci1', kind: 'audio', ref: { threadId: 'd1' }, by: 'human' },
    } as never);
    expect(await createDraft('p1', 's1', 'voice')).toBe(true);
    await vi.waitFor(() => expect(getChatContextState('s1').primary?.ref.threadId).toBe('d1'));
    expect(get).toHaveBeenCalledWith('s1');
  });
});
