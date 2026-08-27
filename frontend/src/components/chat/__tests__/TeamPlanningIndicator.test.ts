// Плашка паузы планирования: рендер без падений и правильные подписи.
// Рендерим статикой через react-dom/server, как остальные карточки ленты —
// DOM-тестов у ленты нет, а проверить надо само дерево
import { describe, it, expect } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { TeamPlanningIndicator } from '../TeamPlanningIndicator';
import { TEAM_PLANNING_TITLE, TEAM_PLANNING_TEXT } from '../../../lib/teamImplement';
import type { Persona } from '../../../types';

const plannerPersona: Persona = {
  id: 'p-planner',
  name: 'Соня Планировщик',
  role: 'Планировщик',
  handle: 'planner',
  scope: 'project',
  avatar: { color: 'mint', initials: 'СП' },
} as unknown as Persona;

describe('TeamPlanningIndicator', () => {
  const html = renderToStaticMarkup(createElement(TeamPlanningIndicator));

  it('показывает заголовок и объяснение паузы', () => {
    expect(html).toContain(TEAM_PLANNING_TITLE);
    expect(html).toContain(TEAM_PLANNING_TEXT);
  });

  it('при монтировании отсчёт начинается с «меньше минуты»', () => {
    expect(html).toContain('меньше минуты');
  });

  it('живой отсчёт — спиннер, а не статичная иконка', () => {
    expect(html).toContain('tool-spinner');
  });

  it('спокойный тон: без accent-заливки и warning-цветов', () => {
    // Плашка — индикатор хода, а не главное действие и не тревога:
    // оранжевой заливки и warning-токенов в ней быть не должно
    expect(html).not.toContain('background:var(--c-accent)');
    expect(html).not.toContain('var(--c-warning');
  });

  it('принимает startedAt из события team_planning, не падает без него', () => {
    // SSR-рендер не гоняет эффекты — начальное состояние то же «меньше минуты»,
    // startedAt влияет на отсчёт только в браузере (useEffect); тест лишь ловит падения
    const withStartedAt = renderToStaticMarkup(createElement(TeamPlanningIndicator, { startedAt: Date.now() - 5_000 }));
    expect(withStartedAt).toContain(TEAM_PLANNING_TITLE);
  });

  it('с персоной рисует карточку с именем планировщика вместо безличной плашки', () => {
    const withPersona = renderToStaticMarkup(createElement(TeamPlanningIndicator, { persona: plannerPersona }));
    // Имя персоны — в шапке карточки, безличный текст TEAM_PLANNING_TEXT (мелкая подпись)
    // в карточке с лицом не используется: текст подменяется на короткий TEAM_PLANNING_TITLE
    expect(withPersona).toContain('Соня Планировщик');
    expect(withPersona).toContain(TEAM_PLANNING_TITLE);
    expect(withPersona).not.toContain(TEAM_PLANNING_TEXT);
  });
});
