// Пилюля «План хода» (TurnPlanPill) — F9: одноразовый сигнал «открыть меню» (стор Ф1, per chat).
// Чип «Шаг N фичи» (TaskCreatedView) шлёт его, когда в ленте нет живой карточки плана:
// пилюля потребляет сигнал (seq) и открывает меню. Повторный сигнал (seq растёт) —
// меню открывается заново; сигнал другого чата (sessionId) пилюлю не трогает.
//
// Окружение node (без jsdom): fake-DOM (паттерн FeatureRunView.test.ts). Меню в anchor-режиме
// рисуется ПОРТАЛОМ в document.body — поэтому «меню открыто» проверяем текстом «План хода»
// в дереве body, а не в контейнере.
import { createElement } from 'react';
import { act } from 'react';
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { TurnPlanPill } from './TurnPlanPill';
import { __test, openPillMenu } from '../../lib/featureRuns';
import type { TodoItem } from '../../hooks/useSessionArtifacts';
import { findNodeByAttr, installFakeDom, mount, reactProps, uninstallFakeDom } from './__tests__/fakeDom';

const zeroRect = { top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0, x: 0, y: 0 };

function todos(): TodoItem[] {
  return [{ content: 'Шаг плана', status: 'pending' }];
}

describe('TurnPlanPill: F9 (сигнал «открыть меню»)', () => {
  beforeAll(() => {
    installFakeDom();
    // Меню в anchor-режиме читает window.innerWidth/innerHeight (расчёт позиции) —
    // фейковому window их нет, подставляем нули (позиция в тесте не важна).
    const w = (globalThis as { window: Record<string, unknown> }).window;
    w.innerWidth = 1280;
    w.innerHeight = 800;
  });
  afterAll(() => { uninstallFakeDom(); });
  beforeEach(() => { __test.reset(); });

  function mountPill(sessionId: string) {
    const node = createElement(TurnPlanPill, { todos: todos(), runs: [], sessionId });
    return mount(node);
  }

  // Меню-портал живёт в document.body, не в контейнере. Ищем по role="menu" (Глеб:
  // подстрока «План хода» могла зеленеть ложно, если этот текст есть в body вне открытого меню).
  function bodyHasMenu(): boolean {
    const doc = (globalThis as { document: { body: unknown } }).document as { body: Parameters<typeof findNodeByAttr>[0] };
    return findNodeByAttr(doc.body, 'role', 'menu') !== null;
  }

  it('сигнал: меню открывается (появляется в body-портале)', () => {
    const { root } = mountPill('sess-1');
    expect(bodyHasMenu(), 'до сигнала меню нет').toBe(false);
    act(() => { openPillMenu('sess-1'); });
    expect(bodyHasMenu(), 'после сигнала меню открыто').toBe(true);
    root.unmount();
  });

  it('повторный сигнал (seq растёт): закрытое меню открывается заново', () => {
    const { root, container } = mountPill('sess-1');
    act(() => { openPillMenu('sess-1'); });
    expect(bodyHasMenu(), 'первый сигнал: меню открыто').toBe(true);
    // Пользователь закрывает меню кликом по самой пилюле
    const btn = findNodeByAttr(container, 'aria-label', 'Показать план хода');
    expect(btn, 'кнопка-пилюля').not.toBeNull();
    act(() => (reactProps(btn!)!.onClick as (e: unknown) => void)({ currentTarget: { getBoundingClientRect: () => zeroRect } }));
    expect(bodyHasMenu(), 'после клика меню закрыто').toBe(false);
    // Повторный клик по чипу — новый сигнал (seq 2): меню открывается снова
    act(() => { openPillMenu('sess-1'); });
    expect(bodyHasMenu(), 'повторный сигнал: меню открыто снова').toBe(true);
    root.unmount();
  });

  it('сигнал другого чата (sessionId) пилюлю не трогает', () => {
    const { root } = mountPill('sess-A');
    act(() => { openPillMenu('sess-B'); });
    expect(bodyHasMenu(), 'сигнал чата B меню чата A не открыл').toBe(false);
    root.unmount();
  });
});
