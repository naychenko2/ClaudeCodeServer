import { beforeEach, describe, expect, it, vi } from 'vitest';

// Живые сообщения «Картинок» подменяем: тест сам шлёт image_thread_changed
let push: (m: unknown) => void = () => {};
vi.mock('aihome_shell/kit', () => ({
  onMessage: (fn: (m: unknown) => void) => { push = fn; return () => {}; },
  readStoredToken: () => null,
  request: vi.fn(),
}));

const { bindFrame, onFrameReady, unbindFrame } = await import('./imageFrames');

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
