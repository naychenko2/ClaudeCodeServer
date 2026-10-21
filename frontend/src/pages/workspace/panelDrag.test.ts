// S2: пока панель тащат, body несёт data-panel-drag — CSS-правило (index.css,
// рядом с правилами зон) глушит pointer-events у iframe панели плагина, иначе
// iframe глотает dragover/drop хоста и цель сброса не подсвечивается.
//
// Живёт атрибут ровно столько, сколько drag-состояние: ставится в startPanelDrag,
// снимается в endPanelDrag. dragend — единственный выход из drag'а (после drop
// и при отмене Esc), поэтому снятие на нём покрывает оба случая.
//
// Мутация «не снимать атрибут на отмене»: убрать setBodyDragAttr(false) из
// endPanelDrag → второй тест красный.
//
// Окружение node: fakeDom даёт document.body (setAttribute/getAttribute).

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { installFakeDom, mount, uninstallFakeDom } from '../../components/chat/__tests__/fakeDom';
import { endPanelDrag, startPanelDrag, usePanelDragState } from './panelDrag';
import { usePanelDnd } from './zoneGestures';

beforeEach(() => { installFakeDom(); });
afterAll(() => { uninstallFakeDom(); });

describe('data-panel-drag на body (S2: drag поверх iframe панели плагина)', () => {
  it('на старте перетаскивания атрибут есть', () => {
    startPanelDrag('tasks', 'left');
    expect(document.body.getAttribute('data-panel-drag')).toBe('1');
  });

  it('на dragend (в т.ч. отмена Esc) атрибут снят', () => {
    startPanelDrag('tasks', 'right', true);
    // Esc: браузер шлёт только dragend — зона уходит через endPanelDrag
    endPanelDrag();
    expect(document.body.getAttribute('data-panel-drag')).toBeNull();
  });
});

// Критичный дефект R-A: dragstart откладывает startPanelDrag на rAF, а при отменённом
// drag'е dragend приходит РАНЬШЕ кадра. Раньше endPanelDrag выходил (состояние IDLE),
// затем rAF ставил data-panel-drag — и снимать было некому (iframe панели не кликается).
// Токен в zoneGestures делает порядок неважным.
//
// Мутация: убрать `dragTokenRef.current++` из onDragEnd → тест красный.
describe('zoneGestures: dragend раньше отложенного startPanelDrag (R-A, critical 2)', () => {
  it('dragend до rAF-кадра не оставляет data-panel-drag и from', () => {
    const frames: Array<() => void> = [];
    vi.stubGlobal('requestAnimationFrame', (cb: () => void) => { frames.push(cb); return frames.length; });
    let props: ReturnType<ReturnType<typeof usePanelDnd>['dragSourceProps']> | null = null;
    let state: ReturnType<typeof usePanelDragState> | null = null;
    function Probe() {
      const dnd = usePanelDnd({ zone: 'left', enabled: true, onSwap: () => {} });
      props = dnd.dragSourceProps('tasks');
      state = usePanelDragState();
      return createElement('div');
    }
    mount(createElement(Probe));

    const dt = { effectAllowed: '', setData: () => {} };
    // dragstart → (кадр ещё не наступил) → dragend → кадр
    (props!.onDragStart as (e: unknown) => void)({ dataTransfer: dt, currentTarget: null });
    (props!.onDragEnd as () => void)();
    frames.splice(0).forEach(cb => cb());

    expect(document.body.getAttribute('data-panel-drag')).toBeNull();
    expect(state!.from).toBeNull();
    vi.unstubAllGlobals();
  });
});
