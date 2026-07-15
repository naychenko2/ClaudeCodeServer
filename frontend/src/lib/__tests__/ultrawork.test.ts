import { describe, it, expect } from 'vitest';
import { hasUltraworkKeyword } from '../ultrawork';

describe('hasUltraworkKeyword', () => {
  it('детектит слова, которые ловит keyword-detector плагина oh-my-claudecode', () => {
    expect(hasUltraworkKeyword('запусти ultrawork прямо сейчас')).toBe(true);
    expect(hasUltraworkKeyword('ulw')).toBe(true);
    expect(hasUltraworkKeyword('Сделай (ULW), пожалуйста')).toBe(true);
  });

  it('не детектит кириллические алиасы — серверной вставки больше нет, хук их не знает', () => {
    expect(hasUltraworkKeyword('сделай ультра качественно')).toBe(false);
    expect(hasUltraworkKeyword('нужен ультраворк')).toBe(false);
  });

  it('не матчит части других слов', () => {
    expect(hasUltraworkKeyword('culw2')).toBe(false);
    expect(hasUltraworkKeyword('schulwahl')).toBe(false);
    expect(hasUltraworkKeyword('обычное сообщение')).toBe(false);
  });
});
