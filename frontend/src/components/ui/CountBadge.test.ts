// Тон info: синий кружок-счётчик и синяя точка рельсы (решение по «Журналу»:
// точка = «ИИ заменил вашу формулировку», токен C.info, тот же цвет в тексте).
import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { BookOpen } from 'lucide-react';
import { CountBadge } from './CountBadge';
import { PanelRail, type RailItem } from './PanelRail';

describe('CountBadge tone=info', () => {
  it('фон — C.info, цифра — C.onAccent', () => {
    const html = renderToStaticMarkup(createElement(CountBadge, { value: 3, tone: 'info' }));
    expect(html).toContain('background:var(--c-info)');
    expect(html).toContain('color:var(--c-on-accent)');
  });
  it('accent и warning не поменялись (регрессия)', () => {
    const accent = renderToStaticMarkup(createElement(CountBadge, { value: 1 }));
    expect(accent).toContain('background:var(--c-accent)');
    expect(accent).toContain('color:var(--c-on-accent)');
    expect(renderToStaticMarkup(createElement(CountBadge, { value: 1, tone: 'warning' })))
      .toContain('background:var(--c-warning)');
  });
});

// Точка-индикатор на кнопке рельсы рисуется внутри PanelRail (PanelRail.tsx:473,
// выбор цвета Dot по item.dot); проверяем её тем же статическим рендером.
const railWithDot = (dot: RailItem['dot']) => renderToStaticMarkup(createElement(PanelRail, {
  side: 'right',
  groups: [[{ key: 'wiki-journal', title: 'Журнал', Icon: BookOpen, active: false, onClick: () => {}, dot }]],
}));

describe('точка рельсы (PanelRail → Dot)', () => {
  it('dot=info — синий C.info', () => expect(railWithDot('info')).toContain('fill="var(--c-info)"'));
  it('dot=warning — жёлтый (регрессия)', () => expect(railWithDot('warning')).toContain('fill="var(--c-warning)"'));
  it('dot=accent — оранжевый (регрессия)', () => expect(railWithDot('accent')).toContain('fill="var(--c-accent)"'));
  it('без точки — ни одного круга Dot', () => expect(railWithDot(undefined)).not.toContain('fill="var(--c-'));
});
