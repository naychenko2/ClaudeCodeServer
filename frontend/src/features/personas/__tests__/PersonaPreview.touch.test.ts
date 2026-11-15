// Тач-цель кнопки «Специальность: … →» на мобильной ширине (Ф-2.35).
//
// Правило: на теле кнопка-мостик не меньше TB.iconHitMobile (40 px) — иначе палец
// промахивается мимо цели. Проверяем разметку карточки персоны: у кнопки «Специальность»
// на мобиле задана min-height 40, на десктопе — не задана (там ширина не помеха).
//
// SSR (renderToStaticMarkup): карточка тянет сеть в useEffect, а при серверном рендере
// эффекты не выполняются — разметка получается без шума от загрузок.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect } from 'vitest';
import { PersonaPreview } from '../PersonaPreview';
import { C, TB } from '../../../lib/design';
import type { Persona } from '../types';

const BASE = {
  id: 'per-1', ownerId: 'o1', name: 'Светлана', role: 'Дизайнер', handle: 'designer',
  scope: 'global', access: 'full', memoryEnabled: false, specialty: 'frontend',
  avatar: { kind: 'initials' as const }, tools: ['tasks', 'notes', 'web'],
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-02T00:00:00Z',
} as unknown as Persona;

function html(isMobile: boolean): string {
  return renderToStaticMarkup(createElement(PersonaPreview, {
    persona: BASE,
    accent: C.accent,
    onOpenSession: () => {},
    onOpenSpecialties: () => {},
    isMobile,
  }));
}

// Фрагмент разметки кнопки «Специальность» — по её testid.
function specialtyButton(h: string): string {
  const at = h.indexOf('data-testid="persona-specialty-open"');
  expect(at, 'кнопка «Специальность» не отрисована').toBeGreaterThanOrEqual(0);
  const open = h.lastIndexOf('<button', at);
  const close = h.indexOf('</button>', at);
  return h.slice(open, close);
}

describe('PersonaPreview: тач-цель кнопки «Специальность»', () => {
  it('мобила: min-height кнопки не меньше TB.iconHitMobile (40)', () => {
    const btn = specialtyButton(html(true));
    const m = btn.match(/min-height:\s*(\d+)px/);
    expect(m, 'у кнопки «Специальность» не задана min-height').not.toBeNull();
    expect(Number(m![1])).toBeGreaterThanOrEqual(TB.iconHitMobile);
  });

  it('десктоп: min-height кнопке не задана (шириной не мешает)', () => {
    const btn = specialtyButton(html(false));
    expect(btn).not.toMatch(/min-height/);
  });
});
