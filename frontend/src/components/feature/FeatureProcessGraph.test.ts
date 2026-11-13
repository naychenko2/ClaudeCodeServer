// Граф процесса фичи (C3a, доработка по вердикту R-C3a, контракт a5d0c76e / шаг G7).
// Рендер-тесты на fakeDom (десктоп: matchMedia у fakeDom даёт matches:false).
//
// Покрытие:
// - данные пресета «Параллельный цикл» (после G7 у qa-этапов есть Title с бэка): оба qa-узла
//   подписаны по title и различимы — «Проверка плана» / «Проверка», aria-label тоже различны
//   (пункт Major вердикта: раньше оба назывались «Проверка»);
// - без title (легаси и этап без имени) — как раньше: имя роли (stageName) + линия (laneWord)
//   для параллельных этапов одной роли; без линии — просто имя роли;
// - длинное имя этапа (как в шагах воспроизведения дефекта) ограничено рамкой узла:
//   multiline-clamp на 2 строки; полное имя остаётся в title и aria-label.
// Колонки, рёбра и состояния компонент не пересчитывает — это покрыто в FeatureProcessGraphModel.test.ts.
//
// Мутации (проверяются прогоном): (1) убрать `if (stage.title)` в nodeTitle — краснеет
// «данные пресета…»; (2) убрать линию в фолбэке — краснеет «без title…»; (3) убрать clamp
// подписи (WebkitLineClamp/overflow) — краснеет «подпись узла ограничена рамкой».

import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps, textNodes } from '../chat/__tests__/fakeDom';
import { FeatureProcessGraph } from './FeatureProcessGraph';
import type { FeatureRun, FeatureStage, StageKey, StageState } from '../../types/featureRun';

function mkRun(stages: FeatureStage[]): FeatureRun {
  return {
    featureId: 'f-1',
    state: 'running',
    steps: [],
    reviews: [],
    qa: [],
    final: null,
    merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0,
    ready: false,
    stages,
  };
}

// Стадия графа: id/role/lane/column/after/title — как их отдаёт бэк после G7
// (FeatureRun.cs:279-296; Title — из RouteStage, у легаси и этапа без имени — null).
function mkStage(
  key: StageKey, id: string, role: string, column: number, state: StageState,
  after: string[] = [], lane?: string, title?: string | null,
): FeatureStage {
  return {
    key, id, role, lane: lane ?? null, title: title ?? null, column, state, after,
    passes: 0, taskIds: [], rounds: [], firstStartedAt: null, lastCompletedAt: null,
    barEnd: null, approxMinutes: 0, overlapsActive: false, executors: [], runningSince: null,
  };
}

// Пресет «Параллельный цикл» (WorkflowTemplateStore.cs:123-137) в проекции на данные run:
// 10 этапов, колонки 0..5. withTitles — как приходят с бэка после G7: имена обязательны и
// различны у qa-plan/qa (общая пара role=qa, линии нет); остальным этапам имени не нужно.
function parallelCycle(withTitles: boolean): FeatureStage[] {
  const t = (s: string | null) => (withTitles ? s : null);
  return [
    mkStage('cut', 'cut', 'cut', 0, 'done'),
    mkStage('design', 'design', 'design', 1, 'done', ['cut']),
    mkStage('plan', 'plan', 'plan', 2, 'done', ['design']),
    mkStage('qa', 'qa-plan', 'qa', 2, 'pending', ['design'], undefined, t('Проверка плана')),
    mkStage('prod', 'step-back', 'step', 3, 'active', ['plan'], 'back'),
    mkStage('prod', 'step-front', 'step', 3, 'pending', ['plan'], 'front'),
    mkStage('qa', 'qa', 'qa', 3, 'pending', ['review', 'qa-plan']),
    mkStage('review', 'review', 'review', 4, 'pending', ['step-back', 'step-front']),
    mkStage('final', 'final', 'final', 4, 'pending', ['qa']),
    mkStage('merge', 'merge', 'merge', 5, 'pending', ['final']),
  ];
}

// Подпись и aria-label карточки узла по id стадии.
function nodeLabel(container: unknown, id: string): { text: string; aria: string } {
  const n = findNodeByAttr(container as never, 'data-testid', `pgraph-node-${id}`);
  expect(n, `узел pgraph-node-${id}`).not.toBeNull();
  const title = findNodeByAttr(container as never, 'data-testid', `pgraph-title-${id}`);
  return {
    text: textNodes(title as never).join(''),
    aria: n!.getAttribute('aria-label') ?? '',
  };
}

beforeEach(() => { installFakeDom(); });
afterEach(() => { uninstallFakeDom(); });

function render(stages: FeatureStage[]): unknown {
  const { container } = mount(createElement(FeatureProcessGraph, { run: mkRun(stages) }));
  return container;
}

describe('FeatureProcessGraph: подпись узла — имя этапа с бэка (доработка C3a)', () => {
  it('данные пресета: оба qa-узла названы по title и различимы подписью и aria-label', () => {
    const c = render(parallelCycle(true));
    const qaPlan = nodeLabel(c, 'qa-plan');
    const qa = nodeLabel(c, 'qa');
    expect(qaPlan.text).toBe('Проверка плана');
    expect(qa.text).toBe('Проверка');
    // Пункт Major вердикта R-C3a: раньше оба назывались «Проверка» (stageName по общему
    // ключу qa) — теперь подписи и aria-label различны.
    expect(qaPlan.text).not.toBe(qa.text);
    expect(qaPlan.aria).toBe('Проверка плана: впереди');
    expect(qa.aria).toBe('Проверка: впереди');
    expect(qaPlan.aria).not.toBe(qa.aria);
  });

  it('без title (легаси и этап без имени) — как раньше: имя роли плюс линия', () => {
    const c = render(parallelCycle(false));
    // Параллельные этапы одной роли step различаются линией (тот же фолбэк, что до доработки).
    expect(nodeLabel(c, 'step-back').text).toBe('Производство · бэк');
    expect(nodeLabel(c, 'step-front').text).toBe('Производство · фронт');
    // Этап без линии и без имени — просто имя роли; два qa здесь снова одинаковы —
    // это легаси-ответ, различимость даёт бэк (G7), фронт своих правил не придумывает.
    expect(nodeLabel(c, 'qa').text).toBe('Проверка');
    expect(nodeLabel(c, 'qa-plan').text).toBe('Проверка');
    expect(nodeLabel(c, 'step-back').aria).toBe('Производство · бэк: идёт');
  });

  it('title есть — берётся как есть, без линии и без склонений (бэк уже различил группу)', () => {
    // Имя с бэка не дополняется линией, даже когда lane пришла: title — итоговая подпись.
    const stages = parallelCycle(true).map(s => (s.id === 'qa-plan' ? { ...s, lane: 'test' } : s));
    expect(nodeLabel(render(stages), 'qa-plan').text).toBe('Проверка плана');
  });
});

describe('FeatureProcessGraph: длинное имя этапа переносится без обрезки', () => {
  const LONG_TITLE = 'Проверка плана и макетов перед работами';

  const longQaNode = () => render([mkStage('qa', 'qa-long', 'qa', 0, 'pending', [], undefined, LONG_TITLE)]);
  const titleSpan = (c: unknown) => findNodeByAttr(c as never, 'data-testid', 'pgraph-title-qa-long')!;

  it('подпись без clamp и overflow hidden, сжимается по ширине узла', () => {
    const style = (reactProps(titleSpan(longQaNode()) as never) as { style: Record<string, unknown> }).style;
    expect(style.WebkitLineClamp).toBeUndefined();
    expect(style.overflow).toBeUndefined();
    expect(style.minWidth).toBe(0);
    expect(textNodes(titleSpan(longQaNode()) as never).join('')).toBe(LONG_TITLE);
  });

  it('полное имя доступно: aria-label и title', () => {
    const c = longQaNode();
    expect(findNodeByAttr(c as never, 'data-testid', 'pgraph-node-qa-long')!.getAttribute('aria-label')).toBe(`${LONG_TITLE}: впереди`);
    expect(titleSpan(c).getAttribute('title')).toBe(LONG_TITLE);
  });
});

describe('FeatureProcessGraph: данные узла B01 (F06)', () => {
  const get = (c: unknown, tid: string) => findNodeByAttr(c as never, 'data-testid', tid);
  const txt = (n: unknown) => textNodes(n as never).join('');
  const one = (extra: Partial<FeatureStage>) =>
    render([{ ...mkStage('review', 'rv', 'review', 0, 'active', [], 'back'), ...extra }]);

  it('вторая строка: «Персона · время»', () => {
    const c = one({ personaNames: ['Полина'], elapsedMinutes: 70 });
    expect(txt(get(c, 'pgraph-meta-rv'))).toBe('Полина · 1 ч 10 мин');
  });

  it('ждёт человека — warning-цвет; ждёт ветки — текст про ветки и не warning', () => {
    const human = get(one({ waiting: 'human' }), 'pgraph-meta-rv')!;
    expect(txt(human)).toBe('ждёт ответа вас');
    expect((reactProps(human as never) as { style: { color: string } }).style.color).toBe('var(--c-warning)');
    const br = get(one({ waiting: 'branches' }), 'pgraph-meta-rv')!;
    expect(txt(br)).toBe('ждёт обе ветки');
    expect((reactProps(br as never) as { style: { color: string } }).style.color).not.toBe('var(--c-warning)');
  });

  it('метка роли и чип линии — всегда; без данных меты строки нет', () => {
    const c = one({ line: 'front' });
    expect(txt(get(c, 'pgraph-role-rv'))).toBe('review');
    expect(txt(get(c, 'pgraph-line-rv'))).toBe('фронт');
    expect(get(c, 'pgraph-meta-rv')).toBeNull();
    expect(get(c, 'pgraph-returns-rv')).toBeNull();
    expect(get(c, 'pgraph-segments')).toBeNull();
  });

  it('бейдж ↺N при returns>0', () => {
    expect(txt(get(one({ returns: 2 }), 'pgraph-returns-rv'))).toBe('↺2');
  });

  it('полоска задач: сегменты и «2/3»', () => {
    const c = one({ taskSegments: { done: 1, running: 1, total: 3 } });
    expect(txt(get(c, 'pgraph-segcount-rv'))).toBe('1/3');
    const bar = get(c, 'pgraph-segments') as unknown as { children: { getAttribute(n: string): string | null }[] };
    expect(bar.children.map((x) => x.getAttribute('data-kind'))).toEqual(['done', 'run', 'todo']);
  });
});
