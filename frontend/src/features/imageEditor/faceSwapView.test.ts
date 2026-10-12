import { describe, expect, it } from 'vitest';
import type { DetectedFace } from './api';
import {
  faceCaption, faceRect, faceSwapEntryVisible, faceSwapView, faceTargetCaption, faceVisual,
  type FaceSwapViewInput,
} from './faceSwapView';

const face = (n: number, small = false): DetectedFace => ({ n, bbox: [10, 10, 30, 30], score: 0.9, small });

// Базовый вход: готово, два лица, локальные модели, ни одно не выбрано
const base = (over: Partial<FaceSwapViewInput> = {}): FaceSwapViewInput => ({
  phase: 'ready', faces: [face(1), face(2)], selectedFace: null, selectedCharacter: null,
  error: null, provider: 'local', localAvailable: true, useLocalOnce: false,
  tappedSmall: null, over8: false, facesTooClose: false, busy: false, facesChanged: false, hasMask: false,
  ...over,
});

// ── Масштаб рамок bbox→доли ───────────────────────────────────────────────────
describe('faceRect: bbox в пикселях → доли', () => {
  it('координаты делятся на размеры картинки', () => {
    const f = { bbox: [100, 50, 200, 150] as [number, number, number, number] };
    expect(faceRect(f, 1000, 500)).toEqual({ x: 0.1, y: 0.1, width: 0.1, height: 0.2 });
  });

  it('защита от деления на ноль: размеры 0 не дают NaN/Infinity', () => {
    const f = { bbox: [10, 10, 30, 30] as [number, number, number, number] };
    const r = faceRect(f, 0, 0);
    expect(Number.isFinite(r.x) && Number.isFinite(r.width)).toBe(true);
  });
});

// ── Видимость входа ───────────────────────────────────────────────────────────
describe('faceSwapEntryVisible: вход скрыт', () => {
  const on = { personal: false, flag: true, hasCharacters: true, localAvailable: true };
  it('все условия — вход есть', () => expect(faceSwapEntryVisible(on)).toBe(true));
  it('флаг off — нет', () => expect(faceSwapEntryVisible({ ...on, flag: false })).toBe(false));
  it('личная область — нет', () => expect(faceSwapEntryVisible({ ...on, personal: true })).toBe(false));
  it('нет персонажей — нет', () => expect(faceSwapEntryVisible({ ...on, hasCharacters: false })).toBe(false));
  it('локальные модели недоступны — нет', () => expect(faceSwapEntryVisible({ ...on, localAvailable: false })).toBe(false));
});

// ── canSubmit по матрице ──────────────────────────────────────────────────────
describe('faceSwapView: canSubmit', () => {
  it('лицо+персонаж+local → true', () => {
    expect(faceSwapView(base({ selectedFace: 1, selectedCharacter: 'marina' })).canSubmit).toBe(true);
  });
  it('без лица → false', () => {
    expect(faceSwapView(base({ selectedCharacter: 'marina' })).canSubmit).toBe(false);
  });
  it('без персонажа → false', () => {
    expect(faceSwapView(base({ selectedFace: 1 })).canSubmit).toBe(false);
  });
  it('провайдер не local (fal) → false, даже с лицом и персонажем', () => {
    expect(faceSwapView(base({ selectedFace: 1, selectedCharacter: 'marina', provider: 'fal' })).canSubmit).toBe(false);
  });
  it('локальные недоступны → false', () => {
    expect(faceSwapView(base({ selectedFace: 1, selectedCharacter: 'marina', localAvailable: false })).canSubmit).toBe(false);
  });
  it('useLocalOnce (9б): не-local переключён на этот запуск → true', () => {
    expect(faceSwapView(base({ selectedFace: 1, selectedCharacter: 'marina', provider: 'fal', useLocalOnce: true })).canSubmit).toBe(true);
  });
  it('«лиц нет» + закрашено кистью → true (состояние 2)', () => {
    expect(faceSwapView(base({ faces: [], hasMask: true, selectedCharacter: 'marina' })).canSubmit).toBe(true);
  });
  it('«лиц нет» без кисти → false', () => {
    expect(faceSwapView(base({ faces: [], selectedCharacter: 'marina' })).canSubmit).toBe(false);
  });
  it('отказ «близко» (7) → false, пока выбор не изменится', () => {
    expect(faceSwapView(base({ selectedFace: 1, selectedCharacter: 'marina', facesTooClose: true })).canSubmit).toBe(false);
  });
});

// ── Плашка режима (шаг) ──────────────────────────────────────────────────────
describe('faceSwapView: плашка режима', () => {
  it('поиск (1): спиннер, текст «Ищу лица…»', () => {
    const v = faceSwapView(base({ phase: 'loading' }));
    expect(v.barIcon).toBe('search');
    expect(v.bar).toContain('Ищу лица');
  });
  it('поиск в очереди (1б): часы, текст про очередь', () => {
    const v = faceSwapView(base({ phase: 'loading', error: 'busy' }));
    expect(v.barIcon).toBe('clock');
    expect(v.bar).toContain('Видеокарта занята');
  });
  it('одно лицо (3): «Нашёлся один человек…»', () => {
    expect(faceSwapView(base({ faces: [face(1)] })).bar).toContain('один человек');
  });
  it('несколько лиц (4): «Нажмите на лицо…»', () => {
    expect(faceSwapView(base()).bar).toContain('Нажмите на лицо');
  });
});

// ── Плашки (notices) ──────────────────────────────────────────────────────────
describe('faceSwapView: плашки', () => {
  it('«лиц нет» (2): info + «Закрасить кистью»', () => {
    const v = faceSwapView(base({ faces: [] }));
    expect(v.notices).toHaveLength(1);
    expect(v.notices[0]).toMatchObject({ tone: 'info', action: { label: 'Закрасить кистью' } });
  });
  it('больше 8 (5): info «Показаны 8 самых крупных лиц»', () => {
    expect(faceSwapView(base({ over8: true })).notices.some(n => n.text.includes('8 самых крупных'))).toBe(true);
  });
  it('мелкое лицо (6): warning + «Закрасить кистью»', () => {
    const v = faceSwapView(base({ tappedSmall: 3 }));
    expect(v.notices.some(n => n.tone === 'warning' && n.action?.label === 'Закрасить кистью')).toBe(true);
  });
  it('«близко» (7): danger + «Закрасить кистью»', () => {
    const v = faceSwapView(base({ facesTooClose: true }));
    expect(v.notices.some(n => n.tone === 'danger' && n.text.includes('близко'))).toBe(true);
  });
  it('занята долгой задачей (8): danger', () => {
    expect(faceSwapView(base({ busy: true })).notices.some(n => n.tone === 'danger' && n.icon === 'clock')).toBe(true);
  });
  it('провайдер не local (9): warning + «Использовать локальные…»', () => {
    const v = faceSwapView(base({ provider: 'fal' }));
    expect(v.notices.some(n => n.tone === 'warning' && n.action?.label === 'Использовать локальные для этого запуска')).toBe(true);
  });
  it('409 faces_changed (10): info «Картинка изменилась…»', () => {
    expect(faceSwapView(base({ facesChanged: true })).notices.some(n => n.tone === 'info' && n.icon === 'redo')).toBe(true);
  });
});

// ── Список персонажей: доступность ────────────────────────────────────────────
describe('faceSwapView: список персонажей', () => {
  it('до выбора лица (несколько) — недоступен', () => {
    expect(faceSwapView(base()).listEnabled).toBe(false);
  });
  it('лицо выбрано — доступен', () => {
    expect(faceSwapView(base({ selectedFace: 1 })).listEnabled).toBe(true);
  });
  it('«лиц нет» — доступен (после закраски)', () => {
    expect(faceSwapView(base({ faces: [] })).listEnabled).toBe(true);
  });
});

// ── Бейдж «локальные · этот запуск» (9б) ──────────────────────────────────────
describe('faceSwapView: localOnce', () => {
  it('useLocalOnce + локальные доступны → бейдж', () => {
    expect(faceSwapView(base({ provider: 'fal', useLocalOnce: true })).localOnce).toBe(true);
  });
  it('без переключения — нет', () => {
    expect(faceSwapView(base()).localOnce).toBe(false);
  });
});

// ── Состояние рамки лица ─────────────────────────────────────────────────────
describe('faceVisual: рамка', () => {
  const i = { selectedFace: 2, tappedSmall: null, errorFaces: [] };
  it('выбранное → selected', () => expect(faceVisual(face(2), i)).toBe('selected'));
  it('остальные при выбранной → dim', () => expect(faceVisual(face(1), i)).toBe('dim'));
  it('мелкое → small (не выбирается)', () => expect(faceVisual(face(3, true), i)).toBe('small'));
  it('мелкое тапнуто → smallTapped', () => expect(faceVisual(face(3, true), { ...i, tappedSmall: 3 })).toBe('smallTapped'));
  it('«близко» → error', () => expect(faceVisual(face(3), { ...i, errorFaces: [3] })).toBe('error'));
  it('ничего не выбрано → normal', () => expect(faceVisual(face(1), { selectedFace: null, tappedSmall: null, errorFaces: [] })).toBe('normal'));
});

// ── Подпись карточки ──────────────────────────────────────────────────────────
describe('faceCaption', () => {
  it('«{Имя} → лицо {n}»', () => expect(faceCaption('Марина', 2)).toBe('Марина → лицо 2'));
});

describe('faceTargetCaption: выбор подписи на карточке запуска', () => {
  it('faceTarget есть — подпись «{Имя} → лицо {n}»', () => {
    expect(faceTargetCaption({ n: 2, characterSlug: 'marina' }, 'Марина')).toBe('Марина → лицо 2');
  });
  it('faceTarget нет — подписи нет (карточка прежняя)', () => {
    expect(faceTargetCaption(null, 'Марина')).toBeNull();
    expect(faceTargetCaption(undefined, 'Марина')).toBeNull();
  });
  it('имя персонажа по slug; персонаж не найден в списке — fallback на slug', () => {
    expect(faceTargetCaption({ n: 1, characterSlug: 'marina' }, null)).toBe('marina → лицо 1');
    expect(faceTargetCaption({ n: 1, characterSlug: 'marina' }, '  ')).toBe('marina → лицо 1');
  });
});
