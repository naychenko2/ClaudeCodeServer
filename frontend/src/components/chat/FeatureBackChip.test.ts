// Чип возврата «← к фиче «{название}» · {вкладка}» в шапке чата (волна 3, Ф9b).
//
// Рендер настоящего компонента + настоящие сторы (featureBackNav, featurePage): вкладку
// запоминает recordFeatureReturn В МОМЕНТ перехода (читает стор страницы), чип показывает её
// название, клик возвращает на ту же вкладку и открывает страницу (cc-open-feature).
//
// Мутации (в отчёте):
//   (1) подменить название вкладки в чипе (всегда «Хронология») → таблица ниже краснеет;
//   (2) возврат ведёт не на ту вкладку (не ставить fromTab / ставить 'timeline') → «возврат» краснеет;
//   (3) recordFeatureReturn не запоминает вкладку → «вкладка в момент перехода» краснеет.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { FeatureBackChip } from './FeatureBackChip';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from './__tests__/fakeDom';
import { __test as backNav } from '../../lib/featureBackNav';
import { __test as pageStore, setFeaturePageTab, type FeaturePageTab } from '../../lib/featurePage';

// Ожидаемые подписи — литералы (не из словаря кода), иначе подмена словаря не краснела бы.
const CHIP_TEXT: Record<FeaturePageTab, string> = {
  timeline: 'Хронология', plans: 'Планы', chats: 'Чаты', tasks: 'Задачи', events: 'События', stats: 'Статистика',
};
const TABS = Object.keys(CHIP_TEXT) as FeaturePageTab[];

const roots: import('react-dom/client').Root[] = [];
function render(sessionId: string) {
  installFakeDom();
  const { root, container } = mount(createElement(FeatureBackChip, { sessionId }));
  roots.push(root);
  return container;
}
function dispatched(): { type: string; detail: unknown }[] {
  const log: { type: string; detail: unknown }[] = [];
  (globalThis as { window: { dispatchEvent: (e: Event) => boolean } }).window.dispatchEvent =
    (e: Event) => { log.push({ type: e.type, detail: (e as CustomEvent).detail }); return true; };
  return log;
}

beforeEach(() => { backNav.reset(); pageStore.reset(); installFakeDom(); });
afterEach(() => {
  for (const r of roots) act(() => { r.unmount(); });
  roots.length = 0;
  uninstallFakeDom();
});

describe('FeatureBackChip: название вкладки', () => {
  for (const tab of TABS) {
    it(`пришли с вкладки «${CHIP_TEXT[tab]}» → «к фиче «Моя фича» · ${CHIP_TEXT[tab]}»`, () => {
      setFeaturePageTab(tab);
      backNav.recordFeatureReturn('f1', { kind: 'chat', id: 'c1' }, 'Моя фича');
      const c = render('c1');
      const chip = findNodeByAttr(c, 'data-testid', 'feature-back-chip');
      expect(chip, 'чипа нет').not.toBeNull();
      expect(textNodes(chip!).join('')).toBe(`к фиче «Моя фича» · ${CHIP_TEXT[tab]}`);
    });
  }

  it('aria-label несёт название фичи и вкладки (полное имя «Планы и макеты»)', () => {
    setFeaturePageTab('plans');
    backNav.recordFeatureReturn('f1', { kind: 'chat', id: 'c1' }, 'Моя фича');
    const c = render('c1');
    const btn = findNodeByAttr(c, 'role', 'button')!;
    expect(btn.getAttribute('aria-label')).toBe('К фиче «Моя фича», вкладка «Планы и макеты»');
  });

  it('вкладка запоминается В МОМЕНТ перехода: потом сменили — чип показывает прежнюю', () => {
    setFeaturePageTab('stats');
    backNav.recordFeatureReturn('f1', { kind: 'chat', id: 'c1' }, 'Моя фича');
    setFeaturePageTab('events');
    const c = render('c1');
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'feature-back-chip')!).join('')).toContain('· Статистика');
  });

  it('без названия фичи: «к фиче · Чаты»', () => {
    setFeaturePageTab('chats');
    backNav.recordFeatureReturn('f1', { kind: 'chat', id: 'c1' }, null);
    const c = render('c1');
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'feature-back-chip')!).join('')).toBe('к фиче · Чаты');
  });

  it('перехода из фичи не было, либо открыт другой чат → чипа нет', () => {
    const c0 = render('c1');
    expect(findNodeByAttr(c0, 'data-testid', 'feature-back-chip')).toBeNull();
    setFeaturePageTab('chats');
    backNav.recordFeatureReturn('f1', { kind: 'chat', id: 'c1' }, 'Моя фича');
    const c1 = render('другой-чат');
    expect(findNodeByAttr(c1, 'data-testid', 'feature-back-chip')).toBeNull();
  });
});

describe('FeatureBackChip: возврат на ту же вкладку', () => {
  for (const tab of TABS) {
    it(`клик возвращает на вкладку «${CHIP_TEXT[tab]}» и открывает страницу фичи`, () => {
      setFeaturePageTab(tab);
      backNav.recordFeatureReturn('f1', { kind: 'chat', id: 'c1' }, 'Моя фича');
      const c = render('c1');
      // Пока страница была скрыта, вкладку сменили на другую.
      setFeaturePageTab(tab === 'timeline' ? 'events' : 'timeline');
      expect(pageStore.tab()).not.toBe(tab);

      const log = dispatched();
      const btn = findNodeByAttr(c, 'role', 'button')!;
      act(() => { (reactProps(btn)!.onClick as () => void)(); });

      expect(pageStore.tab(), 'вернулись не на ту вкладку').toBe(tab);
      const ev = log.find(d => d.type === 'cc-open-feature');
      expect(ev, 'cc-open-feature не диспатчен').toBeDefined();
      expect(ev!.detail).toEqual({ featureId: 'f1', fromChat: false });
    });
  }
});
