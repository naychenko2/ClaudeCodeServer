import { beforeEach, describe, expect, it, vi } from 'vitest';

// Новая сцена сразу попадает в контекст чата: перечитываем его, не дожидаясь события рассылки.
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
const { __resetVideoStore } = await import('../store/videoStore');
const { videoApi } = await import('../api');
const { CATALOG, PREFS, scene, threads } = await import('../mocks');
const { changeSettings, createScene } = await import('./actions');

beforeEach(() => {
  vi.restoreAllMocks();
  __resetVideoStore();
  __resetChatContextStore();
});

describe('createScene: контекст чата', () => {
  it('после заведения сцены контекст перечитывается и сцена — основной объект', async () => {
    vi.spyOn(videoApi, 'state').mockResolvedValue({ threads: threads(1, []), catalog: CATALOG, prefs: PREFS });
    vi.spyOn(videoApi, 'addScene').mockResolvedValue(threads(2, [scene('s1')], { sceneId: 's1' }));
    const get = vi.spyOn(chatContextApi, 'get').mockResolvedValue({
      revision: 1, refs: [], primary: { id: 'ci1', kind: 'video-scene', ref: { sceneId: 's1' }, by: 'human' },
    } as never);
    expect(await createScene('p1', 'c1')).toBe(true);
    await vi.waitFor(() => expect(getChatContextState('c1').primary?.ref.sceneId).toBe('s1'));
    expect(get).toHaveBeenCalledWith('c1');
  });

  it('стоящая правка без сцены сама заводит сцену — createScene второй не заводит', async () => {
    vi.spyOn(videoApi, 'state').mockResolvedValue({ threads: threads(1, []), catalog: CATALOG, prefs: PREFS });
    const add = vi.spyOn(videoApi, 'addScene').mockResolvedValue(threads(2, [scene('s1')], { sceneId: 's1' }));
    vi.spyOn(chatContextApi, 'get').mockResolvedValue({ revision: 1, refs: [], primary: null } as never);
    changeSettings('p1', 'c1', { text: 'закат над горами' }, true);
    expect(await createScene('p1', 'c1')).toBe(true);
    expect(add).toHaveBeenCalledTimes(1);
    // Без стоящей правки createScene заводит сцену как обычно
    expect(await createScene('p1', 'c1')).toBe(true);
    expect(add).toHaveBeenCalledTimes(2);
  });
});
