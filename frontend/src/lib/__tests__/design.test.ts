import { describe, it, expect } from 'vitest';
import { C, FONT, GROUP_COLORS } from '../design';

// Санити-проверки дизайн-токенов: валидные CSS-цвета и ключевые значения дизайн-системы.

const HEX = /^#[0-9A-Fa-f]{6}$/;
const CSS_COLOR = /^(#[0-9A-Fa-f]{6}|#[0-9A-Fa-f]{8}|rgba?\([\d\s.,%]+\))$/;

describe('дизайн-токены', () => {
  it('акцентный цвет соответствует дизайн-системе', () => {
    expect(C.accent).toBe('#D97757');
  });

  it('все цвета C — валидные CSS-цвета (hex или rgba)', () => {
    for (const [key, value] of Object.entries(C)) {
      expect(value, `C.${key}`).toMatch(CSS_COLOR);
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
