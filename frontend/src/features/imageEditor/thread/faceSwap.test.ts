import { describe, expect, it } from 'vitest';
import { facesError, type FacesResponse } from '../api';
import {
  faceSwapCanSubmit, faceSwapReducer, faceSelectable, initialFaceSwap, selectableFaces,
  type FaceSwapState,
} from './faceSwap';

// ── Чистая логика: доступность лиц ─────────────────────────────────────────────
const face = (n: number, small = false) => ({ n, bbox: [0, 0, 10, 10] as [number, number, number, number], score: 0.9, small });

describe('faceSelectable / selectableFaces', () => {
  it('мелкое лицо (small) недоступно, обычное — доступно', () => {
    expect(faceSelectable(face(1))).toBe(true);
    expect(faceSelectable(face(1, true))).toBe(false);
  });

  it('selectableFaces оставляет только не-small', () => {
    expect(selectableFaces([face(1), face(2, true), face(3)]).map(f => f.n)).toEqual([1, 3]);
  });
});

// ── Редьюсер: авто-выбор и сброс ───────────────────────────────────────────────
const resp = (faces: number[], smallAt?: number[]): FacesResponse => ({
  facesKey: 'fk', width: 100, height: 100,
  faces: faces.map((n, i) => face(n, smallAt?.includes(i) ?? false)),
});

describe('faceSwapReducer: авто-выбор', () => {
  it('одно доступное лицо выбирается сразу', () => {
    const s = faceSwapReducer(initialFaceSwap, { type: 'searchOk', response: resp([1]) });
    expect(s.phase).toBe('ready');
    expect(s.selectedFace).toBe(1);
  });

  it('мелкое лицо не выбирается (доступных нет)', () => {
    const s = faceSwapReducer(initialFaceSwap, { type: 'searchOk', response: resp([1], [0]) });
    expect(s.selectedFace).toBeNull();
  });

  it('несколько доступных лиц — авто-выбора нет, человек выбирает', () => {
    const s = faceSwapReducer(initialFaceSwap, { type: 'searchOk', response: resp([1, 2]) });
    expect(s.selectedFace).toBeNull();
  });

  it('faces_changed (409) обновляет список и СБРАСЫВАЕТ выбор', () => {
    const withSel = faceSwapReducer(initialFaceSwap, { type: 'searchOk', response: resp([1]) });
    expect(withSel.selectedFace).toBe(1);
    const after = faceSwapReducer(withSel, { type: 'facesChanged', response: resp([2, 3]) });
    expect(after.faces.map(f => f.n)).toEqual([2, 3]);
    expect(after.selectedFace).toBeNull();
  });

  it('повторный searchOk без reset сохраняет валидный выбор', () => {
    const withSel = faceSwapReducer(initialFaceSwap, { type: 'searchOk', response: resp([1, 2]) });
    faceSwapReducer(withSel, { type: 'selectFace', n: 2 });
    const kept = faceSwapReducer({ ...withSel, selectedFace: 2 }, { type: 'searchOk', response: resp([1, 2]) });
    expect(kept.selectedFace).toBe(2);
  });
});

// ── Редьюсер: выбор персонажа и canSubmit ──────────────────────────────────────
describe('faceSwapReducer: персонаж и canSubmit', () => {
  it('персонаж по умолчанию не выбран (персонаж контекста сам не подставляется)', () => {
    const s = faceSwapReducer(initialFaceSwap, { type: 'searchOk', response: resp([1]) });
    expect(s.selectedCharacter).toBeNull();
  });

  it('canSubmit: лицо+персонаж+local → true', () => {
    let s: FaceSwapState = faceSwapReducer(initialFaceSwap, { type: 'searchOk', response: resp([1]) });
    s = faceSwapReducer(s, { type: 'selectCharacter', slug: 'marina' });
    expect(faceSwapCanSubmit(s, 'local')).toBe(true);
  });

  it('canSubmit: без персонажа → false', () => {
    const s = faceSwapReducer(initialFaceSwap, { type: 'searchOk', response: resp([1]) });
    expect(faceSwapCanSubmit(s, 'local')).toBe(false);
  });

  it('canSubmit: без лица → false', () => {
    let s: FaceSwapState = faceSwapReducer(initialFaceSwap, { type: 'searchOk', response: resp([1, 2]) });
    s = faceSwapReducer(s, { type: 'selectCharacter', slug: 'marina' });
    expect(faceSwapCanSubmit(s, 'local')).toBe(false);
  });

  it('canSubmit: не-local (fal) → false, даже с лицом и персонажем', () => {
    let s: FaceSwapState = faceSwapReducer(initialFaceSwap, { type: 'searchOk', response: resp([1]) });
    s = faceSwapReducer(s, { type: 'selectCharacter', slug: 'marina' });
    expect(faceSwapCanSubmit(s, 'fal')).toBe(false);
    expect(faceSwapCanSubmit(s, null)).toBe(false);
  });
});

// ── Парсер ошибок findFaces ───────────────────────────────────────────────────
const err = (status: number, body: Record<string, unknown>) =>
  Object.assign(new Error('x'), { status, body });

describe('facesError: разбор по кодам/статусам', () => {
  it('busy (503) → busy', () => {
    expect(facesError(err(503, { code: 'busy' }))).toEqual({ code: 'busy' });
  });

  it('provider_unavailable → provider_unavailable', () => {
    expect(facesError(err(409, { code: 'provider_unavailable' }))).toEqual({ code: 'provider_unavailable' });
  });

  it('version_not_found → version_not_found', () => {
    expect(facesError(err(404, { code: 'version_not_found' }))).toEqual({ code: 'version_not_found' });
  });

  it('flag_off → flag_off', () => {
    expect(facesError(err(409, { code: 'flag_off' }))).toEqual({ code: 'flag_off' });
  });

  it('404 без кода → not_found (личная область)', () => {
    expect(facesError(err(404, {}))).toEqual({ code: 'not_found' });
  });

  it('503 без кода → busy', () => {
    expect(facesError(err(503, {}))).toEqual({ code: 'busy' });
  });

  it('faces_changed (409) → faces_changed со свежим списком', () => {
    const fresh: FacesResponse = { facesKey: 'fk2', width: 100, height: 100, faces: [face(1), face(2)] };
    const e = facesError(err(409, { code: 'faces_changed', faces: fresh }));
    expect(e.code).toBe('faces_changed');
    expect(e.faces).toEqual(fresh);
  });

  it('faces_changed без списка → faces_changed, faces не задан', () => {
    const e = facesError(err(409, { code: 'faces_changed' }));
    expect(e.code).toBe('faces_changed');
    expect(e).not.toHaveProperty('faces');
  });

  it('неизвестный код → other', () => {
    expect(facesError(err(400, { code: 'weird_code' }))).toEqual({ code: 'other' });
  });

  it('нет ни кода, ни статуса → other', () => {
    expect(facesError(new Error('сеть'))).toEqual({ code: 'other' });
  });
});
