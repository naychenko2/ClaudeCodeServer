// Рамки лиц на холсте (ADR-028, этап 3 · шаг 16б): виды рамки по состоянию лица, ярлык с именем
// у назначенной, отказ danger, тап по рамке.
import { describe, expect, it, vi } from 'vitest';
import { createElement, type ReactElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { FaceOverlay, type FaceLabel } from './FaceOverlay';
import { C } from 'aihome_shell/kit';
import {
  clearFailures, faceAria, faceSwapView, faceVisual, labelPlace, overlayTap, refusalFaces, FS_TEXT,
  type FaceSwapViewInput, type FaceUi,
} from './faceSwapView';
import type { DetectedFace } from './api';

const face = (n: number, x = n * 200, small = false): DetectedFace => ({ n, bbox: [x, 100, x + 100, 200], score: 0.9, small });
const faces = [face(1), face(2), face(3)];

type Props = Parameters<typeof FaceOverlay>[0];
const props = (over: Partial<Props> = {}): Props => ({
  faces, width: 1000, height: 1000, selectedFace: null, tappedSmall: null, errorFaces: [], mobile: false,
  onSelect: () => {}, ...over,
});
const html = (over: Partial<Props> = {}) => renderToStaticMarkup(createElement(FaceOverlay, props(over)));
// Кусок разметки одной рамки (от её <button до следующей)
const box = (h: string, n: number) => {
  const i = h.indexOf(`aria-label="Лицо ${n}`);
  const start = h.lastIndexOf('<button', i);
  const end = h.indexOf('</button>', i);
  return h.slice(start, end);
};
const maria = new Map<number, FaceLabel>([[1, { name: 'Мария', avatar: null }]]);

describe('FaceOverlay: виды рамки', () => {
  it('свободная — normal, aria «назначить персонажа»', () => {
    const b = box(html(), 2);
    expect(b).toContain('data-face-visual="normal"');
    expect(b).toContain('aria-label="Лицо 2: назначить персонажа"');
  });

  it('активная — selected, остальные свободные приглушены, назначенная — нет', () => {
    const h = html({ selectedFace: 2, labels: maria });
    expect(box(h, 2)).toContain('data-face-visual="selected"');
    expect(box(h, 2)).toContain('aria-pressed="true"');
    expect(box(h, 3)).toContain('data-face-visual="dim"');
    expect(box(h, 1)).toContain('data-face-visual="assigned"');
  });

  it('назначенная — ярлык с именем, aria «Переназначить»', () => {
    const b = box(html({ labels: maria }), 1);
    expect(b).toContain('data-face-visual="assigned"');
    expect(b).toContain('data-face-label="true"');
    expect(b).toContain('>Мария<');
    expect(b).toContain('aria-label="Лицо 1: Мария. Переназначить"');
    // У свободной ярлыка нет
    expect(box(html({ labels: maria }), 2)).not.toContain('data-face-label');
  });

  it('длинное имя обрезается многоточием, ярлык не шире 120', () => {
    const b = box(html({ labels: new Map([[1, { name: 'Очень-очень длинное имя персонажа', avatar: null }]]) }), 1);
    expect(b).toContain('text-overflow:ellipsis');
    expect(b).toContain('max-width:120px');
  });

  it('лимит — limit (C.warning)', () => {
    const b = box(html({ labels: maria, limitFace: 3 }), 3);
    expect(b).toContain('data-face-visual="limit"');
    expect(b).toContain(C.warning);
  });

  it('отказ — рамка, номер и ярлык danger', () => {
    const b = box(html({ labels: maria, errorFaces: [1] }), 1);
    expect(b).toContain('data-face-visual="error"');
    expect(b).toContain('aria-invalid="true"');
    expect(b).toContain(`2px solid ${C.danger}`);
    expect(b).toContain(`background:${C.danger}`);
    expect(b).toContain('>Мария<');
    // Соседняя рамка не красится
    expect(box(html({ labels: maria, errorFaces: [1] }), 2)).not.toContain(C.danger);
  });

  it('номер лица виден на рамке всегда', () => {
    const h = html({ labels: maria, selectedFace: 2, limitFace: 3 });
    for (const n of [1, 2, 3]) expect(box(h, n)).toContain(`>${n}</span>`);
  });

  it('на мобиле зона касания рамки не меньше 44', () => {
    expect(box(html({ mobile: true }), 1)).toContain('min-width:44px');
    expect(box(html({ mobile: true }), 1)).toContain('min-height:44px');
  });
});

describe('FaceOverlay: тап по рамке', () => {
  it('рамка вызывает onSelect со своим номером', () => {
    const onSelect = vi.fn();
    const root = FaceOverlay(props({ onSelect })) as ReactElement<{ children: ReactElement<{ onTap: () => void }>[] }>;
    root.props.children[1].props.onTap();
    expect(onSelect).toHaveBeenCalledWith(2);
  });

  it('overlayTap: обычное лицо → tap, мелкое → только подсветка', () => {
    const h = { tap: vi.fn(), small: vi.fn() };
    overlayTap(2, [face(1), face(2), face(3, 600, true)], h);
    overlayTap(3, [face(1), face(2), face(3, 600, true)], h);
    expect(h.tap).toHaveBeenCalledWith(2);
    expect(h.tap).toHaveBeenCalledTimes(1);
    expect(h.small).toHaveBeenCalledWith(3);
  });
});

describe('логика рамок (чистые функции)', () => {
  it('faceVisual: порядок — отказ, лимит, активная, назначенная, приглушённая', () => {
    const i = { selectedFace: 2, tappedSmall: null, errorFaces: [] as number[], assigned: [1, 2], limitFace: 3 };
    expect(faceVisual(face(1), i)).toBe('assigned');
    expect(faceVisual(face(2), i)).toBe('selected');
    expect(faceVisual(face(3), i)).toBe('limit');
    expect(faceVisual(face(1), { ...i, errorFaces: [1] })).toBe('error');
    expect(faceVisual(face(4), i)).toBe('dim');
  });

  it('refusalFaces: номер из тела — только он; без номера «близко» — все, «вне кадра» — никто', () => {
    const ui = { tooClose: true, faceOutOfFrame: false, refusedFace: 2 };
    expect(refusalFaces(ui, faces)).toEqual([2]);
    expect(refusalFaces({ ...ui, refusedFace: null }, faces)).toEqual([1, 2, 3]);
    expect(refusalFaces({ tooClose: false, faceOutOfFrame: true, refusedFace: null }, faces)).toEqual([]);
    expect(refusalFaces({ tooClose: false, faceOutOfFrame: false, refusedFace: 2 }, faces)).toEqual([]);
  });

  it('clearFailures сбрасывает номер отказавшего лица', () => {
    const ui = { refusedFace: 2, tooClose: true } as FaceUi;
    expect(clearFailures(ui).refusedFace).toBeNull();
  });

  it('отказ с номером при двух назначениях — текст и снятие только по нему', () => {
    const base: FaceSwapViewInput = {
      phase: 'ready', faces, selectedFace: null, selectedCharacter: null, error: null, provider: 'local',
      localAvailable: true, useLocalOnce: false, tappedSmall: null, over8: false, facesTooClose: true,
      faceOutOfFrame: false, busy: false, providerUnavailable: false, facesChanged: false, hasMask: false,
      assignments: { assignments: new Map([[1, 'maria'], [2, 'ivan']]), active: null, limit: null },
    };
    const n = faceSwapView({ ...base, refusedFace: 2 }).notices.find(x => x.unassign);
    expect(n?.text).toBe(FS_TEXT.closeN(2));
    expect(n?.unassign).toEqual([2]);
    // Без номера — как раньше: общий текст и кнопка на каждое
    expect(faceSwapView(base).notices.find(x => x.unassign)?.unassign).toEqual([1, 2]);
  });

  it('faceAria: тексты из v2-notes', () => {
    expect(faceAria(1, false, null)).toBe('Лицо 1: назначить персонажа');
    expect(faceAria(1, false, 'Мария')).toBe('Лицо 1: Мария. Переназначить');
    expect(faceAria(1, true, null)).toBe('Лицо 1: слишком мелкое');
  });

  it('labelPlace: у краёв картинки ярлык прижат к рамке и не вылезает', () => {
    expect(labelPlace({ x: 0.4, y: 0.1, width: 0.1, height: 0.1 })).toEqual({ h: 'center', inside: false });
    expect(labelPlace({ x: 0.85, y: 0.1, width: 0.1, height: 0.1 })).toEqual({ h: 'right', inside: false });
    expect(labelPlace({ x: 0.02, y: 0.1, width: 0.1, height: 0.1 })).toEqual({ h: 'left', inside: false });
    expect(labelPlace({ x: 0.4, y: 0.85, width: 0.1, height: 0.1 })).toEqual({ h: 'center', inside: true });
  });
});
