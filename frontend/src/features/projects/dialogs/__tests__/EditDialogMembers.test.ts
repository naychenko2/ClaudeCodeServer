// EditDialog для общего проекта (S1-F3): вид участника и секция владельца.
// Статический рендер: Modal мокается в плоскую обёртку, эффекты в SSR не идут.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { Project } from '../../../../types';

const { meId, flagOn } = vi.hoisted(() => ({ meId: { value: 'u-owner' }, flagOn: { value: true } }));

vi.mock('../../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => false };
});
vi.mock('../../../../lib/defaultPersona', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../../lib/defaultPersona')>();
  return { ...orig, useMe: () => ({ ...orig.useMe(), userId: meId.value }) };
});
vi.mock('../../../../lib/featureFlags', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../../lib/featureFlags')>();
  return { ...orig, useFeature: () => flagOn.value };
});
vi.mock('../../../../components/ui/Modal', () => ({
  Modal: ({ title, footer, children }: { title: string; footer?: unknown; children?: unknown }) =>
    createElement('div', null, createElement('h1', null, title), children, footer as never),
}));

// Прочие секции диалога тянут свои сторы и эффекты — в этом тесте они заглушены
const { stub } = vi.hoisted(() => ({ stub: (name: string) => ({ [name]: () => null }) }));
vi.mock('../ChatSubjectsSection', () => stub('ChatSubjectsSection'));
vi.mock('../BackgroundSection', () => stub('BackgroundSection'));
vi.mock('../ProjectMapSection', () => stub('ProjectMapSection'));
vi.mock('../../../mcp/McpProjectSection', () => stub('McpProjectSection'));
vi.mock('../../../plugins/PluginsProjectSection', () => stub('PluginsProjectSection'));
vi.mock('../../../desktop/DeviceSection', () => stub('DeviceSection'));
vi.mock('../../../localHands/LocalHandsSection', () => stub('LocalHandsSection'));
vi.mock('../../ProjectIconSection', () => stub('ProjectIconSection'));
vi.mock('../../../../components/ProjectSyncToggle', () => stub('ProjectSyncToggle'));
vi.mock('../../../../components/ArchiveSettings', () => stub('ArchiveSettings'));

import { EditDialog } from '../EditDialog';

const base = {
  id: 'p1', name: 'Витрина', rootPath: '/srv/vitrina', ownerId: 'u-owner',
  createdAt: '', updatedAt: '', defaultPersonaId: 'persona-1',
} as Project;

const render = (project: Project) =>
  renderToStaticMarkup(createElement(EditDialog, { project, groups: [{ id: 'g1', name: 'Группа' } as never], onSuccess: () => {}, onClose: () => {} }));

describe('EditDialog: общий проект', () => {
  it('участник: заголовок «Проект …», только «Участники», «Покинуть проект», скрыто лишнее', () => {
    meId.value = 'u-maria';
    const html = render({ ...base, myRole: 'reader', ownerDisplayName: 'Андрей', defaultPersonaId: null });
    expect(html).toContain('Проект Андрей');
    expect(html).not.toContain('Редактировать проект');
    expect(html).toContain('Участники');
    expect(html).toContain('Покинуть проект');
    expect(html).toContain('Закрыть');
    for (const hidden of ['Группа', 'Системный промпт', 'Правила разрешений', 'Руководитель проекта', 'Сохранить', 'Имя учётки']) {
      expect(html).not.toContain(hidden);
    }
  });

  it('владелец: «Редактировать проект», форма приглашения, без «Покинуть»', () => {
    meId.value = 'u-owner';
    const html = render(base);
    expect(html).toContain('Редактировать проект');
    expect(html).toContain('Имя учётки');
    expect(html).not.toContain('Покинуть проект');
  });

  it('владелец, флаг «Общие проекты» выключен: секции «Участники» нет', () => {
    meId.value = 'u-owner';
    flagOn.value = false;
    try {
      expect(render(base)).not.toContain('Имя учётки');
    } finally {
      flagOn.value = true;
    }
  });

  it('локальный проект владельца: формы нет, причина есть', () => {
    meId.value = 'u-owner';
    const html = render({ ...base, deviceId: 'dev-1' });
    expect(html).not.toContain('Имя учётки');
    expect(html).toContain('хранится на вашем устройстве');
  });
});
