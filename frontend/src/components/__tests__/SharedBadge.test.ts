// Метка «общий проект» (SharedBadge, S1-F4; спецификация shared-projects-v2-proposal.md п.6).
//
// Окружение node (без jsdom), как в TaskCreatedView.test.ts: SSR-рендер
// (присутствие/отсутствие метки, её текст, плотность xs/sm, мобильный значок).
// Видимый текст плашки в разметке — всегда «>текст<» внутри span; aria-label/title —
// значения атрибутов, их эта проверка не трогает.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SharedBadge, type SharedBadgeProps, type SharedProjectData } from '../SharedBadge';
import { SP } from '../../lib/design';
import type { ProjectMember } from '../../types';

// Мобила — управляемый флаг (в SSR нет matchMedia — useIsMobile дала бы false всегда)
const { mobileFlag } = vi.hoisted(() => ({ mobileFlag: { value: false } }));
vi.mock('../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => mobileFlag.value };
});

beforeEach(() => {
  mobileFlag.value = false;
});

const member = (userId: string): ProjectMember => ({ userId, role: 'editor' });

const render = (props: SharedBadgeProps): string =>
  renderToStaticMarkup(createElement(SharedBadge, props));

const ownShared: SharedProjectData = { ownerId: 'me', members: [member('other')], myRole: 'owner' };
const foreign: SharedProjectData = { ownerId: 'andrey' };

describe('что считается общим проектом', () => {
  it('свой проект с участниками — метка «общий»', () => {
    const html = render({ project: ownShared, currentUserId: 'me' });
    expect(html).toContain('>общий<');
  });

  it('свой проект без участников — метки нет вовсе', () => {
    for (const members of [[], null, undefined]) {
      expect(render({ project: { ownerId: 'me', members }, currentUserId: 'me' })).toBe('');
    }
  });

  it('чужой проект — «проект » + имя владельца', () => {
    const html = render({ project: foreign, currentUserId: 'me', ownerName: 'Андрей' });
    expect(html).toContain('>проект Андрей<');
    expect(html).not.toContain('>общий<');
  });

  it('чужой проект без имени — «чужой проект»', () => {
    const html = render({ project: foreign, currentUserId: 'me' });
    expect(html).toContain('>чужой проект<');
  });

  it('myRole авторитетна: reader видит чужую метку, owner без участников — ничего', () => {
    expect(render({ project: { myRole: 'reader' } })).toContain('>чужой проект<');
    expect(render({ project: { myRole: 'editor', members: [] } })).toContain('>чужой проект<');
    expect(render({ project: { myRole: 'owner', ownerId: 'me' }, currentUserId: 'me' })).toBe('');
  });

  it('старые данные без ownerId — проект свой: без участников метки нет, с ними — «общий»', () => {
    expect(render({ project: {}, currentUserId: 'me' })).toBe('');
    expect(render({ project: { members: [member('other')] }, currentUserId: 'me' })).toContain('>общий<');
  });
});

describe('размеры и мобила', () => {
  it('xs — плотная плашка строк, sm — плотность шапок (по Badge)', () => {
    expect(render({ project: ownShared, currentUserId: 'me' })).toContain(`padding:1px ${SP.sm}px`);
    expect(render({ project: ownShared, currentUserId: 'me', size: 'sm' })).toContain(`padding:3px ${SP.sm}px`);
  });

  it('на мобиле — только значок, смысл в title/aria-label', () => {
    mobileFlag.value = true;
    const own = render({ project: ownShared, currentUserId: 'me' });
    expect(own).toContain('aria-label="общий проект"');
    expect(own).toContain('title="общий проект"');
    expect(own).toContain('<svg');
    // видимого текста плашки на мобиле нет
    expect(own).not.toContain('>общий<');

    const foreignHtml = render({ project: foreign, currentUserId: 'me', ownerName: 'Андрей' });
    expect(foreignHtml).toContain('aria-label="проект Андрей"');
    expect(foreignHtml).not.toContain('>проект Андрей<');
  });
});
