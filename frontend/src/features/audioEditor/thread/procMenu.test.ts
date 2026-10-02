import { describe, expect, it } from 'vitest';

// requestOperation открывает панель событием окна; в node окна нет
const dispatched: { init: { detail: unknown } }[] = [];
(globalThis as unknown as { window: unknown }).window = {
  dispatchEvent: (e: { init: { detail: unknown } }) => { dispatched.push(e); return true; },
  addEventListener: () => {}, removeEventListener: () => {},
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
};
(globalThis as unknown as { CustomEvent: unknown }).CustomEvent = class { constructor(public type: string, public init: { detail: unknown }) {} };

const { DEFAULT_CONCAT } = await import('../panel/inputs');
const { procMenuItems, versionPiece, withFirstPiece } = await import('./procMenu');
const { getOperationRequest, requestOperation } = await import('./threadStore');
import type { AudioThread } from '../api';

const thread: AudioThread = {
  id: 't1', file: 'audio/podcast-intro.mp3', lineage: [], draftFolder: null, createdAt: '',
  versions: [
    { id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, files: [], license: null, createdAt: '' },
    { id: 'v3', number: 3, jobId: 'j', variant: 1, baseVersionId: 'origin', files: [], license: null, createdAt: '' },
  ],
  currentVersionId: 'v3', launches: [], settings: null,
};

describe('«Обработать ▾» в карточке версии', () => {
  it('операции «Обработки», склейка с другим звуком, смена голоса, кавер и кусок', () => {
    const items = procMenuItems();
    const labels = items.map(i => i.label);
    expect(labels).toContain('Стемы');
    expect(labels).toContain('Обрезка и громкость');
    expect(labels).toContain('Склеить с другим звуком');
    expect(items.filter(i => i.group === 'other').map(i => i.op)).toEqual(['convertVoice', 'cover', 'repaint']);
    // Скрытые в панели операции в меню не всплывают
    expect(items.map(i => i.op)).not.toContain('mixStems');
  });

  it('склейка ставит эту версию первым куском, повтор её не дублирует', () => {
    const p = versionPiece(thread, 'origin');
    expect(p).toEqual({ threadId: 't1', versionId: 'origin', label: 'podcast-intro.mp3 · исходник' });
    const other = { threadId: 't2', versionId: 'v1', label: 'b' };
    const c = withFirstPiece({ ...DEFAULT_CONCAT, pieces: [other, p], joints: [{ kind: 'butt', seconds: 0 }] }, p);
    expect(c.pieces).toEqual([p, other]);
    expect(c.joints).toEqual([null]);
    expect(withFirstPiece(c, p)).toBe(c);
  });

  it('просьба к панели несёт кусок и открывает «Звук» на «Настройках»', () => {
    dispatched.length = 0;
    const p = versionPiece(thread, 'v3');
    requestOperation('s1', 't1', 'concat', p);
    expect(getOperationRequest('s1')).toMatchObject({ threadId: 't1', op: 'concat', piece: p });
    expect(dispatched.at(-1)?.init.detail).toMatchObject({ key: 'sound', tab: 'settings' });
    requestOperation('s1', 't1', 'denoise');
    expect(getOperationRequest('s1')?.piece).toBeUndefined();
  });
});
