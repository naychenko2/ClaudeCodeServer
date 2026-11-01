// Точка-индикатор на триггере ToolbarOverflowMenu (шаг W2-2, план хвостов LLM-wiki).
// Контракт пропа indicator: number>0 — счётчик-бейдж, true — точка, ReactNode — как есть.
// Дефект: в ветке триггера с подписью точка для true не рисовалась никогда (nodeIndicator
// на boolean даёт null), а вызов NotificationsPage («Фильтр» + indicator={… ? true : undefined})
// ждал её всё это время. Иконочная ветка не меняется: там точка была и раньше — рисовать
// её дважды (и здесь, и там) значило бы двойную точку у Вики и локальной модели.
//
// Рендер — статический (renderToStaticMarkup, как headerHtml в WikiChatActions.test.ts:604):
// эффекты в SSR не работают, меню закрыто — в разметке только триггер; точка считается по
// метрикам dotBadge (8×8, круг) — в закрытом триггере такой стиль даёт только она
// (бейдж-счётчик — 999px, скругление кнопки — 10px, «⋯»-кнопка — 32px; элементы меню
// при закрытом состоянии не рендерятся вовсе).
//
// Мутации этого прогона (вывод — в отчёте задачи):
// (1) убрать строку true в ветке triggerLabel → «с подписью + true — ровно 1 точка» краснеет.

import { createElement } from 'react';
import type { ReactNode } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect } from 'vitest';
import { C } from '../../lib/design';
import { ToolbarOverflowMenu, type OverflowItem } from '../ToolbarOverflowMenu';

// Метрики точки из dotBadge компонента (8×8, круг). Порядок в строке — порядок полей
// объекта стиля; сигнатура уникальна для точки в закрытом триггере (см. шапку файла).
const DOT_SIG = 'width:8px;height:8px;border-radius:50%';
function dots(html: string): string[] {
  return html.match(new RegExp(`${DOT_SIG}[^"]*`, 'g')) ?? [];
}

const ITEMS: OverflowItem[] = [{ key: 'x', label: 'Пункт' }];

// Закрытый триггер: с подписью — тот же вызов, что «Фильтр» в NotificationsPage;
// без подписи — дефолтный иконочный «⋯»
function htmlWith(indicator: number | boolean | ReactNode | undefined, labeled: boolean): string {
  const props: { items: OverflowItem[]; triggerLabel?: string; indicator?: number | boolean | ReactNode } =
    { items: ITEMS, indicator };
  if (labeled) props.triggerLabel = 'Фильтр';
  return renderToStaticMarkup(createElement(ToolbarOverflowMenu, props));
}

describe('Триггер с подписью (W2-2)', () => {
  it('triggerLabel + indicator={true} — ровно 1 точка цвета accent', () => {
    const d = dots(htmlWith(true, true));
    expect(d.length).toBe(1);
    expect(d[0]).toContain(`background:${C.accent}`);
  });

  it('triggerLabel + undefined/false/0 — ни точки, ни бейджа', () => {
    for (const ind of [undefined, false, 0]) {
      const html = htmlWith(ind, true);
      expect(dots(html).length).toBe(0);
      expect(html).not.toContain('border-radius:999px');
    }
  });

  it('triggerLabel + indicator={3} — бейдж «3» в строке, точки нет', () => {
    const html = htmlWith(3, true);
    expect(html).toContain('>3</span>');
    expect(html).toContain('border-radius:999px');
    expect(dots(html).length).toBe(0);
  });
});

describe('Иконочный триггер — поведение не меняется', () => {
  it('icon + indicator={true} — ровно 1 точка (не задваивается)', () => {
    expect(dots(htmlWith(true, false)).length).toBe(1);
  });

  it('icon + ReactNode — ровно 1 узел «как есть», без точки от компонента', () => {
    const own = createElement('span', { 'data-own': '1', style: { width: 9, height: 9, borderRadius: 2, background: C.textMuted } });
    const html = htmlWith(own, false);
    expect((html.match(/data-own="1"/g) ?? []).length).toBe(1);
    expect(dots(html).length).toBe(0);
  });
});
