import { describe, it, expect } from 'vitest';
import { effectiveTierModel, parseTier } from '../modelTiers';

describe('parseTier — уровень модели с провода', () => {
  it('принимает имена слотов', () => {
    expect(parseTier('strong')).toBe('strong');
    expect(parseTier('medium')).toBe('medium');
    expect(parseTier('weak')).toBe('weak');
  });

  it('пустое/незнакомое значение — «не задан»', () => {
    expect(parseTier(null)).toBe('');
    expect(parseTier(undefined)).toBe('');
    expect(parseTier('')).toBe('');
    expect(parseTier('Strong')).toBe('');   // с бэка приходит camelCase
    expect(parseTier('0')).toBe('');        // индекс enum уровнем не считаем
  });
});

describe('effectiveTierModel — модель за слотом', () => {
  const global = { strong: 'opus', medium: 'sonnet', weak: 'haiku' };

  it('личный слот сильнее глобального', () => {
    expect(effectiveTierModel('strong', { strong: 'glm-5.2', medium: null, weak: null }, global))
      .toBe('glm-5.2');
  });

  it('без личного слота — глобальный', () => {
    expect(effectiveTierModel('medium', { strong: 'glm-5.2', medium: null, weak: null }, global))
      .toBe('sonnet');
  });

  it('пустая строка в личном слоте трактуется как наследование', () => {
    // saveTier(t, '') оптимистично кладёт пустую строку до ответа сервера —
    // подпись не должна мигать на «не задана»
    expect(effectiveTierModel('weak', { strong: null, medium: null, weak: '' }, global))
      .toBe('haiku');
  });

  it('оба слота пусты — модель не задана (решает CLI)', () => {
    expect(effectiveTierModel('strong', null, { strong: null, medium: null, weak: null })).toBe('');
    expect(effectiveTierModel('strong', null, null)).toBe('');
  });
});
