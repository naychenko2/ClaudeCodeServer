// Тесты оболочки блока «Кто и на чём работал» (ST-F13). Рендер на fakeDom: шапка
// с двумя переключателями; смена разреза и меры; действующая мера решается в
// оболочке один раз: выбранная без данных фолбэчит на время, и шапка, кольцо и
// карточки показывают одну меру (М1); hiddenKeys отсекается один раз —
// кольцо, таблица и карточки видят ОДИН набор групп; плашка costPartial стоит
// рядом с кольцом, колонца кольца фиксирована 140 px и не растягивается плашками
// (М2); «без расхода» — плашка «нет учёта» и приглушённые опции меры;
// меньше двух групп — «Пока нечего разрезать»; мобила — карточки, без кольца и
// таблицы. Числа — те, что отдаёт модель ST-F8; пересчёт в оболочке не проверяем.
//
// Мутации (проверены прогоном):
// - убрать фильтр hiddenKeys в оболочке (дать ShareRing сырые группы) — краснеет
//   тест «один набор групп» (дуг в кольце 4, строк 3);
// - дать карточкам сырую measure вместо действующей — краснеет тест «деньги без
//   расхода» на 360 (доли share-pct исчезают);
// - снять width: 140 у колонцы кольца — краснеет тест фиксированной ширины.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureBreakdownBlock } from './FeatureBreakdownBlock';
import { C } from '../../lib/design';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureBreakdown, FeatureBreakdownGroup } from '../../types/featureStats';
import type { FeatureRun } from '../../types/featureRun';

// useIsMobile — управляемый флаг (по умолчанию десктоп).
const { mobileFlag } = vi.hoisted(() => ({ mobileFlag: { value: false } }));
vi.mock('../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => mobileFlag.value };
});

function mkGroup(
  key: string,
  minutes: number,
  tokens: number | null,
  costUsd: number | null,
  chatIds: string[] = [],
): FeatureBreakdownGroup {
  return { key, minutes, tokens, costUsd, chatIds };
}

// Фикстура НЕ в порядке выдачи по мере: 'feature' (500 мин) идёт первой, затем
// vera(30), kira(120), timur(60) — сортировка по времени даёт другой порядок.
// Скрытый ключ 'feature' — самый крупный: если фильтр не сработает, это видно
// и по дугам кольца, и по строкам таблицы.
const PERSONAS: FeatureBreakdownGroup[] = [
  mkGroup('feature', 500, 9000, 9.0, ['c0']),
  mkGroup('vera', 30, null, null),
  mkGroup('kira', 120, 1000, 0, ['c1']),
  mkGroup('timur', 60, 3000, 5.0, ['c2']),
];
const MODELS: FeatureBreakdownGroup[] = [
  mkGroup('opus', 180, 4000, 5.0, ['c0', 'c2']),
  mkGroup('qwen', 120, 1000, 0, ['c1']),
];
const ROLES: FeatureBreakdownGroup[] = [
  mkGroup('step', 120, 1000, 0, ['c1']),
  mkGroup('review', 60, 3000, 5.0, ['c2']),
];
const BREAKDOWN: FeatureBreakdown = { personas: PERSONAS, models: MODELS, roles: ROLES };
// «Без расхода» (М1): время есть, токены и деньги — null у всех групп.
const NO_COST: FeatureBreakdown = {
  personas: PERSONAS.map(g => ({ ...g, tokens: null, costUsd: null })),
  models: [],
  roles: [],
};

const LABELS: Record<string, string> = {
  feature: 'фича', vera: 'Вера', kira: 'Кира', timur: 'Тимур', opus: 'Opus', qwen: 'Qwen 3.8',
  step: 'шаг', review: 'ревью',
};
const labelOf = (key: string): string => LABELS[key] ?? key;

function mkRun(): FeatureRun {
  return {
    featureId: 'f-1',
    state: 'merged',
    steps: [],
    reviews: [],
    qa: [],
    final: null,
    merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 100,
    ready: true,
  };
}

type MountExtra = {
  breakdown?: FeatureBreakdown;
  costPartial?: boolean;
  hiddenKeys?: string[];
  run?: FeatureRun;
};

function mountBlock(extra: MountExtra = {}) {
  return mount(createElement(FeatureBreakdownBlock, {
    breakdown: extra.breakdown ?? BREAKDOWN,
    costPartial: extra.costPartial ?? false,
    run: extra.run ?? mkRun(),
    labelOf,
    hiddenKeys: extra.hiddenKeys,
  }));
}

function textOf(node: unknown): string {
  return textNodes(node as never).join('');
}

// Кнопки SegmentedControl без data-testid — искать по точному тексту (приём из
// FeatureStatsTab.test.ts), но только внутри шапки: заголовки числовых колонок
// таблицы тоже читаются как «время»/«токены».
type FNodeLike = { nodeType: number; nodeName: string; childNodes: FNodeLike[] };
function headNode(container: ReturnType<typeof mount>['container']): FNodeLike | null {
  return findNodeByAttr(container, 'data-testid', 'breakdown-head') as FNodeLike | null;
}
function findButtonByLabel(root: FNodeLike, label: string): FNodeLike | null {
  const stack: FNodeLike[] = [root];
  while (stack.length) {
    const n = stack.pop()!;
    if (n.nodeType === 1 && n.nodeName === 'BUTTON' && textNodes(n as never).join('').trim() === label) return n;
    for (const ch of n.childNodes) stack.push(ch);
  }
  return null;
}

// aria-label живёт на svg внутри обёртки share-ring — брать его у svg, не у div.
function ringAria(container: ReturnType<typeof mount>['container']): string | null {
  const ring = findNodeByAttr(container, 'data-testid', 'share-ring');
  if (!ring) return null;
  const svg = ring.children.find(c => c.nodeName === 'SVG');
  return svg ? svg.getAttribute('aria-label') : null;
}

function click(node: FNodeLike | null) {
  if (!node) return;
  act(() => { (reactProps(node as never) as { onClick: () => void }).onClick(); });
}

// Дуги кольца (без фоновой окружности) и строки таблицы — как множества ключей.
function ringArcCount(container: ReturnType<typeof mount>['container']): number {
  const ring = findNodeByAttr(container, 'data-testid', 'share-ring');
  if (!ring) return 0;
  return ring.querySelectorAll('circle').length - 1;
}

function tableRowKeys(container: ReturnType<typeof mount>['container']): string[] {
  const table = findNodeByAttr(container, 'data-testid', 'feature-breakdown-table');
  if (!table) return [];
  return table
    .querySelectorAll('tr')
    .filter(t => (t.getAttribute('data-testid') ?? '').startsWith('breakdown-row-'))
    .map(t => (t.getAttribute('data-testid') as string).slice('breakdown-row-'.length));
}

beforeEach(() => { installFakeDom(); });
afterEach(() => { uninstallFakeDom(); mobileFlag.value = false; });

describe('шапка (к1)', () => {
  it('заголовок и оба переключателя на месте', () => {
    const { container } = mountBlock();
    const head = headNode(container);
    expect(head, 'шапки блока').toBeTruthy();
    expect(textOf(findNodeByAttr(container, 'data-testid', 'breakdown-title'))).toBe('Кто и на чём работал');
    for (const label of ['персоны', 'модели', 'роли', 'время', 'токены', 'деньги']) {
      expect(findButtonByLabel(head!, label), `сегмент «${label}»`).toBeTruthy();
    }
  });
});

describe('разрез и мера (к1, к3)', () => {
  it('смена разреза: кольцо и таблица работают выбранной осью, центр — «модели суммарно»', () => {
    const { container } = mountBlock();
    click(findButtonByLabel(headNode(container)!, 'модели'));
    expect(ringAria(container)).toContain('Доли по времени: Opus');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-cost'))).toContain('модели суммарно');
    expect(tableRowKeys(container)).toEqual(['opus', 'qwen']);
  });

  it('смена меры: центр кольца — итог по выбранной мере', () => {
    const { container } = mountBlock();
    click(findButtonByLabel(headNode(container)!, 'токены'));
    const total = textOf(findNodeByAttr(container, 'data-testid', 'share-ring-total'));
    // 9000+1000+3000 = 13000 → «13 тыс.» (числа из модели, не из оболочки).
    expect(total).toBe('13 тыс.');
    expect(ringAria(container)).toContain('Доли по токенам');
  });

  it('«деньги» без расхода: действующая мера — время, шапка и кольцо не противоречат (М1)', () => {
    const { container } = mountBlock({ breakdown: NO_COST });
    click(findButtonByLabel(headNode(container)!, 'деньги'));
    // Кольцо — по времени, а не «по деньгам».
    const aria = ringAria(container) ?? '';
    expect(aria).toContain('Доли по времени');
    expect(aria).not.toContain('по деньгам');
    // Активный сегмент шапки — «время», а не «деньги».
    const head = headNode(container)!;
    expect(findButtonByLabel(head, 'деньги')!.style.background).not.toBe(C.accent);
    expect(findButtonByLabel(head, 'время')!.style.background).toBe(C.accent);
  });

  it('labelOf прокинут в таблицу: имя строки — метка, не сырой ключ', () => {
    const { container } = mountBlock();
    const text = textOf(findNodeByAttr(container, 'data-testid', 'breakdown-row-kira'));
    expect(text).toContain('Кира');
    expect(text).not.toContain('kira');
  });
});

describe('один набор групп для кольца, таблицы и карточек (к1, к3)', () => {
  it('hiddenKeys отсекается один раз: скрытой группы нет и в кольце, и в таблице', () => {
    const { container } = mountBlock({ hiddenKeys: ['feature'] });
    expect(findNodeByAttr(container, 'data-testid', 'share-ring'), 'кольца нет').toBeTruthy();
    const aria = ringAria(container) ?? '';
    expect(aria).toContain('Кира');
    expect(aria).toContain('Тимур');
    expect(aria).toContain('Вера');
    expect(aria, 'скрытый ключ в aria-label кольца').not.toContain('фича');
    expect(findNodeByAttr(container, 'data-testid', 'breakdown-row-feature'), 'скрытая строка в таблице').toBeNull();
    // Одинаковое множество: дуг в кольце ровно столько, сколько строк в таблице.
    expect(ringArcCount(container)).toBe(3);
    expect(tableRowKeys(container)).toEqual(['kira', 'timur', 'vera']);
  });

  it('без hiddenKeys скрытых нет: и «фича», и её строка видны', () => {
    const { container } = mountBlock();
    expect(ringArcCount(container)).toBe(4);
    expect(tableRowKeys(container).length).toBe(4);
  });
});

describe('плашки (к2)', () => {
  it('costPartial: плашка «цена по части моделей недоступна» стоит рядом с кольцом', () => {
    const { container } = mountBlock({ costPartial: true });
    const plate = findNodeByAttr(container, 'data-testid', 'breakdown-cost-partial');
    const ring = findNodeByAttr(container, 'data-testid', 'share-ring');
    expect(plate, 'плашки costPartial').toBeTruthy();
    expect(ring, 'кольца нет').toBeTruthy();
    expect(textOf(plate!)).toContain('цена по части моделей недоступна');
    // Рядом — в той же колонце, что и кольцо (общий родитель).
    expect(plate!.parentElement!.contains(ring!)).toBe(true);
  });

  it('costPartial=false — плашки нет', () => {
    const { container } = mountBlock();
    expect(findNodeByAttr(container, 'data-testid', 'breakdown-cost-partial')).toBeNull();
  });

  it('колонца кольца фиксирована 140 px: плашки не растягивают её (М2, раскладка 1280)', () => {
    const { container } = mountBlock({ costPartial: true });
    const col = findNodeByAttr(container, 'data-testid', 'breakdown-ring-col');
    expect(col, 'колонцы кольца').toBeTruthy();
    const w = Number(String(col!.style.width).replace('px', ''));
    expect(w, 'ширина колонцы').toBe(140);
  });

  it('без расхода: плашка «нет учёта» и приглушённые «токены»/«деньги»', () => {
    const noCost: FeatureBreakdown = {
      personas: PERSONAS.map(g => ({ ...g, tokens: null, costUsd: null })),
      models: [],
      roles: [],
    };
    const { container } = mountBlock({ breakdown: noCost });
    const plate = findNodeByAttr(container, 'data-testid', 'breakdown-no-cost');
    expect(plate, 'плашки «без расхода»').toBeTruthy();
    expect(textOf(plate!)).toContain('нет учёта');
    const head = headNode(container)!;
    const tok = findButtonByLabel(head, 'токены');
    const cost = findButtonByLabel(head, 'деньги');
    expect(tok!.getAttribute('title')).toBe('токены недоступны без учёта расхода');
    expect(cost!.getAttribute('title')).toBe('деньги недоступны без учёта расхода');
  });

  it('меньше двух групп: «Пока нечего разрезать» с объяснением, без кольца и таблицы', () => {
    const { container } = mountBlock({
      breakdown: { personas: [mkGroup('kira', 120, 1000, 0, ['c1'])], models: [], roles: [] },
    });
    const empty = findNodeByAttr(container, 'data-testid', 'breakdown-empty');
    expect(empty, 'пустого блока').toBeTruthy();
    expect(textOf(empty!)).toContain('Пока нечего разрезать');
    expect(textOf(empty!)).toContain('Кира — 2 ч');
    expect(findNodeByAttr(container, 'data-testid', 'share-ring')).toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'feature-breakdown-table')).toBeNull();
  });
});

describe('мобила (к1)', () => {
  it('на 360 — карточки, без кольца и таблицы; плашки остаются', () => {
    mobileFlag.value = true;
    const { container } = mountBlock({ costPartial: true });
    expect(findNodeByAttr(container, 'data-testid', 'feature-breakdown-cards'), 'карточек нет').toBeTruthy();
    expect(findNodeByAttr(container, 'data-testid', 'share-ring'), 'кольца на мобиле').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'feature-breakdown-table'), 'таблицы на мобиле').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'breakdown-cost-partial'), 'плашки costPartial').toBeTruthy();
  });

  it('на 360 «деньги» без расхода: доли карточек на месте (мера — время, М1)', () => {
    mobileFlag.value = true;
    const { container } = mountBlock({ breakdown: NO_COST });
    click(findButtonByLabel(headNode(container)!, 'деньги'));
    expect(findNodeByAttr(container, 'data-testid', 'feature-breakdown-cards'), 'карточек нет').toBeTruthy();
    // Доли по времени есть у каждой карточки; сырая мера 'cost' их бы сняла.
    for (const key of ['feature', 'kira', 'timur', 'vera']) {
      expect(findNodeByAttr(container, 'data-testid', `share-pct-${key}`), `доли карточки ${key}`).toBeTruthy();
    }
  });
});
