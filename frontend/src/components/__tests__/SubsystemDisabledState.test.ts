// S2b-3b: заглушка раздела, выключенного администратором (SubsystemDisabledState).
// Среда vitest — node, DOM нет; рендер статический (renderToStaticMarkup,
// образец — ToolbarOverflowMenu.test.ts).
//
// Мутация этого прогона (вывод — в отчёте задачи): переставить слова в шаблоне
// заголовка («выключен администратором» → «администратором выключен») →
// «с title — точный заголовок» краснеет: проверка держится на собранной строке,
// а не на фрагментах.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect } from 'vitest';
import { SubsystemDisabledState } from '../SubsystemDisabledState';

describe('SubsystemDisabledState (S2b-3b)', () => {
  it('без title — общий заголовок, подсказка и статус-якорь', () => {
    const html = renderToStaticMarkup(createElement(SubsystemDisabledState));
    expect(html).toContain('role="status"');
    expect(html).toContain('data-subsystem-disabled=""');
    expect(html).toContain('Раздел выключен администратором');
    expect(html).toContain('Включить его можно в «Компонентах» у администратора');
  });

  it('с title="Заметки" — точный заголовок и якорь с именем раздела', () => {
    const html = renderToStaticMarkup(createElement(SubsystemDisabledState, { title: 'Заметки' }));
    expect(html).toContain('role="status"');
    expect(html).toContain('data-subsystem-disabled="Заметки"');
    expect(html).toContain('Раздел «Заметки» выключен администратором');
  });
});
