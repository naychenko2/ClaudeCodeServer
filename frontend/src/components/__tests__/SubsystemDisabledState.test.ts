// S2b-3b: заглушка раздела, выключенного администратором (SubsystemDisabledState).
// Среда vitest — node, DOM нет; рендер статический (renderToStaticMarkup,
// образец — ToolbarOverflowMenu.test.ts).
//
// Тесты 1–2 держат точные продуктовые заголовки: текст константы — контракт,
// его смена краснит их и требует явного решения продукта.
// Тест 3 держит ИСТОЧНИК текста: рендер с подменённой SUBSYSTEM_DISABLED_REASON
// (vi.doMock + повторный импорт, образец — notes.test.ts) — обе формы заголовка,
// с именем раздела и общая, обязаны следовать константе.
//
// Мутация этого прогона (вывод — в отчёте задачи): вернуть в SubsystemDisabledState.tsx
// литерал текста в обход константы → тест 3 краснеет: проверка держится на
// подмене источника, а не на наличии слов в выводе.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect, vi } from 'vitest';
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

  it('заголовок собран из SUBSYSTEM_DISABLED_REASON: подмена константы видна в выводе', async () => {
    // Литерал в компоненте в обход константы сделал бы этот кейс красным:
    // вывод не последовал бы за подменённым источником.
    vi.resetModules();
    vi.doMock('../../lib/subsystems', () => ({ SUBSYSTEM_DISABLED_REASON: 'выключен тестом' }));
    const { SubsystemDisabledState: InjectedState } = await import('../SubsystemDisabledState');
    const html = renderToStaticMarkup(createElement(InjectedState, { title: 'Заметки' }));
    expect(html).toContain('Раздел «Заметки» выключен тестом');
    // Общая форма (без title) — та же подмена: захардкожить её в обход константы
    // краснит эту строку, кейсы 1–2 остаются зелёными (их тексты не тронуты).
    const htmlCommon = renderToStaticMarkup(createElement(InjectedState));
    expect(htmlCommon).toContain('Раздел выключен тестом');
  });
});
