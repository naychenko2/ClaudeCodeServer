// F28: группа «Модели» формы персоны — переключатель, «своё · N из 3», отключённые строки, «Сбросить».
// SSR (образец PersonaForm.inherit.test.ts).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PersonaForm } from '../PersonaForm';
import { PersonaModelsGroup } from '../PersonaModelsGroup';
import { claimTierReset, clearedTiers, ownTierCount, tiersModeAction } from '../personaInheritModel';
import { setAllFlags } from '../../../lib/featureFlags';
import type { Persona } from '../../../types';

vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => true };
});
vi.mock('../../../lib/aiJobStore', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/aiJobStore')>();
  return { ...orig, useAiJob: orig.getAiJob };
});

const BASE = {
  id: 'per-1', ownerId: 'o1', name: 'Светлана', role: 'Дизайнер', handle: 'designer',
  scope: 'global', memoryEnabled: false, specialty: 'frontend',
} as Persona;

function renderForm(p: Partial<Persona>): string {
  return renderToStaticMarkup(createElement(PersonaForm, {
    persona: { ...BASE, ...p } as Persona, projects: [], onSaved: () => {}, onDelete: () => {},
  }));
}

function renderGroup(over: Partial<Parameters<typeof PersonaModelsGroup>[0]> = {}): string {
  return renderToStaticMarkup(createElement(PersonaModelsGroup, {
    own: false, onModeChange: () => {}, ownCount: 0,
    cells: { strong: '', medium: '', weak: '' },
    inheritedLabel: t => `${t} · спец.`,
    renderPicker: t => createElement('i', { 'data-picker': t }),
    renderPreview: () => null,
    onReset: () => {},
    ...over,
  }));
}

beforeEach(() => { setAllFlags({}); });

describe('ownTierCount', () => {
  it('считает только непустые ячейки', () => {
    expect(ownTierCount({})).toBe(0);
    expect(ownTierCount({ tierStrong: 'a', tierMedium: '', tierWeak: null })).toBe(1);
    expect(ownTierCount({ tierStrong: 'a', tierMedium: 'b', tierWeak: 'c' })).toBe(3);
  });
});

describe('PersonaModelsGroup (F28)', () => {
  it('наследование: три отключённые строки вместо выбирателей, бейджа и «Сбросить» нет', () => {
    const html = renderGroup();
    for (const t of ['strong', 'medium', 'weak']) {
      expect(html).toContain(`data-testid="models-inherited-${t}"`);
      expect(html).toContain(`${t} · спец.`);
    }
    expect(html).not.toContain('data-picker');
    expect(html).not.toContain('своё ·');
    expect(html).not.toContain('Сбросить');
    expect(html).not.toContain('models-own-hint');
  });

  it('«Своё»: бейдж «своё · 1 из 3», подпись про пустую ячейку, «Сбросить» только у заданной', () => {
    const html = renderGroup({ own: true, ownCount: 1, cells: { strong: '', medium: '', weak: 'flash' } });
    expect(html).toContain('своё · 1 из 3');
    expect(html).toContain('пустая ячейка по-прежнему берётся у специальности');
    expect(html).toContain('data-picker="strong"');
    expect(html).not.toContain('models-inherited-');
    expect(html.split('>Сбросить<').length - 1).toBe(1);
  });

  it('busy: сегменты и «Сбросить» выключены', () => {
    const html = renderGroup({ own: true, ownCount: 1, busy: true, cells: { strong: 'x', medium: '', weak: '' } });
    expect(html.split('disabled').length - 1).toBeGreaterThanOrEqual(3);
  });
});

describe('PersonaForm: группа «Модели» (F28)', () => {
  it('все ячейки пусты — «Как у специальности», кнопки «Вернуть наследование» нет', () => {
    const html = renderForm({});
    expect(html).toContain('data-testid="models-group"');
    expect(html).toContain('data-testid="models-inherited-strong"');
    expect(html).not.toContain('Вернуть наследование');
  });

  it('есть своя ячейка — «Своё», счёт N из 3', () => {
    const html = renderForm({ tierStrong: 'preset:1', tierWeak: 'preset:2' });
    expect(html).toContain('своё · 2 из 3');
    expect(html).not.toContain('models-inherited-');
  });
});

describe('логика переключения группы «Модели» (F28)', () => {
  it('два быстрых захвата сброса — отправка ровно одна, после отпускания — снова можно', () => {
    const flag = { current: false };
    let puts = 0;
    for (let i = 0; i < 2; i++) if (claimTierReset(flag)) puts++;
    expect(puts).toBe(1);
    flag.current = false;
    expect(claimTierReset(flag)).toBe(true);
  });

  it('возврат к специальности при своей ячейке — подтверждение; без неё — сразу; при запросе — игнор', () => {
    expect(tiersModeAction('inherited', true, 1, false)).toBe('confirm');
    expect(tiersModeAction('inherited', true, 0, false)).toBe('close');
    expect(tiersModeAction('own', false, 0, false)).toBe('open');
    expect(tiersModeAction('inherited', true, 1, true)).toBe('ignore');
    expect(tiersModeAction('own', true, 1, false)).toBe('ignore');
  });

  it('«Сбросить» очищает ячейку — данные для PUT и формы пустые', () => {
    expect(clearedTiers()).toEqual({ tierStrong: '', tierMedium: '', tierWeak: '' });
    expect(ownTierCount(clearedTiers())).toBe(0);
  });
});
