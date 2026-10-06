import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { afterEach, describe, expect, it } from 'vitest';
import { ProjectSidebar } from '../ProjectSidebar';
import { setAllFlags } from '../../../lib/featureFlags';

// Гейт флага spheres на левой панели вкладки «Проекты»: выключен — как в master, без сферного UI.
const render = () => renderToStaticMarkup(createElement(ProjectSidebar, {
  view: 'all', onSelect: () => {}, total: 0, groups: [], sleepingCount: 0,
  onManageGroups: () => {}, onCreateSphere: () => {},
}));

describe('ProjectSidebar и флаг spheres', () => {
  afterEach(() => setAllFlags({}));

  it('выключен — нет «Сфер», «Новая сфера», «Управление сферами»', () => {
    setAllFlags({ spheres: false });
    const html = render();
    expect(html).not.toMatch(/СФЕР|Сфер|сфер/);
    expect(html).toContain('ГРУППЫ');
  });

  it('включён — секция «СФЕРЫ» и кнопки на месте', () => {
    setAllFlags({ spheres: true });
    const html = render();
    expect(html).toContain('СФЕРЫ');
    expect(html).toContain('Новая сфера');
    expect(html).toContain('Управление сферами');
  });
});
