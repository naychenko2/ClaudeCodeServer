// S2b-F5: фильтр «Исполнитель» в общем проекте — Все · Я · AI · Участники.
// «Я» = задачи, где исполнитель — текущий пользователь (assigneeUserId = мой id,
// либо старый 'me' без id у владельца — К9). «Участники» открывает выбор участника.
// В личном проекте (без контекста/без people) — поведение и снимок как на master.
//
// Логика — applyTaskFilters (чистая функция); UI — рендер FilterSections поверх fakeDom
// (тот же путь, что в TasksListFilter.test.ts): кликаем по раскрытому DOM, сверяем onFilters.

import { act, createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Task, TaskAssignee } from '../../../types';
import {
  applyTaskFilters, EMPTY_TASK_FILTERS, FilterSections, matchTaskAssignee,
  type AssigneePersonOption, type TaskListFilters,
} from '../TasksListFilter';
import { installFakeDom, mount, reactProps, uninstallFakeDom } from '../../../components/chat/__tests__/fakeDom';

// === Фикстуры задач (по форме К9: человек — 'me' + assigneeUserId) ===

let seq = 0;
function mk(p: Partial<Task> = {}): Task {
  seq++;
  return {
    id: `t${seq}`, title: `Задача ${seq}`, description: '',
    status: 'todo', priority: 'medium', linkedFiles: [], subtasks: [], labels: [],
    order: 0, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    ...p,
  };
}

const ME = 'u-me';
const MARY = 'u-mary';
// своя новая (по id) / своя старая (Me без id — владельца) / чужая человеческая / AI / без исполнителя
const mineNew = mk({ assignee: 'me' as TaskAssignee, assigneeUserId: ME });
const mineOld = mk({ assignee: 'me' as TaskAssignee });
const theirs = mk({ assignee: 'me' as TaskAssignee, assigneeUserId: MARY });
const ai = mk({ assignee: 'claude' as TaskAssignee });
const none = mk();
const ALL = [mineNew, mineOld, theirs, ai, none];

const ownerCtx = { currentUserId: ME, isOwner: true };
const memberCtx = { currentUserId: MARY, isOwner: false };

const ids = (ts: Task[]) => ts.map(t => t.id);

describe('Фильтр «Исполнитель» общего проекта: логика (applyTaskFilters)', () => {
  it('Я у владельца — свои по id и старые Me без id; чужие и AI выпадают', () => {
    const got = applyTaskFilters(ALL, { ...EMPTY_TASK_FILTERS, assignee: 'me' }, ownerCtx);
    expect(ids(got)).toEqual([mineNew.id, mineOld.id]);
  });

  it('Я у участника — только свои по id; старый Me (это задача владельца) не моё', () => {
    const got = applyTaskFilters(ALL, { ...EMPTY_TASK_FILTERS, assignee: 'me' }, memberCtx);
    expect(ids(got)).toEqual([theirs.id]);
  });

  it('Участники: без выбранного — любой человек с id; с выбранным — только он', () => {
    const anyPerson = applyTaskFilters(ALL, { ...EMPTY_TASK_FILTERS, assignee: 'member' }, ownerCtx);
    expect(ids(anyPerson)).toEqual([mineNew.id, theirs.id]);
    const one = applyTaskFilters(ALL, { ...EMPTY_TASK_FILTERS, assignee: 'member', assigneeUserId: MARY }, ownerCtx);
    expect(ids(one)).toEqual([theirs.id]);
  });

  it('AI и Все — по-прежнему по assignee (claude) / без фильтра', () => {
    expect(ids(applyTaskFilters(ALL, { ...EMPTY_TASK_FILTERS, assignee: 'claude' }, ownerCtx))).toEqual([ai.id]);
    expect(applyTaskFilters(ALL, EMPTY_TASK_FILTERS, ownerCtx)).toBe(ALL);
  });

  it('личное поведение без контекста — как на master: Я = t.assignee === \'me\'', () => {
    expect(ids(applyTaskFilters(ALL, { ...EMPTY_TASK_FILTERS, assignee: 'me' }))).toEqual([mineNew.id, mineOld.id, theirs.id]);
    expect(matchTaskAssignee(mineNew, 'me', null, undefined)).toBe(true);
  });

});

// === UI: сегмент «Исполнитель» с «Участниками» (общий проект) и без них (личный) ===

type AnyNode = {
  nodeType: number; nodeName: string; nodeValue?: string;
  childNodes: AnyNode[]; style: Record<string, string>;
};
function textOf(n: AnyNode, out: string[] = []): string[] {
  if (n.nodeType === 3) out.push(n.nodeValue ?? '');
  for (const c of n.childNodes) textOf(c, out);
  return out;
}
function findButtonByText(container: AnyNode, text: string): AnyNode | null {
  let hit: AnyNode | null = null;
  const walk = (x: AnyNode) => {
    if (hit) return;
    if (x.nodeType === 1 && x.nodeName === 'BUTTON' && textOf(x).join(' ').includes(text)) { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return hit;
}

const PEOPLE: AssigneePersonOption[] = [
  { userId: MARY, name: 'Мария' },
  { userId: 'u-old', name: 'Игорь' },
];

function mountFilters(initial: TaskListFilters, people?: AssigneePersonOption[], mobile = false) {
  const onFilters = vi.fn();
  const { root, container } = mount(
    createElement(FilterSections, { filters: initial, onFilters, mobile, people }),
  );
  return { onFilters, root, container };
}

describe('Фильтр «Исполнитель»: общий проект даёт «Все · Я · AI · Участники»', () => {
  beforeEach(() => installFakeDom());
  afterEach(() => uninstallFakeDom());

  it('с participants (people) сегмент содержит «Участники»; клик открывает выбор участника', () => {
    const { onFilters, root, container } = mountFilters(EMPTY_TASK_FILTERS, PEOPLE);
    const btn = findButtonByText(container, 'Участники');
    expect(btn, 'кнопка «Участники» есть в общем проекте').not.toBeNull();
    expect(btn!.nodeName).toBe('BUTTON');
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(onFilters).toHaveBeenCalledWith({ ...EMPTY_TASK_FILTERS, assignee: 'member', assigneeUserId: null });
    root.unmount();
  });

  it('под «Участники» — чипы участников; клик по чипу отдаёт его id в onFilters', () => {
    const { onFilters, root, container } = mountFilters(
      { ...EMPTY_TASK_FILTERS, assignee: 'member', assigneeUserId: null }, PEOPLE,
    );
    const chip = findButtonByText(container, 'Мария');
    expect(chip, 'чип участника «Мария» раскрыт').not.toBeNull();
    expect(chip!.nodeName).toBe('BUTTON');
    act(() => (reactProps(chip!)!.onClick as () => void)());
    expect(onFilters).toHaveBeenCalledWith({ ...EMPTY_TASK_FILTERS, assignee: 'member', assigneeUserId: MARY });
    root.unmount();
  });

  it('без people (личный проект) — снимок как на master: Все · Я · AI, «Участников» нет', () => {
    const { onFilters, root, container } = mountFilters(EMPTY_TASK_FILTERS);
    expect(findButtonByText(container, 'Все')).not.toBeNull();
    expect(findButtonByText(container, 'Я')).not.toBeNull();
    expect(findButtonByText(container, 'AI')).not.toBeNull();
    expect(findButtonByText(container, 'Участники'), 'в личном проекте «Участников» нет').toBeNull();
    // прежнее поведение сегмента: клик «Я» — без поля участника
    const meBtn = findButtonByText(container, 'Я')!;
    act(() => (reactProps(meBtn)!.onClick as () => void)());
    expect(onFilters).toHaveBeenCalledWith({ ...EMPTY_TASK_FILTERS, assignee: 'me', assigneeUserId: null });
    root.unmount();
  });

  it('M1: на мобиле чипы участников — тач-цель 40px', () => {
    const { root, container } = mountFilters(
      { ...EMPTY_TASK_FILTERS, assignee: 'member', assigneeUserId: null }, PEOPLE, true,
    );
    const chip = findButtonByText(container, 'Мария');
    expect(chip, 'чип участника (мобила) найден').not.toBeNull();
    expect(chip!.style.minHeight, 'чип участника (мобила): minHeight 40px').toBe('40px');
    root.unmount();
  });
});
