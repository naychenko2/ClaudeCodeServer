// Рендер-тест фильтров списка задач (FilterSections): чи́пы-мультивыбор — общий
// ui/Chip variant="toggle" (волна 2 «Обзор фич», Ф3.1). Клик по чипу переключает
// группу фильтра: статус / приоритет / срок. «Исполнитель» — отдельный сегмент, не чип.
//
// Окружение node (без jsdom): fakeDom даёт document/window, mount + reactProps — клик.
// «По тексту и роли»: ищем чип по подписи и проверяем, что это настоящий <button>
// (роль button), а не span; затем кликаем и сверяем onFilters.

import { act, createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { EMPTY_TASK_FILTERS, FilterSections, type TaskListFilters } from './TasksListFilter';
import { STATUS_LABEL, PRIORITY_LABEL } from '../../lib/tasks';
import { installFakeDom, mount, reactProps, uninstallFakeDom } from '../../components/chat/__tests__/fakeDom';

// fakeDom не экспортирует FNode — тип узла берём из сигнатуры reactProps.
type AnyNode = {
  nodeType: number; nodeName: string; nodeValue?: string;
  childNodes: AnyNode[]; style: Record<string, string>;
};
// Полный текст элемента (все текстовые потомки, склеенные пробелом).
function textOf(n: AnyNode, out: string[] = []): string[] {
  if (n.nodeType === 3) out.push(n.nodeValue ?? '');
  for (const c of n.childNodes) textOf(c, out);
  return out;
}
// Кнопка (роль button) по содержащейся в ней подписи.
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

function mountFilters(initial: TaskListFilters, mobile = false) {
  const onFilters = vi.fn();
  const { root, container } = mount(
    createElement(FilterSections, { filters: initial, onFilters, mobile }),
  );
  return { onFilters, root, container };
}

describe('FilterSections: клик по ui/Chip переключает фильтр', () => {
  beforeEach(() => installFakeDom());
  afterEach(() => uninstallFakeDom());

  it('статус: клик «В работе» — чип button, onFilters получает status=[inProgress]', () => {
    const { onFilters, root, container } = mountFilters(EMPTY_TASK_FILTERS);
    const btn = findButtonByText(container, STATUS_LABEL.inProgress);
    expect(btn, 'чип «В работе» найден').not.toBeNull();
    expect(btn!.nodeName, 'роль — настоящий button').toBe('BUTTON');
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(onFilters, 'клик вызвал onFilters').toHaveBeenCalledTimes(1);
    expect(onFilters).toHaveBeenCalledWith({ ...EMPTY_TASK_FILTERS, status: ['inProgress'] });
    root.unmount();
  });

  it('статус: повторный клик снимает фильтр (status=[])', () => {
    const { onFilters, root, container } = mountFilters({ ...EMPTY_TASK_FILTERS, status: ['inProgress'] });
    const btn = findButtonByText(container, STATUS_LABEL.inProgress);
    expect(btn, 'чип «В работе» найден').not.toBeNull();
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(onFilters).toHaveBeenCalledWith({ ...EMPTY_TASK_FILTERS, status: [] });
    root.unmount();
  });

  it('приоритет: клик «Срочно» — onFilters получает priorities=[urgent]', () => {
    const { onFilters, root, container } = mountFilters(EMPTY_TASK_FILTERS);
    const btn = findButtonByText(container, PRIORITY_LABEL.urgent);
    expect(btn, 'чип «Срочно» найден').not.toBeNull();
    expect(btn!.nodeName).toBe('BUTTON');
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(onFilters).toHaveBeenCalledWith({ ...EMPTY_TASK_FILTERS, priorities: ['urgent'] });
    root.unmount();
  });

  it('срок: клик «Сегодня» — onFilters получает due=[today]', () => {
    const { onFilters, root, container } = mountFilters(EMPTY_TASK_FILTERS);
    const btn = findButtonByText(container, 'Сегодня');
    expect(btn, 'чип «Сегодня» найден').not.toBeNull();
    expect(btn!.nodeName).toBe('BUTTON');
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(onFilters).toHaveBeenCalledWith({ ...EMPTY_TASK_FILTERS, due: ['today'] });
    root.unmount();
  });

  it('мультивыбор (OR): «К выполнению» при уже активном «В работе» — оба в onFilters', () => {
    // controlled: filters-prop не меняется между кликами, поэтому «накопление» проверяем
    // от уже активного статуса — клик добавляет второй, а не заменяет
    const { onFilters, root, container } = mountFilters({ ...EMPTY_TASK_FILTERS, status: ['inProgress'] });
    const btn = findButtonByText(container, STATUS_LABEL.todo);
    expect(btn, 'чип «К выполнению» найден').not.toBeNull();
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(onFilters).toHaveBeenCalledWith({ ...EMPTY_TASK_FILTERS, status: ['inProgress', 'todo'] });
    root.unmount();
  });

  it('M1: на мобиле у чипов minHeight 40px, на десктопе — нет', () => {
    const labels = [STATUS_LABEL.inProgress, PRIORITY_LABEL.urgent, 'Сегодня'];
    // Мобила: minHeight 40px (тач-цель по таблице токенов §4 / guidelines п.4)
    {
      const { root, container } = mountFilters(EMPTY_TASK_FILTERS, true);
      for (const label of labels) {
        const btn = findButtonByText(container, label);
        expect(btn, `чип «${label}» (мобила) найден`).not.toBeNull();
        expect(btn!.style.minHeight, `чип «${label}» (мобила): minHeight 40px`).toBe('40px');
      }
      root.unmount();
    }
    // Десктоп: minHeight не задан (высота по padding+контенту)
    {
      const { root, container } = mountFilters(EMPTY_TASK_FILTERS, false);
      for (const label of labels) {
        const btn = findButtonByText(container, label);
        expect(btn, `чип «${label}» (десктоп) найден`).not.toBeNull();
        expect(btn!.style.minHeight, `чип «${label}» (десктоп): minHeight не задан`).toBeUndefined();
      }
      root.unmount();
    }
  });

  it('m1: точка на включённом чипе — currentColor, на выключенном — свой цвет', () => {
    // «В работе» (inProgress) включён: точка currentColor (onAccent на accent-заливке)
    {
      const { root, container } = mountFilters({ ...EMPTY_TASK_FILTERS, status: ['inProgress'] });
      const btn = findButtonByText(container, STATUS_LABEL.inProgress);
      expect(btn, 'чип «В работе» (включён) найден').not.toBeNull();
      const dot = btn!.childNodes[0];
      expect(dot.nodeName, 'точка — span').toBe('SPAN');
      expect(dot.style.background, 'включённый чип: точка currentColor (видна на accent)').toBe('currentColor');
      root.unmount();
    }
    // «В работе» выключен: точка var(--c-accent) (STATUS_DOT.inProgress)
    {
      const { root, container } = mountFilters(EMPTY_TASK_FILTERS);
      const btn = findButtonByText(container, STATUS_LABEL.inProgress);
      expect(btn, 'чип «В работе» (выключен) найден').not.toBeNull();
      const dot = btn!.childNodes[0];
      expect(dot.style.background, 'выключенный чип: точка var(--c-accent)').toBe('var(--c-accent)');
      root.unmount();
    }
  });
});
