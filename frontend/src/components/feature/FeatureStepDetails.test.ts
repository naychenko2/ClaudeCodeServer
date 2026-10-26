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
// Мутации (в отчёте): (1) ✓/○ по индексу строки → тест приёмки красный; (2) запрос «Итога»
// в теле компонента вместо эффекта → ленивость красная; (3) доля без axisFrac (своя арифметика)
// → тест оси красный.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureStepDetails, type StepDetailTurn } from './FeatureStepDetails';
import { axisFrac, type LaneAxis } from './FeatureLaneModel';
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

function renderItem(props: Record<string, unknown>) {
  installFakeDom();
  return mount(createElement(FeatureStepDetails, props));
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
