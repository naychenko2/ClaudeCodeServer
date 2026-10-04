import { beforeEach, describe, expect, it, vi } from 'vitest';

const fakeStorage = () => ({ getItem: () => null, setItem: () => {}, removeItem: () => {}, clear: () => {}, key: () => null, length: 0 }) as Storage;
vi.stubGlobal('localStorage', fakeStorage());
vi.stubGlobal('sessionStorage', fakeStorage());
vi.stubGlobal('window', Object.assign(new EventTarget(), {
  setTimeout, clearTimeout, setInterval, clearInterval,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));

const { chatContextApi } = await import('../../../lib/chatContext/api');
const { __applyChatContext, __resetChatContextStore } = await import('../../../lib/chatContext/store');
const { frameInputOf, frameOfRef, frameRefOf, setFrameRef } = await import('./frameRefs');
import type { ChatContextDto, ChatContextRef } from '../../../lib/chatContext/types';

const S = 's1';
const ref = (id: string, role: string, at: string, over: Partial<ChatContextRef> = {}): ChatContextRef => ({
  id, kind: 'project-file', ref: { path: `${id}.png` }, role, by: 'human', addedAt: at, label: `${id}.png`,
  version: null, thumb: null, missing: false, usedBy: ['shoot'], ...over,
});
const dto = (revision: number, refs: ChatContextRef[]): ChatContextDto => ({
  revision, refs,
  primary: { id: 'p', kind: 'video-scene', ref: { sceneId: 'sc1' }, by: 'human', addedAt: '2026-10-03T00:00:00Z', label: 'сцена', version: null, thumb: null, missing: false, role: null },
});

let calls: string[];
beforeEach(() => {
  __resetChatContextStore();
  calls = [];
  vi.spyOn(chatContextApi, 'attachRef').mockImplementation(async (_s, input, rev) => {
    calls.push(`attach ${input.role} ${JSON.stringify(input.ref)}`);
    return dto(rev + 1, []);
  });
  vi.spyOn(chatContextApi, 'detachRef').mockImplementation(async (_s, id, rev) => { calls.push(`detach ${id}`); return dto(rev + 1, []); });
});

describe('кадры сцены как референсы', () => {
  it('кадр слота — самый поздно добавленный из подходящих по виду и роли', () => {
    const refs = [
      ref('old', 'frame-a', '2026-10-03T10:00:00Z'), ref('new', 'frame-a', '2026-10-03T11:00:00Z'),
      ref('b', 'frame-b', '2026-10-03T09:00:00Z'), ref('aud', 'frame-a', '2026-10-03T12:00:00Z', { kind: 'audio' }),
    ];
    expect(frameRefOf(refs, 'A')?.id).toBe('new');
    expect(frameRefOf(refs, 'B')?.id).toBe('b');
    expect(frameRefOf([], 'A')).toBeNull();
  });

  it('кадр сцены ↔ референс: картинка с версией и файл проекта; файл личного чата референсом не бывает', () => {
    expect(frameInputOf({ kind: 'image', threadId: 't1', versionId: 'v2' }, false)).toEqual({ kind: 'image', ref: { threadId: 't1', versionId: 'v2' } });
    expect(frameInputOf({ kind: 'file', path: 'a.png' }, false)).toEqual({ kind: 'project-file', ref: { path: 'a.png' } });
    expect(frameInputOf({ kind: 'file', path: 'frames/x.png' }, true)).toBeNull();
    expect(frameOfRef(ref('i', 'frame-a', '', { kind: 'image', ref: { threadId: 't', versionId: 'v' } }))).toEqual({ kind: 'image', threadId: 't', versionId: 'v' });
    expect(frameOfRef(ref('i', 'frame-a', '', { kind: 'image', ref: { threadId: 't' } }))).toBeNull();
    expect(frameOfRef(ref('f', 'frame-b', ''))).toEqual({ kind: 'file', path: 'f.png' });
  });

  it('новый кадр ставится ролью слота, прежние кадры слота снимаются после постановки', async () => {
    __applyChatContext(S, dto(1, [ref('a1', 'frame-a', '2026-10-03T10:00:00Z'), ref('b1', 'frame-b', '2026-10-03T10:00:00Z')]));
    expect(await setFrameRef(S, 'A', { kind: 'project-file', ref: { path: 'n.png' } })).toBe(true);
    expect(calls).toEqual(['attach frame-a {"path":"n.png"}', 'detach a1']);
  });

  it('тот же кадр под той же ролью второй раз не ставится и не снимается', async () => {
    __applyChatContext(S, dto(1, [ref('a1', 'frame-a', '2026-10-03T10:00:00Z')]));
    expect(await setFrameRef(S, 'A', { kind: 'project-file', ref: { path: 'a1.png' } })).toBe(true);
    expect(calls).toEqual([]);
  });

  it('null — убрать кадр слота', async () => {
    __applyChatContext(S, dto(1, [ref('a1', 'frame-a', '2026-10-03T10:00:00Z'), ref('b1', 'frame-b', '2026-10-03T10:00:00Z')]));
    expect(await setFrameRef(S, 'B', null)).toBe(true);
    expect(calls).toEqual(['detach b1']);
  });

  it('отказ постановки не оставляет слот пустым: прежний кадр остаётся', async () => {
    __applyChatContext(S, dto(1, [ref('a1', 'frame-a', '2026-10-03T10:00:00Z')]));
    vi.spyOn(chatContextApi, 'attachRef').mockRejectedValueOnce(new Error('400'));
    expect(await setFrameRef(S, 'A', { kind: 'project-file', ref: { path: 'n.png' } })).toBe(false);
    expect(calls).toEqual([]);
  });
});
