import { beforeEach, describe, expect, it } from 'vitest';

// Окружение node: localStorage и window — минимальные заглушки
const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  length: 0,
} as Storage;
(globalThis as unknown as { window: Pick<Window, 'dispatchEvent' | 'matchMedia'> }).window = {
  dispatchEvent: () => true,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }) as unknown as MediaQueryList,
};

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { TO_END } from '../player/selection';
import { __resetAudioStore, getSelection, setSelection } from '../thread/threadStore';
import { DEFAULT_INPUTS } from './inputs';
import { commitPiece, pieceFromText, pieceText } from './piece';
import { PieceField } from './PieceField';
import { trimSteps, trimWithPiece } from './run';

const PIECE = { sessionId: 's1', threadId: 't1', versionId: 'v1' };

beforeEach(() => {
  localStorage.clear();
  __resetAudioStore();
});

describe('кусок ⇄ волна', () => {
  it('поле → волна: вписанное время становится выделением нити', () => {
    commitPiece('s1', 't1', 'v1', { start: '1:02.5', end: '70', toEnd: false });
    expect(getSelection('s1', 't1')).toEqual({ start: 62.5, end: 70, versionId: 'v1' });
    commitPiece('s1', 't1', 'v1', { start: '3', end: '', toEnd: true });
    expect(getSelection('s1', 't1')).toEqual({ start: 3, end: TO_END, versionId: 'v1' });
    // Недобор при наборе выделение не трогает, пустые поля — снимают
    commitPiece('s1', 't1', 'v1', { start: '1:', end: '', toEnd: false });
    expect(getSelection('s1', 't1')).toEqual({ start: 3, end: TO_END, versionId: 'v1' });
    commitPiece('s1', 't1', 'v1', { start: '', end: '', toEnd: false });
    expect(getSelection('s1', 't1')).toBeNull();
  });

  it('волна → поле: выделение на волне показывается в «Куске»', () => {
    setSelection('s1', 't1', { start: 4.2, end: 9.8, versionId: 'v1' });
    expect(pieceText(getSelection('s1', 't1'))).toEqual({ start: '4.2', end: '9.8', toEnd: false });
    const html = renderToStaticMarkup(createElement(PieceField, { binding: PIECE }));
    expect(html).toContain('value="4.2"');
    expect(html).toContain('value="9.8"');
    expect(html).toContain('data-piece-linked');
    expect(pieceFromText({ start: '5', end: '2', toEnd: false })).toBeUndefined();
  });

  it('кусок уходит в обрезку: без конца, если выделение «до конца»', () => {
    const sel = { start: 2, end: TO_END };
    expect(trimSteps(trimWithPiece(DEFAULT_INPUTS.trim, { start: 1, end: 4 }))[0]).toMatchObject({ op: 'trim', startSec: 1, endSec: 4 });
    expect(trimSteps(trimWithPiece(DEFAULT_INPUTS.trim, sel))[0]).toMatchObject({ startSec: 2, endSec: null });
    expect(trimSteps(trimWithPiece({ ...DEFAULT_INPUTS.trim, start: 7, end: 9 }, null))).toEqual([]);
  });
});
