// Заголовок «Пресеты для роли» блок рисует сам только при showTitle (по умолчанию true);
// родитель с showTitle=false вешает свой SectionLabel — дубля быть не должно ни в одном состоянии.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { RolePresetsBlock } from './RolePresetsBlock';
import type { SpecialtyPromptSectionsCatalog, SpecialtySettingsLayer } from '../../types';

vi.mock('../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => false };
});

const CATALOG: SpecialtyPromptSectionsCatalog = {
  textLimit: 1024,
  sections: [{ id: 'history', label: 'История решений', description: 'История решений проекта' }],
  specialties: {
    tester: { sections: [{ id: 'history', enabled: true, text: 'текст' }], defaultBindings: [] },
  },
};
const EMPTY_LAYER: SpecialtySettingsLayer = { specialties: {}, presets: [] };
const DISABLED_LAYER: SpecialtySettingsLayer = {
  specialties: {
    tester: { access: 'full', promptSections: [{ id: 'history', enabled: false, text: 'текст' }] },
  },
  presets: [],
};

const TITLE = 'Пресеты для роли';

function render(mode: 'view' | 'edit', editLayer: SpecialtySettingsLayer, showTitle: boolean): string {
  return renderToStaticMarkup(createElement(RolePresetsBlock, {
    roleKey: 'tester', catalog: CATALOG, editLayer, globalLayer: EMPTY_LAYER,
    userLayer: null, mode, onSave: async () => {}, showTitle,
  })).replace(/<[^>]*>/g, '');
}

const STATES: Array<[string, 'view' | 'edit', SpecialtySettingsLayer]> = [
  ['view с содержимым', 'view', EMPTY_LAYER],
  ['view пустой', 'view', DISABLED_LAYER],
  ['edit пустой', 'edit', DISABLED_LAYER],
  ['edit обычный', 'edit', EMPTY_LAYER],
];

describe('RolePresetsBlock · заголовок «Пресеты для роли»', () => {
  it.each(STATES)('%s: showTitle=false — заголовка нет', (_n, mode, layer) => {
    expect(render(mode, layer, false)).not.toContain(TITLE);
  });
  it.each(STATES)('%s: showTitle=true — заголовок есть', (_n, mode, layer) => {
    expect(render(mode, layer, true)).toContain(TITLE);
  });
});
