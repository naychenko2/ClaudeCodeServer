// Плашка паузы планирования: рендер без падений и правильные подписи.
// Рендерим статикой через react-dom/server, как остальные карточки ленты —
// DOM-тестов у ленты нет, а проверить надо само дерево
import { describe, it, expect } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { TeamPlanningIndicator } from '../TeamPlanningIndicator';
import { TEAM_PLANNING_TITLE, TEAM_PLANNING_TEXT } from '../../../lib/teamImplement';

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
});
