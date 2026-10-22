// Читалка плана (Ф-plans-b): свёртка `##`-разделов длинного документа.
// Порог длинного — 6 и больше `##`; короткий остаётся сплошным текстом без свёртки.
// «Свернуть все»/«Развернуть все» в панели оглавления; переход по пункту «Содержание»
// раскрывает свёрнутый раздел. `##` внутри ```-забора — не заголовок раздела.
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
}));

vi.mock('../../lib/api', () => ({
  api: {
    tasks: {
      get: (id: string) => Promise.resolve({ resultMarkdown: TEXTS[id] ?? '' }),
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

// Состояние свёртки каждого раздела: aria-expanded заголовка по порядку.
function expandedFlags(container: FNode): string[] {
  return allNodes(container, 'plan-section-head').map(n => String(n.getAttribute('aria-expanded')));
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
});
