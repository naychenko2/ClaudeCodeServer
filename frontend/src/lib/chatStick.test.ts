import { describe, expect, it } from 'vitest';
import { stickAfterScroll } from './chatStick';

describe('прилипание ленты чата к низу', () => {
  it('программный сдвиг вверх (scrollIntoView карточки) отклеивает ленту — B1 «Звука»', () => {
    expect(stickAfterScroll(true, { atBottom: false, gesture: false, movedUp: true })).toBe(false);
  });

  it('рост контента и домер композера без жеста прилипание не снимают', () => {
    expect(stickAfterScroll(true, { atBottom: false, gesture: false, movedUp: false })).toBe(true);
  });

  it('жест отклеивает, доведённая до конца лента приклеивается снова', () => {
    expect(stickAfterScroll(true, { atBottom: false, gesture: true, movedUp: false })).toBe(false);
    expect(stickAfterScroll(false, { atBottom: true, gesture: false, movedUp: false })).toBe(true);
    expect(stickAfterScroll(false, { atBottom: false, gesture: false, movedUp: false })).toBe(false);
  });
});
