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
// minor 1: ЕДИНЫЙ словарь FEATURE_TAB_LABEL — чип и полоса не разъезжаются, «Планы и макеты»
// (короткого «Планы» в чипе больше нет).
const CHIP_TEXT: Record<FeaturePageTab, string> = {
  timeline: 'Хронология', plans: 'Планы и макеты', chats: 'Чаты', tasks: 'Задачи', events: 'События', stats: 'Статистика',
};
const TABS = Object.keys(CHIP_TEXT) as FeaturePageTab[];

const roots: import('react-dom/client').Root[] = [];
function render(sessionId: string) {
  installFakeDom();
  const { root, container } = mount(createElement(FeatureBackChip, { sessionId, projectId: 'p1' }));
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

// major 1: из попапа/панели/шторки (showTab=false) — чип БЕЗ вкладки, но возврат
// ведёт на последнюю использованную вкладку.
describe('FeatureBackChip: без вкладки (попап/панель/шторка, major 1)', () => {
  it('showTab=false → чип «к фиче «Моя фича»» БЕЗ вкладки; aria-label тоже без вкладки', () => {
    setFeaturePageTab('chats');
    backNav.recordFeatureReturn('f1', { kind: 'chat', id: 'c1' }, 'Моя фича', undefined, false);
    const c = render('c1');
    const chip = findNodeByAttr(c, 'data-testid', 'feature-back-chip')!;
    expect(chip, 'чипа нет').not.toBeNull();
    expect(textNodes(chip).join(''), 'видимый текст без вкладки').toBe('к фиче «Моя фича»');
    expect(findNodeByAttr(c, 'data-testid', 'feature-back-tab'), 'вкладка в чипе').toBeNull();
    const btn = findNodeByAttr(c, 'role', 'button')!;
    expect(btn.getAttribute('aria-label'), 'aria-label без вкладки').toBe('К фиче «Моя фича»');
  });
});

// minor 4: длинное название — обрезка с многоточием; полное название в title/aria-label.
describe('FeatureBackChip: обрезка (minor 4)', () => {
  it('title несёт полное название (фича + вкладка) — для длинного названия', () => {
    setFeaturePageTab('chats');
    backNav.recordFeatureReturn('f1', { kind: 'chat', id: 'c1' }, 'Очень длинное название фичи для обрезки');
    const c = render('c1');
    const btn = findNodeByAttr(c, 'role', 'button')!;
    expect(btn.getAttribute('title')).toBe('К фиче «Очень длинное название фичи для обрезки», вкладка «Чаты»');
  });
});

// minor 5: один указатель — повторный переход из другой вкладки стирает возврат у
// прежнего чата (в новом чате чип с новой вкладкой, в прежнем чипа нет).
describe('FeatureBackChip: один указатель (minor 5)', () => {
  it('повторный переход из другой вкладки стирает возврат у прежнего чата', () => {
    setFeaturePageTab('chats');
    backNav.recordFeatureReturn('f1', { kind: 'chat', id: 'c1' }, 'Моя фича');
    setFeaturePageTab('stats');
    backNav.recordFeatureReturn('f1', { kind: 'chat', id: 'c2' }, 'Моя фича');
    // Чат c1: чипа нет (указатель переписан на c2).
    const c1 = render('c1');
    expect(findNodeByAttr(c1, 'data-testid', 'feature-back-chip'), 'чип у c1 (должен исчезнуть)').toBeNull();
    // Чат c2: чип с новой вкладкой («Статистика»).
    const c2 = render('c2');
    const chip = findNodeByAttr(c2, 'data-testid', 'feature-back-chip');
    expect(chip, 'чип у c2').not.toBeNull();
    expect(textNodes(chip!).join(''), 'вкладка c2').toContain('· Статистика');
  });
});

// Ф9b-д3: вне проекта (раздел «Чаты») воркспейса нет — cc-open-feature никто не принимает
// (handled не выставлен), и клик открывает проект фичи запасным событием для App.
describe('FeatureBackChip: вне проекта', () => {
  const click = (c: unknown) => {
    const btn = findNodeByAttr(c as never, 'role', 'button')!;
    act(() => { (reactProps(btn)!.onClick as () => void)(); });
  };
  it('событие не принято + есть projectId → cc-open-project-feature { projectId, featureId }', () => {
    backNav.recordFeatureReturn('f1', { kind: 'chat', id: 'c1' }, 'Моя фича');
    installFakeDom();
    const { root, container } = mount(createElement(FeatureBackChip, { sessionId: 'c1', projectId: 'p1' }));
    roots.push(root);
    const log = dispatched();
    click(container);
    const ev = log.find(d => d.type === 'cc-open-project-feature');
    expect(ev, 'кнопка мертва: запасной переход не отправлен').toBeDefined();
    expect(ev!.detail).toEqual({ projectId: 'p1', featureId: 'f1', chatId: 'c1' });
  });
  // Личный чат (нет проекта): вести некуда — чип не рисуем (иначе мёртвая кнопка).
  // Мутация «рисовать чип при пустом projectId» краснит оба утверждения.
  for (const projectId of [undefined, null, '']) {
    it(`projectId=${JSON.stringify(projectId)} → чипа нет`, () => {
      backNav.recordFeatureReturn('f1', { kind: 'chat', id: 'c1' }, 'Моя фича');
      installFakeDom();
      const { root, container } = mount(createElement(FeatureBackChip, { sessionId: 'c1', projectId }));
      roots.push(root);
      expect(findNodeByAttr(container, 'data-testid', 'feature-back-chip'), 'мёртвый чип нарисован').toBeNull();
      expect(findNodeByAttr(container, 'role', 'button'), 'мёртвая кнопка нарисована').toBeNull();
    });
  }
  it('воркспейс принял событие (handled) → запасного перехода нет', () => {
    backNav.recordFeatureReturn('f1', { kind: 'chat', id: 'c1' }, 'Моя фича');
    installFakeDom();
    const { root, container } = mount(createElement(FeatureBackChip, { sessionId: 'c1', projectId: 'p1' }));
    roots.push(root);
    const log: string[] = [];
    (globalThis as { window: { dispatchEvent: (e: Event) => boolean } }).window.dispatchEvent = (e: Event) => {
      log.push(e.type);
      if (e.type === 'cc-open-feature') (e as CustomEvent).detail.handled = true;
      return true;
    };
    click(container);
    expect(log).toEqual(['cc-open-feature']);
  });
});
