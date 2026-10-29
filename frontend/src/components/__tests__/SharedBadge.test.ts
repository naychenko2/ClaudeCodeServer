// Метка «общий проект» (SharedBadge, S1-F4 + доработка по ревью e206def1: имя и состав
// участников — из данных проекта, currentUserId обязателен; спецификация
// shared-projects-v2-proposal.md п.6).
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
import type { ProjectMembersSummary } from '../../types';

// Мобила — управляемый флаг (в SSR нет matchMedia — useIsMobile дала бы false всегда)
const { mobileFlag } = vi.hoisted(() => ({ mobileFlag: { value: false } }));
vi.mock('../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => mobileFlag.value };
});

beforeEach(() => {
  mobileFlag.value = false;
});

// Форма сводки такая, какую отдаёт бэк (ProjectMembersView.Summary): count + первые трое.
const summary = (count: number): ProjectMembersSummary => ({
  count,
  first: count > 0 ? [{ userId: 'other', username: 'other' }] : [],
});

const render = (props: SharedBadgeProps): string =>
  renderToStaticMarkup(createElement(SharedBadge, props));

const ownShared: SharedProjectData = { ownerId: 'me', members: summary(1), myRole: 'owner' };
const foreign: SharedProjectData = { ownerId: 'andrey', ownerDisplayName: 'Андрей' };

describe('что считается общим проектом', () => {
  it('свой проект с участниками (сводка count) — метка «общий»', () => {
    const html = render({ project: ownShared, currentUserId: 'me' });
    expect(html).toContain('>общий<');
  });

  it('свой проект без участников — метки нет вовсе', () => {
    for (const members of [summary(0), null, undefined]) {
      expect(render({ project: { ownerId: 'me', members }, currentUserId: 'me' })).toBe('');
    }
  });

  it('чужой проект — «проект » + имя из данных проекта, без отдельного пропа', () => {
    const html = render({ project: foreign, currentUserId: 'me' });
    expect(html).toContain('>проект Андрей<');
    expect(html).not.toContain('>общий<');
  });

  it('запасное имя — ownerUsername, когда displayName нет', () => {
    const html = render({ project: { ownerId: 'andrey', ownerUsername: 'andrey' }, currentUserId: 'me' });
    expect(html).toContain('>проект andrey<');
  });

  it('чужой проект без имени — «чужой проект»', () => {
    const html = render({ project: { ownerId: 'andrey' }, currentUserId: 'me' });
    expect(html).toContain('>чужой проект<');
  });

  it('чужой общий проект без myRole не получает «общий» (ревью M-37)', () => {
    const html = render({
      project: { ownerId: 'andrey', ownerDisplayName: 'Андрей', members: summary(2) },
      currentUserId: 'me',
    });
    expect(html).toContain('>проект Андрей<');
    expect(html).not.toContain('>общий<');
  });

  it('myRole авторитетна: reader видит чужую метку, owner без участников — ничего', () => {
    expect(render({ project: { myRole: 'reader' }, currentUserId: 'me' })).toContain('>чужой проект<');
    expect(render({ project: { myRole: 'editor', members: summary(0) }, currentUserId: 'me' })).toContain('>чужой проект<');
    expect(render({ project: { myRole: 'owner', ownerId: 'me' }, currentUserId: 'me' })).toBe('');
  });

  it('старые данные без ownerId — проект свой: без участников метки нет, с ними — «общий»', () => {
    expect(render({ project: {}, currentUserId: 'me' })).toBe('');
    expect(render({ project: { members: summary(1) }, currentUserId: 'me' })).toContain('>общий<');
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

    const foreignHtml = render({ project: foreign, currentUserId: 'me' });
    expect(foreignHtml).toContain('aria-label="проект Андрей"');
    expect(foreignHtml).not.toContain('>проект Андрей<');
  });
});
