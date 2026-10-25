// Дефект 4c4645e2: чип возврата «← к фиче · Статистика» на экране открытой задачи.
// Тот же FeatureBackChip, что в шапке чата, и тот же стор featureBackNav: переход
// записывается recordFeatureReturn с {kind:'task'}, панель деталей читает его по id задачи.
//
// Мутации (в отчёте):
//   (1) убрать вызов FeatureBackChip в TaskDetailsPane → «чип на задаче» красный;
//   (2) featureBackNavFromTask вернуть null для kind='task' → тот же тест красный;
//   (3) убрать clearBackNav на unmount панели → «точка гасится при уходе» красная.
import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import type { Project, Task } from '../../../types';
import { TaskDetailsPane } from '../TaskDetailsPane';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../../../components/chat/__tests__/fakeDom';
import { __test as backNav } from '../../../lib/featureBackNav';
import { __test as pageStore, setFeaturePageTab } from '../../../lib/featurePage';

const PROJECT: Project = { id: 'p1', name: 'Проект' } as Project;

function task(over: Partial<Task> = {}): Task {
  return {
    id: 't1', title: 'Собрать отчёт', status: 'todo', priority: 'medium', assignee: 'me',
    projectId: 'p1', linkedFiles: [], subtasks: [], labels: [], order: 0,
    createdAt: '2026-10-25T00:00:00Z', updatedAt: '2026-10-25T00:00:00Z', ...over,
  } as Task;
}

const roots: import('react-dom/client').Root[] = [];
function renderPane(t: Task, project: Project | null) {
  installFakeDom();
  const { root, container } = mount(createElement(TaskDetailsPane, { task: t, project, onDeleted: () => {} }));
  roots.push(root);
  return container;
}

beforeEach(() => { backNav.reset(); pageStore.reset(); installFakeDom(); });
afterEach(() => {
  for (const r of roots) act(() => { r.unmount(); });
  roots.length = 0;
  uninstallFakeDom();
});

describe('Возврат к фиче на экране задачи (дефект 4c4645e2)', () => {
  it('точка возврата записана → чип «к фиче «Фича» · Статистика»', () => {
    setFeaturePageTab('stats');
    backNav.recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    const c = renderPane(task(), PROJECT);
    const chip = findNodeByAttr(c, 'data-testid', 'feature-back-chip');
    expect(chip, 'чипа нет').not.toBeNull();
    expect(textNodes(chip!).join('')).toBe('к фиче «Фича» · Статистика');
  });

  it('без точки возврата чипа нет', () => {
    const c = renderPane(task(), PROJECT);
    expect(findNodeByAttr(c, 'data-testid', 'feature-back-chip')).toBeNull();
  });

  it('точка ведёт в ДРУГУЮ задачу → на этой чипа нет', () => {
    setFeaturePageTab('stats');
    backNav.recordFeatureReturn('f1', { kind: 'task', id: 'other' }, 'Фича');
    const c = renderPane(task(), PROJECT);
    expect(findNodeByAttr(c, 'data-testid', 'feature-back-chip')).toBeNull();
  });

  it('точка ведёт в чат, а не в задачу → на задаче чипа нет', () => {
    setFeaturePageTab('stats');
    backNav.recordFeatureReturn('f1', { kind: 'chat', id: 'c1' }, 'Фича');
    const c = renderPane(task(), PROJECT);
    expect(findNodeByAttr(c, 'data-testid', 'feature-back-chip')).toBeNull();
  });

  it('личная задача (без проекта) → чипа нет, вести некуда', () => {
    setFeaturePageTab('stats');
    backNav.recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    const c = renderPane(task(), null);
    expect(findNodeByAttr(c, 'data-testid', 'feature-back-chip')).toBeNull();
  });

  it('клик возвращает на фичу и на записанную вкладку (cc-open-feature)', () => {
    setFeaturePageTab('stats');
    backNav.recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    const c = renderPane(task(), PROJECT);
    // Пока страница была скрыта, вкладку сменили: клик обязан вернуть записанную.
    setFeaturePageTab('timeline');
    const log: { type: string; detail: unknown }[] = [];
    (globalThis as { window: { dispatchEvent: (e: Event) => boolean } }).window.dispatchEvent =
      (e: Event) => { log.push({ type: e.type, detail: (e as CustomEvent).detail }); return true; };
    const btn = findNodeByAttr(c, 'aria-label', 'К фиче «Фича», вкладка «Статистика»');
    expect(btn, 'кнопки чипа нет').not.toBeNull();
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(pageStore.tab(), 'вернулись не на ту вкладку').toBe('stats');
    const ev = log.find(d => d.type === 'cc-open-feature');
    expect(ev, 'cc-open-feature не диспатчен').toBeDefined();
    expect(ev!.detail).toEqual({ featureId: 'f1', fromChat: false });
  });

  it('уход с задачи (unmount панели) гасит точку, как у чата', () => {
    setFeaturePageTab('stats');
    backNav.recordFeatureReturn('f1', { kind: 'task', id: 't1' }, 'Фича');
    const c = renderPane(task(), PROJECT);
    expect(findNodeByAttr(c, 'data-testid', 'feature-back-chip')).not.toBeNull();
    const root = roots[roots.length - 1];
    roots.pop();
    act(() => { root.unmount(); });
    expect(backNav.getBackNav(), 'точка не гасится при уходе').toBeNull();
  });
});
