// Тесты кольца долей (ST-F9, доработка по вердикту Глеба). Рендер на fakeDom:
// число дуг, старт сверху, зазор 3 px, палитра и colorOf, центр по мере, фолбэк
// меры, aria-label с человекочитаемыми именами. Числа — те, что даёт модель ST-F8
// (FeatureBreakdownModel), пересчитывать их не проверяем.
//
// Покрытие:
//   · <2 групп — компонент null (к1);
//   · число дуг (circle со stroke-dasharray) равно числу групп (к1);
//   · «сырые» длины дуг (dash + зазор) точно заполняют окружность при процентах,
//     которые percentText даёт как 101 % (67/67/66 → 34+34+33) (к1);
//   · старт дуг сверху: svg rotate(-90deg), первая дуга offset 0 (к1);
//   · зазор 3 px между дугами; доминантная группа 100 % — одна дуга без зазора;
//     total=0 — нулевые дуги, рендер не пустой (к1);
//   · размер по умолчанию 140, явный 180 — viewBox 180; радиус с запасом по
//     макету (R=70 при 180), толщины 21/15 при 140 (к2);
//   · stroke — палитра accent→info→plan→…; colorOf от вызывающего перебивает
//     палитру; C.smoke/danger/navInk в дугах нет (к2);
//   · центр по мере: время/токены/деньги дают своё число, вторая строка —
//     «<разрез> суммарно · …»; мера без данных — фолбэк на время (к2, к3);
//   · role="img" и aria-label «имя — доля», имена из labelOf, а не сырой ключ
//     (UUID) (к3).
//
//   · нулевая сумма «деньги» при tokens>0 — трек без дуг, «бесплатно» и «все модели
//     локальные»; нулевое время/токены — дуг нет, центр «нет данных» (решение дизайна);
//   · цвет дуги — по порядку ВХОДА групп, а не сортировки по мере (дуга ↔ строка).
//
// Мутации (проверены прогоном): (1) offset всегда 0 — краснеет тест смещений дуг
// («дуги заполняют окружность»); (2) убрать guard `groups.length < 2` — краснеет
// тест «<2 групп — кольца нет»; (3) фолбэк меры убрать (effMeasure = measure) —
// краснеет тест «меры без данных — фолбэк на время»; (4) gap всегда 3 — краснеет
// тест «доминантная 100 % без зазора»; (5) снять Math.max(0, …) в длине дуги —
// краснеет тест «доля короче зазора»; (6) в moneyText вместо tokens: totalTokens
// поставить tokens: 0 — краснеют «бесплатно» (центр и вторая строка); (7) цвет по
// индексу сортировки вместо индекса входа — краснеет тест «цвет не зависит от меры».

import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { C } from '../../lib/design';
import { ShareRing } from './ShareRing';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes } from '../chat/__tests__/fakeDom';
import type { FeatureBreakdownGroup } from '../../types/featureStats';

const GAP = 3;

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

function mountRing(
  groups: FeatureBreakdownGroup[],
  measure: 'time' | 'tokens' | 'cost',
  size?: number,
  extra?: { labelOf?: (k: string) => string; colorOf?: (k: string) => string; axisLabel?: string },
) {
  return mount(createElement(ShareRing, { groups, measure, size, ...extra }));
}

// Кольцо — обёртка div[data-testid], внутри неё svg; атрибуты и дуги — на svg.
type FNodeLike = {
  nodeName: string;
  children: FNodeLike[];
  getAttribute(n: string): string | null;
  style?: Record<string, string>;
};

function ringSvg(container: ReturnType<typeof mount>['container']): FNodeLike | null {
  const wrap = findNodeByAttr(container, 'data-testid', 'share-ring');
  if (!wrap) return null;
  return wrap.children.find(c => c.nodeName === 'SVG') ?? null;
}

type FNodeArc = { dash: number; offset: number; stroke: string | null; width: number };

// Дуги — все circle в svg, кроме фонового круга (его находим по data-testid,
// а не по slice(1): порядок детей не часть контракта).
function arcs(container: ReturnType<typeof mount>['container']): FNodeArc[] {
  const svg = ringSvg(container);
  if (!svg) return [];
  return svg.children
    .filter(c => c.nodeName === 'CIRCLE' && c.getAttribute('data-testid') !== 'share-ring-bg')
    .map(c => ({
      dash: Number(c.getAttribute('stroke-dasharray')!.split(' ')[0]),
      offset: Number(c.getAttribute('stroke-dashoffset')),
      stroke: c.getAttribute('stroke'),
      width: Number(c.getAttribute('stroke-width')),
    }));
}

function bgCircle(container: ReturnType<typeof mount>['container']): FNodeLike | null {
  const svg = ringSvg(container);
  if (!svg) return null;
  return svg.children.find(c => c.getAttribute('data-testid') === 'share-ring-bg') ?? null;
}

function ringRadius(container: ReturnType<typeof mount>['container']): number {
  return Number(bgCircle(container)!.getAttribute('r'));
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
  it('три группы — три дуги (плюс фоновый круг по data-testid)', () => {
    const { container } = mountRing(GROUPS, 'time');
    expect(arcs(container).length).toBe(3);
    expect(bgCircle(container)).not.toBeNull();
  });

  it('дуги заполняют окружность ровно с зазором 3 px, даже если percentText даёт 101 %', () => {
    const { container } = mountRing(GROUPS, 'time');
    const L = 2 * Math.PI * ringRadius(container);
    const a = arcs(container);
    // «Сырая» длина дуги — dash + зазор: сумма сырых — ровно длина окружности.
    // Если бы доли брались из percentText (34+34+33), сумма была бы 1.01·L.
    const raw = a.map(arc => arc.dash + GAP);
    expect(Math.abs(raw.reduce((s, v) => s + v, 0) - L)).toBeLessThan(1e-6);
    // Первая дуга — 67/200 окружности, а не «34 %» из текстового процента.
    expect(Math.abs(raw[0] - (67 / 200) * L)).toBeLessThan(1e-6);
    // Смещения: первая стартует с 0, каждая следующая — с конца предыдущей.
    expect(a[0].offset).toBe(0);
    expect(Math.abs(a[1].offset + (67 / 200) * L)).toBeLessThan(1e-6);
    expect(Math.abs(a[2].offset + (134 / 200) * L)).toBeLessThan(1e-6);
  });

  it('мера «деньги»: доли по costUsd; без учёта — нулевая дуга, но она в списке', () => {
    const { container } = mountRing(GROUPS, 'cost');
    const L = 2 * Math.PI * ringRadius(container);
    const a = arcs(container);
    expect(a.length).toBe(3);
    expect(Math.abs(a[0].dash + GAP - (7 / 12) * L)).toBeLessThan(1e-6); // 7.0 — крупнейшая
    expect(Math.abs(a[1].dash + GAP - (5 / 12) * L)).toBeLessThan(1e-6);
    // Нулевая дуга: max(0, 0 - gap) = 0, число дуг не меняется.
    expect(a[2].dash).toBe(0);
  });

  it('доминантная группа 100 % — одна дуга без зазора, замыкает круг', () => {
    const { container } = mountRing([mkGroup('kira', 100, 0, 0), mkGroup('vera', 0, 0, 0)], 'time');
    const L = 2 * Math.PI * ringRadius(container);
    const a = arcs(container);
    expect(Math.abs(a[0].dash - L)).toBeLessThan(1e-6);
    expect(a[1].dash).toBe(0);
  });

  it('total=0 по деньгам — дуги нулевые, но рендер не пустой (трек виден)', () => {
    const { container } = mountRing([mkGroup('a', 5, 10, 0), mkGroup('b', 5, 10, 0)], 'cost');
    expect(findNodeByAttr(container, 'data-testid', 'share-ring')).not.toBeNull();
    const a = arcs(container);
    expect(a.length).toBe(2);
    for (const arc of a) expect(arc.dash).toBe(0);
  });
});

describe('старт сверху и геометрия (к1, к2)', () => {
  it('svg повёрнут на -90°: дуги стартуют сверху, первая — с offset 0', () => {
    const { container } = mountRing(GROUPS, 'time');
    const svg = ringSvg(container)!;
    expect(svg.style?.transform).toBe('rotate(-90deg)');
    expect(arcs(container)[0].offset).toBe(0);
  });

  it('радиус с запасом по макету (R=70 при 180): толстая дуга не выходит за viewBox', () => {
    const { container } = mountRing(GROUPS, 'time', 180);
    const r = ringRadius(container);
    expect(Math.abs(r - 70)).toBeLessThan(0.01);
    const thick = arcs(container)[0].width;
    expect(r + thick / 2).toBeLessThan(90);
  });

  it('по умолчанию 140; явно 180 — viewBox 180', () => {
    const def = ringSvg(mountRing(GROUPS, 'time').container)!;
    expect(def.getAttribute('width')).toBe('140');
    expect(def.getAttribute('viewBox')).toBe('0 0 140 140');

    const big = ringSvg(mountRing(GROUPS, 'time', 180).container)!;
    expect(big.getAttribute('width')).toBe('180');
    expect(big.getAttribute('viewBox')).toBe('0 0 180 180');
  });

  it('толщины «толще» зафиксированы: 21/15 при 140 (макет: 26 против 20 при 180)', () => {
    const { container } = mountRing(GROUPS, 'time');
    const a = arcs(container);
    expect(a[0].width).toBe(21);
    expect(a[1].width).toBe(15);
    expect(a[2].width).toBe(15);
  });
});

describe('цвета (к2)', () => {
  it('фолбэк — палитра по порядку: accent, info, plan; фоновый круг — bgSelected', () => {
    const { container } = mountRing(GROUPS, 'time');
    expect(bgCircle(container)!.getAttribute('stroke')).toBe(C.bgSelected);
    const a = arcs(container);
    expect(a[0].stroke).toBe(C.accent);
    expect(a[1].stroke).toBe(C.info);
    expect(a[2].stroke).toBe(C.plan);
    for (const arc of a) expect(arc.stroke).toMatch(/^var\(--c-/);
    // Дым, красный и «чернильный» из палитры убраны (контраст 1.8:1).
    for (const arc of a) {
      expect(arc.stroke).not.toBe(C.smoke);
      expect(arc.stroke).not.toBe(C.danger);
      expect(arc.stroke).not.toBe(C.navInk);
    }
  });

  it('colorOf от вызывающего перебивает палитру (цвет персоны)', () => {
    const { container } = mountRing(GROUPS, 'time', undefined, {
      colorOf: key => (key === 'kira' ? C.success : key === 'timur' ? C.warning : C.progressTrack),
    });
    const a = arcs(container);
    expect(a[0].stroke).toBe(C.success);
    expect(a[1].stroke).toBe(C.warning);
    expect(a[2].stroke).toBe(C.progressTrack);
  });

  it('цвет не зависит от меры: порядок входа, а не сортировки (дуга ↔ строка таблицы)', () => {
    // Вход kira(мало), timur(много): по времени первая дуга — timur, но цвет timur
    // остаётся вторым цветом палитры, как и строка таблицы.
    const input = [mkGroup('kira', 10, 5000, 1), mkGroup('timur', 90, 100, 9)];
    const byTime = arcs(mountRing(input, 'time').container);
    const byTokens = arcs(mountRing(input, 'tokens').container);
    expect(byTime[0].stroke).toBe(C.info);   // timur — крупнейшая по времени
    expect(byTokens[0].stroke).toBe(C.accent); // kira — крупнейшая по токенам
    expect(byTime[1].stroke).toBe(C.accent);
    expect(byTokens[1].stroke).toBe(C.info);
  });

  it('больше групп, чем цветов — цикл палитры', () => {
    const many = [
      mkGroup('a', 40, 0, 0), mkGroup('b', 30, 0, 0), mkGroup('c', 20, 0, 0),
      mkGroup('d', 10, 0, 0), mkGroup('e', 9, 0, 0), mkGroup('f', 8, 0, 0),
      mkGroup('g', 7, 0, 0), mkGroup('h', 6, 0, 0),
    ];
    const { container } = mountRing(many, 'time');
    const a = arcs(container);
    expect(a.length).toBe(8);
    expect(a[6].stroke).toBe(C.accent); // 7-я группа — снова accent (цикл)
    expect(a[7].stroke).toBe(C.info);
  });
});

describe('центр по мере (к2, к3)', () => {
  it('мера «время»: крупная строка — время, вторая — деньги', () => {
    const { container } = mountRing(GROUPS, 'time');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-total'))).toBe('3 ч 20 мин');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-cost'))).toBe('$12,00');
  });

  it('axisLabel: вторая строка — «персоны суммарно · …» (макет :298)', () => {
    const { container } = mountRing(GROUPS, 'time', undefined, { axisLabel: 'персоны' });
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-total'))).toBe('3 ч 20 мин');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-cost'))).toBe('персоны суммарно · $12,00');
  });

  it('мера «токены»: крупная строка — токены', () => {
    const { container } = mountRing(GROUPS, 'tokens', undefined, { axisLabel: 'персоны' });
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-total'))).toBe('2 тыс.');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-cost'))).toBe('персоны суммарно · $12,00');
  });

  it('мера «деньги»: крупная строка — сумма, вторая не дублирует деньги', () => {
    const { container } = mountRing(GROUPS, 'cost', undefined, { axisLabel: 'персоны' });
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-total'))).toBe('$12,00');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-cost'))).toBe('персоны суммарно');
  });

  it('расхода ни у кого нет — «нет учёта», а не $0,00', () => {
    const noCost = GROUPS.map(g => ({ ...g, costUsd: null }));
    const { container } = mountRing(noCost, 'time');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-cost'))).toBe('нет учёта');
  });

  it('меры без данных — фолбэк на время: доли и центр по времени, рендер не пустой', () => {
    const noCost = GROUPS.map(g => ({ ...g, costUsd: null }));
    const { container } = mountRing(noCost, 'cost');
    expect(findNodeByAttr(container, 'data-testid', 'share-ring')).not.toBeNull();
    const L = 2 * Math.PI * ringRadius(container);
    const a = arcs(container);
    expect(Math.abs(a[0].dash + GAP - (67 / 200) * L)).toBeLessThan(1e-6); // доли по минутам
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-total'))).toBe('3 ч 20 мин');
    const svg = ringSvg(container)!;
    expect(svg.getAttribute('aria-label')).toContain('Доли по времени');
  });
});

describe('«бесплатно» и фолбэк только при null (доработка 2)', () => {
  const FREE = [mkGroup('kira', 60, 1000, 0), mkGroup('timur', 40, 500, 0)];

  it('все costUsd=0 при tokens>0 — центр «бесплатно», а не «$0,00»', () => {
    const { container } = mountRing(FREE, 'cost');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-total'))).toBe('бесплатно');
  });

  it('мера «время» при бесплатных группах: вторая строка — «бесплатно»', () => {
    const { container } = mountRing(FREE, 'time');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-cost'))).toBe('бесплатно');
  });

  it('нулевая сумма «деньги» — мера остаётся выбранной: aria-label по деньгам, дуги нулевые', () => {
    const { container } = mountRing(FREE, 'cost');
    expect(ringSvg(container)!.getAttribute('aria-label')).toContain('Доли по деньгам');
    for (const arc of arcs(container)) expect(arc.dash).toBe(0);
  });

  it('нулевая сумма «деньги»: вторая строка — «все модели локальные», дуг с длиной нет', () => {
    const { container } = mountRing(FREE, 'cost', undefined, { axisLabel: 'персоны' });
    expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-cost'))).toBe('все модели локальные');
    expect(findNodeByAttr(container, 'data-testid', 'share-ring-bg')).not.toBeNull();
    for (const arc of arcs(container)) expect(arc.dash).toBe(0);
  });

  it('нулевое время или токены — дуг нет, центр «нет данных»', () => {
    const zero = [mkGroup('a', 0, 0, 0), mkGroup('b', 0, 0, 0)];
    for (const measure of ['time', 'tokens'] as const) {
      const { container } = mountRing(zero, measure);
      expect(arcs(container).length).toBe(0);
      expect(textOf(findNodeByAttr(container, 'data-testid', 'share-ring-total'))).toBe('нет данных');
    }
  });

  it('null у всех — фолбэк на время (aria-label и дуги по минутам)', () => {
    const none = FREE.map(g => ({ ...g, costUsd: null }));
    const { container } = mountRing(none, 'cost');
    expect(ringSvg(container)!.getAttribute('aria-label')).toContain('Доли по времени');
    const L = 2 * Math.PI * ringRadius(container);
    expect(Math.abs(arcs(container)[0].dash + GAP - 0.6 * L)).toBeLessThan(1e-6);
  });
});

describe('геометрия: доля короче зазора и равные группы (хвост 3)', () => {
  it('доля короче зазора — длина дуги 0, а не отрицательная', () => {
    // 1/10001 окружности при r≈54 заметно короче 3 px.
    const { container } = mountRing([mkGroup('a', 10000, 0, 0), mkGroup('b', 1, 0, 0)], 'time');
    const a = arcs(container);
    expect(a[1].dash).toBe(0);
    expect(a[0].dash).toBeGreaterThan(0);
  });

  it('две равные группы: каждая дуга — L/2 − зазор, смещения 0 и −L/2', () => {
    const { container } = mountRing([mkGroup('a', 50, 0, 0), mkGroup('b', 50, 0, 0)], 'time');
    const L = 2 * Math.PI * ringRadius(container);
    const a = arcs(container);
    expect(Math.abs(a[0].dash - (L / 2 - GAP))).toBeLessThan(1e-6);
    expect(Math.abs(a[1].dash - (L / 2 - GAP))).toBeLessThan(1e-6);
    expect(a[0].offset).toBe(0);
    expect(Math.abs(a[1].offset + L / 2)).toBeLessThan(1e-6);
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

  it('labelOf: в aria-label имя группы, а не сырой ключ (UUID)', () => {
    const uuidKey = '7f3a9c2e-1234-4a5b-8c9d-0e1f2a3b4c5d';
    const groups = [mkGroup(uuidKey, 67, 100, 7.0), mkGroup('timur', 67, 800, 5.0)];
    const { container } = mountRing(groups, 'time', undefined, {
      labelOf: key => (key === uuidKey ? 'Кира' : 'Тимур'),
    });
    const label = ringSvg(container)!.getAttribute('aria-label');
    // Две равные группы — 50 % каждая.
    expect(label).toContain('Кира — 50 %');
    expect(label).toContain('Тимур — 50 %');
    expect(label).not.toContain('7f3a9c2e');
  });
});
