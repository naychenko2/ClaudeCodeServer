// Карточка «Задача создана» (TaskCreatedView) — F9: чип «Шаг N фичи» — КНОПКА,
// открывающая план фичи на этом шаге (макет v1 п. 5).
//
// Окружение node (без jsdom): SSR-рендер (присутствие/отсутствие чипа, текст, геометрия)
// + fake-DOM (паттерн FeatureRunView.test.ts) для кликов: stopPropagation (карточка —
// ссылка на задачу, чип — нет), сигнал фокуса в сторе Ф1, меню пилюли без живой карточки.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { act } from 'react';
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ChatItem, Task } from '../../../types';
import { TaskCreatedView } from '../TaskCreatedView';
import { setAllFlags } from '../../../lib/featureFlags';
import { __test } from '../../../lib/featureRuns';
import { ChatSessionContext } from '../contexts';
import { findNodeByAttr, installFakeDom, mount, reactProps, textNodes, uninstallFakeDom } from './fakeDom';

type ToolItem = Extract<ChatItem, { kind: 'tool_use' }>;
// fakeDom не экспортирует FNode — тип узла берём из сигнатуры findNodeByAttr
type FNode = Parameters<typeof findNodeByAttr>[0];

const FLAG = 'turn-plan-feature-mode';

// Мобила — управляемый флаг (SSR window не видит; useIsMobile задаёт minHeight чипа 28/40).
const { mobileFlag } = vi.hoisted(() => ({ mobileFlag: { value: false } }));
vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => mobileFlag.value };
});
// lib/tasks — spy openTaskInSection: клик по чипу (stopPropagation) задачу НЕ открывает,
// клик по карточке — открывает.
const { openTaskSpy } = vi.hoisted(() => ({ openTaskSpy: vi.fn() }));
vi.mock('../../../lib/tasks', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/tasks')>();
  return { ...orig, openTaskInSection: openTaskSpy };
});
// lib/personas — ensurePersonasLoaded в node-окружении трогает localStorage и сеть
// (joinUserGroup/reloadPersonas) — в тесте эффект не нужен, stub.
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, ensurePersonasLoaded: () => Promise.resolve() };
});

beforeEach(() => {
  __test.reset();
  mobileFlag.value = false;
  setAllFlags({ [FLAG]: true });
  openTaskSpy.mockClear();
});
afterEach(() => {
  __test.reset();
  setAllFlags({ [FLAG]: false });
});

// === Фикстуры ===

function task(over: Partial<Task> = {}): Task {
  return {
    id: 'task-1', title: 'Шаг D1', description: '', status: 'todo', priority: 'medium',
    linkedFiles: [], subtasks: [], labels: [], order: 1, createdAt: '', updatedAt: '',
    featureId: 'feat-1', stepRef: 'D1', lane: 'back',
    ...over,
  } as Task;
}

function item(t: Task | null, over: Partial<ToolItem> = {}): ToolItem {
  return {
    kind: 'tool_use',
    id: 't1',
    name: 'mcp__tasks__tasks_create',
    input: { title: 'Шаг D1' },
    result: t ? JSON.stringify(t) : undefined,
    ...over,
  } as ToolItem;
}

const render = (i: ToolItem) =>
  renderToStaticMarkup(createElement(TaskCreatedView, { item: i, online: true }));

// === Присутствие чипа: флаг + featureId + stepRef ===

describe('F9: чип «Шаг N фичи» — присутствие', () => {
  it('флаг выключен — чипа нет (даже с featureId/stepRef)', () => {
    setAllFlags({ [FLAG]: false });
    const html = render(item(task()));
    expect(html).not.toContain('Шаг D1 фичи');
  });

  it('нет featureId — чипа нет (Major 3 Глеба: не врать про неразмеченные задачи)', () => {
    const html = render(item(task({ featureId: null })));
    expect(html).not.toContain('Шаг D1 фичи');
  });

  it('нет stepRef — чипа нет', () => {
    const html = render(item(task({ stepRef: null })));
    expect(html).not.toContain('Шаг D1 фичи');
  });

  it('флаг + featureId + stepRef — чип есть', () => {
    expect(render(item(task()))).toContain('Шаг D1 фичи');
  });
});

// === Текст: линия из task.lane (нормализация back/бэк, front/фронт) ===

describe('F9: чип — текст с линией', () => {
  it('lane=back → «Шаг D1 фичи · Производство, бэк»', () => {
    expect(render(item(task({ lane: 'back' })))).toContain('Шаг D1 фичи · Производство, бэк');
  });

  it('lane=бэк (рус.) → та же подпись (нормализация)', () => {
    expect(render(item(task({ lane: 'бэк' })))).toContain('Шаг D1 фичи · Производство, бэк');
  });

  it('lane=front → «· Производство, фронт»', () => {
    expect(render(item(task({ lane: 'front' })))).toContain('Шаг D1 фичи · Производство, фронт');
  });

  it('линии нет (null) → «· Производство» без суффикса', () => {
    const html = render(item(task({ lane: null })));
    expect(html).toContain('Шаг D1 фичи · Производство');
    expect(html).not.toContain('Производство,');
  });

  it('линия другая (тест) → без суффикса', () => {
    const html = render(item(task({ lane: 'тест' })));
    expect(html).toContain('Шаг D1 фичи · Производство');
    expect(html).not.toContain('Производство,');
  });
});

// === Геометрия: minHeight 28 (десктоп) / 40 (мобила, TB.iconHitMobile) ===

describe('F9: чип — геометрия', () => {
  it('десктоп: minHeight 28, padding 3/9, gap 6, margin-top 11, R.md, рамка borderLight', () => {
    const html = render(item(task()));
    // Кнопка чипа (Button xs + override): проверяем сериализованные инлайн-стили
    expect(html).toContain('min-height:28px');
    expect(html).toContain('padding:3px 9px');
    expect(html).toContain('gap:6px');
    expect(html).toContain('margin-top:11px');
    expect(html).toContain('border-radius:8px');
    expect(html).toContain('var(--c-border-light)');
    expect(html).toContain('var(--c-bg-card)');
  });

  it('мобила: minHeight 40 (TB.iconHitMobile)', () => {
    mobileFlag.value = true;
    const html = render(item(task()));
    expect(html).toContain('min-height:40px');
  });

  it('иконка Route (lucide) C.accent вместо точки ●', () => {
    const html = render(item(task()));
    // lucide Route — svg с path; цвет — C.accent (var(--c-accent))
    expect(html).toContain('var(--c-accent)');
    expect(html).not.toContain('●');
  });
});

// === Клик: stopPropagation + сигнал фокуса + меню пилюли ===

describe('F9: чип — клик', () => {
  beforeAll(() => {
    installFakeDom();
    // Клик откладывает решение «открыть меню пилюли» на requestAnimationFrame (Глеб: гонка
    // hasPlanCard). Node-окружение rAF не знает — стаб синхронный (выполняет колбэк сразу).
    (globalThis as { requestAnimationFrame?: unknown }).requestAnimationFrame = (fn: () => void) => { fn(); return 1; };
    (globalThis as { cancelAnimationFrame?: unknown }).cancelAnimationFrame = () => {};
  });
  afterAll(() => { uninstallFakeDom(); });

  function mountChip(t: Task, sessionId: string | null) {
    const node = createElement(
      ChatSessionContext.Provider,
      { value: sessionId },
      createElement(TaskCreatedView, { item: item(t), online: true }),
    );
    return mount(node);
  }

  // Симуляция bubbling реального клика: обработчики от цели вверх по дереву,
  // остановка, если цель вызвала stopPropagation.
  function clickBubbling(container: FNode, target: FNode) {
    let stopped = false;
    const e = { stopPropagation: () => { stopped = true; } };
    for (let n: FNode | null = target; n && !stopped && n !== container; n = n.parentNode as FNode | null) {
      const props = reactProps(n);
      if (props?.onClick) (props.onClick as (ev: unknown) => void)(e);
    }
  }

  it('клик: focusFeatureStep(sessionId, featureId, taskId) и задача НЕ открывается (stopPropagation)', () => {
    const { root, container } = mountChip(task(), 'sess-1');
    const chip = findNodeByAttr(container, 'title', 'Открыть план фичи на этом шаге');
    expect(chip, 'кнопка чипа').not.toBeNull();
    // Реальный клик: обработчики от цели к корню, пока не вызван stopPropagation.
    // Прямой вызов обработчика чипа в fake-DOM до карточки не доходит — без
    // симуляции bubbling мутация «убрать stopPropagation» была бы невидима.
    act(() => clickBubbling(container, chip!));
    // Сигнал фокуса в сторе Ф1: featureId + taskId из задачи
    const focus = __test.focusFor('sess-1');
    expect(focus, 'сигнал фокуса поставлен').not.toBeNull();
    expect(focus!.featureId).toBe('feat-1');
    expect(focus!.taskId).toBe('task-1');
    // stopPropagation: карточка-ссылка задачу не открыла
    expect(openTaskSpy, 'задача не открыта (клик по чипу)').not.toHaveBeenCalled();
    root.unmount();
  });

  it('клик по КАРТОЧКЕ (не чипу) — задача открывается', () => {
    const { root, container } = mountChip(task(), 'sess-1');
    // Корневая div карточки — первый элемент дерева с onClick
    const card = container.childNodes[0];
    expect(reactProps(card!), 'у карточки есть onClick').not.toBeNull();
    act(() => (reactProps(card!)!.onClick as () => void)());
    expect(openTaskSpy, 'карточка открыла задачу').toHaveBeenCalledTimes(1);
    root.unmount();
  });

  it('без живой карточки плана (hasPlanCard=false) — клик открывает меню пилюли', () => {
    __test.setHasPlanCard('sess-1', false);
    const { root, container } = mountChip(task(), 'sess-1');
    const chip = findNodeByAttr(container, 'title', 'Открыть план фичи на этом шаге');
    act(() => (reactProps(chip!)!.onClick as (e: { stopPropagation: () => void }) => void)({ stopPropagation: () => {} }));
    expect(__test.pillMenuFor('sess-1'), 'сигнал «открыть меню пилюли»').not.toBeNull();
    root.unmount();
  });

  it('живая карточка плана есть (hasPlanCard=true) — меню пилюли НЕ открывается', () => {
    __test.setHasPlanCard('sess-1', true);
    const { root, container } = mountChip(task(), 'sess-1');
    const chip = findNodeByAttr(container, 'title', 'Открыть план фичи на этом шаге');
    act(() => (reactProps(chip!)!.onClick as (e: { stopPropagation: () => void }) => void)({ stopPropagation: () => {} }));
    expect(__test.pillMenuFor('sess-1'), 'меню пилюли не зовут').toBeNull();
    // но сигнал фокуса на месте — его потребит живая карточка
    expect(__test.focusFor('sess-1')).not.toBeNull();
    root.unmount();
  });

  it('сигнал другого чата не доходит (изоляция по sessionId)', () => {
    const { root, container } = mountChip(task(), 'sess-A');
    const chip = findNodeByAttr(container, 'title', 'Открыть план фичи на этом шаге');
    act(() => (reactProps(chip!)!.onClick as (e: { stopPropagation: () => void }) => void)({ stopPropagation: () => {} }));
    expect(__test.focusFor('sess-A')).not.toBeNull();
    expect(__test.focusFor('sess-B'), 'чужой чат сигнала не получил').toBeNull();
    root.unmount();
  });
});

// === Hover/фокус: подложка C.bgSelected + текст C.textPrimary, кольцо SHADOW.focus ===

describe('F9: чип — hover и фокус', () => {
  beforeAll(() => { installFakeDom(); });
  afterAll(() => { uninstallFakeDom(); });

  it('hover: background C.bgSelected, color C.textPrimary; blur/hover-off — обратно', () => {
    const node = createElement(
      ChatSessionContext.Provider,
      { value: 'sess-1' },
      createElement(TaskCreatedView, { item: item(task()), online: true }),
    );
    const { root, container } = mount(node);
    const chip = findNodeByAttr(container, 'title', 'Открыть план фичи на этом шаге');
    expect(chip, 'кнопка чипа').not.toBeNull();
    // Обёртка span (родитель кнопки) несёт mouse/focus-обработчики (Button их не форвардит)
    const spanProps = reactProps(chip!.parentNode as never);
    expect(spanProps, 'у обёртки есть onMouseEnter').not.toBeNull();
    act(() => (spanProps!.onMouseEnter as () => void)());
    expect(chip!.style.background, 'hover — подложка bgSelected').toBe('var(--c-bg-selected)');
    expect(chip!.style.color, 'hover — текст textPrimary').toBe('var(--c-text-primary)');
    act(() => (spanProps!.onMouseLeave as () => void)());
    expect(chip!.style.background, 'с hover — обратно bgCard').toBe('var(--c-bg-card)');
    root.unmount();
  });

  it('фокус: boxShadow SHADOW.focus; blur — none', () => {
    const node = createElement(
      ChatSessionContext.Provider,
      { value: 'sess-1' },
      createElement(TaskCreatedView, { item: item(task()), online: true }),
    );
    const { root, container } = mount(node);
    const chip = findNodeByAttr(container, 'title', 'Открыть план фичи на этом шаге');
    const spanProps = reactProps(chip!.parentNode as never);
    act(() => (spanProps!.onFocus as () => void)());
    expect(chip!.style.boxShadow, 'фокус — кольцо SHADOW.focus').toBe('var(--shadow-focus)');
    act(() => (spanProps!.onBlur as () => void)());
    expect(chip!.style.boxShadow, 'blur — без кольца').toBe('none');
    root.unmount();
  });
});
