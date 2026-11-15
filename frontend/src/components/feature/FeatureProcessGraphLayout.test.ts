// Раскладка графа страницы (F07, макет feature-page.html P14, P23–P25, P27, P29–P31).
// Покрытие: подпись секции и шаблона, рамка «⇉ параллельно» (десктоп) / скобка (мобила),
// дальнее ребро — обходом под графом пунктиром, выбранный узел — рамка accent, легенда,
// автопрокрутка ленты к выбранному на мобиле.
//
// Мутации (проверяются прогоном): (1) в isFarEdge поменять `> 1` на `> 5` — краснеет «дальнее
// ребро…»; (2) убрать strokeDasharray у far — краснеет то же; (3) в graphCaption убрать ветку
// proposal — краснеет «подпись шаблона»; (4) убрать scrollIntoView-эффект — краснеет «мобила»;
// (5) сделать условие края несбыточным (`over > 1` → `over > 1e5`) — краснеют «контейнер 768…»
// и «мобильная лента 328…»; (6) убрать перемер после кадра (useEffect без deps) — краснеют те же.

import { createElement, act } from 'react';
import { type Root } from 'react-dom/client';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps, textNodes } from '../chat/__tests__/fakeDom';
import { graphColumns, graphEdges, columnRanks, isFarEdge, graphCaption } from './FeatureProcessGraphModel';
import type { FeatureRun, FeatureStage, StageKey, StageState } from '../../types/featureRun';

const mobileFlag = vi.hoisted(() => ({ value: false }));
vi.mock('../../lib/breakpoints', async (orig) => ({
  ...(await orig<typeof import('../../lib/breakpoints')>()),
  useIsMobile: () => mobileFlag.value,
}));

import { FeatureProcessGraph } from './FeatureProcessGraph';

function mkStage(key: StageKey, id: string, role: string, column: number, state: StageState, after: string[] = []): FeatureStage {
  return {
    key, id, role, lane: null, title: null, column, state, after,
    passes: 0, taskIds: [], rounds: [], firstStartedAt: null, lastCompletedAt: null,
    barEnd: null, approxMinutes: 0, overlapsActive: false, executors: [], runningSince: null,
  };
}

function mkRun(stages: FeatureStage[], epic?: Record<string, unknown>): FeatureRun {
  return {
    featureId: 'f-1', state: 'running', steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false, stages, ...(epic ? { epic } : {}),
  } as unknown as FeatureRun;
}

// cut(0) → design(1) → [qa-plan, plan](2, параллельная колонка) → qa(3): qa-plan → qa через колонку.
function stages(): FeatureStage[] {
  return [
    mkStage('cut', 'cut', 'cut', 0, 'done'),
    mkStage('design', 'design', 'design', 1, 'done', ['cut']),
    mkStage('qa', 'qa-plan', 'qa', 2, 'active', ['design']),
    mkStage('plan', 'plan', 'plan', 2, 'pending', ['design']),
    mkStage('qa', 'qa', 'qa', 4, 'pending', ['qa-plan', 'plan']),
  ];
}

beforeEach(() => { installFakeDom(); mobileFlag.value = false; });
afterEach(() => { uninstallFakeDom(); });

const byId = (c: unknown, id: string) => findNodeByAttr(c as never, 'data-testid', id);
const styleOf = (n: unknown) => (reactProps(n as never) as { style: Record<string, unknown> }).style;

describe('модель: дальние рёбра и подпись шаблона', () => {
  it('дальнее ребро — через ≥1 колонку по рангам колонок', () => {
    const run = mkRun(stages());
    const ranks = columnRanks(graphColumns(run));
    // column у qa = 4, но рангов всего четыре (0,1,2,4 → 0..3): qa-plan(2)→qa(3) соседние, design→… тоже.
    expect(ranks.get('qa')).toBe(3);
    const far = graphEdges(run).filter(e => isFarEdge(ranks, e));
    expect(far).toEqual([]);
    expect(isFarEdge(ranks, { from: 'cut', to: 'plan' })).toBe(true);
  });

  it('подпись шаблона: подтверждён / предложение / фиксированный список', () => {
    const base = stages();
    expect(graphCaption(mkRun(base, { workflow: { templateId: 'cycle', confirmedVersion: 2 } }), 'Параллельный цикл')).toBe('Параллельный цикл v2');
    expect(graphCaption(mkRun(base, { workflow: { templateId: 'cycle', confirmedVersion: 2 } }))).toBe('cycle v2');
    expect(graphCaption(mkRun(base, { workflowProposal: { templateId: 'cycle', state: 'proposed' } }))).toBe('предложение · ещё не подтверждено');
    expect(graphCaption(mkRun(base, { workflowProposal: { state: 'none' } }))).toBe('фиксированный список');
    expect(graphCaption(mkRun(base))).toBe('фиксированный список');
  });
});

describe('FeatureProcessGraph: раскладка (F07)', () => {
  const run = () => mkRun([
    mkStage('cut', 'cut', 'cut', 0, 'done'),
    mkStage('design', 'design', 'design', 1, 'done', ['cut']),
    mkStage('plan', 'a', 'plan', 2, 'active', ['design']),
    mkStage('plan', 'b', 'plan', 2, 'pending', ['design']),
    mkStage('final', 'final', 'final', 3, 'pending', ['a', 'b']),
    mkStage('merge', 'merge', 'merge', 4, 'pending', ['design', 'final']),
  ], { workflow: { templateId: 'cycle', confirmedVersion: 1 } });

  it('заголовок «Процесс», подпись шаблона и легенда', () => {
    const { container } = mount(createElement(FeatureProcessGraph, { run: run(), templateName: 'Параллельный цикл' }));
    expect(textNodes(byId(container, 'feature-process-section') as never).join('|')).toContain('Процесс');
    expect(textNodes(byId(container, 'pgraph-caption') as never).join('')).toBe('Параллельный цикл v1');
    const legend = textNodes(byId(container, 'pgraph-legend') as never).join(' ');
    for (const w of ['готово', 'в работе', 'ждёт человека', '↺', 'параллельно']) expect(legend).toContain(w);
  });

  it('десктоп: параллельная колонка — рамка с подписью «⇉ параллельно»', () => {
    const { container } = mount(createElement(FeatureProcessGraph, { run: run() }));
    const group = byId(container, 'pgraph-group-0')!;
    expect(group.getAttribute('data-kind')).toBe('frame');
    expect(textNodes(group as never).join('')).toBe('⇉ параллельно');
    expect(byId(container, 'pgraph-group-1')).toBeNull(); // одиночные колонки без рамки
  });

  it('дальнее ребро (design → merge через колонки) — пунктиром, обычное — сплошным', () => {
    const { container } = mount(createElement(FeatureProcessGraph, { run: run() }));
    const far = byId(container, 'pgraph-edge-design-merge')!;
    expect(far.getAttribute('data-far')).toBe('true');
    expect(far.getAttribute('stroke-dasharray')).toBe('5 4');
    const near = byId(container, 'pgraph-edge-cut-design')!;
    expect(near.getAttribute('data-far')).toBeNull();
    expect(near.getAttribute('stroke-dasharray')).toBeNull();
  });

  it('выбранный узел — рамка accent и кольцо фокуса', () => {
    const { container } = mount(createElement(FeatureProcessGraph, { run: run(), selectedId: 'a' }));
    const s = styleOf(byId(container, 'pgraph-node-a'));
    expect(String(s.border)).toContain('var(--c-accent)');
    expect(s.boxShadow).toBe('var(--shadow-focus)');
    expect(styleOf(byId(container, 'pgraph-node-b')).boxShadow).toBeUndefined();
  });

  it('мобила: скобка вместо рамки, подпись та же, выбранный узел прокручивается в центр ленты', () => {
    mobileFlag.value = true;
    const { root, container } = mount(createElement(FeatureProcessGraph, { run: run(), selectedId: 'cut' }));
    const group = byId(container, 'pgraph-group-0')!;
    expect(group.getAttribute('data-kind')).toBe('bracket');
    expect(textNodes(group as never).join('')).toBe('⇉ параллельно');
    const calls: unknown[] = [];
    (byId(container, 'pgraph-node-b') as unknown as { scrollIntoView: (o: unknown) => void }).scrollIntoView = (o) => { calls.push(o); };
    act(() => { root.render(createElement(FeatureProcessGraph, { run: run(), selectedId: 'b' })); });
    expect(calls).toEqual([{ inline: 'center', block: 'nearest', behavior: 'smooth' }]);
    expect(styleOf(byId(container, 'pgraph-node-b')).width).toBe(104);
  });

  it('пресет «Параллельный цикл»: подпись «⇉ параллельно» — по одной на параллельную колонку, не в легенде', () => {
    const preset = mkRun([
      mkStage('cut', 'cut', 'cut', 0, 'done'),
      mkStage('design', 'design', 'design', 1, 'done', ['cut']),
      mkStage('plan', 'plan', 'plan', 2, 'done', ['design']),
      mkStage('qa', 'qa-plan', 'qa', 2, 'pending', ['design']),
      mkStage('prod', 'step-back', 'step', 3, 'active', ['plan']),
      mkStage('prod', 'step-front', 'step', 3, 'pending', ['plan']),
      mkStage('review', 'review', 'review', 4, 'pending', ['step-back', 'step-front']),
      mkStage('final', 'final', 'final', 5, 'pending', ['review', 'qa-plan']),
      mkStage('merge', 'merge', 'merge', 6, 'pending', ['final']),
    ]);
    const { container } = mount(createElement(FeatureProcessGraph, { run: preset }));
    const all = textNodes(container as never).filter(t => t === '⇉ параллельно');
    expect(all).toHaveLength(2); // колонки 2 и 3
    expect(byId(container, 'pgraph-edge-qa-plan-final')!.getAttribute('stroke-dasharray')).toBe('5 4');
  });

  it('прокрутка по X — только у ленты графа; корень секции и заголовок не прокручиваются', () => {
    for (const mobile of [false, true]) {
      mobileFlag.value = mobile;
      const { container } = mount(createElement(FeatureProcessGraph, { run: run() }));
      expect(styleOf(byId(container, 'feature-process-graph')).overflowX).toBe('auto');
      expect(styleOf(byId(container, 'feature-process-section')).overflowX).toBeUndefined();
      expect(styleOf(byId(container, 'pgraph-legend')).overflowX).toBeUndefined();
    }
  });
});

// Прокрутка ленты шире контейнера была молча: на 360 граф обрезалось по слову («Провер|ка
// плана»), и ничего не говорило о том, что справа ещё узлы (дефект 1f6df093). Замер в
// Chromium (1280/932/800/360, обе темы) дал границы: при контейнере 1248 и 900 лента
// растягивается ровно под контейнер (1248 и 900) и за край не выходит; при 768 — 828
// (min-ширина узлов 116), при 328 на мобильной ленте — 756. Ниже — те же контейнеры.
describe('FeatureProcessGraph: прокрутка ленты обозначена краем (дефект 1f6df093)', () => {
  // Данные пресета «Параллельный цикл» (10 этапов, 6 колонок; те же, что в
  // FeatureProcessGraph.test.ts:72-86): имена — только у пары qa, как их отдаёт бэк.
  const preset = () => mkRun([
    mkStage('cut', 'cut', 'cut', 0, 'done'),
    mkStage('design', 'design', 'design', 1, 'done', ['cut']),
    mkStage('plan', 'plan', 'plan', 2, 'done', ['design']),
    { ...mkStage('qa', 'qa-plan', 'qa', 2, 'pending', ['design']), title: 'Проверка плана' },
    { ...mkStage('prod', 'step-back', 'step', 3, 'active', ['plan']), lane: 'back' },
    { ...mkStage('prod', 'step-front', 'step', 3, 'pending', ['plan']), lane: 'front' },
    { ...mkStage('qa', 'qa', 'qa', 3, 'pending', ['review', 'qa-plan']), title: 'Проверка' },
    mkStage('review', 'review', 'review', 4, 'pending', ['step-back', 'step-front']),
    mkStage('final', 'final', 'final', 4, 'pending', ['qa']),
    mkStage('merge', 'merge', 'merge', 5, 'pending', ['final']),
  ]);

  // Контейнер и положение ленты в нём — то, что компонент читает с DOM (clientWidth,
  // scrollWidth, scrollLeft). Фейк-DOM раскладки не считает: тест ставит числа на узле
  // скроллера и перерисовывает — компонент перемеряет их после кадра (measure без deps);
  // из узлов теста нужны только дети и родитель.
  type El = { children: El[]; parentNode: El | null };
  const setBox = (el: unknown, box: { clientWidth: number; scrollWidth: number; scrollLeft?: number }) =>
    Object.assign(el as Record<string, unknown>, { scrollLeft: 0 }, box);
  const relayout = (root: Root, r: FeatureRun) => act(() => { root.render(createElement(FeatureProcessGraph, { run: r })); });
  const contentWidth = (container: unknown) => Number(styleOf((byId(container, 'feature-process-graph') as never as El).children[0]).width);

  const mountPreset = () => {
    const { root, container } = mount(createElement(FeatureProcessGraph, { run: preset() }));
    return { root, container, sc: byId(container, 'feature-process-graph') as never as El };
  };

  it('контейнер 1248 (окно 1280): лента растягивается под контейнер, за край не выходит, признака нет', () => {
    const { root, container, sc } = mountPreset();
    setBox(sc, { clientWidth: 1248, scrollWidth: 1248 });
    relayout(root, preset());
    expect(contentWidth(container)).toBe(1248);
    expect(byId(container, 'pgraph-scroll-right')).toBeNull();
    expect(byId(container, 'pgraph-scroll-left')).toBeNull();
  });

  it('контейнер 768 (окно 800): лента 828 шире контейнера — есть правый край-подсказка, прокрутка остаётся внутри блока', () => {
    const { root, container, sc } = mountPreset();
    setBox(sc, { clientWidth: 768, scrollWidth: 828 });
    relayout(root, preset());
    expect(contentWidth(container)).toBe(828);
    expect(styleOf(sc).overflowX).toBe('auto');
    const hint = byId(container, 'pgraph-scroll-right');
    expect(hint).not.toBeNull();
    // Край-подсказка — вне скроллера: внутри она уехала бы вместе с лентой, а не осталась на краю.
    expect((hint as never as El).parentNode).toBe(byId(container, 'pgraph-scroll'));
    expect(byId(container, 'pgraph-scroll-left')).toBeNull();
  });

  it('мобильная лента 328: прокручивается 756 — правый край есть; доехал до конца — правый убран, появился левый', () => {
    mobileFlag.value = true;
    const { root, container, sc } = mountPreset();
    setBox(sc, { clientWidth: 328, scrollWidth: 756 });
    relayout(root, preset());
    expect(contentWidth(container)).toBe(756);
    expect(byId(container, 'pgraph-scroll-right')).not.toBeNull();
    expect(byId(container, 'pgraph-scroll-left')).toBeNull();

    setBox(sc, { clientWidth: 328, scrollWidth: 756, scrollLeft: 428 });
    relayout(root, preset());
    expect(byId(container, 'pgraph-scroll-right')).toBeNull();
    expect(byId(container, 'pgraph-scroll-left')).not.toBeNull();

    // Вернулся к началу — снова только правый.
    setBox(sc, { clientWidth: 328, scrollWidth: 756, scrollLeft: 0 });
    relayout(root, preset());
    expect(byId(container, 'pgraph-scroll-right')).not.toBeNull();
    expect(byId(container, 'pgraph-scroll-left')).toBeNull();
  });

  it('без замера контейнера (первый кадр, тест без раскладки) признака прокрутки нет', () => {
    const { container } = mount(createElement(FeatureProcessGraph, { run: preset() }));
    expect(byId(container, 'pgraph-scroll-right')).toBeNull();
    expect(byId(container, 'pgraph-scroll-left')).toBeNull();
  });
});
