// Читалка плана (Ф-plans-b): свёртка `##`-разделов длинного документа.
// Порог длинного — 6 и больше `##`; короткий остаётся сплошным текстом без свёртки.
// «Свернуть все»/«Развернуть все» в панели оглавления; переход по пункту «Содержание»
// раскрывает свёрнутый раздел. `##` внутри ```-забора — не заголовок раздела.
// Доработка по ревью: тач-цель переключателя раздела ≥ 40 на 360, заголовок — left,
// сброс свёртки при новом тексте и после «Повторить», привязка скролла к секции (не к
// индексу h2 в DOM), закрытие забора только той же разновидности.
//
// Окружение node (без jsdom): fakeDom (mount) + mock api.tasks.get (текст документа).

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeaturePlanReader } from './FeaturePlanReader';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeaturePlan } from '../../types/featurePlans';

// Тексты по taskId: каждый тест берёт свой id — кэш читалки модульный, пересечений нет.
const TEXTS = vi.hoisted(() => ({
  // 5 разделов — короткий: без оглавления и без свёртки.
  short: [
    'Преамбула.',
    '## Раздел 1', 'тело 1',
    '## Раздел 2', 'тело 2',
    '## Раздел 3', 'тело 3',
    '## Раздел 4', 'тело 4',
    '## Раздел 5', 'тело 5',
  ].join('\n'),
  // 6 разделов — длинный: оглавление и свёртка.
  long: [
    'Преамбула длинного плана.',
    '## Раздел 1', 'тело 1',
    '## Раздел 2', 'тело 2',
    '## Раздел 3', 'тело 3',
    '## Раздел 4', 'тело 4',
    '## Раздел 5', 'тело 5',
    '## Раздел 6', 'тело 6',
  ].join('\n'),
  // 5 настоящих разделов + `##` внутри код-забора: забор не добавляет раздел.
  fenced5: [
    'Преамбула.',
    '## Раздел 1', 'тело 1',
    '## Раздел 2', 'тело 2',
    '## Раздел 3', 'тело 3',
    '## Раздел 4', 'тело 4',
    '## Раздел 5', 'тело 5',
    '```md',
    '## Внутри кода',
    '```',
    'хвост после кода',
  ].join('\n'),
  // 6 настоящих разделов + `##` внутри забора: оглавление считает только настоящие.
  fenced6: [
    'Преамбула.',
    '## Раздел 1', 'тело 1',
    '## Раздел 2', 'тело 2',
    '## Раздел 3', 'тело 3',
    '## Раздел 4', 'тело 4',
    '## Раздел 5', 'тело 5',
    '## Раздел 6', 'тело 6',
    '```md',
    '## Внутри кода',
    '```',
  ].join('\n'),
  // Другой длинный план — для теста сброса свёртки, когда приходит новый текст.
  longB: [
    'Преамбула второго плана.',
    '## Раздел 1', 'тело Б1',
    '## Раздел 2', 'тело Б2',
    '## Раздел 3', 'тело Б3',
    '## Раздел 4', 'тело Б4',
    '## Раздел 5', 'тело Б5',
    '## Раздел 6', 'тело Б6',
  ].join('\n'),
  // 6 настоящих разделов + h2 внутри блок-цитаты: сканер его не считает, но в DOM он есть —
  // переход по «Содержанию» не должен прыгать на соседний раздел.
  quoteH2: [
    'Преамбула.',
    '## Раздел 1', 'тело 1',
    '> ## Заголовок в цитате',
    '## Раздел 2', 'тело 2',
    '## Раздел 3', 'тело 3',
    '## Раздел 4', 'тело 4',
    '## Раздел 5', 'тело 5',
    '## Раздел 6', 'тело 6',
  ].join('\n'),
  // ``` внутри ~~~-забора: закрывающий забор — той же разновидности, строка ``` — текст.
  fenceMix: [
    'Преамбула.',
    '## Раздел 1', 'тело 1',
    '## Раздел 2', 'тело 2',
    '## Раздел 3', 'тело 3',
    '## Раздел 4', 'тело 4',
    '## Раздел 5', 'тело 5',
    '## Раздел 6', 'тело 6',
    '~~~text',
    '```',
    '## Внутри забора',
    '~~~',
  ].join('\n'),
  // ```js внутри ```-забора — содержимое кода, забор не закрывает: `##` после неё остаётся в коде.
  fenceInfo: [
    'Преамбула.',
    '## Раздел 1', 'тело 1',
    '## Раздел 2', 'тело 2',
    '## Раздел 3', 'тело 3',
    '## Раздел 4', 'тело 4',
    '## Раздел 5', 'тело 5',
    '## Раздел 6', 'тело 6',
    '```',
    '```js',
    '## Внутри забора',
    '```',
  ].join('\n'),
  // Забор с отступом 4 пробела — indented code, а не забор: `##` после него настоящий раздел.
  fenceIndent4: [
    'Преамбула.',
    '## Раздел 1', 'тело 1',
    '## Раздел 2', 'тело 2',
    '## Раздел 3', 'тело 3',
    '## Раздел 4', 'тело 4',
    '## Раздел 5', 'тело 5',
    '    ```md',
    '## Раздел 6', 'тело 6',
  ].join('\n'),
  // Ссылка внутри заголовка раздела.
  link: [
    'Преамбула.',
    '## Шаг 2 (см. [ADR-016](https://example.com/adr))', 'тело 1',
    '## Раздел 2', 'тело 2',
    '## Раздел 3', 'тело 3',
    '## Раздел 4', 'тело 4',
    '## Раздел 5', 'тело 5',
    '## Раздел 6', 'тело 6',
  ].join('\n'),
  // Номера и маркеры в начале заголовка не теряются, разметка снимается.
  plainHeads: [
    'Преамбула.',
    '## 1. Введение', 'тело 1',
    '## - Пункт', 'тело 2',
    '## Шаг **два**', 'тело 3',
    '## snake_case', 'тело 4',
    '## a*b', 'тело 5',
    '## Раздел 6', 'тело 6',
  ].join('\n'),
  // Заголовки с разметкой — подпись переключателя без `**`.
  markup: [
    'Преамбула.',
    '## Шаг **два**', 'тело 1',
    '## Раздел 2', 'тело 2',
    '## Раздел 3', 'тело 3',
    '## Раздел 4', 'тело 4',
    '## Раздел 5', 'тело 5',
    '## Раздел 6', 'тело 6',
  ].join('\n'),
}));

// 'flaky': первый вызов роняет (состояние error), последующие дают текст — путь «Повторить».
const _flakyCalls = vi.hoisted(() => new Map<string, number>());
vi.mock('../../lib/api', () => ({
  api: {
    tasks: {
      get: (id: string) => {
        if (id === 'flaky') {
          const n = (_flakyCalls.get(id) ?? 0) + 1;
          _flakyCalls.set(id, n);
          return n === 1
            ? Promise.reject(new Error('отказ загрузки'))
            : Promise.resolve({ resultMarkdown: TEXTS.long });
        }
        return Promise.resolve({ resultMarkdown: TEXTS[id] ?? '' });
      },
    },
  },
}));

type FNode = ReturnType<typeof findNodeByAttr> extends infer T ? (T extends null ? never : NonNullable<T>) : never;

function plan(taskId: string): FeaturePlan {
  return {
    taskId, role: 'plan', title: 'План', authorPersonaId: null,
    createdAt: '2026-10-20T06:00:00Z', completedAt: '2026-10-21T12:00:00Z',
    hasText: true, mockupPaths: [],
  };
}

// Дождаться flush'а microtasks (текст приходит через Promise).
async function flush() {
  await act(async () => { await new Promise(r => setTimeout(r, 10)); });
}

function click(container: FNode, testId: string): void {
  const node = findNodeByAttr(container, 'data-testid', testId);
  expect(node, `node ${testId}`).not.toBeNull();
  const props = reactProps(node!);
  expect(props?.onClick, `onClick on ${testId}`).toBeTruthy();
  act(() => { (props!.onClick as () => void)(); });
}

// Все узлы с data-testid (в порядке DOM).
function allNodes(container: FNode, testId: string): FNode[] {
  const out: FNode[] = [];
  (function walk(n: FNode): void {
    if (n.nodeType === 1 && n.getAttribute('data-testid') === testId) out.push(n);
    for (const c of n.childNodes) walk(c);
  })(container);
  return out;
}

// Состояние свёртки каждого раздела: aria-expanded кнопки-переключателя по порядку.
function expandedFlags(container: FNode): string[] {
  return allNodes(container, 'plan-section-toggle').map(n => String(n.getAttribute('aria-expanded')));
}

// Текст тел разделов (содержимое контейнеров plan-section, кроме самих заголовков).
function bodyText(container: FNode): string {
  return allNodes(container, 'plan-section').map(n => textNodes(n).join(' ')).join(' ');
}

describe('FeaturePlanReader: свёртка разделов', () => {
  beforeEach(() => installFakeDom());
  afterEach(() => {
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // === 1. Короткий документ (5 `##`) — без оглавления и без свёртки ===
  it('короткий документ (5 разделов) — без оглавления и без свёртки', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('short'), mockups: [] }));
    await flush();
    expect(findNodeByAttr(container, 'data-testid', 'plan-reader-toc')).toBeNull();
    expect(allNodes(container, 'plan-section-head').length).toBe(0);
    // Текст сплошной: тела всех пяти разделов на месте, одним блоком.
    expect(bodyText(container)).toBe('');
    expect(textNodes(findNodeByAttr(container, 'data-testid', 'plan-reader-body')!).join(' ')).toContain('тело 5');
  });

  // === 2. Длинный документ (6 `##`) — оглавление и разделы, по умолчанию раскрыты ===
  it('длинный документ (6 разделов) — оглавление и все разделы раскрыты', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('long'), mockups: [] }));
    await flush();
    const toc = findNodeByAttr(container, 'data-testid', 'plan-reader-toc');
    expect(toc).not.toBeNull();
    // «Содержание · 6»: подпись и ровно 6 пунктов оглавления.
    expect(textNodes(toc!).join(' ')).toContain('Содержание');
    expect(allNodes(container, 'plan-reader-toc-item').length).toBe(6);
    expect(allNodes(container, 'plan-section-head').length).toBe(6);
    expect(expandedFlags(container)).toEqual(['true', 'true', 'true', 'true', 'true', 'true']);
    expect(bodyText(container)).toContain('тело 6');
  });

  // === 3. «Свернуть все» меняет состояние разделов, «Развернуть все» становится активной ===
  it('«Свернуть все» сворачивает разделы и гасит себя', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('long'), mockups: [] }));
    await flush();
    const collapse = findNodeByAttr(container, 'data-testid', 'plan-collapse-all');
    const expand = findNodeByAttr(container, 'data-testid', 'plan-expand-all');
    expect(collapse).not.toBeNull();
    expect(reactProps(collapse!)?.disabled).toBeFalsy();
    expect(reactProps(expand!)?.disabled).toBeTruthy(); // всё уже раскрыто — делать нечего
    click(container, 'plan-collapse-all');
    expect(expandedFlags(container)).toEqual(['false', 'false', 'false', 'false', 'false', 'false']);
    // Тела скрыты: в тексте разделов остались только заголовки.
    expect(bodyText(container)).not.toContain('тело 6');
    expect(bodyText(container)).toContain('Раздел 6');
    expect(reactProps(findNodeByAttr(container, 'data-testid', 'plan-collapse-all')!)?.disabled).toBeTruthy();
    expect(reactProps(findNodeByAttr(container, 'data-testid', 'plan-expand-all')!)?.disabled).toBeFalsy();
  });

  // === 4. «Развернуть все» после «Свернуть все» возвращает тела ===
  it('«Развернуть все» раскрывает свёрнутые разделы', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('long'), mockups: [] }));
    await flush();
    click(container, 'plan-collapse-all');
    click(container, 'plan-expand-all');
    expect(expandedFlags(container)).toEqual(['true', 'true', 'true', 'true', 'true', 'true']);
    expect(bodyText(container)).toContain('тело 6');
  });

  // === 5. Переход по «Содержание» к свёрнутому разделу раскрывает его ===
  it('переход по пункту «Содержание» раскрывает свёрнутый раздел', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('long'), mockups: [] }));
    await flush();
    click(container, 'plan-collapse-all');
    const items = allNodes(container, 'plan-reader-toc-item');
    expect(items.length).toBe(6);
    const props = reactProps(items[2]);
    expect(props?.onClick).toBeTruthy();
    act(() => { (props!.onClick as () => void)(); });
    const flags = expandedFlags(container);
    expect(flags[2]).toBe('true');
    expect(textNodes(allNodes(container, 'plan-section')[2]).join(' ')).toContain('тело 3');
  });

  // === 6. `##` внутри код-забора — не заголовок раздела ===
  it('`##` внутри код-блока не добавляет раздел (5 настоящих — короткий документ)', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('fenced5'), mockups: [] }));
    await flush();
    expect(findNodeByAttr(container, 'data-testid', 'plan-reader-toc')).toBeNull();
    expect(allNodes(container, 'plan-section-head').length).toBe(0);
  });

  it('`##` внутри код-блока не попадает в «Содержание» (счёт 6, не 7)', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('fenced6'), mockups: [] }));
    await flush();
    const toc = findNodeByAttr(container, 'data-testid', 'plan-reader-toc');
    expect(toc).not.toBeNull();
    // Счёт только настоящих разделов: 6, не 7 (заголовок в заборе не считается).
    expect(textNodes(toc!).join(' ')).toContain('Содержание');
    expect(allNodes(container, 'plan-reader-toc-item').length).toBe(6);
    expect(allNodes(container, 'plan-section-head').length).toBe(6);
    // Код-блок остаётся телом последнего раздела и рендерится как код, а не как заголовок.
    expect(bodyText(container)).toContain('Внутри кода');
  });

  // === 7. 360: кнопки свёртки ≥ 40 px ===
  it('на 360 кнопки «Свернуть все»/«Развернуть все» не ниже 40 px', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('long'), mockups: [], isMobile: true }));
    await flush();
    for (const testId of ['plan-collapse-all', 'plan-expand-all']) {
      const props = reactProps(findNodeByAttr(container, 'data-testid', testId)!);
      const style = props?.style as Record<string, unknown> | undefined;
      expect(Number(style?.minHeight ?? 0)).toBeGreaterThanOrEqual(40);
    }
  });

  // === 8. 360: тач-цель переключателя раздела ≥ 40 px (макет: тач-целей < 40 — 0) ===
  it('на 360 кнопка-переключатель раздела не ниже 40 px', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('long'), mockups: [], isMobile: true }));
    await flush();
    const toggles = allNodes(container, 'plan-section-toggle');
    expect(toggles.length).toBe(6);
    for (const n of toggles) {
      const props = reactProps(n);
      const style = props?.style as Record<string, unknown> | undefined;
      expect(Number(style?.minHeight ?? 0)).toBeGreaterThanOrEqual(40);
      // Цель меряется по обеим сторонам: ширина тоже не меньше 40.
      expect(style?.width).toBe(40);
      expect(style?.minHeight).toBe(40);
    }
  });

  it('на десктопе кнопка-переключатель раздела 34×26', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('long'), mockups: [] }));
    await flush();
    for (const n of allNodes(container, 'plan-section-toggle')) {
      const style = reactProps(n)?.style as Record<string, unknown> | undefined;
      expect(style?.width).toBe(34);
      expect(style?.minHeight).toBe(26);
    }
  });

  // === 9а. 360: тач-цель пункта «Содержание» ≥ 40 px (дефект приёмки: высота 17 px) ===
  it('на 360 пункт «Содержания» не ниже 40 px', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('long'), mockups: [], isMobile: true }));
    await flush();
    // На 360 оглавление по умолчанию свёрнуто (макет): сначала раскрыть «Содержание · N».
    const toc = findNodeByAttr(container, 'data-testid', 'plan-reader-toc');
    expect(toc, 'панель «Содержание»').not.toBeNull();
    const header = toc!.children[0];
    act(() => { (reactProps(header)!.onClick as () => void)(); });
    const items = allNodes(container, 'plan-reader-toc-item');
    expect(items.length).toBe(6);
    for (const n of items) {
      const style = reactProps(n)?.style as Record<string, unknown> | undefined;
      expect(Number(style?.minHeight ?? 0), 'высота пункта оглавления на 360').toBeGreaterThanOrEqual(40);
    }
    // сетка оглавления на 360 — одна колонка
    const ol = items[0].parentNode!.parentNode!;
    expect((reactProps(ol)?.style as Record<string, unknown>).gridTemplateColumns, 'колонки оглавления на 360').toBe('1fr');
  });

  it('на 1280 пункт «Содержания» остаётся компактной строкой (двухколоночная сетка не разъезжается)', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('long'), mockups: [] }));
    await flush();
    const items = allNodes(container, 'plan-reader-toc-item');
    expect(items.length).toBe(6);
    for (const n of items) {
      const style = reactProps(n)?.style as Record<string, unknown> | undefined;
      expect(Number(style?.minHeight ?? 0)).toBe(0);
    }
    // две колонки на десктопе — утверждение по самой сетке оглавления
    const ol = items[0].parentNode!.parentNode!;
    expect((reactProps(ol)?.style as Record<string, unknown>).gridTemplateColumns, 'колонки оглавления на 1280').toBe('1fr 1fr');
  });

  // === 9б. Порядок «раскрыть → докрутить»: прокрука считается по уже раскрытому разделу ===
  it('докрутка к разделу считается после раскрытия, а не по свёрнутому', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('long'), mockups: [] }));
    await flush();
    click(container, 'plan-collapse-all');
    const target = findNodeByAttr(container, 'data-plan-section', '2');
    expect(target, 'секция 3').not.toBeNull();
    // Что видно в DOM в момент прокрутки: подменяем scrollIntoView и снимаем состояние.
    const atScroll: string[] = [];
    target!.scrollIntoView = () => { atScroll.push(textNodes(target!).join(' ')); };
    const items = allNodes(container, 'plan-reader-toc-item');
    act(() => { (reactProps(items[2])!.onClick as () => void)(); });
    expect(atScroll.length, 'докрутка не выполнена').toBe(1);
    // Тело раздела уже в DOM: позиция считается по раскрытому содержимому, а не по одной
    // строке заголовка (иначе после раскрытия раздел уезжает из-под липких вкладок).
    expect(atScroll[0], 'прокрука по свёрнутому разделу').toContain('тело 3');
  });

  // === 9. Заголовок раздела выровнен по левому краю (кнопка по умолчанию центрирует) ===
  it('заголовок раздела имеет text-align: left', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('long'), mockups: [] }));
    await flush();
    const heads = allNodes(container, 'plan-section-head');
    expect(heads.length).toBe(6);
    for (const n of heads) {
      const props = reactProps(n);
      const style = props?.style as Record<string, unknown> | undefined;
      expect(String(style?.textAlign ?? '')).toBe('left');
    }
  });

  // === 10. Приходит текст другого плана — свёртка сбрасывается ===
  it('при новом тексте все разделы раскрыты', async () => {
    const { root, container } = mount(createElement(FeaturePlanReader, { plan: plan('long'), mockups: [] }));
    await flush();
    click(container, 'plan-collapse-all');
    expect(expandedFlags(container)).toEqual(['false', 'false', 'false', 'false', 'false', 'false']);
    // Тот же читатель рисует другой план: эффект загрузки проходит заново и сбрасывает свёртку.
    act(() => { root.render(createElement(FeaturePlanReader, { plan: plan('longB'), mockups: [] })); });
    await flush();
    expect(expandedFlags(container)).toEqual(['true', 'true', 'true', 'true', 'true', 'true']);
    expect(bodyText(container)).toContain('тело Б6');
  });

  // === 11. Ошибка → «Повторить» → текст: свёртка тоже сброшена ===
  it('после «Повторить» все разделы раскрыты', async () => {
    const { root, container } = mount(createElement(FeaturePlanReader, { plan: plan('long'), mockups: [] }));
    await flush();
    click(container, 'plan-collapse-all');
    // План переписан: новая загрузка сначала ошибается, после «Повторить» даёт текст.
    act(() => { root.render(createElement(FeaturePlanReader, { plan: plan('flaky'), mockups: [] })); });
    await flush();
    expect(findNodeByAttr(container, 'data-testid', 'plan-reader-error')).not.toBeNull();
    click(container, 'plan-retry');
    await flush();
    expect(expandedFlags(container)).toEqual(['true', 'true', 'true', 'true', 'true', 'true']);
    expect(bodyText(container)).toContain('тело 6');
  });

  // === 12. Переход по «Содержанию» — к своей секции, а не по индексу h2 в DOM ===
  it('переход к разделу 3, когда в теле раздела 1 есть h2 в цитате', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('quoteH2'), mockups: [] }));
    await flush();
    // Сканер считает только настоящие разделы: 6, без заголовка в цитате.
    expect(allNodes(container, 'plan-reader-toc-item').length).toBe(6);
    const items = allNodes(container, 'plan-reader-toc-item');
    const props = reactProps(items[2]);
    act(() => { (props!.onClick as () => void)(); });
    // Цель скролла — сама секция 3 (data-plan-section="2"), а не h2 из цитаты раздела 1.
    const target = findNodeByAttr(container, 'data-plan-section', '2');
    expect(target, 'секция 3').not.toBeNull();
    expect(target!.style.scrollMarginTop).toBe('72px');
  });

  // === 13. ``` внутри ~~~-забора не закрывает его (CommonMark: та же разновидность) ===
  it('строка ``` внутри ~~~-забора — текст, а не закрытие забора', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('fenceMix'), mockups: [] }));
    await flush();
    // Если бы ``` закрыл ~~~-забор, «## Внутри забора» стал бы седьмым разделом.
    expect(allNodes(container, 'plan-section-head').length).toBe(6);
    expect(allNodes(container, 'plan-reader-toc-item').length).toBe(6);
  });

  // === 14. Забор: info-строка не закрывает, отступ 4 не открывает ===
  it('строка ```js внутри ```-забора не закрывает его: `##` после неё остаётся в коде', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('fenceInfo'), mockups: [] }));
    await flush();
    expect(allNodes(container, 'plan-section-head').length).toBe(6);
    expect(allNodes(container, 'plan-reader-toc-item').length).toBe(6);
  });

  it('забор с отступом 4 пробела не открывает забор: следующий `##` — раздел', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('fenceIndent4'), mockups: [] }));
    await flush();
    expect(allNodes(container, 'plan-section-head').length).toBe(6);
    expect(allNodes(container, 'plan-reader-toc-item').length).toBe(6);
  });

  // === 15. Подпись переключателя и «Содержание» — без markdown ===
  it('aria-label переключателя и пункт «Содержание» без разметки markdown', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('markup'), mockups: [] }));
    await flush();
    const toggle = allNodes(container, 'plan-section-toggle')[0];
    expect(toggle.getAttribute('aria-label')).toBe('Свернуть раздел Шаг два');
    expect(textNodes(allNodes(container, 'plan-reader-toc-item')[0]).join(' ')).toBe('Шаг два');
  });

  it('подпись раздела сохраняет номер и маркер, снимает только инлайн-разметку', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('plainHeads'), mockups: [] }));
    await flush();
    const labels = allNodes(container, 'plan-section-toggle').map(n => n.getAttribute('aria-label'));
    expect(labels.slice(0, 5)).toEqual([
      'Свернуть раздел 1. Введение',
      'Свернуть раздел - Пункт',
      'Свернуть раздел Шаг два',
      'Свернуть раздел snake_case',
      'Свернуть раздел a*b',
    ]);
    expect(allNodes(container, 'plan-reader-toc-item').slice(0, 2).map(n => textNodes(n).join(' '))).toEqual(['1. Введение', '- Пункт']);
  });

  // === 16. Свёртка — только кнопкой-шевелоном; строка заголовка не кликабельна ===
  const fakeEvent = () => {
    const ev = { stopped: false, stopPropagation() { ev.stopped = true; }, preventDefault() {} };
    return ev;
  };

  // Клик по узлу с всплытием вверх до корня: зовём onClick каждого предка, пока не остановят.
  function bubbleClick(target: FNode): void {
    const ev = fakeEvent();
    act(() => {
      for (let n: FNode | null = target; n && !ev.stopped; n = n.parentNode as FNode | null) {
        const h = reactProps(n)?.onClick as ((e: unknown) => void) | undefined;
        h?.(ev);
      }
    });
  }

  function firstDescendant(root: FNode, pred: (n: FNode) => boolean): FNode | null {
    for (const c of root.childNodes as FNode[]) {
      if (c.nodeType === 1 && pred(c)) return c;
      const r = firstDescendant(c, pred);
      if (r) return r;
    }
    return null;
  }

  it('клик по тексту заголовка раздел не переключает', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('long'), mockups: [] }));
    await flush();
    const head = allNodes(container, 'plan-section-head')[1];
    expect(reactProps(head)!.onClick).toBeUndefined();
    bubbleClick(firstDescendant(head, n => n.tagName?.toLowerCase() === 'h2') ?? head);
    expect(expandedFlags(container)).toEqual(['true', 'true', 'true', 'true', 'true', 'true']);
  });

  it('клик по ссылке внутри заголовка раздел не переключает', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('link'), mockups: [] }));
    await flush();
    const head = allNodes(container, 'plan-section-head')[0];
    const link = firstDescendant(head, n => n.tagName?.toLowerCase() === 'a');
    expect(link, 'ссылка в заголовке').not.toBeNull();
    bubbleClick(link!);
    expect(expandedFlags(container)[0]).toBe('true');
  });

  it('двойной клик по тексту заголовка раздел не переключает', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('long'), mockups: [] }));
    await flush();
    const head = allNodes(container, 'plan-section-head')[0];
    bubbleClick(head);
    expect(expandedFlags(container)[0]).toBe('true');
    bubbleClick(head);
    expect(expandedFlags(container)[0]).toBe('true');
  });

  it('клик по шевелону переключает раздел один раз', async () => {
    const { container } = mount(createElement(FeaturePlanReader, { plan: plan('long'), mockups: [] }));
    await flush();
    bubbleClick(allNodes(container, 'plan-section-toggle')[0]);
    expect(expandedFlags(container)).toEqual(['false', 'true', 'true', 'true', 'true', 'true']);
  });
});
