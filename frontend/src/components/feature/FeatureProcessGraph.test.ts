// Граф процесса фичи (C3a, доработка по вердикту R-C3a, контракт a5d0c76e / шаг G7).
// Рендер-тесты на fakeDom (десктоп: matchMedia у fakeDom даёт matches:false).
//
// Покрытие:
// - данные пресета «Параллельный цикл» (после G7 у qa-этапов есть Title с бэка): оба qa-узла
//   подписаны по title и различимы — «Проверка плана» / «Проверка», aria-label тоже различны
//   (пункт Major вердикта: раньше оба назывались «Проверка»);
// - без title (легаси и этап без имени) — как раньше: имя роли (stageName) + линия (laneWord)
//   для параллельных этапов одной роли; без линии — просто имя роли;
// - длинное имя этапа ограничено рамкой узла: multiline-clamp на 3 строки; полное имя остаётся в title и aria-label;
// - перенос имени — по слогам со знаком (hyphens+break-word), а не по букве ('anywhere', дефект 20e96078);
// - узел «предложено» по макету (F33a): прозрачный фон (.node.prop), рамка dashed c-plan
//   и заголовок plan-text; обычный узел — фон bgWhite (контроль в том же тесте);
//   пометка «предложено» в строке меты (при пустой мете — на её месте, при занятой —
//   рядом через « · »), один раз и не в нижней строке; proposed=false (бэк шлёт false
//   у подтверждённого маршрута) — стиль прежний; строка легенды только при proposed-узле.
// - доработка R-F33a: «предложено» и фиолетовый пунктир — только незавершённым стадиям;
//   завершённый узел предложенного маршрута — обычный done (рамка и заголовок как у done
//   без признака, пометки нет), в мете суффикс «до маршрута» (с исполнителем — после « · »,
//   без него — без висящего разделителя); незавершённый proposed в том же графе — как раньше.
// - доработка R-F33a·дельта (дефект 7c75b702): та же формула доведена до рёбер и легенды —
//   ребро к завершённому proposed-узлу обычное (не фиолетовое и не пунктирное), строка
//   легенды «предложено» только при незавершённом proposed-узле; незавершённый
//   proposed-конец — фиолетовый пунктир и легенда на месте (макет :374).
// - признак прокрутки по факту прокрутки (дефект 1f6df093): тест прокручивает контейнер
//   программно — задаёт элементы реальные метрики прокрутки и поднимает слушателя 'scroll'
//   на контейнере (путь браузера, не имитация через ре-рендер); шевроны следуют scrollLeft:
//   в начале ленты только правый, в конце только левый, в середине оба, прокрутка обратно — наоборот.
// Колонки, рёбра и состояния компонент не пересчитывает — это покрыто в FeatureProcessGraphModel.test.ts.
//
// Мутации (проверяются прогоном): (1) убрать `if (stage.title)` в nodeTitle — краснеет
// «данные пресета…»; (2) убрать линию в фолбэке — краснеет «без title…»; (3) убрать clamp
// подписи (WebkitLineClamp/overflow) — краснеет «подпись узла ограничена рамкой»;
// (4) игнорировать признак proposed (nodeMeta и рендер узла) — краснеют блоки F33a;
// (5) вернуть оформление proposed без учёта state (asProposed → stage.proposed в nodeMeta
// и в рендере узла) — краснеет блок R-F33a.
// (6) вернуть сырой proposed в цвете ребра и в условии легенды (без state) — краснеет
//     тест рёбер и легенды (R-F33a·дельта).
// (7) вернуть `overflowWrap: 'anywhere'` без `hyphens` в стиле подписи — краснеет
//     «перенос имени — по слогам со знаком».
// (8) вернуть `C.planLight` в фоне proposed-узла (доработка F33·фон по макету) — краснеет
//     «proposed=true: фон прозрачный…».
// (9) убрать подписку `el.addEventListener('scroll', measure)` в эффекте — краснеет
//     «прокрутка вправо — правый шеврон исчезает…» (слушателя на контейнере нет).

import { act, createElement } from 'react';
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

describe('FeatureProcessGraph: длинное имя этапа не выходит за рамку узла', () => {
  const LONG_TITLE = 'Проверка плана и макетов перед работами';

  const longQaNode = () => render([mkStage('qa', 'qa-long', 'qa', 0, 'pending', [], undefined, LONG_TITLE)]);
  const titleSpan = (c: unknown) => findNodeByAttr(c as never, 'data-testid', 'pgraph-title-qa-long')!;

  it('подпись ограничена тремя строками с многоточием и сжимается по ширине узла', () => {
    const style = (reactProps(titleSpan(longQaNode()) as never) as { style: Record<string, unknown> }).style;
    expect(style.WebkitLineClamp).toBe(3);
    expect(style.overflow).toBe('hidden');
    expect(style.display).toBe('-webkit-box');
    expect(style.minWidth).toBe(0);
    expect(textNodes(titleSpan(longQaNode()) as never).join('')).toBe(LONG_TITLE);
  });

  it('короткое имя — тот же стиль, текст не меняется', () => {
    const c = render([mkStage('qa', 'qa-long', 'qa', 0, 'pending', [], undefined, 'Проверка')]);
    expect(textNodes(titleSpan(c) as never).join('')).toBe('Проверка');
  });

  it('полное имя доступно: aria-label и title', () => {
    const c = longQaNode();
    expect(findNodeByAttr(c as never, 'data-testid', 'pgraph-node-qa-long')!.getAttribute('aria-label')).toBe(`${LONG_TITLE}: впереди`);
    expect(titleSpan(c).getAttribute('title')).toBe(LONG_TITLE);
  });

  it('перенос имени — по слогам со знаком, а не по букве (дефект 20e96078)', () => {
    const style = (reactProps(titleSpan(longQaNode()) as never) as { style: Record<string, unknown> }).style;
    // Меряется политика переноса подписи, а не ширина узла: 'anywhere' рвала слово по букве
    // («Производст|во») — её быть не должно; перенос по слогам даёт hyphens (язык текста —
    // lang="ru" в frontend/index.html:2), break-word — крайний случай для слов, которые
    // не переносятся ни целиком, ни по слогам.
    expect(style.hyphens).toBe('auto');
    expect(style.overflowWrap).toBe('break-word');
    expect(style.overflowWrap).not.toBe('anywhere');
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

  it('{2,1,3} → «2/3» и три сегмента; role=qa → метка «qa»; returns=0 → бейджа нет', () => {
    const c = render([{ ...mkStage('qa', 'q', 'qa', 0, 'active'), returns: 0, taskSegments: { done: 2, running: 1, total: 3 } }]);
    expect(txt(get(c, 'pgraph-segcount-q'))).toBe('2/3');
    expect((get(c, 'pgraph-segments') as unknown as { children: unknown[] }).children).toHaveLength(3);
    expect(txt(get(c, 'pgraph-role-q'))).toBe('qa');
    expect(get(c, 'pgraph-returns-q')).toBeNull();
  });

  it('легаси: поля B01 null/отсутствуют — узел рендерится без меты, бейджа и полоски', () => {
    const c = render([{
      ...mkStage('prod', 'l', 'step', 0, 'active'),
      personaNames: undefined, elapsedMinutes: null, waiting: null, returns: undefined, taskSegments: null, line: null,
    }]);
    expect(get(c, 'pgraph-node-l')).not.toBeNull();
    expect(get(c, 'pgraph-meta-l')).toBeNull();
    expect(get(c, 'pgraph-returns-l')).toBeNull();
    expect(get(c, 'pgraph-segments')).toBeNull();
  });

  it('proposed: пометка «предложено» только у узла с признаком', () => {
    const c = render([
      { ...mkStage('review', 'rv', 'review', 0, 'pending'), proposed: true },
      mkStage('final', 'fn', 'final', 1, 'pending', ['rv']),
    ]);
    expect(txt(get(c, 'pgraph-proposed-rv'))).toBe('предложено');
    expect(get(c, 'pgraph-proposed-fn')).toBeNull();
    expect(txt(get(c, 'pgraph-role-rv'))).toBe('review');
  });
});

describe('FeatureProcessGraph: узел «предложено» по макету (F33a)', () => {
  const get = (c: unknown, tid: string) => findNodeByAttr(c as never, 'data-testid', tid);
  const txt = (n: unknown) => textNodes(n as never).join('');
  // Значение style из пропсов элемента (border/color — строки токенов C.*).
  const css = (n: unknown, key: string): string =>
    String((reactProps(n as never) as { style: Record<string, unknown> }).style[key]);

  it('proposed=true: фон прозрачный (не planLight), рамка dashed c-plan, заголовок plan-text', () => {
    const c = render([
      { ...mkStage('design', 'dz', 'design', 0, 'pending'), proposed: true },
      mkStage('plan', 'pl', 'plan', 1, 'done', ['dz']),
    ]);
    const node = get(c, 'pgraph-node-dz')!;
    expect(node.getAttribute('data-proposed')).toBe('true');
    // Фон proposed-узла — transparent (макет .node.prop, feature-page.html:280): «призрак».
    expect(css(node, 'background')).toBe('transparent');
    expect(css(node, 'border')).toBe('1px dashed var(--c-plan)');
    expect(css(get(c, 'pgraph-title-dz'), 'color')).toBe('var(--c-plan-text)');
    // Контрольный сосед: обычный узел того же графа — фон как был (bgWhite).
    expect(css(get(c, 'pgraph-node-pl'), 'background')).toBe('var(--c-bg-white)');
  });

  it('proposed=false (бэк шлёт false у подтверждённого маршрута): стиль и мета прежние', () => {
    const c = render([
      { ...mkStage('review', 'rv', 'review', 0, 'pending'), proposed: false },
      mkStage('final', 'fn', 'final', 1, 'pending', ['rv']),
    ]);
    // Тот же узел, что без поля: рамка и цвет заголовка совпадают, пометки и меты нет.
    expect(css(get(c, 'pgraph-node-rv'), 'border')).toBe(css(get(c, 'pgraph-node-fn'), 'border'));
    expect(css(get(c, 'pgraph-node-rv'), 'border')).not.toContain('var(--c-plan)');
    expect(css(get(c, 'pgraph-title-rv'), 'color')).toBe('var(--c-text-secondary)');
    expect(get(c, 'pgraph-proposed-rv')).toBeNull();
    expect(get(c, 'pgraph-meta-rv')).toBeNull();
    expect(get(c, 'pgraph-legend-proposed')).toBeNull();
  });

  it('пометка в строке меты: при пустой мете — на её месте, при занятой — рядом через « · »', () => {
    const c = render([
      { ...mkStage('review', 'rv', 'review', 0, 'pending'), proposed: true },
      { ...mkStage('final', 'fn', 'final', 1, 'pending', ['rv']), proposed: true, personaNames: ['Полина'], elapsedMinutes: 70 },
    ]);
    expect(txt(get(c, 'pgraph-meta-rv'))).toBe('предложено');
    expect(txt(get(c, 'pgraph-meta-fn'))).toBe('Полина · 1 ч 10 мин · предложено');
    expect(css(get(c, 'pgraph-proposed-rv'), 'color')).toBe('var(--c-text-muted)');
  });

  it('в нижней строке с ролью пометки нет: во всем узле она встречается ровно один раз', () => {
    const c = render([{ ...mkStage('review', 'rv', 'review', 0, 'pending'), proposed: true, line: 'front' }]);
    const whole = txt(get(c, 'pgraph-node-rv'));
    expect((whole.match(/предложено/g) ?? []).length).toBe(1);
    // Роль и линия на своих местах — пометка их не вытеснила.
    expect(txt(get(c, 'pgraph-role-rv'))).toBe('review');
    expect(txt(get(c, 'pgraph-line-rv'))).toBe('фронт');
  });

  it('строка легенды про «предложено» — только когда на графе есть proposed-узел', () => {
    const withProposed = render([
      { ...mkStage('review', 'rv', 'review', 0, 'pending'), proposed: true },
      mkStage('final', 'fn', 'final', 1, 'pending', ['rv']),
    ]);
    const legend = get(withProposed, 'pgraph-legend-proposed');
    expect(legend).not.toBeNull();
    expect(txt(legend!)).toContain('фиолетовый пунктир — предложено, не подтверждено');
    const without = render([mkStage('review', 'rv', 'review', 0, 'pending')]);
    expect(get(without, 'pgraph-legend-proposed')).toBeNull();
    // Остальная легенда — без изменений.
    expect(get(without, 'pgraph-legend')).not.toBeNull();
  });
});

describe('FeatureProcessGraph: завершённый узел предложенного маршрута — обычный done (доработка R-F33a)', () => {
  const get = (c: unknown, tid: string) => findNodeByAttr(c as never, 'data-testid', tid);
  const txt = (n: unknown) => textNodes(n as never).join('');
  const css = (n: unknown, key: string): string =>
    String((reactProps(n as never) as { style: Record<string, unknown> }).style[key]);

  it('done+proposed: рамка и заголовок как у done без признака, пометки «предложено» нет', () => {
    // Та же страница, что в сценарии отказа: неподтверждённый маршрут, «Разрез» сделан,
    // следующий этап — ещё нет. Завершённый узел неотличим от обычного done,
    // незавершённый сосед — остаётся в фиолетовом пунктире (инвариант: рамка и цвет
    // читают состояние, «предложено» говорит только о том, чего ещё не было).
    const c = render([
      { ...mkStage('design', 'dz', 'design', 0, 'done'), proposed: true },
      { ...mkStage('plan', 'pl', 'plan', 1, 'pending', ['dz']), proposed: true },
    ]);
    const plain = render([mkStage('design', 'dz', 'design', 0, 'done')]);
    const node = get(c, 'pgraph-node-dz')!;
    expect(node.getAttribute('data-proposed')).toBeNull();
    const border = css(node, 'border');
    expect(border).toBe(css(get(plain, 'pgraph-node-dz'), 'border'));
    expect(border).toContain('1px solid');
    expect(border).not.toContain('var(--c-plan)');
    const titleColor = css(get(c, 'pgraph-title-dz'), 'color');
    expect(titleColor).not.toBe('var(--c-plan-text)');
    expect(titleColor).toBe(css(get(plain, 'pgraph-title-dz'), 'color'));
    expect(get(c, 'pgraph-proposed-dz')).toBeNull();
    expect(txt(node)).not.toContain('предложено');
    // Контрольный сосед: незавершённый proposed в том же графе — как сейчас (F33a).
    const plNode = get(c, 'pgraph-node-pl')!;
    expect(plNode.getAttribute('data-proposed')).toBe('true');
    expect(css(plNode, 'border')).toBe('1px dashed var(--c-plan)');
    expect(css(get(c, 'pgraph-title-pl'), 'color')).toBe('var(--c-plan-text)');
    expect(txt(get(c, 'pgraph-proposed-pl'))).toBe('предложено');
  });

  it('мета done+proposed: «Александр · до маршрута»; без исполнителя — «до маршрута», без висящего разделителя', () => {
    // Бэк у неподтверждённого маршрута personaNames = [] и elapsedMinutes = null
    // (FeatureProjector.cs:581, routed=false) — «до маршрута» остаётся единственной строкой.
    const c = render([
      { ...mkStage('design', 'dz', 'design', 0, 'done'), proposed: true, personaNames: ['Александр'] },
      { ...mkStage('plan', 'pl', 'plan', 1, 'done', ['dz']), proposed: true },
    ]);
    expect(txt(get(c, 'pgraph-meta-dz'))).toBe('Александр · до маршрута');
    expect(txt(get(c, 'pgraph-meta-pl'))).toBe('до маршрута');
    expect(txt(get(c, 'pgraph-meta-pl'))).not.toContain('предложено');
  });

  it('ребро к завершённому proposed-узлу — обычное; незавершённый proposed-конец — фиолетовый пунктир; легенда — только при незавершённом', () => {
    // Сценарий major-пункта (дефект 7c75b702): оба этапа предложенного маршрута завершены.
    // Ребро к завершённому цели не фиолетовое и не пунктирное (тот же отделённый флаг, что у
    // узла), строки легенды нет: фиолетовых узлов на графе нет (макет :374).
    const donePair = render([
      { ...mkStage('design', 'dz', 'design', 0, 'done'), proposed: true },
      { ...mkStage('plan', 'pl', 'plan', 1, 'done', ['dz']), proposed: true },
    ]);
    const edge = get(donePair, 'pgraph-edge-dz-pl')!;
    expect(edge.getAttribute('data-proposed')).toBeNull();
    const props = reactProps(edge as never) as { stroke?: string; strokeDasharray?: string };
    expect(props.stroke).not.toBe('var(--c-plan-border)');
    expect(props.stroke).toBe('var(--c-success)'); // источник done — пройденное ребро (макет .link.done)
    expect(props.strokeDasharray).toBeUndefined();
    expect(get(donePair, 'pgraph-legend-proposed')).toBeNull();
    // Контрольный: незавершённый proposed-конец в том же графе — фиолетовый пунктир (F33a),
    // легенда на месте.
    const openEnd = render([
      { ...mkStage('design', 'dz', 'design', 0, 'done'), proposed: true },
      { ...mkStage('plan', 'pl', 'plan', 1, 'pending', ['dz']), proposed: true },
    ]);
    const openEdge = get(openEnd, 'pgraph-edge-dz-pl')!;
    expect(openEdge.getAttribute('data-proposed')).toBe('true');
    const openProps = reactProps(openEdge as never) as { stroke?: string; strokeDasharray?: string };
    expect(openProps.stroke).toBe('var(--c-plan-border)');
    expect(openProps.strokeDasharray).toBe('5 4');
    expect(get(openEnd, 'pgraph-legend-proposed')).not.toBeNull();
  });
});

describe('FeatureProcessGraph: признак края следует фактической прокрутке (дефект 1f6df093)', () => {
  const get = (c: unknown, tid: string) => findNodeByAttr(c as never, 'data-testid', tid);

  it('прокрутка вправо — правый шеврон исчезает, левый появляется; прокрутка обратно — наоборот', () => {
    const c = render(parallelCycle(true));
    // Слушатель 'scroll' висит на контейнере ленты (feature-process-graph). Тест не имитирует
    // перемер ре-рендером, а повторяет путь браузера: даёт элементу фактические метрики
    // прокрутки, меняет scrollLeft и поднимает слушателя 'scroll' элемента — measure() читает
    // их заново, и шеврон следует фактической позиции прокрутки, а не пересборке компонента.
    const el = get(c, 'feature-process-graph') as unknown as {
      clientWidth: number; scrollWidth: number; scrollLeft: number;
      listeners: Record<string, Set<(e: unknown) => void>>;
    };
    el.clientWidth = 400; el.scrollWidth = 1000; el.scrollLeft = 0;
    const handlers = [...(el.listeners['scroll'] ?? [])];
    expect(handlers.length, 'на контейнере ленты нет слушателя «scroll»').toBe(1);
    const scrollTo = (to: number) => act(() => { el.scrollLeft = to; handlers.forEach((fn) => fn({})); });
    const right = () => get(c, 'pgraph-scroll-right');
    const left = () => get(c, 'pgraph-scroll-left');

    // Начало ленты: выступ только справа — один правый шеврон.
    scrollTo(0);
    expect(right()).not.toBeNull();
    expect(left()).toBeNull();

    // Доехали до конца ленты (over = 1000 − 400): выступ слева — правый пропал, левый появился.
    scrollTo(600);
    expect(right()).toBeNull();
    expect(left()).not.toBeNull();

    // Середина ленты: обрезаны оба края — оба шеврона.
    scrollTo(300);
    expect(right()).not.toBeNull();
    expect(left()).not.toBeNull();

    // Прокрутка обратно к началу — наоборот.
    scrollTo(0);
    expect(right()).not.toBeNull();
    expect(left()).toBeNull();
  });
});
