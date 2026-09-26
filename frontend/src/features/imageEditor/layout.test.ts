import { describe, expect, it } from 'vitest';
import {
  DEFAULT_SECTIONS, parseSections, promptHeight, promptOverflows, sectionsStorageKey, toggleSection,
} from './layout';

describe('секции левой панели', () => {
  it('по умолчанию открыты пометки, образцы, быстрые действия и история', () => {
    const s = parseSections(null);
    expect(s).toEqual(DEFAULT_SECTIONS);
    expect(s).toEqual({ marks: true, samples: true, chars: false, quick: true, adjust: false, model: false, hist: true });
  });

  it('сохранённое состояние переживает круг «свернуть → записать → прочитать»', () => {
    const s = toggleSection(toggleSection(parseSections(null), 'marks'), 'model');
    const back = parseSections(JSON.stringify(s));
    expect(back.marks).toBe(false);
    expect(back.model).toBe(true);
    expect(back.samples).toBe(true);
  });

  it('мусор и неизвестные ключи не ломают умолчания', () => {
    expect(parseSections('{битое')).toEqual(DEFAULT_SECTIONS);
    expect(parseSections('"строка"')).toEqual(DEFAULT_SECTIONS);
    expect(parseSections(JSON.stringify({ marks: 'да', extra: true, chars: true }))).toEqual({ ...DEFAULT_SECTIONS, chars: true });
  });

  it('ключ хранения свой у каждого пользователя', () => {
    expect(sectionsStorageKey('u1')).not.toBe(sectionsStorageKey('u2'));
  });
});

describe('авторазмер поля промпта', () => {
  it('растёт с текстом до 240 px на десктопе и до 132 px на телефоне', () => {
    expect(promptHeight(10, false)).toBe(64);
    expect(promptHeight(180, false)).toBe(180);
    expect(promptHeight(600, false)).toBe(240);
    expect(promptHeight(100, true)).toBe(100);
    expect(promptHeight(600, true)).toBe(132);
  });

  it('подсказка прокрутки — только когда текст не влез', () => {
    expect(promptOverflows(200, false)).toBe(false);
    expect(promptOverflows(241, false)).toBe(true);
    expect(promptOverflows(200, true)).toBe(true);
  });
});
