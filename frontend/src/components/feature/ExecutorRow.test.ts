// ExecutorRow (GC-F9, план «Гант на „Хронологии“», макет feature-gantt-7) — раскрываемая
// строка исполнителя.
//
// Покрытие (критерии GC-F9):
// • клик по шапке → aria-expanded="true", внутри полное название, мета (id, даты),
//   текст критериев приёмки; клики по «задача» внутри и по «задача» в свёрнутой шапке
//   строку не сворачивают/не раскрывают;
// • ход outcome='cancelled' показан с «(cancelled)», 'success' — без скобок; живой ход —
//   «идёт»; ходы чужой задачи в строку не попадают;
// • на 360 чип модели и «оценивает →» — только в раскрытой части; на десктопе чип и
//   компактные чипы «оценивает →» остаются в шапке (прежнее поведение);
// • «оценивает →» в раскрытой части — ссылки: клик открывает страницу оцениваемой задачи;
// • FeatureStepDetails без колбэка подсветки (кнопки «Показать интервалы» нет);
// • без раскрытия тела нет и ленивой догрузки «Итога» (ни одного GET).
//
// Мутации (в отчёте): (1) убрать stopPropagation у пары в свёрнутой шапке → тест
// «клик по „задача“ не раскрывает» красный; (2) показывать ходы без фильтра по taskId →
// «ходы чужой задачи» красный; (3) ставить скобки любому исходу (в т.ч. success) →
// «success без скобок» красный; (4) оставить чип модели в шапке на 360 → «360: чип
// только внутри» красный.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { ExecutorRow } from './FeatureTimelineBits';
import { upsertTaskLocal } from '../../lib/tasks';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureStageExecutor, FeatureTask } from '../../types/featureRun';
import type { StageTurn } from '../../types/featureStats';
import type { AcceptanceCriterion, AcceptanceEvidence, Task } from '../../types';

// === Мок персон (тот же паттерн, что FeatureTimelineTab.test.ts) ===
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
personaMap.set('p1', 'Денис');
vi.mock('../../lib/personas', () => ({
  getPersonaById: (id: string) =>
    personaMap.has(id)
      ? { id, name: personaMap.get(id), avatar: { kind: 'initials' as const } }
      : undefined,
  usePersonasVersion: () => 0,
  personaInitials: (name: string) => (name ? name.trim().charAt(0).toUpperCase() : ''),
}));
// api: tasks.get — spy (ленивость «Итога»); personas.avatarUrl — для PersonaAvatar.
const { tasksGetMock } = vi.hoisted(() => ({ tasksGetMock: vi.fn() }));
vi.mock('../../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/api')>();
  return { ...orig, api: { ...orig.api, personas: { ...orig.api.personas, avatarUrl: () => '' }, tasks: { ...orig.api.tasks, get: tasksGetMock } } };
});
vi.mock('../../lib/openChat', () => ({ openChatById: async () => true }));
// lib/tasks — настоящий (стор для FeatureStepDetails), openTaskInSection — spy журнала целей.
const { openTaskCalls } = vi.hoisted(() => ({ openTaskCalls: [] as { id: string; projectId?: string }[] }));
vi.mock('../../lib/tasks', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/tasks')>();
  return { ...orig, openTaskInSection: (t: { id: string; projectId?: string }) => { openTaskCalls.push(t); } };
});

type FNode = Parameters<typeof findNodeByAttr>[0];

// Фиксированное «сейчас» (стабильные границы живых ходов).
const NOW = Date.parse('2026-10-20T12:00:00Z');

// Местные подписи (те же локальные геттеры, что featureTime) — пояс независим.
function localTime(iso: string): string {
  const d = new Date(iso);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}
function localDateTime(iso: string): string {
  const d = new Date(iso);
  return `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')} ${localTime(iso)}`;
}

function mkExec(over: Partial<FeatureStageExecutor> = {}): FeatureStageExecutor {
  return {
    personaId: 'p1',
    taskId: 'exec-task-1',
    taskRole: 'review',
    stepRef: null,
    lane: null,
    status: 'done',
    chatId: 'chat-1',
    chatArchived: false,
    modelLabel: 'Opus',
    modelFull: 'Opus 4.5',
    reviewOf: [],
    reviewOfOutside: 0,
    ...over,
  };
}

const TASK: FeatureTask = {
  id: 'exec-task-1',
  title: 'Полное название задачи',
  projectId: 'proj-1',
  status: 'done',
  createdAt: '2026-10-19T09:00:00Z',
  claudeStartedAt: '2026-10-19T09:30:00Z',
  completedAt: '2026-10-19T10:15:00Z',
  acceptance: [{ id: 'c1', text: 'критерий приёмки один' }] as AcceptanceCriterion[],
  evidence: [{ criterionId: 'c1', how: 'тест', result: 'зелёный' }] as AcceptanceEvidence[],
};

// Полная задача в сторе: «Итог» сразу ready (без догрузки) и без результата — блока нет.
function seedStoreTask(): void {
  upsertTaskLocal({
    id: TASK.id, title: TASK.title, description: '', status: 'done', priority: 'medium',
    linkedFiles: [], subtasks: [], labels: [], order: 1,
    createdAt: TASK.createdAt!, updatedAt: TASK.completedAt!,
  } as Task);
}

function renderRow(over: Record<string, unknown> = {}): { container: FNode; row: FNode } {
  installFakeDom();
  const x = (over.x as FeatureStageExecutor) ?? mkExec();
  const taskMap = new Map<string, FeatureTask>();
  taskMap.set(TASK.id, TASK);
  for (const t of (over.extraTasks as FeatureTask[]) ?? []) taskMap.set(t.id, t);
  const { container } = mount(createElement(ExecutorRow, {
    x,
    task: TASK,
    now: NOW,
    featureId: 'f1',
    featureTitle: 'Фича',
    taskMap,
    ...over,
  }));
  const row = findNodeByAttr(container, 'data-testid', 'timeline-executor')!;
  return { container, row };
}

const head = (row: FNode): FNode => findNodeByAttr(row, 'data-testid', 'timeline-executor-head')!;
const body = (row: FNode): FNode | null => findNodeByAttr(row, 'data-testid', 'timeline-executor-body');
const expanded = (row: FNode): string | null => head(row).getAttribute('aria-expanded');

function open(row: FNode): void {
  act(() => { (reactProps(head(row))!.onClick as () => void)(); });
}

// Клик с всплытием (паттерн FeaturePlanReader.test.ts): зовём onClick предков,
// пока их не остановят stopPropagation.
function bubbleClick(target: FNode): void {
  const ev = { stopped: false, stopPropagation() { ev.stopped = true; }, preventDefault() {} };
  act(() => {
    for (let n: FNode | null = target; n && !ev.stopped; n = n.parentNode as FNode | null) {
      const h = reactProps(n)?.onClick as ((e: unknown) => void) | undefined;
      h?.(ev);
    }
  });
}

beforeEach(() => {
  tasksGetMock.mockReset();
  openTaskCalls.length = 0;
  seedStoreTask();
});
afterEach(() => {
  uninstallFakeDom();
});

// === Свёрнутая шапка ===

describe('свёрнутая шапка', () => {
  it('строка — PressableArea с aria-expanded="false"; тела, деталей и догрузки «Итога» нет', () => {
    const { row } = renderRow();
    expect(expanded(row)).toBe('false');
    expect(body(row)).toBeNull();
    expect(findNodeByAttr(row, 'data-testid', 'step-details')).toBeNull();
    expect(tasksGetMock).not.toHaveBeenCalled();
    // Содержание шапки — прежнее (имя, роль, статус, пара «чат · задача»).
    const t = textNodes(head(row)).join('');
    expect(t).toContain('Денис');
    expect(t).toContain('ревью');
    expect(t).toContain('готово');
    expect(findNodeByAttr(row, 'data-testid', 'nav-target-pair')).not.toBeNull();
  });

  it('десктоп: чип модели и компактные чипы «оценивает →» остаются в шапке', () => {
    const target: FeatureTask = { id: 't-target', title: 'Целевая задача', projectId: 'p9' };
    const { row } = renderRow({ x: mkExec({ reviewOf: ['t-target'] }), extraTasks: [target] });
    expect(findNodeByAttr(head(row), 'data-testid', 'timeline-model-chip')).not.toBeNull();
    const chip = findNodeByAttr(head(row), 'data-testid', 'timeline-eval-target');
    expect(chip, 'компактный чип «оценивает →» в шапке').not.toBeNull();
    expect(textNodes(chip!).join('')).toContain('Целевая задача');
  });

  it('клик по «задача» в свёрнутой шапке ведёт в задачу и НЕ раскрывает строку (stopPropagation)', () => {
    const { row } = renderRow();
    const taskBtn = findNodeByAttr(head(row), 'data-testid', 'nav-target-task')!;
    bubbleClick(taskBtn);
    expect(openTaskCalls).toEqual([{ id: TASK.id, projectId: 'proj-1' }]);
    expect(expanded(row)).toBe('false');
    expect(body(row)).toBeNull();
  });
});

// === Раскрытая часть (c1) ===

describe('раскрытая часть', () => {
  it('клик по шапке → aria-expanded="true"; внутри название, мета (id, даты) и текст критериев', () => {
    const { row } = renderRow();
    open(row);
    expect(expanded(row)).toBe('true');
    const b = body(row)!;
    expect(textNodes(findNodeByAttr(b, 'data-testid', 'timeline-executor-title')!).join('')).toBe('Полное название задачи');
    const meta = textNodes(findNodeByAttr(b, 'data-testid', 'timeline-executor-meta')!).join('');
    expect(meta).toContain(TASK.id);
    expect(meta).toContain(`создана ${localDateTime(TASK.createdAt!)}`);
    expect(meta).toContain(`старт ${localDateTime(TASK.claudeStartedAt!)}`);
    expect(meta).toContain(`закрыта ${localDateTime(TASK.completedAt!)}`);
    // FeatureStepDetails: «Приёмка» с текстом критерия и «✓» по свидетельству.
    const crit = findNodeByAttr(b, 'data-testid', 'step-crit-c1');
    expect(crit, 'строки критерия внутри нет').not.toBeNull();
    expect(textNodes(crit!).join('')).toContain('критерий приёмки один');
    expect(textNodes(findNodeByAttr(crit!, 'data-testid', 'step-crit-glyph')!).join('')).toBe('✓');
    // Без колбэка подсветки — кнопки «Показать интервалы» нет.
    expect(findNodeByAttr(b, 'data-testid', 'step-show-intervals')).toBeNull();
    // Мета-данных нет — их строк нет (честно, не выдумываем).
    const noTask = renderRow({ task: undefined as unknown as FeatureTask });
    open(noTask.row);
    const metaNoTask = textNodes(findNodeByAttr(body(noTask.row)!, 'data-testid', 'timeline-executor-meta')!).join('');
    expect(metaNoTask).toContain(TASK.id);
    expect(metaNoTask).not.toContain('создана');
    expect(findNodeByAttr(body(noTask.row)!, 'data-testid', 'timeline-executor-title')).toBeNull();
  });

  it('клик по «задача» внутри раскрытой части не сворачивает строку (c1)', () => {
    const { row } = renderRow();
    open(row);
    const taskBtn = findNodeByAttr(body(row)!, 'data-testid', 'nav-target-task')!;
    bubbleClick(taskBtn);
    expect(openTaskCalls).toEqual([{ id: TASK.id, projectId: 'proj-1' }]);
    expect(expanded(row)).toBe('true');
    expect(body(row)).not.toBeNull();
  });

  it('«оценивает →» ссылками: клик открывает страницу оцениваемой задачи, строка остаётся раскрытой', () => {
    const target: FeatureTask = { id: 't-target', title: 'Целевая задача', projectId: 'p9' };
    const { row } = renderRow({ x: mkExec({ reviewOf: ['t-target'] }), extraTasks: [target] });
    open(row);
    const link = findNodeByAttr(body(row)!, 'data-testid', 'timeline-eval-link');
    expect(link, 'ссылки «оценивает →» внутри нет').not.toBeNull();
    expect(textNodes(link!).join('')).toContain('Целевая задача');
    bubbleClick(link!);
    expect(openTaskCalls).toEqual([{ id: 't-target', projectId: 'p9' }]);
    expect(expanded(row)).toBe('true');
  });
});

// === Ходы (c2) ===

describe('ходы в раскрытой части', () => {
  const TURNS: StageTurn[] = [
    { taskId: TASK.id, from: '2026-10-20T10:00:00Z', to: '2026-10-20T11:00:00Z', outcome: 'success', model: 'qwen3.8-27b' },
    { taskId: TASK.id, from: '2026-10-20T11:10:00Z', to: '2026-10-20T11:40:00Z', outcome: 'cancelled', model: 'qwen3.8-27b' },
    { taskId: TASK.id, from: '2026-10-20T12:10:00Z', to: '2026-10-20T12:40:00Z', outcome: null, model: null },
    { taskId: 'other-task', from: '2026-10-20T01:11:00Z', to: '2026-10-20T01:22:00Z', outcome: 'cancelled', model: null },
  ];

  it('success без скобок, cancelled с «(cancelled)», живой — «идёт»; ходы чужой задачи не показываются', () => {
    const { row } = renderRow({ turns: TURNS });
    open(row);
    const node = findNodeByAttr(body(row)!, 'data-testid', 'timeline-executor-turns');
    expect(node, 'строки ходов нет').not.toBeNull();
    const expected = `ходы: ${localTime(TURNS[0].from)}–${localTime(TURNS[0].to)} · ${localTime(TURNS[1].from)}–${localTime(TURNS[1].to)} (cancelled) · ${localTime(TURNS[2].from)}–идёт`;
    expect(textNodes(node!).join('')).toBe(expected);
    expect(textNodes(node!).join('')).not.toContain('(success)');
    expect(textNodes(node!).join('')).not.toContain(`${localTime(TURNS[3].from)}–${localTime(TURNS[3].to)}`);
  });

  it('без turns строки ходов нет; исход null у закрытого хода — без скобок', () => {
    const plain = renderRow();
    open(plain.row);
    expect(findNodeByAttr(body(plain.row)!, 'data-testid', 'timeline-executor-turns')).toBeNull();

    const nullOut: StageTurn[] = [{ taskId: TASK.id, from: '2026-10-20T10:00:00Z', to: '2026-10-20T11:00:00Z', outcome: null, model: null }];
    const { row } = renderRow({ turns: nullOut });
    open(row);
    const t = textNodes(findNodeByAttr(body(row)!, 'data-testid', 'timeline-executor-turns')!).join('');
    expect(t).toBe(`ходы: ${localTime(nullOut[0].from)}–${localTime(nullOut[0].to)}`);
  });
});

// === 360 (c3) ===

describe('360: чип модели и «оценивает →» переезжают в раскрытую часть', () => {
  it('в свёрнутой строке на 360 их нет; в раскрытой — чип и ссылка есть', () => {
    const target: FeatureTask = { id: 't-target', title: 'Целевая задача', projectId: 'p9' };
    const { row } = renderRow({ isMobile: true, x: mkExec({ reviewOf: ['t-target'] }), extraTasks: [target] });
    expect(findNodeByAttr(row, 'data-testid', 'timeline-model-chip'), 'чип модели в свёрнутой строке на 360').toBeNull();
    expect(findNodeByAttr(row, 'data-testid', 'timeline-eval-line'), '«оценивает →» в свёрнутой строке на 360').toBeNull();
    open(row);
    const b = body(row)!;
    const chip = findNodeByAttr(b, 'data-testid', 'timeline-model-chip');
    expect(chip, 'чипа модели внутри нет').not.toBeNull();
    expect(textNodes(chip!).join('')).toBe('Opus');
    expect(findNodeByAttr(b, 'data-testid', 'timeline-eval-link')).not.toBeNull();
  });
});
