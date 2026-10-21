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

import { afterAll, beforeEach, describe, expect, it } from 'vitest';
import { installFakeDom, uninstallFakeDom } from '../../components/chat/__tests__/fakeDom';
import { endPanelDrag, startPanelDrag } from './panelDrag';

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
