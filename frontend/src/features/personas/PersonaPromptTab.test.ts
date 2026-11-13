// P2b: вкладка «Промпт» формы персоны. Окружение vitest — node (без jsdom), поэтому кликов нет:
// решение «спрашивать ли подтверждение при смене специальности» вынесено чистой функцией
// (её и зовёт форма), а разметка проверяется SSR-рендером.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { PersonaPromptTab, hasOwnSections, sectionsResetNeedsConfirm } from './PersonaPromptTab';
import { applySections, emptyState, setOwnText, startOwn, type PersonaPromptSection } from './promptSectionsModel';

const SECTIONS: PersonaPromptSection[] = [
  { id: 'role', enabled: true, text: 'Ты — фронтенд.', enabledSource: 'code', textSource: 'code' },
  { id: 'rules', enabled: true, text: 'Правила.', enabledSource: 'code', textSource: 'global' },
];

const loaded = () => applySections(emptyState('p1'), 'p1', SECTIONS);

function render(state = loaded(), over: Partial<Parameters<typeof PersonaPromptTab>[0]> = {}): string {
  return renderToStaticMarkup(createElement(PersonaPromptTab, {
    state, onChange: () => {}, hasSpecialty: true, specialtyChanged: false, isNew: false,
    loadError: null, isMobile: true, children: createElement('div', { id: 'slots' }, 'слоты'), ...over,
  }));
}

describe('смена специальности: подтверждение сброса своих секций', () => {
  it('со своей секцией — спрашивает', () => {
    const state = setOwnText(startOwn(loaded(), 'role'), 'role', 'Моя роль');
    expect(hasOwnSections(state)).toBe(true);
    expect(sectionsResetNeedsConfirm(state, false)).toBe(true);
  });

  it('без своих секций — не спрашивает', () => {
    expect(sectionsResetNeedsConfirm(loaded(), false)).toBe(false);
  });

  it('черновик, равный тексту специальности, своей секцией не считается', () => {
    expect(sectionsResetNeedsConfirm(startOwn(loaded(), 'role'), false)).toBe(false);
  });

  it('специальность уже сменена — повторно не спрашивает', () => {
    const state = setOwnText(startOwn(loaded(), 'role'), 'role', 'Моя роль');
    expect(sectionsResetNeedsConfirm(state, true)).toBe(false);
  });
});

describe('PersonaPromptTab: разметка', () => {
  it('блок «От специальности»: секции, бейдж источника и серый текст у «как у специальности»', () => {
    const html = render();
    expect(html).toContain('От специальности');
    expect(html).toContain('data-testid="prompt-section-role"');
    expect(html).toContain('по умолчанию');
    expect(html).toContain('настроено админом');
    expect(html).toContain('Как у специальности');
    expect(html).toContain('Выключено');
    expect(html).toContain('Ты — фронтенд.');
  });

  it('«своё»: поле с текстом и счётчик N / 1024', () => {
    const html = render(setOwnText(startOwn(loaded(), 'role'), 'role', 'Моя роль'));
    expect(html).toContain('Моя роль');
    expect(html).toContain('8 / 1024');
  });

  it('Specialty=None: блока «От специальности» нет, свои подразделы на месте', () => {
    const html = render(loaded(), { hasSpecialty: false });
    expect(html).not.toContain('От специальности');
    expect(html).toContain('id="slots"');
  });

  it('создание и смена специальности — вместо секций пояснение', () => {
    expect(render(emptyState(''), { isNew: true })).toContain('после сохранения персоны');
    expect(render(loaded(), { specialtyChanged: true })).not.toContain('data-testid="prompt-section-role"');
  });

  it('отказ загрузки — текст ошибки', () => {
    expect(render(emptyState('p1'), { loadError: 'Не удалось' })).toContain('Не удалось');
  });
});
