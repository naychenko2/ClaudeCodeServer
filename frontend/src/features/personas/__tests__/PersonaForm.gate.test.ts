// W3-F1e: гейт по роли в форме персоны (матрица §2 разреза
// docs/research/shared-projects-personas-cut-2026-11.md §3.4). Читатель — форма только для
// чтения: поля disabled, «Удалить» нет (своей «Сохранить» у формы нет никогда — она в
// тулбаре родителя, для читателя форма сообщает canSave=false). Редактор — карточка
// правится; поля зоны и денег (зона, профиль доступа, модель/уровень/усилие, специальность —
// доработка ревью fd328e70 п.3) выключены с видимым текстом «Меняет только владелец
// проекта», «Удалить» нет. Владелец и обычный проект (myRole нет) — всё, как раньше.
//
// Окружение node (без jsdom), SSR renderToStaticMarkup — образец ProjectPersonasPanel.gate.test.ts
// и components/SessionList.test.ts: effects не идут (аватар-caps, память, фокус, превью
// остаются незагруженными — на доступность контролов это не влияет).
// Два мока: useIsMobile → true — «Удалить» живёт в мобильной «Опасной зоне», без мобильности
// его отсутствие у редактора проходило бы тривиально даже у владельца (кейс «владелец:
// «Удалить персону» есть» — доказательство, что тест вообще его видит); useAiJob —
// useSyncExternalStore без getServerSnapshot, legacy-SSR-рендерер его роняет (замена —
// чистый getAiJob: job всегда idle, кликов в тесте нет).
// Зона — главный путь (PersonaZoneSelect): флаг spheres включён setAllFlags.
// Маркеры: поле имени — input с placeholder="Например, Ассистент"; выбор зоны — select
// с title="Зона персоны" (его ставит только PersonaZoneSelect); модельные контролы —
// кнопки «Вернуть наследование»/«Сбросить» (рисуются только при заполненной ячейке —
// в фикстуре задан tierStrong).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PersonaForm } from '../PersonaForm';
import { setAllFlags } from '../../../lib/featureFlags';
import type { Persona, Project, ProjectRoleView } from '../../../types';

// «Удалить» — в мобильной опасной зоне: флаг мобильности подменяем (остальные экспорты — как в модуле)
vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => true };
});

// useAiJob без getServerSnapshot роняет legacy-SSR: тот же снимок стора без подписки
vi.mock('../../../lib/aiJobStore', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/aiJobStore')>();
  return { ...orig, useAiJob: orig.getAiJob };
});

// Проектная персона проекта 'proj1'. Заполненная ячейка уровня: контролы модельного поля
// «Вернуть наследование»/«Сбросить» рисуются только при значении — они маркеры состояния
// модельного поля (ячейка сильной модели у персоны задана)
const PERSONA: Persona = {
  id: 'per-1', ownerId: 'o1', name: 'Светлана', role: 'Дизайнер', handle: 'designer',
  scope: 'project', projectId: 'proj1', memoryEnabled: false, tierStrong: 'preset:1',
} as Persona;

function projectWith(myRole?: ProjectRoleView): Project {
  return {
    id: 'proj1', name: 'Росмарафон', rootPath: '/p',
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    myRole,
  } as Project;
}

// Форма редактирования этой персоны в проекте с ролью myRole
function renderForm(myRole?: ProjectRoleView): string {
  setAllFlags({ spheres: true }); // главный путь выбора зоны — PersonaZoneSelect
  return renderToStaticMarkup(createElement(PersonaForm, {
    persona: PERSONA, projects: [projectWith(myRole)],
    onSaved: () => {}, onDelete: () => {},
  }));
}

beforeEach(() => { setAllFlags({}); });

function countOf(html: string, s: string): number { return html.split(s).length - 1; }

// Открывающий тег <input …> / <select …>, несущий маркер (маркер внутри тега):
// состояние контрола читается по его атрибутам, а не по тексту
function openTagWith(html: string, open: '<input' | '<select', marker: string): string | null {
  const at = html.indexOf(marker);
  if (at < 0) return null;
  const start = html.lastIndexOf(open, at);
  if (start < 0) return null;
  const end = html.indexOf('>', start);
  return end < 0 ? null : html.slice(start, end + 1);
}

// Открывающий <button …> кнопки с точным текстом-содержимым (образец buttonTagWithText
// из SessionList.test.ts): SSR кладёт единственный текстовый ребёнок сразу за '>'
function buttonTagWithText(html: string, text: string): string | null {
  const end = html.indexOf(`>${text}</button>`);
  if (end < 0) return null;
  const start = html.lastIndexOf('<button', end);
  if (start < 0) return null;
  return html.slice(start, end);
}

const NAME_MARK = 'placeholder="Например, Ассистент"';
const ZONE_MARK = 'title="Зона персоны"';
// select «Специальности» — свой aria-label, он встречается только в его открывающем теге
// (подпись поля Field рисует «Специальность» как текст, без aria-label)
const SPEC_MARK = 'aria-label="Специальность"';
const HINT_TEXT = 'Меняет только владелец проекта';

describe('PersonaForm: гейт по роли (W3-F1e)', () => {
  it('читатель: имя и зона выключены, модельные контролы выключены, «Сохранить»/«Удалить» нет', () => {
    const html = renderForm('reader');
    const nameTag = openTagWith(html, '<input', NAME_MARK);
    expect(nameTag, 'поля имени нет').not.toBeNull();
    expect(nameTag!, 'поле имени читателю не выключено').toContain('disabled');
    const zoneTag = openTagWith(html, '<select', ZONE_MARK);
    expect(zoneTag, 'выбора зоны нет').not.toBeNull();
    expect(zoneTag!, 'выбор зоны читателю не выключен').toContain('disabled');
    const resetTag = buttonTagWithText(html, 'Сбросить');
    expect(resetTag, 'контрола модели («Сбросить» ячейки) нет').not.toBeNull();
    expect(resetTag!, 'контроль модели читателю не выключен').toContain('disabled');
    const specTag = openTagWith(html, '<select', SPEC_MARK);
    expect(specTag, 'выбора специальности нет').not.toBeNull();
    expect(specTag!, 'выбор специальности читателю не выключен').toContain('disabled');
    expect(countOf(html, 'Сохранить'), '«Сохранить» в форме читателю').toBe(0);
    expect(countOf(html, 'Удалить'), '«Удалить» в форме читателю').toBe(0);
  });

  it('редактор: имя открыто; зона и модель выключены; «Удалить» нет', () => {
    const html = renderForm('editor');
    const nameTag = openTagWith(html, '<input', NAME_MARK);
    expect(nameTag, 'поля имени нет').not.toBeNull();
    expect(nameTag!, 'поле имени редактору выключено').not.toContain('disabled');
    const zoneTag = openTagWith(html, '<select', ZONE_MARK);
    expect(zoneTag, 'выбора зоны нет').not.toBeNull();
    expect(zoneTag!, 'выбор зоны редактору не выключен').toContain('disabled');
    const resetTag = buttonTagWithText(html, 'Сбросить');
    expect(resetTag, 'контрола модели («Сбросить» ячейки) нет').not.toBeNull();
    expect(resetTag!, 'контроль модели редактору не выключен').toContain('disabled');
    const inheritTag = buttonTagWithText(html, 'Вернуть наследование');
    expect(inheritTag, 'кнопки «Вернуть наследование» нет').not.toBeNull();
    expect(inheritTag!, '«Вернуть наследование» редактору не выключена').toContain('disabled');
    // доработка ревью fd328e70 п.3: бэк (01ebbf0ed) 403-ит участнику смену специальности —
    // фронт выключает её тем же гейтом, что зону и модель
    const specTag = openTagWith(html, '<select', SPEC_MARK);
    expect(specTag, 'выбора специальности нет').not.toBeNull();
    expect(specTag!, 'выбор специальности редактору не выключен').toContain('disabled');
    // Текст кнопки идёт после иконки через пробел («</svg> Удалить персону») — строгий
    // маркер text-in-tag его не ловит, здесь достаточно самого текста: он встречается
    // только в этой кнопке (описание зоны — «Удаление…», без слова «Удалить»)
    expect(countOf(html, 'Удалить персону'), '«Удалить» редактору').toBe(0);
  });

  // Мобила смонтирована (useIsMobile → true): этот кейс доказывает, что «Удалить персону»
  // вообще рисуется в этом рендере — отсутствие его у редактора/читателя значит именно гейт
  it('владелец: нигде disabled, подсказки нет, «Удалить персону» есть', () => {
    const html = renderForm('owner');
    const nameTag = openTagWith(html, '<input', NAME_MARK);
    expect(nameTag, 'поля имени нет').not.toBeNull();
    expect(nameTag!, 'поле имени владельцу выключено').not.toContain('disabled');
    const zoneTag = openTagWith(html, '<select', ZONE_MARK);
    expect(zoneTag, 'выбора зоны нет').not.toBeNull();
    expect(zoneTag!, 'выбор зоны владельцу выключен').not.toContain('disabled');
    const resetTag = buttonTagWithText(html, 'Сбросить');
    expect(resetTag, 'контрола модели («Сбросить» ячейки) нет').not.toBeNull();
    expect(resetTag!, 'контроль модели владельцу выключен').not.toContain('disabled');
    const specTag = openTagWith(html, '<select', SPEC_MARK);
    expect(specTag, 'выбора специальности нет').not.toBeNull();
    expect(specTag!, 'выбор специальности владельцу выключен').not.toContain('disabled');
    expect(countOf(html, 'Удалить персону'), '«Удалить персону» владельцу').toBe(1);
    expect(html, 'подсказки владельца в форме владельца быть не должно').not.toContain(HINT_TEXT);
  });

  // «нет роли» = обычный проект / старый бэк — матрица §2 читает её как «все»
  it('без myRole (обычный проект / старый бэк) — как владелец', () => {
    const html = renderForm(undefined);
    const nameTag = openTagWith(html, '<input', NAME_MARK);
    expect(nameTag, 'поля имени нет').not.toBeNull();
    expect(nameTag!, 'поле имени без роли выключено').not.toContain('disabled');
    expect(countOf(html, 'Удалить персону'), '«Удалить персону» без роли').toBe(1);
  });

  // c2: подсказка — видимым текстом под полем (Field рисует hint span-ом), а не только title
  it('редактор: «Меняет только владелец проекта» — текст в разметке', () => {
    const html = renderForm('editor');
    expect(html, 'текста подсказки под выключенными полями нет').toContain(`>${HINT_TEXT}<`);
  });
});
