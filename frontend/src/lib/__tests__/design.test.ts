import { describe, it, expect } from 'vitest';
import { C, FONT, GROUP_COLORS } from '../design';

// Санити-проверки дизайн-токенов. С появлением тёмной темы значения C — ссылки
// на CSS-переменные (var(--c-*)), конкретные hex обеих тем живут в theme.css.

const HEX = /^#[0-9A-Fa-f]{6}$/;
const CSS_VAR = /^var\(--[a-z0-9-]+\)$/;

describe('дизайн-токены', () => {
  it('акцентный цвет — ссылка на переменную темы', () => {
    expect(C.accent).toBe('var(--c-accent)');
  });

  it('все цвета C — ссылки на CSS-переменные (переключаются темой)', () => {
    for (const [key, value] of Object.entries(C)) {
      expect(value, `C.${key}`).toMatch(CSS_VAR);
    }
  });

  it('палитра групп GROUP_COLORS — hex-цвета без дублей', () => {
    for (const color of GROUP_COLORS) {
      expect(color).toMatch(HEX);
    }
    expect(new Set(GROUP_COLORS).size).toBe(GROUP_COLORS.length);
  });

  it('семейства шрифтов заданы и содержат фирменные шрифты', () => {
    expect(FONT.sans).toContain('Hanken Grotesk');
    expect(FONT.serif).toContain('PT Serif');
    expect(FONT.mono).toContain('JetBrains Mono');
  });
});
