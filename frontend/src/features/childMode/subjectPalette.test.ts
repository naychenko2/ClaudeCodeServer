// Т3b: палитра цветового ключа предмета. Таблица проверяется по макету Д1
// (docs/mockups/kids-tutors.html, §2 «Палитра предметов»): ключ → пара C.*, новых hex нет,
// неизвестный ключ — пара info. DOM здесь не нужен: функция чистая.

import { describe, it, expect } from 'vitest';
import { C } from '../../lib/design';
import { subjectColors } from './subjectPalette';
import type { ChildSubjectColor } from './types';

// Ожидание — те же пары, что в CSS макета (.pal-info/.pal-rose/.pal-success/.pal-warning/.pal-accent)
const EXPECTED: Record<ChildSubjectColor, { bg: string; fg: string }> = {
  info: { bg: C.infoBg, fg: C.info },
  rose: { bg: `color-mix(in srgb, ${C.rose} 14%, ${C.bgCard})`, fg: C.roseText },
  success: { bg: C.successBg, fg: C.successText },
  warning: { bg: C.warningBg, fg: C.warningText },
  accent: { bg: C.accentLight, fg: C.accent },
};

const KEYS = Object.keys(EXPECTED) as ChildSubjectColor[];

describe('subjectColors', () => {
  it('каждый ключ серверной палитры даёт ровно пару токенов макета', () => {
    for (const key of KEYS) {
      expect(subjectColors(key), `ключ ${key}`).toEqual(EXPECTED[key]);
    }
  });

  it('неизвестный ключ — пара info: плитка остаётся читаемой', () => {
    expect(subjectColors('lilac' as ChildSubjectColor)).toEqual(EXPECTED.info);
    expect(subjectColors(undefined as unknown as ChildSubjectColor)).toEqual(EXPECTED.info);
  });

  it('палитра собрана только из токенов C.* — сырых hex нет', () => {
    for (const key of KEYS) {
      const { bg, fg } = subjectColors(key);
      // Каждый цвет — var() из theme.css (color-mix допускается: он собран из var())
      for (const value of [bg, fg]) {
        expect(value, `${key} → ${value}`).toContain('var(--c-');
      }
    }
  });
});
