// P2b: вкладка «Промпт» формы персоны. Окружение vitest — node (без jsdom), поэтому кликов нет:
// решение «спрашивать ли подтверждение при смене специальности» вынесено чистой функцией
// (её и зовёт форма), а разметка проверяется SSR-рендером.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { PersonaPromptTab, hasOwnSections, sectionsResetNeedsConfirm } from './PersonaPromptTab';
import { applySections, emptyState, setOwnText, startOwn, type PersonaPromptSection } from './promptSectionsModel';

const SECTIONS: PersonaPromptSection[] = [
  { id: 'role', enabled: true, text: 'Ты — фронтенд.', enabledSource: 'code', textSource: 'code' },
  { id: 'rules', enabled: true, text: 'Правила.', enabledSource: 'code', textSource: 'global' },
];

const loaded = () => applySections(emptyState('p1'), 'p1', SECTIONS);

function render(state = loaded(), over: Partial<Parameters<typeof PersonaPromptTab>[0]> = {}): string {
  return renderToStaticMarkup(createElement(PersonaPromptTab, {
    state, onChange: () => {}, hasSpecialty: true, specialtyChanged: false, isNew: false,
    loadError: null, isMobile: true, children: createElement('div', { id: 'slots' }, 'слоты'), ...over,
  }));
}

describe('смена специальности: подтверждение сброса своих секций', () => {
  it('со своей секцией — спрашивает', () => {
    const state = setOwnText(startOwn(loaded(), 'role'), 'role', 'Моя роль');
    expect(hasOwnSections(state)).toBe(true);
    expect(sectionsResetNeedsConfirm(state, false)).toBe(true);
  });

  it('без своих секций — не спрашивает', () => {
    expect(sectionsResetNeedsConfirm(loaded(), false)).toBe(false);
  });

  it('черновик, равный тексту специальности, своей секцией не считается', () => {
    expect(sectionsResetNeedsConfirm(startOwn(loaded(), 'role'), false)).toBe(false);
  });

  it('специальность уже сменена — повторно не спрашивает', () => {
    const state = setOwnText(startOwn(loaded(), 'role'), 'role', 'Моя роль');
    expect(sectionsResetNeedsConfirm(state, true)).toBe(false);
  });
});

describe('PersonaPromptTab: разметка', () => {
  it('блок «От специальности»: секции, бейдж источника и серый текст у «как у специальности»', () => {
    const html = render();
    expect(html).toContain('От специальности');
    expect(html).toContain('data-testid="prompt-section-role"');
    expect(html).toContain('по умолчанию');
    expect(html).toContain('настроено админом');
    expect(html).toContain('Как у специальности');
    expect(html).toContain('Выключено');
    expect(html).toContain('Ты — фронтенд.');
  });

  it('«своё»: поле с текстом и счётчик N / 1024', () => {
    const html = render(setOwnText(startOwn(loaded(), 'role'), 'role', 'Моя роль'));
    expect(html).toContain('Моя роль');
    expect(html).toContain('8 / 1024');
  });

  it('Specialty=None: блока «От специальности» нет, свои подразделы на месте', () => {
    const html = render(loaded(), { hasSpecialty: false });
    expect(html).not.toContain('От специальности');
    expect(html).toContain('id="slots"');
  });

  it('создание и смена специальности — вместо секций пояснение', () => {
    expect(render(emptyState(''), { isNew: true })).toContain('после сохранения персоны');
    expect(render(loaded(), { specialtyChanged: true })).not.toContain('data-testid="prompt-section-role"');
  });

  it('отказ загрузки — текст ошибки', () => {
    expect(render(emptyState('p1'), { loadError: 'Не удалось' })).toContain('Не удалось');
  });
});

// F31: цепочка слоёв, подписи источников, теги B09, счётчик и ошибка лимита
describe('PersonaPromptTab: слои и лимит (F31)', () => {
  const LAYERED: PersonaPromptSection[] = [
    { id: 'role', enabled: true, text: 'Ты — тестер.', enabledSource: 'code', textSource: 'code' },
    { id: 'flow', enabled: true, text: 'Процессы.', enabledSource: 'code', textSource: 'owner', updatedFromRole: true },
    { id: 'nav', enabled: false, text: 'Навигация.', enabledSource: 'global', textSource: 'code' },
    { id: 'own', enabled: true, text: 'Своё.', enabledSource: 'code', textSource: 'persona' },
  ];
  const layered = () => applySections(emptyState('p1'), 'p1', LAYERED);
  const section = (html: string, id: string) => {
    const at = html.indexOf(`data-testid="prompt-section-${id}"`);
    expect(at, `секции ${id} нет`).toBeGreaterThan(-1);
    const next = html.indexOf('data-testid="prompt-section-', at + 10);
    return html.slice(at, next === -1 ? undefined : next);
  };

  it('цепочка «Откуда текст» с именем специальности и hint про последний слой', () => {
    const html = render(layered(), { specialtyTitle: 'QA-тестер' });
    expect(html).toContain('data-testid="prompt-chain"');
    expect(html).toContain('Откуда текст:');
    expect(html).toContain('специальность «QA-тестер»');
    for (const l of ['код', 'глобально', 'пользователь', 'владелец', 'персона']) expect(html).toContain(`>${l}<`);
    expect(html).toContain('Побеждает последний слой');
  });

  it('заголовки секций называют слой: по умолчанию · код / настроено админом · владелец / выключено специальностью · глобально / своё · персона', () => {
    const html = render(layered(), { specialtyTitle: 'QA-тестер' });
    expect(section(html, 'role')).toContain('по умолчанию · код');
    expect(section(html, 'flow')).toContain('настроено админом · владелец');
    expect(section(html, 'nav')).toContain('выключено специальностью · глобально');
    expect(section(html, 'own')).toContain('своё · персона');
  });

  it('тег «обновилось от специальности» — только у унаследованной updatedFromRole; у «Своё» тега нет, есть тег «своё»', () => {
    const html = render(layered(), { specialtyTitle: 'QA-тестер' });
    expect(section(html, 'flow')).toContain('обновилось от специальности');
    expect(section(html, 'role')).not.toContain('обновилось от специальности');
    const own = section(html, 'own');
    expect(own).not.toContain('обновилось от специальности');
    expect(own).toContain('>своё<');
    const ownUpdated = applySections(emptyState('p1'), 'p1', [{ ...LAYERED[3], updatedFromRole: true }]);
    expect(render(ownUpdated)).not.toContain('обновилось от специальности');
  });

  it('плашка «Админ только что поправил…» — по changedByAdminSince=true, с названием специальности и обновлёнными секциями', () => {
    const html = render(layered(), { specialtyTitle: 'QA-тестер', changedByAdminSince: true });
    expect(html).toContain('data-testid="prompt-admin-changed"');
    expect(html).toContain('Админ только что поправил у «QA-тестер»');
    expect(render(layered(), { specialtyTitle: 'QA-тестер', changedByAdminSince: false })).not.toContain('Админ только что поправил');
    expect(render(layered(), { specialtyTitle: 'QA-тестер' })).not.toContain('Админ только что поправил');
  });

  it('пояснения: выключена специальностью, подстановка черновика, «правка специальности больше не доедет»', () => {
    const html = render(layered(), { specialtyTitle: 'QA-тестер' });
    expect(section(html, 'nav')).toContain('Админ выключил её для всех «QA-тестер»');
    expect(section(html, 'role')).toContain('подставит этот текст черновиком');
    expect(section(html, 'own')).toContain('Правка специальности сюда больше не доедет');
  });

  it('Specialty=None: «Без специальности секций нет — только свои подразделы ниже»', () => {
    const html = render(layered(), { hasSpecialty: false });
    expect(html).toContain('Без специальности секций нет — только свои подразделы ниже.');
    expect(html).not.toContain('data-testid="prompt-chain"');
  });

  it('textarea «Своё»: aria-label, min-height 120, счётчик нейтральный', () => {
    const html = render(layered());
    const own = section(html, 'own');
    expect(own).toContain('aria-label="Свой текст секции «own»"');
    expect(own).toContain('min-height:120px');
    expect(own).toContain('5 / 1024');
  });

  it('счётчик жёлтый от 90 % лимита, красный и с ошибкой — сверх лимита', () => {
    const pick = (len: number) => {
      const st = setOwnText(startOwn(layered(), 'role'), 'role', 'ф'.repeat(len));
      return section(render(st), 'role');
    };
    const near = pick(950);
    expect(near).toContain('950 / 1024');
    expect(near).toContain('var(--c-warning-text');
    expect(near).not.toContain('Длиннее 1024');
    const calm = pick(100);
    expect(calm).not.toContain('var(--c-warning-text');
    const over = pick(1025);
    expect(over).toContain('Длиннее 1024 символов — сократите, иначе не сохранится');
    expect(over).toContain('aria-invalid="true"');
    expect(over).toContain('var(--c-danger-text');
  });
});
