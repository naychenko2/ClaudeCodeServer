import { describe, it, expect } from 'vitest';
import { hasUltraworkKeyword } from '../ultrawork';

describe('hasUltraworkKeyword', () => {
  it('матчит ключевые слова отдельными словами', () => {
    expect(hasUltraworkKeyword('сделай ультра качественно')).toBe(true);
    expect(hasUltraworkKeyword('ulw')).toBe(true);
    expect(hasUltraworkKeyword('запусти ultrawork прямо сейчас')).toBe(true);
    expect(hasUltraworkKeyword('нужен ультраворк')).toBe(true);
  });

  it('регистронезависим и терпит пунктуацию вокруг слова', () => {
    expect(hasUltraworkKeyword('УЛЬТРА!')).toBe(true);
    expect(hasUltraworkKeyword('Сделай (ULW), пожалуйста')).toBe(true);
  });

  it('не матчит части других слов', () => {
    expect(hasUltraworkKeyword('ультразвук')).toBe(false);
    expect(hasUltraworkKeyword('формула')).toBe(false);
    expect(hasUltraworkKeyword('culw2')).toBe(false);
    expect(hasUltraworkKeyword('обычное сообщение')).toBe(false);
  });
});
