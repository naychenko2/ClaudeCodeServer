import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { afterChatSwitcherClosed, createSwitcherController, HISTORY_FLAG, type SwitcherWindow } from './chatSwitcherHistory';

// Подменный window: стек записей истории и асинхронный back(), как в браузере —
// popstate приходит следующей задачей, а не внутри вызова
function fakeWindow(initial: unknown) {
  const target = new EventTarget();
  const entries: unknown[] = [initial];
  let idx = 0;
  let backCalls = 0;
  const win: SwitcherWindow = {
    history: {
      get state() { return entries[idx]; },
      back() {
        backCalls++;
        setTimeout(() => {
          if (idx > 0) idx--;
          target.dispatchEvent(new Event('popstate'));
        }, 0);
      },
      pushState(s: unknown) {
        entries.splice(idx + 1);
        entries.push(s);
        idx++;
      },
    },
    location: { href: '#/project/A/chat/X' },
    addEventListener: (type, fn, opts) => target.addEventListener(type, fn, opts),
    removeEventListener: (type, fn) => target.removeEventListener(type, fn),
  };
  return {
    win,
    get backCalls() { return backCalls; },
    get top() { return entries[idx]; },
    get depth() { return idx + 1; },
  };
}

// Таймеры подменные: popstate от back() и отложенный за ним переход прогоняются
// явно, без реальных задержек
beforeEach(() => { vi.useFakeTimers(); });
afterEach(() => { vi.runAllTimers(); vi.useRealTimers(); });
const settle = () => { vi.runAllTimers(); };

const X = { screen: 'project', chatId: 'X' };

function setup() {
  const fw = fakeWindow(X);
  const opened: string[] = [];
  let closed = 0;
  const ctl = createSwitcherController<string>(fw.win, {
    onClose: () => { closed++; },
    open: id => { opened.push(id); },
  });
  const unmount = ctl.mount();
  return { fw, ctl, opened, unmount, get closed() { return closed; } };
}

describe('шторка чатов: проводка истории', () => {
  it('открытие кладёт запись с флагом поверх текущей', () => {
    const t = setup();
    expect(t.fw.depth).toBe(2);
    expect((t.fw.top as Record<string, unknown>)[HISTORY_FLAG]).toBe(true);
    t.unmount();
  });

  it('выбор чата: ровно один back() и один переход — после снятия записи шторки', () => {
    const t = setup();
    t.ctl.pick('Y', false);
    expect(t.opened).toEqual([]); // до popstate переход не идёт
    settle();
    expect(t.fw.backCalls).toBe(1);
    expect(t.opened).toEqual(['Y']);
    expect(t.closed).toBe(1);
    expect(t.fw.top).toBe(X); // дубля записи между прошлым и новым чатом нет
    t.unmount();
  });

  it('двойной тап по строкам: всё ещё один back() и переход в первый выбранный', () => {
    const t = setup();
    t.ctl.pick('Y', false);
    t.ctl.pick('Z', false);
    settle();
    expect(t.fw.backCalls).toBe(1);
    expect(t.opened).toEqual(['Y']);
    expect(t.fw.top).toBe(X); // запись X не потеряна вторым back()
    t.unmount();
  });

  it('страница под шторкой размонтирует её на том же popstate — переход всё равно идёт', () => {
    const fw = fakeWindow(X);
    const opened: string[] = [];
    let unmount = () => {};
    // Слушатель страницы стоит раньше шторки (как popstate ChatsPage) и снимает её —
    // так ведёт себя синхронный рендер React между слушателями дискретного события
    fw.win.addEventListener('popstate', () => unmount());
    const ctl = createSwitcherController<string>(fw.win, { onClose: () => {}, open: id => { opened.push(id); } });
    unmount = ctl.mount();
    ctl.pick('Y', false);
    settle();
    expect(fw.backCalls).toBe(1);
    expect(opened).toEqual(['Y']);
  });

  it('повторное закрытие и тап по строке после закрытия — один back()', () => {
    const t = setup();
    t.ctl.close();
    t.ctl.close();
    t.ctl.pick('Y', false);
    settle();
    expect(t.fw.backCalls).toBe(1);
    expect(t.opened).toEqual([]);
    t.unmount();
  });

  it('перезапись записи при открытой шторке ждёт снятия её записи', () => {
    const t = setup();
    const done: unknown[] = [];
    afterChatSwitcherClosed(() => done.push(t.fw.top), t.fw.win);
    expect(done).toEqual([]);
    settle();
    expect(t.fw.backCalls).toBe(1);
    expect(done).toEqual([X]); // fn увидел запись под шторкой, а не запись шторки
    expect(t.closed).toBe(1);
    t.unmount();
  });

  it('без шторки перезапись идёт сразу', () => {
    const fw = fakeWindow(X);
    let ran = false;
    afterChatSwitcherClosed(() => { ran = true; }, fw.win);
    expect(ran).toBe(true);
    expect(fw.backCalls).toBe(0);
  });
});
