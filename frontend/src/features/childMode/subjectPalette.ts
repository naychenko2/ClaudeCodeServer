// Палитра предмета: цветовой ключ серверной записи (ChildSubjectColor) → пара токенов C.*.
// Таблица — макет Д1 (docs/mockups/kids-tutors.html, раздел 2 «Палитра предметов»):
//   info    → C.infoBg / C.info
//   rose    → color-mix(C.rose 14%, C.bgCard) / C.roseText   (как .pal-rose в макете)
//   success → C.successBg / C.successText
//   warning → C.warningBg / C.warningText
//   accent  → C.accentLight / C.accent
// Новых hex нет: вся палитра собрана из токенов design.ts (и из var(), и на тёмной теме
// пересчитывается сама). Неизвестный ключ → пара info: цвет плитки смысла не несёт,
// плитка остаётся читаемой (серверный валидатор Т2a держает ключи, это фолбэк интерфейса).

import { C } from '../../lib/design';
import type { ChildSubjectColor } from './types';

export interface SubjectPalette {
  bg: string;
  fg: string;
}

const PALETTE: Record<ChildSubjectColor, SubjectPalette> = {
  info: { bg: C.infoBg, fg: C.info },
  rose: { bg: `color-mix(in srgb, ${C.rose} 14%, ${C.bgCard})`, fg: C.roseText },
  success: { bg: C.successBg, fg: C.successText },
  warning: { bg: C.warningBg, fg: C.warningText },
  accent: { bg: C.accentLight, fg: C.accent },
};

export function subjectColors(key: ChildSubjectColor): SubjectPalette {
  return PALETTE[key] ?? PALETTE.info;
}
