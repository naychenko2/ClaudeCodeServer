// Карточка плана хода (TodoPlanView) — три вида по решению Андрея 20.10:
// 1. Живая + фичи → короткая строка (без слов состояния фичи, есть «Подробнее»).
// 2. Живая + без фич → полный чек-лист (прежний вид).
// 3. Прошлые → строка-снимок, раскрытие по клику (aria-expanded).
//
// Окружение node (без jsdom): SSR-рендер для структуры + fake-DOM для кликов.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { act } from 'react';
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TodoItem } from '../../../hooks/useSessionArtifacts';
import type { FeatureRun } from '../../../types/featureRun';
import { __test } from '../../../lib/featureRuns';
import { ChatSessionContext } from '../contexts';
import { findNodeByAttr, installFakeDom, mount, reactProps, textNodes, uninstallFakeDom } from './fakeDom';
import { TodoPlanViewForTest } from '../ChatItemView';

// Мобила — управляемый флаг.
const { mobileFlag } = vi.hoisted(() => ({ mobileFlag: { value: false } }));
vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => mobileFlag.value };
});

// useFeatureRunsSnapshot — управляемый: возвращаем нужные runs.
const { runsFlag } = vi.hoisted(() => ({ runsFlag: { value: [] as FeatureRun[] } }));
vi.mock('../../../lib/featureRuns', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/featureRuns')>();
  return {
    ...orig,
    useFeatureRunsSnapshot: () => runsFlag.value,
  };
});

const SESSION_ID = 'sess-test';

function wrap(node: React.ReactElement) {
  return createElement(ChatSessionContext.Provider, { value: SESSION_ID }, node);
}

beforeEach(() => {
  __test.reset();
  mobileFlag.value = false;
  runsFlag.value = [];
});
afterEach(() => {
  __test.reset();
  mobileFlag.value = false;
  runsFlag.value = [];
});

// ── Фикстуры ──────────────────────────────────────────────────────────────────

function todos(over: Partial<TodoItem>[] = []): TodoItem[] {
  return [
    { content: 'Шаг A', status: 'completed' },
    { content: 'Шаг B', status: 'in_progress', activeForm: 'делаю B' },
    { content: 'Шаг C', status: 'pending' },
    ...over,
  ];
}

function runFixture(id: string): FeatureRun {
  return {
    featureId: id, state: 'in_progress', steps: [], reviews: [], qa: [],
    final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
  };
}

// ── 1. Живая + фичи: короткая строка ─────────────────────────────────────────

describe('TodoPlanView: живая + фичи → короткая строка', () => {
  it('содержит «План хода», «N из M», «Подробнее»; НЕ содержит слов состояния фичи', () => {
    runsFlag.value = [runFixture('feat-1')];
    const html = renderToStaticMarkup(
      wrap(createElement(TodoPlanViewForTest, {
        todos: todos(),
        isMobile: false,
        isCurrentPlan: true,
        ts: 1700000000000,
      })),
    );
    // Есть: заголовок, прогресс, «Подробнее»
    expect(html).toContain('План хода');
    expect(html).toContain('1 из 3');
    expect(html).toContain('Подробнее');
    // Нет: слов состояния фичи (ModeSwitcher/FeaturePicker/FeatureCardFooter)
    expect(html).not.toContain('Исполнение фичи');
    expect(html).not.toContain('Задачи фичи в трекере');
  });

  it('«сейчас:» показывает in_progress пункт (activeForm)', () => {
    runsFlag.value = [runFixture('feat-1')];
    const html = renderToStaticMarkup(
      wrap(createElement(TodoPlanViewForTest, {
        todos: todos(),
        isMobile: false,
        isCurrentPlan: true,
        ts: 1700000000000,
      })),
    );
    expect(html).toContain('сейчас: делаю B');
  });

  it('клик по «Подробнее» вызывает openPillMenu', () => {
    installFakeDom();
    runsFlag.value = [runFixture('feat-1')];
    const { root, container } = mount(
      wrap(createElement(TodoPlanViewForTest, {
        todos: todos(),
        isMobile: false,
        isCurrentPlan: true,
        ts: 1700000000000,
      })),
    );
    const btn = findNodeByAttr(container, 'title', 'Подробнее');
    expect(btn, 'кнопка «Подробнее» найдена').not.toBeNull();
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(__test.pillMenuFor(SESSION_ID), 'сигнал openPillMenu').not.toBeNull();
    root.unmount();
    uninstallFakeDom();
  });
});

// ── 2. Живая + без фич: полный чек-лист (прежний вид) ────────────────────────

describe('TodoPlanView: живая + без фич → полный чек-лист', () => {
  it('содержит шапку «План хода» + N/M + все пункты списка', () => {
    runsFlag.value = [];
    const html = renderToStaticMarkup(
      wrap(createElement(TodoPlanViewForTest, {
        todos: todos(),
        isMobile: false,
        isCurrentPlan: true,
        ts: 1700000000000,
      })),
    );
    expect(html).toContain('План хода');
    expect(html).toContain('1/3');
    // Все три пункта списка
    expect(html).toContain('Шаг A');
    expect(html).toContain('делаю B');
    expect(html).toContain('Шаг C');
    // Нет «Подробнее» (это не короткая карточка)
    expect(html).not.toContain('Подробнее');
  });
});

// ── 3. Прошлая карточка: строка-снимок ───────────────────────────────────────

describe('TodoPlanView: прошлая → строка-снимок', () => {
  it('содержит «План хода», N/M, «снимок»; aria-expanded=false', () => {
    runsFlag.value = [runFixture('feat-1')];
    const html = renderToStaticMarkup(
      wrap(createElement(TodoPlanViewForTest, {
        todos: todos(),
        isMobile: false,
        isCurrentPlan: false,
        ts: 1700000000000,
      })),
    );
    expect(html).toContain('План хода');
    expect(html).toContain('1/3');
    expect(html).toContain('снимок');
    expect(html).toContain('aria-expanded="false"');
  });

  it('клик раскрывает чек-лист (aria-expanded=true, появляются пункты)', () => {
    installFakeDom();
    runsFlag.value = [runFixture('feat-1')];
    const { root, container } = mount(
      wrap(createElement(TodoPlanViewForTest, {
        todos: todos(),
        isMobile: false,
        isCurrentPlan: false,
        ts: 1700000000000,
      })),
    );
    // Ищем button с aria-expanded="false"
    const btn = findNodeByAttr(container, 'aria-expanded', 'false');
    expect(btn, 'кнопка-снимок найдена').not.toBeNull();
    act(() => (reactProps(btn!)!.onClick as () => void)());
    // После раскрытия — aria-expanded=true
    const expanded = findNodeByAttr(container, 'aria-expanded', 'true');
    expect(expanded, 'aria-expanded=true после клика').not.toBeNull();
    root.unmount();
    uninstallFakeDom();
  });
});
