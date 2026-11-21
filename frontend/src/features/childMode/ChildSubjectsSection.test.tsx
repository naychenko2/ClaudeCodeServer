// Ф2c: ChildSubjectsSection — раздел «Предметы» окна настроек (ADR-041 ред. 7 §13, решение 74).
// Окружение — node + linkedom, как в соседних .test.tsx волны. Полный цикл на каждое действие:
// клик/ввод → onChange → состояние «родителя» (массив подменён) → повторный рендер с ним →
// проверка строк в новом порядке. Компонент своих запросов не делает и состояния не держит —
// это и проверяется: после действия смотрим ПРИШЕДШИЙ массив, а не DOM.
// Отнесущиеся кейсы перенесены из ChildSubjectsEditor.test.tsx (тот редактор удалит Ф1g);
// кейсы маршрута/согласия/пикеров не перенесены: у предмета окна их нет (решения 69–71).
// Мутации этого прогона (копия дерева, вывод — в отчёте задачи):
// (1) move не менять местами, а вставлять элемент перед целевым (сдвиг, не свап) →
//     «стрелки меняют местами, порядок остальных сохраняется» краснеет на [b,a,c];
// (2) id генерировать при каждой правке названия → «правка названия id не меняет» краснеет;
// (3) при коллизии id не добавлять суффикс (оставляющий первый) → «дубль получает суффикс -2»
//     краснеет (второй id совпал бы с первым);
// (4) в addSubject не опускать enabled (оставить без поля) → «новая строка… включена сразу»
//     краснеет: enabled undefined → тумблер новой строки не отмечен.

import { describe, it, expect, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
// Мобильная ветка: useIsMobile (и Badge внутри) читают window.matchMedia при mount.
let mobile = false;
Object.assign(win, {
  matchMedia: () => ({
    matches: mobile, addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {},
  }),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

import { ChildSubjectsSection } from './ChildSubjectsSection';
import type { ChildSettingsSubject, ChildSubjectColor } from './types';

const subject = (id: string, label: string, over: Partial<ChildSettingsSubject> = {}): ChildSettingsSubject => ({
  id, label, glyph: label.slice(0, 3), color: 'info', enabled: true, ...over,
});

let changes: ChildSettingsSubject[][] = [];

async function mount(initial: ChildSettingsSubject[]) {
  let subjects = [...initial];
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  changes = [];
  const render = () => createElement(ChildSubjectsSection, {
    value: subjects,
    onChange: (next: ChildSettingsSubject[]) => {
      changes.push(next);
      subjects = next; // «родитель» принял новый массив целиком
      root.render(render()); // и сразу показывает результат
    },
  });
  await act(async () => { root.render(render()); });
  return { host: host as HTMLElement };
}

const root = (host: HTMLElement) => host.querySelector('[data-testid="child-subjects-section"]') as HTMLElement;
const rows = (host: HTMLElement) => Array.from(root(host).querySelectorAll('[data-testid="subject-row"]'));
// Порядок строк — по названиям в строках: строка больше не кнопка (карточки репетитора в окне нет).
const rowTexts = (host: HTMLElement) => rows(host).map((r) => r.textContent ?? '');

const reactProps = (el: Element): Record<string, any> | undefined => {
  const key = Object.keys(el).find((k) => k.startsWith('__reactProps$'));
  return key ? (el as unknown as Record<string, any>)[key] : undefined;
};
const click = async (el: Element | null, what: string) => {
  expect(el, what).not.toBeNull();
  await act(async () => { reactProps(el!)?.onClick?.({}); });
};
const byAria = (host: HTMLElement, aria: string) =>
  root(host).querySelector(`[aria-label="${aria.replace(/"/g, '\\"')}"]`);
// Кнопка добавления: текст — её aria-имя, отдельного aria-label у Button нет.
const addBtn = (host: HTMLElement) => root(host).querySelector('[data-testid="add-subject"]');
const inputByLabel = (row: Element, label: string) => {
  const lbl = Array.from(row.querySelectorAll('label')).find((l) => (l.textContent ?? '').includes(label));
  return (lbl?.parentElement?.querySelector('input') ?? null) as Element | null;
};
const typeInto = async (input: Element | null, value: string, what: string) => {
  expect(input, what).not.toBeNull();
  await act(async () => { reactProps(input!)?.onChange?.({ target: { value } }); });
};
const last = (): ChildSettingsSubject[] => changes[changes.length - 1];

describe('ChildSubjectsSection', () => {
  it('стрелки меняют местами, порядок остальных сохраняется', async () => {
    const { host } = await mount([subject('a', 'Английский'), subject('b', 'Биология'), subject('c', 'Геометрия')]);
    await click(byAria(host, 'Выше: Биология'), 'стрелка „Выше“ у второй строки');
    expect(rowTexts(host)[0]).toContain('Биология');
    expect(rowTexts(host)[1]).toContain('Английский');
    expect(last().map((s) => s.id)).toEqual(['b', 'a', 'c']);

    // «Ниже» у последней строки после первого свапа disabled (она стала последней) —
    // двигаем середину: «Ниже: Английский» (индекс 1) → [b,c,a].
    await click(byAria(host, 'Ниже: Английский'), 'стрелка „Ниже“ у середины');
    expect(last().map((s) => s.id)).toEqual(['b', 'c', 'a']);
  });

  it('крайние строки: „Выше“ у первой и „Ниже“ у последней — disabled', async () => {
    const { host } = await mount([subject('a', 'Английский'), subject('b', 'Биология')]);
    expect(byAria(host, 'Выше: Английский')!.getAttribute('disabled')).not.toBeNull();
    expect(byAria(host, 'Ниже: Биология')!.getAttribute('disabled')).not.toBeNull();
    expect(byAria(host, 'Ниже: Английский')!.getAttribute('disabled')).toBeNull();
    expect(byAria(host, 'Выше: Биология')!.getAttribute('disabled')).toBeNull();
  });

  it('добавление: id — slug введённого названия, шаблон ^[a-z0-9-]{1,32}$; полей маршрута у предмета нет', async () => {
    const { host } = await mount([subject('math', 'Математика')]);
    await click(addBtn(host), 'кнопка „Добавить предмет“');
    expect(rows(host)).toHaveLength(2);
    const draft = rows(host)[1];
    await typeInto(inputByLabel(draft, 'Название'), 'Окружающий мир', 'поле „Название“ новой строки');

    const added = last()[1];
    expect(added.label).toBe('Окружающий мир');
    expect(added.id).toMatch(/^[a-z0-9-]{1,32}$/);
    // Таблица транслита lib/slug (зеркало бэкового Slugify): «щ» → «sch».
    expect(added.id).toBe('okruzhayuschiy-mir');
    // Предмет окна — только плитка (ChildSettingsSubject, Ф2a): маршрут и репетитор — проект-предмета.
    expect(added, 'никакого маршрута в новом предмете нет').not.toHaveProperty('providerKey');
    expect(added).not.toHaveProperty('model');
    expect(added).not.toHaveProperty('tutorPersonaId');
    // id новой строки не зависел от порядка: она добавлена в конец (порядок = порядок плиток)
    expect(last()[0].id).toBe('math');
  });

  it('новая строка до названия: без id, помечена «новый», включена сразу', async () => {
    const { host } = await mount([subject('math', 'Математика')]);
    await click(addBtn(host), 'кнопка „Добавить предмет“');
    const added = last()[1];
    expect(added.id).toBe('');
    expect(added.enabled, 'новая строка начинается включённой').toBe(true);
    expect(rows(host)[1].textContent).toContain('новый');
    expect(rows(host)[1].querySelector('[role="switch"]')!.getAttribute('aria-checked')).toBe('true');
  });

  it('дубль названия: второй id получает суффикс -2', async () => {
    const { host } = await mount([subject('okruzhayuschiy-mir', 'Окружающий мир')]);
    await click(addBtn(host), 'кнопка „Добавить предмет“');
    await typeInto(inputByLabel(rows(host)[1], 'Название'), 'Окружающий мир', 'поле „Название“ новой строки');
    expect(last()[1].id).toBe('okruzhayuschiy-mir-2');
  });

  it('правка названия id не меняет', async () => {
    const { host } = await mount([subject('math', 'Математика')]);
    await typeInto(inputByLabel(rows(host)[0], 'Название'), 'Математика 2 класс', 'поле „Название“');
    expect(last()[0].id).toBe('math');
    expect(last()[0].label).toBe('Математика 2 класс');
  });

  it('название обрезается по правилу валидатора (40), глиф — по правилу (3); подсказки длин видны', async () => {
    const { host } = await mount([subject('math', 'Математика')]);
    const row = rows(host)[0];
    // Подсказки до отправки: счётчик названия и правило глифа — тексты Field.
    expect(row.textContent).toContain('10/40');
    expect(row.textContent).toContain('1–3 символа');

    await typeInto(inputByLabel(row, 'Название'), 'О'.repeat(60), 'поле „Название“');
    expect(last()[0].label).toHaveLength(40);
    await typeInto(inputByLabel(row, 'Глиф'), 'Математика', 'поле „Глиф“');
    expect(last()[0].glyph).toHaveLength(3);
  });

  it('цвет: ровно 5 вариантов (info, rose, success, warning, accent), выбранный — aria-checked', async () => {
    const { host } = await mount([subject('math', 'Математика', { color: 'rose' })]);
    const swatches = Array.from(rows(host)[0].querySelectorAll('[data-testid^="subject-color-"]'));
    expect(swatches.map((s) => s.getAttribute('data-testid')!.replace('subject-color-', ''))).toEqual(
      ['info', 'rose', 'success', 'warning', 'accent'],
    );
    expect(swatches.map((s) => s.getAttribute('aria-checked'))).toEqual(['false', 'true', 'false', 'false', 'false']);

    await click(rows(host)[0].querySelector('[data-testid="subject-color-accent"]'), 'swatch „accent“');
    expect(last()[0].color).toBe<ChildSubjectColor>('accent');
  });

  it('тумблер „включён” меняет enabled; выключенная строка помечена', async () => {
    const { host } = await mount([subject('math', 'Математика')]);
    const toggle = rows(host)[0].querySelector('[role="switch"]');
    expect(toggle, 'тумблер строки').not.toBeNull();
    expect(toggle!.getAttribute('aria-checked')).toBe('true');
    await click(toggle, 'тумблер');
    expect(last()[0].enabled).toBe(false);
    expect(rows(host)[0].textContent).toContain('выключен');
  });

  it('„Удалить” убирает предмет из массива целиком', async () => {
    const { host } = await mount([subject('a', 'Английский'), subject('b', 'Биология')]);
    await click(byAria(host, 'Удалить предмет «Английский»'), 'кнопка „Удалить“');
    expect(last().map((s) => s.id)).toEqual(['b']);
    expect(rows(host)).toHaveLength(1);
  });

  it('после добавления строка ключуется id, а не позицией: id переживает удаление соседа', async () => {
    const { host } = await mount([subject('a', 'Английский')]);
    await click(addBtn(host), 'кнопка „Добавить предмет“');
    await typeInto(inputByLabel(rows(host)[1], 'Название'), 'Физика', 'поле „Название“ новой строки');
    const newId = last()[1].id;
    expect(newId).toBe('fizika');
    await click(byAria(host, 'Удалить предмет «Английский»'), 'удаление первой строки');
    expect(last().map((s) => s.id)).toEqual([newId]);
  });

  it('после добавления видна подсказка о проекте-предмете; без несохранённой строки её нет', async () => {
    const { host } = await mount([subject('math', 'Математика')]);
    expect(root(host).querySelector('[data-testid="subject-new-hint"]'), 'без новой строки подсказки нет').toBeNull();

    await click(addBtn(host), 'кнопка „Добавить предмет“');
    const hint = root(host).querySelector('[data-testid="subject-new-hint"]');
    expect(hint, 'после создания — подсказка').not.toBeNull();
    expect(hint!.textContent).toContain('Предмет появится у ребёнка как проект');
    expect(hint!.textContent).toContain('файлы и репетитор — в самом проекте');

    // Удалили несохранённую строку — подсказка уходит: она про создание, а не про экран.
    await click(byAria(host, 'Удалить предмет «без названия»'), 'удаление новой строки');
    expect(root(host).querySelector('[data-testid="subject-new-hint"]')).toBeNull();
  });

  it('knowledgeMoved: строка о переносе материалов видна; без маркера её нет, массив не тронут', async () => {
    const { host } = await mount([
      subject('math', 'Математика', { knowledgeMoved: true }),
      subject('geo', 'География'),
    ]);
    const move1 = rows(host)[0].querySelector('[data-testid="subject-materials-move"]');
    expect(move1, 'маркер переноса').not.toBeNull();
    expect(move1!.textContent).toContain('Перенесите материалы в папку предмета');
    expect(rows(host)[1].querySelector('[data-testid="subject-materials-move"]'), 'без маркера — строки нет').toBeNull();
    // Маркер не правится и не порождает действий: компонент не менял массив сам.
    expect(changes.length, 'никто ничего не делал — нового массива нет').toBe(0);
  });
});
