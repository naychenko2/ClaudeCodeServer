// S2b-F5: тулбар доски в общем проекте — сегмент «Исполнитель» Все · Я · AI · Участники
// и выбор участника чипом (стор boardControls). В личном проекте и хабе (people нет) —
// состав и снимок как на master. Рендер — настоящий BoardToolbar поверх linkedom
// (тот же путь, что в tasksPanelRender.test.ts: select тулбара требует настоящего DOM);
// состояние стора читаем пробом на том же useBoardControls, что читает тулбар.

import { afterEach, beforeEach, describe, it, expect } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';

// boardControls читает localStorage при загрузке модуля — стабим ДО динамического импорта
(globalThis as { localStorage?: unknown }).localStorage = {
  getItem: () => null, setItem: () => {}, removeItem: () => {},
};

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
Object.assign(win, {
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
});
// порядок важен: модули тулбара читают window/document при загрузке
Object.assign(globalThis, {
  window: win, document: doc,
  HTMLElement: win.HTMLElement, Node: win.Node, Event: win.Event,
  ResizeObserver: class { observe() {} unobserve() {} disconnect() {} },
});
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const { BoardToolbar } = await import('./BoardToolbar');
const { useBoardControls, setAssigneeFilter } = await import('../../../lib/boardControls');
import type { AssigneePersonOption } from '../TasksListFilter';

const PEOPLE: AssigneePersonOption[] = [
  { userId: 'u-mary', name: 'Мария' },
  { userId: 'u-igor', name: 'Игорь' },
];

// Проб состояния стора: то же состояние, что у тулбара (общий синглтон)
function StoreProbe() {
  const c = useBoardControls();
  return createElement('span', { 'data-testid': 'store-probe' }, `${c.assignee}|${c.assigneeUserId ?? ''}`);
}

function mountToolbar(people?: AssigneePersonOption[]) {
  const root = createRoot(doc.getElementById('root') as unknown as HTMLElement);
  act(() => {
    root.render(createElement('div', null,
      createElement(BoardToolbar, { layout: 'sidebar', groupOptions: ['none', 'assignee'] as never, people }),
      createElement(StoreProbe),
    ));
  });
  return root;
}

const buttonByText = (text: string) =>
  [...doc.querySelectorAll('button')].find(b => (b.textContent ?? '').includes(text)) ?? null;
const probeText = () => (doc.querySelector('[data-testid="store-probe"]')?.textContent ?? '');

describe('Тулбар доски: «Исполнитель» в общем проекте', () => {
  beforeEach(() => { setAssigneeFilter('all'); });
  afterEach(() => { setAssigneeFilter('all'); });

  // sidebar layout — сегмент и чипы в одном блоке (тот же код, что inline/mobile)
  it('с people: «Все · Я · AI · Участники»; клик «Участники» ставит member без id', async () => {
    const root = mountToolbar(PEOPLE);
    expect(buttonByText('Все'), '«Все» есть').toBeTruthy();
    expect(buttonByText('Я'), '«Я» есть').toBeTruthy();
    expect(buttonByText('AI'), 'AI есть').toBeTruthy();
    const btn = buttonByText('Участники');
    expect(btn, 'кнопка «Участники» есть').toBeTruthy();
    await act(async () => { (btn as unknown as HTMLElement).click(); });
    expect(probeText(), 'стор: member без выбранного').toBe('member|');
    await act(async () => { root.unmount(); });
  });

  it('под «Участники» — чипы; клик по чипу пишет id в стор', async () => {
    setAssigneeFilter('member');
    const root = mountToolbar(PEOPLE);
    const chip = buttonByText('Мария');
    expect(chip, 'чип «Мария» раскрыт').toBeTruthy();
    await act(async () => { (chip as unknown as HTMLElement).click(); });
    expect(probeText(), 'стор: member + id участника').toBe('member|u-mary');
    await act(async () => { root.unmount(); });
  });

  it('без people (личный/хаб) — снимок как на master: «Участников» нет', () => {
    const root = mountToolbar();
    expect(buttonByText('Все'), '«Все» есть').toBeTruthy();
    expect(buttonByText('Я'), '«Я» есть').toBeTruthy();
    expect(buttonByText('AI'), 'AI есть').toBeTruthy();
    expect(buttonByText('Участники'), 'без people «Участников» нет').toBeFalsy();
    act(() => { root.unmount(); });
  });
});
