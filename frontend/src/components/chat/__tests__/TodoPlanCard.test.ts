// Карточка плана хода (TodoPlanView) — решение Андрея 23.10: в чате с фичей живой
// карточки нет вовсе (статус даёт баннер), остаются только снимки прошлых планов:
// 1. Живая + фичи → ничего.
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

// ── 1. Живая + фичи: живой карточки нет ──────────────────────────────────────

describe('TodoPlanView: живая + фичи → ничего (решение Андрея 23.10)', () => {
  it('с фичей живая карточка не рендерится вовсе', () => {
    runsFlag.value = [runFixture('feat-1')];
    const html = renderToStaticMarkup(
      wrap(createElement(TodoPlanViewForTest, {
        todos: todos(),
        isMobile: false,
        isCurrentPlan: true,
        ts: 1700000000000,
      })),
    );
    // Ни заголовка, ни прогресса, ни «Подробнее» — узла нет.
    expect(html).not.toContain('План хода');
    expect(html).not.toContain('Подробнее');
    expect(html).not.toContain('сейчас:');
  });

  it('прошлая карточка в том же чате с фичей остаётся снимком', () => {
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
    expect(html).toContain('снимок');
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
    // Подпись — content, а не activeForm (master 2634fd6d8): активность видна по кружку
    expect(html).toContain('Шаг B');
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

  // R2 minor 9: время снимка «· HH:MM» (макет «· 16:12»). Ожидание — тот же момент ts,
  // отрендеренный в TZ хоста (тот же контракт формата, что у компонента:
  // toLocaleTimeString('ru-RU', {hour,minute})): тест зелёный при любом TZ хоста
  // (DEF-TZ-TIMEOUT: прежняя константа «· 22:13» была зашита под TZ=UTC и краснела под MSK).
  it('время снимка «· HH:MM» — момент ts в TZ хоста', () => {
    runsFlag.value = [runFixture('feat-1')];
    const ts = 1700000000000;
    const expected = new Date(ts).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
    const html = renderToStaticMarkup(
      wrap(createElement(TodoPlanViewForTest, {
        todos: todos(),
        isMobile: false,
        isCurrentPlan: false,
        ts,
      })),
    );
    expect(html, `время снимка не момент ts в TZ хоста (ожидалось «· ${expected}»)`).toContain(`· ${expected}`);
    // Контракт формата: ровно «· HH:MM» (две цифры, 24-часовой) — ловит сломанный формат.
    expect(html, 'формат времени не «· HH:MM»').toMatch(/· \d{2}:\d{2}/);
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
