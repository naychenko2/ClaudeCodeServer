import { describe, expect, it } from 'vitest';
import { TO_END, clampSelection, fmtSelection, fmtTime, keySelection, resolveEnd, selectionFromDrag } from './selection';

describe('выделение куска: обрезка по длине', () => {
  it('конец за длиной версии обрезается до длины', () => {
    expect(clampSelection({ start: 4, end: 30 }, 16)).toEqual({ start: 4, end: 16 });
  });

  it('отрицательное начало прижимается к нулю', () => {
    expect(clampSelection({ start: -3, end: 5 }, 16)).toEqual({ start: 0, end: 5 });
  });

  it('кусок целиком за длиной новой версии снимается', () => {
    expect(clampSelection({ start: 18, end: 20 }, 16)).toBeNull();
  });

  it('перевёрнутый кусок разворачивается', () => {
    expect(clampSelection({ start: 9, end: 3 }, 16)).toEqual({ start: 3, end: 9 });
  });

  it('протяжка за правый край упирается в длину', () => {
    expect(selectionFromDrag(12, 40, 16)).toEqual({ start: 12, end: 16 });
  });

  it('протяжка справа налево даёт тот же кусок', () => {
    expect(selectionFromDrag(11.8, 6.2, 16)).toEqual({ start: 6.2, end: 11.8 });
  });
});

describe('выделение куска: «до конца»', () => {
  it('end = -1 сохраняется при обрезке и разворачивается в длину версии', () => {
    const sel = clampSelection({ start: 5, end: TO_END }, 16);
    expect(sel).toEqual({ start: 5, end: TO_END });
    expect(resolveEnd(sel!, 16)).toBe(16);
    // Версия стала короче — «до конца» тянется за ней, не превращаясь в число
    expect(resolveEnd(clampSelection(sel, 10)!, 10)).toBe(10);
  });

  it('«до конца» при начале за длиной снимается', () => {
    expect(clampSelection({ start: 20, end: TO_END }, 16)).toBeNull();
  });

  it('Shift+End ставит «до конца», не трогая начало', () => {
    expect(keySelection({ start: 3, end: 6 }, { key: 'End', shiftKey: true }, 0, 16)).toEqual({ start: 3, end: TO_END });
  });

  it('Shift+← у «до конца» считает от длины версии', () => {
    expect(keySelection({ start: 3, end: TO_END }, { key: 'ArrowLeft', shiftKey: true }, 0, 16)).toEqual({ start: 3, end: 15.5 });
  });

  it('подпись называет «до конца» словами', () => {
    expect(fmtSelection({ start: 6.2, end: TO_END }, 16)).toBe('0:06.2 – до конца · 9,8 с');
  });
});

describe('выделение куска: клавиатура', () => {
  it('Shift+→ без выделения начинает кусок от курсора', () => {
    expect(keySelection(null, { key: 'ArrowRight', shiftKey: true }, 4, 16)).toEqual({ start: 4, end: 4.5 });
  });

  it('Shift+→ у края не выходит за длину', () => {
    expect(keySelection({ start: 10, end: 16 }, { key: 'ArrowRight', shiftKey: true }, 0, 16)).toEqual({ start: 10, end: 16 });
  });

  it('[ и ] ставят границы в позицию курсора', () => {
    expect(keySelection({ start: 2, end: 9 }, { key: '[' }, 5, 16)).toEqual({ start: 5, end: 9 });
    expect(keySelection({ start: 2, end: 9 }, { key: ']' }, 7, 16)).toEqual({ start: 2, end: 7 });
  });

  it('Escape снимает выделение, стрелка без Shift уходит в перемотку', () => {
    expect(keySelection({ start: 2, end: 9 }, { key: 'Escape' }, 0, 16)).toBeNull();
    expect(keySelection({ start: 2, end: 9 }, { key: 'ArrowRight' }, 0, 16)).toBeUndefined();
  });
});

describe('время', () => {
  it('минуты и десятые, без «0:60.0»', () => {
    expect(fmtTime(4.2)).toBe('0:04.2');
    expect(fmtTime(59.96)).toBe('1:00.0');
    expect(fmtTime(182)).toBe('3:02.0');
  });
});
