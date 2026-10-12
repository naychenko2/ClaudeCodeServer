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

// searchStart → requestSeq=1; searchOk/searchError/facesChanged с requestId=1 применяются
const searchOk = (r: FacesResponse, reset = false) =>
  faceSwapReducer(
    faceSwapReducer(initialFaceSwap, { type: 'searchStart' }),
    { type: 'searchOk', response: r, reset, requestId: 1 },
  );

describe('faceSwapReducer: авто-выбор', () => {
  it('одно доступное лицо выбирается сразу', () => {
    const s = searchOk(resp([1]));
    expect(s.phase).toBe('ready');
    expect(s.selectedFace).toBe(1);
  });

  it('мелкое лицо не выбирается (доступных нет)', () => {
    const s = searchOk(resp([1], [0]));
    expect(s.selectedFace).toBeNull();
  });

  it('несколько доступных лиц — авто-выбора нет, человек выбирает', () => {
    const s = searchOk(resp([1, 2]));
    expect(s.selectedFace).toBeNull();
  });

  it('faces_changed (409) обновляет список и СБРАСЫВАЕТ выбор', () => {
    const withSel = searchOk(resp([1]));
    expect(withSel.selectedFace).toBe(1);
    const after = faceSwapReducer(withSel, { type: 'facesChanged', response: resp([2, 3]), requestId: 1 });
    expect(after.faces.map(f => f.n)).toEqual([2, 3]);
    expect(after.selectedFace).toBeNull();
  });

  it('повторный searchOk без reset сохраняет валидный выбор', () => {
    const withSel = searchOk(resp([1, 2]));
    faceSwapReducer(withSel, { type: 'selectFace', n: 2 });
    const kept = faceSwapReducer({ ...withSel, selectedFace: 2 }, { type: 'searchOk', response: resp([1, 2]), requestId: 1 });
    expect(kept.selectedFace).toBe(2);
  });
});

// ── Редьюсер: защита от out-of-order ───────────────────────────────────────────
describe('faceSwapReducer: out-of-order', () => {
  it('ответ запроса №1 после запроса №2 игнорируется (не затирает ни список, ни выбор)', () => {
    // Запрос №1: searchStart → requestSeq=1
    let s = faceSwapReducer(initialFaceSwap, { type: 'searchStart' });
    // Запрос №2: searchStart → requestSeq=2
    s = faceSwapReducer(s, { type: 'searchStart' });
    // Ответ запроса №2 приходит первым: requestId=2 === requestSeq=2 → применяется
    s = faceSwapReducer(s, { type: 'searchOk', response: resp([3, 4]), requestId: 2 });
    expect(s.faces.map(f => f.n)).toEqual([3, 4]);
    expect(s.phase).toBe('ready');
    // Ответ запроса №1 приходит вторым: requestId=1 !== requestSeq=2 → игнорируется
    const stale = faceSwapReducer(s, { type: 'searchOk', response: resp([1, 2]), requestId: 1 });
    expect(stale).toBe(s); // === по ссылке: состояние не изменилось
  });

  it('searchError с устаревшим requestId игнорируется', () => {
    let s = faceSwapReducer(initialFaceSwap, { type: 'searchStart' });
    s = faceSwapReducer(s, { type: 'searchStart' });
    // Запрос №2: error
    s = faceSwapReducer(s, { type: 'searchError', code: 'busy', requestId: 2 });
    expect(s.phase).toBe('error');
    // Запрос №1: error (устаревший)
    const stale = faceSwapReducer(s, { type: 'searchError', code: 'provider_unavailable', requestId: 1 });
    expect(stale).toBe(s);
  });

  it('facesChanged с устаревшим requestId игнорируется', () => {
    let s = faceSwapReducer(initialFaceSwap, { type: 'searchStart' });
    s = faceSwapReducer(s, { type: 'searchStart' });
    // Запрос №2: faces_changed
    s = faceSwapReducer(s, { type: 'facesChanged', response: resp([5]), requestId: 2 });
    expect(s.faces.map(f => f.n)).toEqual([5]);
    // Запрос №1: faces_changed (устаревший)
    const stale = faceSwapReducer(s, { type: 'facesChanged', response: resp([9]), requestId: 1 });
    expect(stale).toBe(s);
  });

  // Наблюдение Глеба (10b): экран обязан на смену версии/нити запускать НОВЫЙ поиск;
  // устаревший ответ прежней версии не применяется. Это и есть out-of-order редьюсера,
  // но в терминах смены версии: поиск версии A, смена на B (новый searchStart), ответ A — мимо
  it('смена версии: новый поиск, устаревший ответ прежней версии не применяется', () => {
    let s = initialFaceSwap;
    // версия A: поиск №1 → одно лицо [1], авто-выбрано
    s = faceSwapReducer(s, { type: 'searchStart' });
    s = faceSwapReducer(s, { type: 'searchOk', response: resp([1]), requestId: 1 });
    expect(s.faces.map(f => f.n)).toEqual([1]);
    expect(s.selectedFace).toBe(1);
    // смена на версию B: новый поиск №2
    s = faceSwapReducer(s, { type: 'searchStart' });
    // устаревший ответ версии A (requestId 1) приходит после старта поиска B → игнорируется
    const stale = faceSwapReducer(s, { type: 'searchOk', response: resp([1]), requestId: 1 });
    expect(stale).toBe(s);
    // ответ версии B (requestId 2) → новое лицо; выбор — новое, а не устаревшее лицо A
    s = faceSwapReducer(s, { type: 'searchOk', response: resp([2]), requestId: 2 });
    expect(s.faces.map(f => f.n)).toEqual([2]);
    expect(s.selectedFace).toBe(2); // autoSelect нового лица, не face 1 прежней версии
  });

  it('unmount обнуляет requestSeq: ответ после размонтирования не применяется', () => {
    let s = faceSwapReducer(initialFaceSwap, { type: 'searchStart' });
    // requestSeq=1, ответ в пути
    s = faceSwapReducer(s, { type: 'unmount' });
    expect(s.requestSeq).toBe(0);
    // Ответ запроса №1 приходит после unmount: requestId=1 !== requestSeq=0 → игнорируется
    const stale = faceSwapReducer(s, { type: 'searchOk', response: resp([1]), requestId: 1 });
    expect(stale).toBe(s);
  });
});

// ── Редьюсер: выбор персонажа и canSubmit ──────────────────────────────────────
describe('faceSwapReducer: персонаж и canSubmit', () => {
  it('персонаж по умолчанию не выбран (персонаж контекста сам не подставляется)', () => {
    const s = searchOk(resp([1]));
    expect(s.selectedCharacter).toBeNull();
  });

  it('canSubmit: лицо+персонаж+local → true', () => {
    let s: FaceSwapState = searchOk(resp([1]));
    s = faceSwapReducer(s, { type: 'selectCharacter', slug: 'marina' });
    expect(faceSwapCanSubmit(s, 'local')).toBe(true);
  });

  it('canSubmit: без персонажа → false', () => {
    const s = searchOk(resp([1]));
    expect(faceSwapCanSubmit(s, 'local')).toBe(false);
  });

  it('canSubmit: без лица → false', () => {
    let s: FaceSwapState = searchOk(resp([1, 2]));
    s = faceSwapReducer(s, { type: 'selectCharacter', slug: 'marina' });
    expect(faceSwapCanSubmit(s, 'local')).toBe(false);
  });

  it('canSubmit: не-local (fal) → false, даже с лицом и персонажем', () => {
    let s: FaceSwapState = searchOk(resp([1]));
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

  it('feature_disabled → feature_disabled', () => {
    expect(facesError(err(409, { code: 'feature_disabled' }))).toEqual({ code: 'feature_disabled' });
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
