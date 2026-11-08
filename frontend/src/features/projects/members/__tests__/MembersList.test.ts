// Список участников (S1-F3): статический рендер обеих ролей.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { ProjectMember } from '../../../../types';
import { MembersList } from '../MembersList';

const owner = { userId: 'u-owner', username: 'andrey', displayName: 'Андрей' };
const maria: ProjectMember = { userId: 'u-maria', username: 'maria', displayName: 'Мария', role: 'editor' };

const list = (canManage: boolean, meId: string | null = 'u-owner', rowErrors: Record<string, string> = {}) =>
  renderToStaticMarkup(createElement(MembersList, {
    owner, members: [maria], meId, canManage,
    busy: new Set<string>(), rowErrors, onRoleChange: () => {}, onMenu: () => {},
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
    expect(list(true, 'u-owner', { 'u-maria': 'Нет связи' })).toContain('Нет связи');
  });
});
