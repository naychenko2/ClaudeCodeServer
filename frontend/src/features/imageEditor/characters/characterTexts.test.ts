import { describe, expect, it } from 'vitest';
import { SHEET } from './characterTexts';

describe('SHEET.photos', () => {
  it('имя из 24 символов подставляется', () => {
    const name = 'я'.repeat(24);
    expect(SHEET.photos(name)).toBe(`Фото ${name}`);
  });
  it('имя из 25 символов — «Фото персонажа»', () => {
    expect(SHEET.photos('я'.repeat(25))).toBe('Фото персонажа');
  });
});
