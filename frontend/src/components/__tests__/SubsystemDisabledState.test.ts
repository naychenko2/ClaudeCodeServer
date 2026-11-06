// S2b-3b: заглушка раздела, выключенного администратором (SubsystemDisabledState).
// Среда vitest — node, DOM нет; рендер статический (renderToStaticMarkup,
// образец — ToolbarOverflowMenu.test.ts).
//
// Мутация этого прогона (вывод — в отчёте задачи): заменить текст заголовка в
// компоненте на другой → «без title — общий заголовок» краснеет.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect } from 'vitest';
import { SubsystemDisabledState } from '../SubsystemDisabledState';

describe('SubsystemDisabledState (S2b-3b)', () => {
  it('без title — общий заголовок и подсказка, где включить', () => {
    const html = renderToStaticMarkup(createElement(SubsystemDisabledState));
    expect(html).toContain('Раздел выключен администратором');
    expect(html).toContain('Включить его можно в «Компонентах» у администратора');
  });

  it('с title="Заметки" — имя раздела в заголовке', () => {
    const html = renderToStaticMarkup(createElement(SubsystemDisabledState, { title: 'Заметки' }));
    expect(html).toContain('Заметки');
    expect(html).toContain('выключен администратором');
  });
});
