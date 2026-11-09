import { beforeEach, describe, expect, it, vi } from 'vitest';

// Живые сообщения «Картинок» подменяем: тест сам шлёт image_thread_changed
let push: (m: unknown) => void = () => {};
vi.mock('aihome_shell/kit', () => ({
  onMessage: (fn: (m: unknown) => void) => { push = fn; return () => {}; },
  readStoredToken: () => null,
  request: vi.fn(),
}));

const { bindFrame, onFrameReady, unbindFrame, __resetImageFrames, __stateOrder } = await import('./imageFrames');

const state = (versions: { id: string; number: number }[], current: string) => ({
  type: 'image_thread_changed', sessionId: 'c1',
  state: {
    focus: 't1', revision: 1,
    threads: [{
      id: 't1', file: 'video/frames/a.png', lineage: [], currentStepId: 'st0', currentVersionId: current,
      versions: versions.map(v => ({ ...v, currentStepId: null, baseStepId: null })),
    }],
  },
});

describe('M3: «Править кадр» без правки не меняет кадр', () => {
  const seen: string[] = [];
  beforeEach(() => { seen.length = 0; unbindFrame('c1'); });
  onFrameReady((_s, _b, v) => { seen.push(v); });

  it('нить на готовом кадре-файле: «исходник» кадром не становится, настоящая версия — становится', () => {
    bindFrame('c1', { sceneId: 's1', slot: 'A', threadId: 't1', needEdit: true });
    push(state([], 'origin'));
    expect(seen).toEqual([]);
    push(state([{ id: 'v1', number: 1 }], 'v1'));
    expect(seen).toEqual(['v1']);
  });

  it('черновик для кадра (без needEdit): первая версия сразу кадр', () => {
    bindFrame('c1', { sceneId: 's1', slot: 'A', threadId: 't1' });
    push(state([], 'origin'));
    expect(seen).toEqual(['origin']);
  });
});

// LRU-вытеснение: set по существующему ключу раньше порядок не двигал, поэтому
// первой вытеснялась самая давняя И самая активная сессия. Теперь — delete+set,
// а привязанная к сцене сессия не вытесняется никогда.
describe('LRU-вытеснение состояний картинок', () => {
  // Событие image_thread_changed для сессии (push выстроен модульным onFrameReady выше)
  const ev = (sessionId: string) => ({
    type: 'image_thread_changed',
    sessionId,
    state: { focus: null, revision: 1, threads: [] },
  });

  beforeEach(() => { __resetImageFrames(); });

  it('переполнение вытесняет старейшее, а не последнюю вставку', () => {
    for (let i = 0; i < 40; i++) push(ev(`s${i}`));
    push(ev('s40')); // 41-я → переполнение
    const order = __stateOrder();
    expect(order).toHaveLength(40);
    expect(order).not.toContain('s0'); // старейшее вытеснено
    expect(order).toContain('s40');    // свежее осталось
  });

  it('повторный set той же сессии двигает её в хвост (LRU): она вытесняется последней', () => {
    for (let i = 0; i < 40; i++) push(ev(`s${i}`));
    // s0 старейшая, но «активна»: повторяем её set — она уходит в хвост
    push(ev('s0'));
    push(ev('s40')); // переполнение
    const order = __stateOrder();
    expect(order).not.toContain('s1'); // теперь старейшая непривязанная — s1
    expect(order).toContain('s0');     // s0 выжила, т.к. была тронута последней
  });

  it('привязанная к сцене сессия не вытесняется, даже будучи старейшей', () => {
    for (let i = 0; i < 40; i++) push(ev(`s${i}`));
    bindFrame('s0', { sceneId: 'sc', slot: 'A', threadId: 't1' }); // s0 — старейшая
    push(ev('s40')); // переполнение
    const order = __stateOrder();
    expect(order).toHaveLength(40);
    expect(order).toContain('s0');  // привязана — выжила
    expect(order).not.toContain('s1'); // старейшая непривязанная вытеснена
  });
});
