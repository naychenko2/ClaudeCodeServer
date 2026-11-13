// F32: панель наследования секций роли (макет persona-inheritance.html, P56–P59):
// строка «Наследуют: …», подписи строк «текст по умолчанию · код / свой текст · общее»,
// тег «изменено только что» с подсветкой hot и эффектом «у Лизы обновилось ·
// у Веры своё — не тронуто», подсказка про лимит 1024 и «выключено специальностью».
// SSR (образец PersonaModelsGroup.test.ts): компонент чистый, все данные — пропами.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { RolePresetsBlock } from './RolePresetsBlock';
import type {
  SpecialtyInheritor, SpecialtyPromptSectionsCatalog, SpecialtySettingsLayer,
} from '../../types';

vi.mock('../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => false };
});

const CATALOG: SpecialtyPromptSectionsCatalog = {
  textLimit: 1024,
  sections: [
    { id: 'history', label: 'История решений', description: 'История решений проекта' },
    { id: 'rules', label: 'Правила роли', description: 'Правила роли' },
  ],
  specialties: {
    tester: {
      sections: [
        { id: 'history', enabled: true, text: 'текст из кода' },
        { id: 'rules', enabled: true, text: 'типовой текст из кода' },
      ],
      defaultBindings: [],
    },
  },
};

const EMPTY_LAYER: SpecialtySettingsLayer = { specialties: {}, presets: [] };
// Своя подмена секции 'history' в черновике (owner-позиция резолва — как editLayer
// формы): 'rules' остаётся «по умолчанию · код».
const OWN_LAYER: SpecialtySettingsLayer = {
  specialties: {
    tester: { access: 'full', promptSections: [{ id: 'history', enabled: true, text: 'мой текст' }] },
  },
  presets: [],
};

const INHERITORS: SpecialtyInheritor[] = [
  { personaId: 'p-vera', name: 'Вера', ownSections: 1 },
  { personaId: 'p-liza', name: 'Лиза', ownSections: 0 },
];

function render(over: Partial<Parameters<typeof RolePresetsBlock>[0]> = {}): string {
  return renderToStaticMarkup(createElement(RolePresetsBlock, {
    roleKey: 'tester',
    catalog: CATALOG,
    editLayer: OWN_LAYER,
    globalLayer: EMPTY_LAYER,
    userLayer: null,
    mode: 'edit',
    onSave: async () => {},
    showTitle: false,
    ...over,
  }));
}

// Строка «Наследуют: …» намеренно набрана с вложенным span (приглушённая часть) —
// сравнение по тексту без тегов, как его прочитает DOM.
const textOf = (html: string): string => html.replace(/<[^>]*>/g, '');

describe('RolePresetsBlock · панель наследования (F32)', () => {
  it('«Наследуют: Вера, Лиза · своё у Веры: 1 секция» (P56, c1)', () => {
    const text = textOf(render({ inheritors: INHERITORS }));
    expect(text).toContain('Наследуют: Вера, Лиза · своё у Веры: 1 секция');
  });

  it('своего нет — части «· своё» нет; наследников нет или не загружены — строки нет', () => {
    const noOwn = textOf(render({ inheritors: [{ personaId: 'p-liza', name: 'Лиза', ownSections: 0 }] }));
    expect(noOwn).toContain('Наследуют: Лиза');
    expect(noOwn).not.toContain('· своё');
    expect(textOf(render({ inheritors: [] }))).not.toContain('Наследуют');
    expect(textOf(render())).not.toContain('Наследуют');
  });

  it('подписи строк: «текст по умолчанию · код» и «свой текст · общее» (P57)', () => {
    const text = textOf(render({ inheritors: INHERITORS }));
    expect(text).toContain('текст по умолчанию · код');
    expect(text).toContain('свой текст · общее');
  });

  it('«изменено только что»: тег, рамка и кольцо hot, эффект «у Лизы обновилось · у Веры своё — не тронуто» (P58, c2)', () => {
    const html = render({ inheritors: INHERITORS, justChanged: new Set(['rules']) });
    expect(html).toContain('изменено только что');
    // та же подсветка, что .spsec.hot в макете: border C.info + кольцо C.infoBg
    expect(html).toContain('border:1px solid var(--c-info)');
    expect(html).toContain('box-shadow:0 0 0 3px var(--c-info-bg)');
    const text = textOf(html);
    expect(text).toContain('у Лизы обновилось');
    expect(text).toContain('у Веры своё — не тронуто');
  });

  it('подсказка конца панели: лимит 1024 и «выключено специальностью — это не её выбор» (P59, c2)', () => {
    const text = textOf(render({ inheritors: INHERITORS }));
    expect(text).toContain('Счётчик и лимит те же: до 1024 символов на секцию');
    expect(text).toContain('выключено специальностью');
    expect(text).toContain('это не её выбор');
  });
});
