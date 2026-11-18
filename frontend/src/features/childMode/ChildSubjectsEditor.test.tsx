// Т4d: ChildSubjectsEditor — контролируемый список предметов (макет Д1, «Предметы на главной»).
// Окружение — node + linkedom, как в соседних .test.tsx волны (пикер Т4c монтируется настоящий).
// Полный цикл на каждое действие: клик/ввод → onChange → состояние «родителя» (массив подменён)
// → повторный рендер с ним → проверка строк в новом порядке. Компонент своих запросов не делает
// и состояния не держит — это и проверяется: после действия смотрим ПРИШЕДШИЙ массив, а не DOM.
// Мутации этого прогона (вывод — в отчёте задачи):
// (1) move не менять местами, а вставлять элемент перед целевым (сдвиг, не свап) →
//     «стрелки меняют местами, порядок остальных сохраняется» краснеет на [b,a,c];
// (2) id генерировать при каждой правке названия → «правка названия id не меняет» краснеет;
// (3) при коллизии id не добавлять суффикс (оставляющий первый) → «дубль получает суффикс -2»
//     краснеет (второй id совпал бы с первым).

import { describe, it, expect, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
// Мобильная ветка: useIsMobile (и Badge/пикер внутри) читают window.matchMedia при mount.
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

import { ChildSubjectsEditor } from './ChildSubjectsEditor';
import type { ChildRouteValue } from './ChildRoutePicker';
import type { ChildRoute, ChildSubjectAdmin, ChildSubjectColor } from './types';

// Тот же состав, что отдаёт GET /api/admin/child-routes (порядок — конфига).
const routes = (): ChildRoute[] => [
  {
    providerKey: 'anthropic-api-kids', kind: 'pool', label: 'Anthropic · ключ «kids»',
    priced: true, requiresAck: false,
    models: [{ id: 'sonnet', label: 'Sonnet' }, { id: 'haiku', label: 'Haiku' }],
  },
  {
    providerKey: 'glm', kind: 'cloud', label: 'GLM (Zhipu)',
    priced: true, requiresAck: true,
    models: [{ id: 'glm-5', label: 'GLM-5' }],
  },
];

const subject = (id: string, label: string, over: Partial<ChildSubjectAdmin> = {}): ChildSubjectAdmin => ({
  id, label, glyph: label.slice(0, 3), color: 'info',
  providerKey: 'anthropic-api-kids', model: 'sonnet', enabled: true, ...over,
});

let changes: ChildSubjectAdmin[][] = [];
let selectedCalls: string[] = [];

async function mount(
  initial: ChildSubjectAdmin[],
  opts: { routes?: ChildRoute[]; defaultRoute?: ChildRouteValue } = {},
) {
  let subjects = [...initial];
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  changes = [];
  selectedCalls = [];
  const render = () => createElement(ChildSubjectsEditor, {
    subjects,
    routes: opts.routes ?? routes(),
    onChange: (next: ChildSubjectAdmin[]) => {
      changes.push(next);
      subjects = next; // «родитель» принял новый массив целиком
      root.render(render()); // и сразу показывает результат
    },
    selectedId: null,
    onSelect: (id: string) => { selectedCalls.push(id); },
    defaultRoute: opts.defaultRoute,
  });
  await act(async () => { root.render(render()); });
  return { host: host as HTMLElement };
}

const root = (host: HTMLElement) => host.querySelector('[data-testid="child-subjects-editor"]') as HTMLElement;
const rows = (host: HTMLElement) => Array.from(root(host).querySelectorAll('[data-testid="subject-row"]'));
// Порядок строк — по доступному имени кнопки строки («Открыть: …»), оно несёт label строки.
const rowTitles = (host: HTMLElement) =>
  rows(host).map((r) => r.querySelector('[data-testid="subject-open"]')?.getAttribute('aria-label') ?? '');

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
const last = (): ChildSubjectAdmin[] => changes[changes.length - 1];

describe('ChildSubjectsEditor', () => {
  it('стрелки меняют местами, порядок остальных сохраняется', async () => {
    const { host } = await mount([subject('a', 'Английский'), subject('b', 'Биология'), subject('c', 'Геометрия')]);
    await click(byAria(host, 'Выше: Биология'), 'стрелка «Выше» у второй строки');
    expect(rowTitles(host)).toEqual(['Открыть: Биология', 'Открыть: Английский', 'Открыть: Геометрия']);
    expect(last().map((s) => s.id)).toEqual(['b', 'a', 'c']);

    // «Ниже» у последней строки после первого свапа disabled (она стала последней) —
    // двигаем середину: «Ниже: Английский» (индекс 1) → [b,c,a].
    await click(byAria(host, 'Ниже: Английский'), 'стрелка «Ниже» у середины');
    expect(last().map((s) => s.id)).toEqual(['b', 'c', 'a']);
  });

  it('крайние строки: «Выше» у первой и «Ниже» у последней — disabled', async () => {
    const { host } = await mount([subject('a', 'Английский'), subject('b', 'Биология')]);
    expect(byAria(host, 'Выше: Английский')!.getAttribute('disabled')).not.toBeNull();
    expect(byAria(host, 'Ниже: Биология')!.getAttribute('disabled')).not.toBeNull();
    expect(byAria(host, 'Ниже: Английский')!.getAttribute('disabled')).toBeNull();
    expect(byAria(host, 'Выше: Биология')!.getAttribute('disabled')).toBeNull();
  });

  it('добавление: id — slug введённого названия, шаблон ^[a-z0-9-]{1,32}$', async () => {
    const { host } = await mount([subject('math', 'Математика')]);
    await click(addBtn(host), 'кнопка «Добавить предмет»');
    expect(rows(host)).toHaveLength(2);
    const draft = rows(host)[1];
    await typeInto(inputByLabel(draft, 'Название'), 'Окружающий мир', 'поле «Название» новой строки');

    const added = last()[1];
    expect(added.label).toBe('Окружающий мир');
    expect(added.id).toMatch(/^[a-z0-9-]{1,32}$/);
    // Таблица транслита lib/slug (зеркало бэкового Slugify): «щ» → «sch».
    expect(added.id).toBe('okruzhayuschiy-mir');
    // id новой строки не зависел от порядка: она добавлена в конец (порядок = порядок плиток)
    expect(last()[0].id).toBe('math');
  });

  it('дубль названия: второй id получает суффикс -2', async () => {
    const { host } = await mount([subject('okruzhayuschiy-mir', 'Окружающий мир')]);
    await click(addBtn(host), 'кнопка «Добавить предмет»');
    await typeInto(inputByLabel(rows(host)[1], 'Название'), 'Окружающий мир', 'поле «Название» новой строки');
    expect(last()[1].id).toBe('okruzhayuschiy-mir-2');
  });

  it('правка названия id не меняет', async () => {
    const { host } = await mount([subject('math', 'Математика')]);
    await typeInto(inputByLabel(rows(host)[0], 'Название'), 'Математика 2 класс', 'поле «Название»');
    expect(last()[0].id).toBe('math');
    expect(last()[0].label).toBe('Математика 2 класс');
  });

  it('название обрезается по правилу валидатора (40), глиф — по правилу (3); подсказки длин видны', async () => {
    const { host } = await mount([subject('math', 'Математика')]);
    const row = rows(host)[0];
    // Подсказки до отправки: счётчик названия и правило глифа — тексты Field.
    expect(row.textContent).toContain('10/40');
    expect(row.textContent).toContain('1–3 символа');

    await typeInto(inputByLabel(row, 'Название'), 'О'.repeat(60), 'поле «Название»');
    expect(last()[0].label).toHaveLength(40);
    await typeInto(inputByLabel(row, 'Глиф'), 'Математика', 'поле «Глиф»');
    expect(last()[0].glyph).toHaveLength(3);
  });

  it('цвет: ровно 5 вариантов (info, rose, success, warning, accent), выбранный — aria-checked', async () => {
    const { host } = await mount([subject('math', 'Математика', { color: 'rose' })]);
    const swatches = Array.from(rows(host)[0].querySelectorAll('[data-testid^="subject-color-"]'));
    expect(swatches.map((s) => s.getAttribute('data-testid')!.replace('subject-color-', ''))).toEqual(
      ['info', 'rose', 'success', 'warning', 'accent'],
    );
    expect(swatches.map((s) => s.getAttribute('aria-checked'))).toEqual(['false', 'true', 'false', 'false', 'false']);

    await click(rows(host)[0].querySelector('[data-testid="subject-color-accent"]'), 'swatch «accent»');
    expect(last()[0].color).toBe<ChildSubjectColor>('accent');
  });

  it('тумблер «включён» меняет enabled; выключенная строка помечена', async () => {
    const { host } = await mount([subject('math', 'Математика')]);
    const toggle = rows(host)[0].querySelector('[role="switch"]');
    expect(toggle, 'тумблер строки').not.toBeNull();
    expect(toggle!.getAttribute('aria-checked')).toBe('true');
    await click(toggle, 'тумблер');
    expect(last()[0].enabled).toBe(false);
    expect(rows(host)[0].textContent).toContain('выключен');
  });

  it('«Удалить» убирает предмет из массива целиком', async () => {
    const { host } = await mount([subject('a', 'Английский'), subject('b', 'Биология')]);
    await click(byAria(host, 'Удалить предмет «Английский»'), 'кнопка «Удалить»');
    expect(last().map((s) => s.id)).toEqual(['b']);
    expect(rows(host)).toHaveLength(1);
  });

  it('клик по строке зовёт onSelect(id); новая строка без id — не выбирается', async () => {
    const { host } = await mount([subject('math', 'Математика')]);
    await click(rows(host)[0].querySelector('[data-testid="subject-open"]'), 'текст строки');
    expect(selectedCalls).toEqual(['math']);

    await click(addBtn(host), 'кнопка «Добавить предмет»');
    await click(rows(host)[1].querySelector('[data-testid="subject-open"]'), 'текст новой строки');
    expect(selectedCalls).toEqual(['math']); // без id — ничего не открылось
  });

  it('маршрут нового предмета — defaultRoute родителя; без него — первая пара routes', async () => {
    const withDefault = await mount([], { defaultRoute: { providerKey: 'glm', model: 'glm-5' } });
    // Пустой список — empty-state со своей кнопкой добавления (тот же dataTestId).
    await click(addBtn(withDefault.host), '«Добавить предмет» в empty-state');
    expect(last()).toHaveLength(1);
    expect(last()[0].providerKey).toBe('glm');
    expect(last()[0].model).toBe('glm-5');

    const plain = await mount([]);
    await click(addBtn(plain.host), '«Добавить предмет» в empty-state');
    expect(last()[0].providerKey).toBe('anthropic-api-kids');
    expect(last()[0].model).toBe('sonnet');
  });

  it('после добавления строка ключуется id, а не позицией: id переживает удаление соседа', async () => {
    const { host } = await mount([subject('a', 'Английский')]);
    await click(addBtn(host), 'кнопка «Добавить предмет»');
    await typeInto(inputByLabel(rows(host)[1], 'Название'), 'Физика', 'поле «Название» новой строки');
    const newId = last()[1].id;
    expect(newId).toBe('fizika');
    await click(byAria(host, 'Удалить предмет «Английский»'), 'удаление первой строки');
    expect(last().map((s) => s.id)).toEqual([newId]);
  });
});
