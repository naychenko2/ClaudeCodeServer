// FeatureStepDetails (GC-F7, план «Гант на „Хронологии“», макет feature-gantt-7) — блоки
// «Приёмка», «Итог», «Время шага на оси фичи».
//
// Покрытие (критерии GC-F7): 4 критерия и 3 свидетельства → 3 «✓» и 1 «○» по criterionId,
// а не по индексу; ленивость «Итога»: до монтирования запроса нет, после — ровно один,
// без повторов при ререндере; задача без итога (hasResult=false) — без запроса и без блока;
// «Итог» — только первая строка; отказ загрузки — текстом, а не пустотой; left/width хода —
// ровно axisFrac (±0.01 %), цвета по виду хода; закрытая ось — без «сейчас»; ход за краем
// окна не рисуется; «Показать интервалы» — только с колбэком; хвост — с маркером.
//
// Покрытие (доработка GC-F7R, ревью GC-R-F7): состояние критерия — скринридеру (у глифа
// нет aria-hidden, рядом — скрытый текст «подтверждён/не подтверждён»); «Повторить» в отказе
// на мобильном ≥ 40 px (на десктопе — кит xs); легенда называет живой ход и цвета токенами,
// а не словами.
//
// Покрытие (доработка GC-F7R2, вердикт GC-R-P2 MAJOR-3): «сейчас» живое — общий useNow
// (интервал только пока ось живая и nowMs не передан): тик двигает вертикаль и подпись;
// без тика ререндеры вертикаль не двигают (нет Date.now() в теле рендера); проп nowMs
// приоритетнее хука — в том числе на тике.
//
// Мутации (в отчёте): (1) ✓/○ по индексу строки → тест приёмки красный; (2) запрос «Итога»
// в теле компонента вместо эффекта → ленивость красная; (3) доля без axisFrac (своя арифметика)
// → тест оси красный; (4) вместо useNow — useState(() => Date.now()) (замершее на монтирование)
// → тест «живое» красное; Date.now() обратно в теле рендера → тест «без тика» красный;
// (5) снятый aria-hidden/скрытый текст → тест скринридера красный; (6) isMobile
// не доходит до «Повторить» → мобильный тест красный.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureStepDetails, type StepDetailTurn } from './FeatureStepDetails';
import { axisFrac, type LaneAxis } from './FeatureLaneModel';
import { formatClock } from './FeatureStatsBits';
import { upsertTaskLocal } from '../../lib/tasks';
import { findNodeByAttr, installFakeDom, mount, textNodes, uninstallFakeDom } from '../chat/__tests__/fakeDom';
import type { AcceptanceCriterion, AcceptanceEvidence, Task } from '../../types';
import { C } from '../../lib/design';

// api.tasks.get — spy: «Итог» ходит к полной задаче только через него (паттерн
// TaskCreatedView.test.ts). useTasks/ensureFullTask/upsertTaskLocal — настоящие:
// стор наполняем upsertTaskLocal или оставляем пустым (нет в сторе → добор).
const { tasksGetMock } = vi.hoisted(() => ({ tasksGetMock: vi.fn() }));
vi.mock('../../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/api')>();
  return { ...orig, api: { ...orig.api, tasks: { ...orig.api.tasks, get: tasksGetMock } } };
});

type FNode = Parameters<typeof findNodeByAttr>[0];

const flush = () => new Promise(r => setTimeout(r, 0));

// Окно 12 ч, живая фича (timeFrac 0.86) и закрытая (1) — та же шкала, что в FeatureStageLane.test.ts.
const AXIS_FROM = '2026-10-20T00:00:00Z';
const AXIS_TO = '2026-10-20T12:00:00Z';
const NOW_MS = Date.parse('2026-10-20T12:00:00Z');
const LIVE_AXIS: LaneAxis = { from: AXIS_FROM, to: AXIS_TO, timeFrac: 0.86 };
const CLOSED_AXIS: LaneAxis = { from: AXIS_FROM, to: AXIS_TO, timeFrac: 1 };

// Стор — модульный синглтон и живёт между тестами файла: id уникальны на тест.
let seq = 0;
const nextId = () => `f7-task-${(seq += 1)}`;

function fullTask(id: string, over: Partial<Task> = {}): Task {
  return {
    id, title: 'Шаг F7', description: '', status: 'done', priority: 'medium',
    linkedFiles: [], subtasks: [], labels: [], order: 1,
    createdAt: '2026-10-20T00:00:00Z', updatedAt: '2026-10-20T00:00:00Z',
    ...over,
  } as Task;
}

// Интервал useNow живёт, пока компонент смонтирован: корни треким и снимаем в afterEach
// (тот же порядок, что у FeaturePageFrame) — иначе тики вешаются на мёртвые корни.
const roots: ReturnType<typeof mount>['root'][] = [];
function renderItem(props: Record<string, unknown>) {
  installFakeDom();
  const r = mount(createElement(FeatureStepDetails, props));
  roots.push(r.root);
  return r;
}

function styleOf(node: FNode): Record<string, string> {
  return (node as unknown as { style: Record<string, string> }).style;
}

// Все узлы с атрибутом (findNodeByAttr отдаёт первый; полос содержит несколько ходов).
function findAllByAttr(root: FNode, attr: string, value: string): FNode[] {
  const out: FNode[] = [];
  const walk = (x: FNode) => {
    if (x.nodeType === 1 && x.getAttribute(attr) === value) out.push(x);
    for (const c of x.childNodes) walk(c);
  };
  walk(root);
  return out;
}

beforeEach(() => {
  tasksGetMock.mockReset();
});
afterEach(() => {
  for (const r of roots) act(() => { r.unmount(); });
  roots.length = 0;
  uninstallFakeDom();
});

// === «Приёмка» ===

describe('«Приёмка»: ✓/○ по criterionId, а не по индексу', () => {
  it('4 критерия и 3 свидетельства → 3 «✓» и 1 «○» (свидетельства в другом порядке)', () => {
    const id = nextId();
    // Полная задача в сторе → «Итог» не мешает (без результата — без блока и без запроса).
    upsertTaskLocal(fullTask(id));
    const acceptance: AcceptanceCriterion[] = [
      { id: 'c1', text: 'критерий 1' },
      { id: 'c2', text: 'критерий 2' },
      { id: 'c3', text: 'критерий 3' },
      { id: 'c4', text: 'критерий 4' },
    ];
    // Свидетельства — не по порядку и без c3: по индексу первые три получили бы «✓».
    const evidence: AcceptanceEvidence[] = [
      { criterionId: 'c4', how: 'прогон', result: 'зелёный' },
      { criterionId: 'c2', how: 'замер', result: 'ок' },
      { criterionId: 'c1', how: 'тест', result: 'ок' },
    ];
    const { container } = renderItem({ taskId: id, acceptance, evidence });
    const glyph = (critId: string): string => {
      const row = findNodeByAttr(container, 'data-testid', `step-crit-${critId}`);
      expect(row, `строки критерия ${critId} нет`).not.toBeNull();
      const g = findNodeByAttr(row!, 'data-testid', 'step-crit-glyph');
      expect(g, `глифа критерия ${critId} нет`).not.toBeNull();
      return textNodes(g!).join('');
    };
    expect(glyph('c1')).toBe('✓');
    expect(glyph('c2')).toBe('✓');
    expect(glyph('c3')).toBe('○');
    expect(glyph('c4')).toBe('✓');
  });

  it('состояние — скринридеру: у глифа нет aria-hidden, рядом — скрытый текст состояния', () => {
    const id = nextId();
    upsertTaskLocal(fullTask(id));
    const { container } = renderItem({
      taskId: id,
      acceptance: [{ id: 'c1', text: 'критерий 1' }, { id: 'c2', text: 'критерий 2' }],
      evidence: [{ criterionId: 'c1', how: 'тест', result: 'ок' }],
    });
    const stateOf = (critId: string): { ariaHidden: unknown; state: string } => {
      const row = findNodeByAttr(container, 'data-testid', `step-crit-${critId}`);
      expect(row, `строки критерия ${critId} нет`).not.toBeNull();
      const glyph = findNodeByAttr(row!, 'data-testid', 'step-crit-glyph');
      expect(glyph, `глифа критерия ${critId} нет`).not.toBeNull();
      const state = findNodeByAttr(row!, 'data-testid', 'step-crit-state');
      expect(state, `скрытого состояния критерия ${critId} нет`).not.toBeNull();
      return { ariaHidden: glyph!.getAttribute('aria-hidden'), state: textNodes(state!).join('') };
    };
    expect(stateOf('c1')).toEqual({ ariaHidden: null, state: 'подтверждён' });
    expect(stateOf('c2')).toEqual({ ariaHidden: null, state: 'не подтверждён' });
  });

  it('вердикт — строкой только когда передан; без критериев блока «Приёмка» нет', () => {
    const id = nextId();
    upsertTaskLocal(fullTask(id));
    const withVerdict = renderItem({
      taskId: id,
      acceptance: [{ id: 'c1', text: 'критерий 1' }],
      evidence: [{ criterionId: 'c1', how: 'тест', result: 'зелёный' }],
      verdictLine: 'Ревью rev-1: принял · Глеб · 20.10 · 14:02',
    });
    const v = findNodeByAttr(withVerdict.container, 'data-testid', 'step-verdict');
    expect(v).not.toBeNull();
    expect(textNodes(v!).join('')).toContain('принял · Глеб');

    const noVerdict = renderItem({
      taskId: id,
      acceptance: [{ id: 'c1', text: 'критерий 1' }],
      evidence: [{ criterionId: 'c1', how: 'тест', result: 'зелёный' }],
    });
    expect(findNodeByAttr(noVerdict.container, 'data-testid', 'step-verdict')).toBeNull();

    const noAcc = renderItem({ taskId: id });
    expect(findNodeByAttr(noAcc.container, 'data-testid', 'step-acceptance')).toBeNull();
  });
});

// === «Итог»: ленивость и состояния ===

describe('«Итог»: догрузка полного текста', () => {
  it('до монтирования запроса нет; после — ровно один; ререндер его не повторяет', async () => {
    const id = nextId();
    tasksGetMock.mockResolvedValue(fullTask(id, { resultMarkdown: 'первая строка\nвторая строка' }));
    // До монтирования — ни одного GET /api/tasks/{id}.
    expect(tasksGetMock).not.toHaveBeenCalled();
    const { root, container } = renderItem({ taskId: id });
    // Пока запрос не разрезолвился — состояние загрузки («…»), не пустота.
    expect(findNodeByAttr(container, 'data-testid', 'step-result-loading')).not.toBeNull();
    await act(async () => { await flush(); });
    expect(tasksGetMock).toHaveBeenCalledTimes(1);
    expect(tasksGetMock).toHaveBeenCalledWith(id);
    // Только первая строка, в одной строке DOM.
    const text = findNodeByAttr(container, 'data-testid', 'step-result-text');
    expect(text).not.toBeNull();
    expect(textNodes(text!).join('')).toBe('первая строка');
    expect(styleOf(text!).whiteSpace).toBe('nowrap');
    // Ререндер тех же пропсов — нового запроса нет (полная версия уже в сторе).
    act(() => { root.render(createElement(FeatureStepDetails, { taskId: id })); });
    await act(async () => { await flush(); });
    expect(tasksGetMock).toHaveBeenCalledTimes(1);
  });

  it('задача в списке без итога (hasResult=false): ни запроса, ни блока', async () => {
    const id = nextId();
    // Mock живый: при убранном гейте краснеет ровно на ассерте «запроса нет», а не TypeError'ом.
    tasksGetMock.mockResolvedValue(fullTask(id));
    upsertTaskLocal(fullTask(id, { listItem: true, hasResult: false }));
    const { container } = renderItem({ taskId: id });
    await act(async () => { await flush(); });
    expect(tasksGetMock).not.toHaveBeenCalled();
    expect(findNodeByAttr(container, 'data-testid', 'step-result')).toBeNull();
  });

  it('отказ загрузки — текстом, а не пустотой', async () => {
    const id = nextId();
    tasksGetMock.mockRejectedValue(new Error('offline'));
    const { container } = renderItem({ taskId: id });
    await act(async () => { await flush(); });
    const err = findNodeByAttr(container, 'data-testid', 'step-result-error');
    expect(err).not.toBeNull();
    expect(textNodes(err!).join('')).toContain('не загрузился');
  });

  it('«Повторить» в отказе: на мобильном minHeight ≥ 40 (макет §360), на десктопе — кит xs', async () => {
    const id = nextId();
    tasksGetMock.mockRejectedValue(new Error('offline'));
    const mobile = renderItem({ taskId: id, isMobile: true });
    await act(async () => { await flush(); });
    const btnM = findNodeByAttr(mobile.container, 'data-testid', 'step-result-retry');
    expect(btnM, 'кнопки «Повторить» (мобильной) нет').not.toBeNull();
    expect(parseFloat(styleOf(btnM!).minHeight!), 'minHeight мобильной цели').toBeGreaterThanOrEqual(40);

    const desktop = renderItem({ taskId: id });
    await act(async () => { await flush(); });
    const btnD = findNodeByAttr(desktop.container, 'data-testid', 'step-result-retry');
    expect(btnD, 'кнопки «Повторить» (десктоп) нет').not.toBeNull();
    expect(styleOf(btnD!).minHeight).toBe('24px');
  });
});

// === «Время шага на оси фичи» ===

describe('«Время шага»: доли — ровно axisFrac', () => {
  const turns: StepDetailTurn[] = [
    { from: '2026-10-20T01:00:00Z', to: '2026-10-20T02:00:00Z', kind: 'work' },
    { from: '2026-10-20T03:00:00Z', to: '2026-10-20T04:30:00Z', kind: 'review' },
    { from: '2026-10-20T05:00:00Z', to: '2026-10-20T05:10:00Z', kind: 'live' },
  ];

  it('left/width каждого хода совпадают с axisFrac (±0.01 %); цвета по виду хода', () => {
    const id = nextId();
    upsertTaskLocal(fullTask(id));
    const { container } = renderItem({ taskId: id, axis: LIVE_AXIS, turns, nowMs: NOW_MS });
    const strip = findNodeByAttr(container, 'data-testid', 'step-strip');
    expect(strip).not.toBeNull();
    // Полоска 14 px (макет); React переводит числовое значение в '14px'.
    expect(styleOf(strip!).height).toBe('14px');
    const nodes = findAllByAttr(container, 'data-testid', 'step-turn');
    expect(nodes.length).toBe(3);
    turns.forEach((t, i) => {
      const s = styleOf(nodes[i]!);
      const left = axisFrac(t.from, LIVE_AXIS) * 100;
      const width = Math.max(0.3, (axisFrac(t.to, LIVE_AXIS) - axisFrac(t.from, LIVE_AXIS)) * 100);
      expect(parseFloat(s.left!), `left[${i}]`).toBeCloseTo(left, 2);
      expect(parseFloat(s.width!), `width[${i}]`).toBeCloseTo(width, 2);
    });
    // работа — C.success, ревью — C.plan, живой — C.accent.
    expect(styleOf(nodes[0]!).background).toBe(C.success);
    expect(styleOf(nodes[1]!).background).toBe(C.plan);
    expect(styleOf(nodes[2]!).background).toBe(C.accent);
  });

  it('живая ось — вертикаль «сейчас» и её подпись; закрытая — ни того, ни другого', () => {
    const id = nextId();
    upsertTaskLocal(fullTask(id));
    const live = renderItem({ taskId: id, axis: LIVE_AXIS, turns, nowMs: NOW_MS });
    expect(findNodeByAttr(live.container, 'data-testid', 'step-now')).not.toBeNull();
    expect(textNodes(findNodeByAttr(live.container, 'data-testid', 'step-time')!).join(' ')).toContain('сейчас');

    const closed = renderItem({ taskId: id, axis: CLOSED_AXIS, turns, nowMs: NOW_MS });
    expect(findNodeByAttr(closed.container, 'data-testid', 'step-now')).toBeNull();
    expect(textNodes(findNodeByAttr(closed.container, 'data-testid', 'step-time')!).join(' ')).not.toContain('сейчас');
  });

  it('ход целиком за правым краем окна не рисуется; без оси блока времени нет', () => {
    const id = nextId();
    upsertTaskLocal(fullTask(id));
    const beyond: StepDetailTurn[] = [{ from: '2026-10-20T13:00:00Z', to: '2026-10-20T14:00:00Z', kind: 'work' }];
    const { container } = renderItem({ taskId: id, axis: CLOSED_AXIS, turns: beyond, nowMs: NOW_MS });
    expect(findAllByAttr(container, 'data-testid', 'step-turn').length).toBe(0);

    const noAxis = renderItem({ taskId: id, turns });
    expect(findNodeByAttr(noAxis.container, 'data-testid', 'step-time')).toBeNull();
  });

  it('хвост после мержа: блок помечен «хвост после мержа», без флага — нет', () => {
    const id = nextId();
    upsertTaskLocal(fullTask(id));
    const tail = renderItem({ taskId: id, axis: CLOSED_AXIS, turns, isTail: true, nowMs: NOW_MS });
    expect(textNodes(findNodeByAttr(tail.container, 'data-testid', 'step-time')!).join(' ')).toContain('хвост после мержа');
    const plain = renderItem({ taskId: id, axis: CLOSED_AXIS, turns, nowMs: NOW_MS });
    expect(textNodes(findNodeByAttr(plain.container, 'data-testid', 'step-time')!).join(' ')).not.toContain('хвост');
  });

  it('легенда называет живой ход и описывает цвета токенами, а не словами', () => {
    const id = nextId();
    upsertTaskLocal(fullTask(id));
    const { container } = renderItem({ taskId: id, axis: LIVE_AXIS, turns, nowMs: NOW_MS });
    const txt = textNodes(findNodeByAttr(container, 'data-testid', 'step-time')!).join(' ');
    expect(txt).toContain('живой ход');
    expect(txt).toContain('работа — C.success');
    expect(txt).toContain('ревью — C.plan');
    expect(txt).toContain('C.accent');
    expect(txt).not.toContain('зелёный');
    expect(txt).not.toContain('фиолетовый');
  });

  it('«сейчас» живое: тик useNow двигает вертикаль и подпись (GC-F7R2)', () => {
    const id = nextId();
    upsertTaskLocal(fullTask(id));
    const T1 = Date.parse('2026-10-20T10:00:00Z');
    const T2 = T1 + 60_000; // тик интервала useNow (60 с)
    vi.useFakeTimers();
    vi.setSystemTime(T1);
    try {
      const { container } = renderItem({ taskId: id, axis: LIVE_AXIS, turns });
      const m1 = findNodeByAttr(container, 'data-testid', 'step-now');
      expect(m1, 'вертикали «сейчас» нет').not.toBeNull();
      const left1 = parseFloat(styleOf(m1!).left!);
      expect(left1).toBeCloseTo(axisFrac(new Date(T1).toISOString(), LIVE_AXIS) * 100, 2);
      // join('') — метка «сейчас HH:MM» в JSX два текстовых узла («сейчас » + «HH:MM»),
      // через join(' ') вышла бы двойная пауза.
      expect(textNodes(findNodeByAttr(container, 'data-testid', 'step-time')!).join(''))
        .toContain(`сейчас ${formatClock(new Date(T1).toISOString())}`);
      // Сценарий вердикта: карточка простояла открытой — по тиках useNow «сейчас» обязано
      // идти за временем, а не стоять на минуте открытия.
      act(() => { vi.advanceTimersByTime(60_000); });
      const m2 = findNodeByAttr(container, 'data-testid', 'step-now');
      expect(m2, 'вертикали «сейчас» после тика нет').not.toBeNull();
      const left2 = parseFloat(styleOf(m2!).left!);
      expect(left2).toBeCloseTo(axisFrac(new Date(T2).toISOString(), LIVE_AXIS) * 100, 2);
      expect(left2).toBeGreaterThan(left1);
      expect(textNodes(findNodeByAttr(container, 'data-testid', 'step-time')!).join(''))
        .toContain(`сейчас ${formatClock(new Date(T2).toISOString())}`);
    } finally {
      vi.useRealTimers();
    }
  });

  it('без тика ререндеры вертикаль не двигают: Date.now() не зовётся из тела рендера', () => {
    const id = nextId();
    upsertTaskLocal(fullTask(id));
    const T1 = Date.parse('2026-10-20T10:00:00Z');
    const T2 = Date.parse('2026-10-20T11:00:00Z');
    const spy = vi.spyOn(Date, 'now').mockReturnValue(T1);
    try {
      const { root, container } = renderItem({ taskId: id, axis: LIVE_AXIS, turns });
      const marker1 = findNodeByAttr(container, 'data-testid', 'step-now');
      expect(marker1, 'вертикали «сейчас» нет').not.toBeNull();
      const left1 = parseFloat(styleOf(marker1!).left!);
      expect(left1).toBeCloseTo(axisFrac(new Date(T1).toISOString(), LIVE_AXIS) * 100, 2);
      // Ререндер при другом «реальном времени» БЕЗ тика интервала — позиция та же:
      // useNow обновляет значение только по тикам, рендер сам по себе его не пересчитывает
      // (защита от Date.now() в теле рендера — дрожания вертикали на каждом ререндере).
      spy.mockReturnValue(T2);
      act(() => { root.render(createElement(FeatureStepDetails, { taskId: id, axis: LIVE_AXIS, turns })); });
      const marker2 = findNodeByAttr(container, 'data-testid', 'step-now');
      expect(marker2, 'вертикали «сейчас» после ререндера нет').not.toBeNull();
      expect(parseFloat(styleOf(marker2!).left!)).toBeCloseTo(left1, 6);
    } finally {
      spy.mockRestore();
    }
  });

  it('nowMs приоритетнее useNow: значение из пропса, тик его не двигает', () => {
    const id = nextId();
    upsertTaskLocal(fullTask(id));
    const T1 = Date.parse('2026-10-20T10:00:00Z');
    const FIXED = Date.parse('2026-10-20T09:00:00Z');
    vi.useFakeTimers();
    vi.setSystemTime(T1);
    try {
      const { container } = renderItem({ taskId: id, axis: LIVE_AXIS, turns, nowMs: FIXED });
      const m1 = findNodeByAttr(container, 'data-testid', 'step-now');
      expect(m1, 'вертикали «сейчас» нет').not.toBeNull();
      const left1 = parseFloat(styleOf(m1!).left!);
      // Не системное T1, а ровно значение пропса.
      expect(left1).toBeCloseTo(axisFrac(new Date(FIXED).toISOString(), LIVE_AXIS) * 100, 2);
      act(() => { vi.advanceTimersByTime(60_000); });
      const m2 = findNodeByAttr(container, 'data-testid', 'step-now');
      expect(m2, 'вертикали «сейчас» после тика нет').not.toBeNull();
      expect(parseFloat(styleOf(m2!).left!)).toBeCloseTo(left1, 6);
    } finally {
      vi.useRealTimers();
    }
  });
});

// === «Показать интервалы на дорожке» ===

describe('кнопка подсветки', () => {
  it('без колбэка кнопки нет; с колбэком — есть с доступным именем', () => {
    const id = nextId();
    upsertTaskLocal(fullTask(id));
    const plain = renderItem({ taskId: id });
    expect(findNodeByAttr(plain.container, 'data-testid', 'step-show-intervals')).toBeNull();

    const withCb = renderItem({ taskId: id, onShowIntervals: () => {} });
    const btn = findNodeByAttr(withCb.container, 'data-testid', 'step-show-intervals');
    expect(btn).not.toBeNull();
    expect(textNodes(btn!).join('')).toContain('Показать интервалы на дорожке');
    expect(btn!.getAttribute('aria-label')).toContain('интервалы');
  });
});
