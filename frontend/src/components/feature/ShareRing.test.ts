// Тесты кольца долей (ST-F9). Рендер на fakeDom: число дуг, доли от суммы
// частей, размер и центр, aria-label. Числа — те, что даёт модель ST-F8
// (FeatureBreakdownModel), пересчитывать их не проверяем.
//
// Покрытие:
//   · <2 групп — компонент null (к1);
//   · число дуг (circle со stroke-dasharray) равно числу групп (к1);
//   · длины дуг в точности заполняют окружность при процентах, которые
//     percentText даёт как 101 % (67/67/66 → 34+34+33) (к1);
//   · размер по умолчанию 140, явный 180 — viewBox 180 (к2);
//   · stroke — только C.* токены, первая (крупнейшая) дуга толще остальных (к2);
//   · центр — formatDuration итога и formatMoney; без расхода — «нет учёта» (к2);
//   · role="img" и aria-label «кому сколько» (к3).
//
// Мутации (проверены прогоном): (1) доли из percentText вместо суммы частей
// (shares[i] = Number(percentText(...).replace(' %',''))/100) — краснеет тест
// «дуги заполняют окружность ровно» (сумма 101 % длины); (2) убрать guard
// `groups.length < 2` — краснеет тест «<2 групп — кольца нет»; (3) `totalCost`
// без ветки «все null» (сумма 0) — краснеет тест «нет учёта».

import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { C } from '../../lib/design';
import { ShareRing } from './ShareRing';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes } from '../chat/__tests__/fakeDom';
import type { FeatureBreakdownGroup } from '../../types/featureStats';

function mkGroup(
  key: string,
  minutes: number,
  tokens: number | null,
  costUsd: number | null,
): FeatureBreakdownGroup {
  return { key, minutes, tokens, costUsd, chatIds: [] };
}

// Три группы: 67/67/66 мин — percentText даёт 34+34+33 = 101 %, а кольцо
// обязано замкнуться ровно. Расход: 7.0 + 5.0 + null → $12,00.
const GROUPS = [
  mkGroup('kira', 67, 1000, 7.0),
  mkGroup('timur', 67, 800, 5.0),
  mkGroup('vera', 66, null, null),
];

function mountRing(groups: FeatureBreakdownGroup[], measure: 'time' | 'tokens' | 'cost', size?: number) {
  return mount(createElement(ShareRing, { groups, measure, size }));
}

// Кольцо — обёртка div[data-testid], внутри неё svg; атрибуты и дуги — на svg.
function ringSvg(container: ReturnType<typeof mount>['container']): FNodeLike | null {
  const wrap = findNodeByAttr(container, 'data-testid', 'share-ring');
  if (!wrap) return null;
  return wrap.children.find(c => c.nodeName === 'SVG') ?? null;
}

type FNodeLike = { children: FNodeLike[]; getAttribute(n: string): string | null };
type FNodeArc = { dash: number; stroke: string; width: number };

// Дуги — все circle в svg, кроме первого (фоновый круг).
function arcs(container: ReturnType<typeof mount>['container']): FNodeArc[] {
  const svg = ringSvg(container);
  if (!svg) return [];
  return svg.children.filter(c => c.nodeName === 'CIRCLE').slice(1).map(c => ({
    dash: Number(c.getAttribute('stroke-dasharray')!.split(' ')[0]),
    stroke: c.getAttribute('stroke'),
    width: Number(c.getAttribute('stroke-width')),
  }));
}

function ringRadius(container: ReturnType<typeof mount>['container']): number {
  const bg = ringSvg(container)!.children.filter(c => c.nodeName === 'CIRCLE')[0];
  return Number(bg.getAttribute('r'));
}

function textOf(node: unknown): string {
  return textNodes(node as never).join('');
}

beforeEach(() => { installFakeDom(); });
afterEach(() => { uninstallFakeDom(); });

describe('кольца нет при <2 групп (к1)', () => {
  it('пустой список — null', () => {
    const { container } = mountRing([], 'time');
    expect(findNodeByAttr(container, 'data-testid', 'share-ring')).toBeNull();
  });

  it('одна группа — null', () => {
    const { container } = mountRing([mkGroup('kira', 60, 100, 1)], 'time');
    expect(findNodeByAttr(container, 'data-testid', 'share-ring')).toBeNull();
  });
});

describe('число дуг и их длины (к1)', () => {
  it('три группы — три дуги (плюс фоновый круг)', () => {
    const { container } = mountRing(GROUPS, 'time');
    expect(arcs(container).length).toBe(3);
  });

  it('дуги заполняют окружность ровно, даже если percentText даёт 101 %', () => {
    const { container } = mountRing(GROUPS, 'time');
    const L = 2 * Math.PI * ringRadius(container);
    const sum = arcs(container).reduce((a, arc) => a + arc.dash, 0);
    // Сумма длин — ровно длина окружности: без зазора и перекрытия. Если бы
    // доли брались из percentText (34+34+33), сумма была бы 1.01·L.
    expect(Math.abs(sum - L)).toBeLessThan(1e-6);
    // Первая дуга — 67/200 окружности, а не «34 %» из текстового процента.
    expect(Math.abs(arcs(container)[0]!.dash - (67 / 200) * L)).toBeLessThan(1e-6);
  });

  it('мера «деньги»: доли по costUsd; без учёта — нулевая дуга, но она в списке', () => {
    const { container } = mountRing(GROUPS, 'cost');
    const L = 2 * Math.PI * ringRadius(container);
    const a = arcs(container);
    expect(a.length).toBe(3);
    expect(Math.abs(a[0]!.dash - (7 / 12) * L)).toBeLessThan(1e-6); // 7.0 — крупнейшая
    expect(Math.abs(a[1]!.dash - (5 / 12) * L)).toBeLessThan(1e-6);
    expect(a[2]!.dash).toBe(0); // costUsd null — нулевая дуга, число дуг не меняется
  });
});

describe('размер и цвета (к2)', () => {
  it('по умолчанию 140; явно 180 — viewBox 180', () => {
    const def = ringSvg(mountRing(GROUPS, 'time').container)!;
    expect(def.getAttribute('width')).toBe('140');
    expect(def.getAttribute('viewBox')).toBe('0 0 140 140');

    const big = ringSvg(mountRing(GROUPS, 'time', 180).container)!;
    expect(big.getAttribute('width')).toBe('180');
    expect(big.getAttribute('viewBox')).toBe('0 0 180 180');
  });

  it('stroke дуг — токены C.*, не hex; фоновый круг — bgSelected', () => {
    const { container } = mountRing(GROUPS, 'time');
    const bg = ringSvg(container)!.children.filter(c => c.nodeName === 'CIRCLE')[0];
    expect(bg.getAttribute('stroke')).toBe(C.bgSelected);
    const a = arcs(container);
    expect(a[0]!.stroke).toBe(C.accent);
    expect(a[1]!.stroke).toBe(C.plan);
    for (const arc of a) expect(arc.stroke).toMatch(/^var\(--c-/);
  });

  it('крупнейшая дуга толще остальных (макет: 26 против 20)', () => {
    const { container } = mountRing(GROUPS, 'time');
    const a = arcs(container);
    expect(a[0]!.width).toBeGreaterThan(a[1]!.width);
    expect(a[1]!.width).toBe(a[2]!.width);
  });
});

describe('центр: итог и деньги (к2)', () => {
  it('суммарное время и сумма денег', () => {
    const { container } = mountRing(GROUPS, 'time');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-total'))).toBe('3 ч 20 мин');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-cost'))).toBe('$12,00');
  });

  it('расхода ни у кого нет — «нет учёта», а не $0,00', () => {
    const noCost = GROUPS.map(g => ({ ...g, costUsd: null }));
    const { container } = mountRing(noCost, 'time');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-cost'))).toBe('нет учёта');
  });
});

describe('доступность (к3)', () => {
  it('role="img" и aria-label «кому сколько»', () => {
    const { container } = mountRing(GROUPS, 'time');
    const svg = ringSvg(container)!;
    expect(svg.getAttribute('role')).toBe('img');
    const label = svg.getAttribute('aria-label');
    expect(label).toContain('Доли по времени');
    // В label — каждая группа и её доля: цвет не единственный носитель смысла.
    expect(label).toContain('timur — 34 %');
    expect(label).toContain('kira — 34 %');
    expect(label).toContain('vera — 33 %');
  });
});
