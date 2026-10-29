// Вкладка «Участники» (S1-F3): статический рендер списка и обеих ролей секции.
// Эффекты в SSR не идут, поэтому секция показывает начальное состояние (скелетон);
// заполненный список проверяем через MembersList.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { Project, ProjectMember } from '../../../../types';
import { MembersList } from '../MembersList';
import { MembersSection } from '../MembersSection';

vi.mock('../../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => false };
});

const owner = { userId: 'u-owner', username: 'andrey', displayName: 'Андрей' };
const maria: ProjectMember = { userId: 'u-maria', username: 'maria', displayName: 'Мария', role: 'editor' };
const project = { id: 'p1', name: 'Test', rootPath: '/x' } as Project;

const list = (canManage: boolean, meId: string | null = 'u-owner') =>
  renderToStaticMarkup(createElement(MembersList, {
    owner, members: [maria], meId, canManage,
    busy: new Set<string>(), rowErrors: {}, onRoleChange: () => {}, onMenu: () => {},
  }));

describe('список участников', () => {
  it('владелец: бейдж «владелец», «(вы)» у своей строки, роль в Select и кнопка «⋯»', () => {
    const html = list(true);
    expect(html).toContain('владелец');
    expect(html).toContain('(вы)');
    expect(html).toContain('<select');
    expect(html).toContain('Действия: Мария');
  });

  it('участник: роль подписью, без Select и без меню', () => {
    const html = list(false, 'u-maria');
    expect(html).toContain('Редактор');
    expect(html).not.toContain('<select');
    expect(html).not.toContain('Действия:');
  });

  it('сбой смены роли — подпись под строкой', () => {
    const html = renderToStaticMarkup(createElement(MembersList, {
      owner, members: [maria], meId: 'u-owner', canManage: true,
      busy: new Set<string>(), rowErrors: { 'u-maria': 'Нет связи' }, onRoleChange: () => {}, onMenu: () => {},
    }));
    expect(html).toContain('Нет связи');
  });
});

describe('секция «Участники»', () => {
  const render = (mode: 'owner' | 'member') =>
    renderToStaticMarkup(createElement(MembersSection, { project, mode }));

  it('владелец: форма «Имя учётки» с кнопкой «Добавить», без «Покинуть»', () => {
    const html = render('owner');
    expect(html).toContain('Имя учётки');
    expect(html).toContain('Добавить');
    expect(html).not.toContain('Покинуть проект');
  });

  it('участник: «Покинуть проект» есть, формы приглашения нет', () => {
    const html = render('member');
    expect(html).toContain('Покинуть проект');
    expect(html).not.toContain('Имя учётки');
  });
});
