import { beforeEach, describe, expect, it, vi } from 'vitest';

// «Панель следует за выбором» (image-editor-v4-panel-proposal.md): клик по карточке
// переключает только открытую панель, выбор агента её не двигает. Окружение node: window —
// приёмник событий revealWorkspacePanel
const dispatched: { type: string; detail: unknown }[] = [];
vi.stubGlobal('window', { dispatchEvent: (e: Event) => { dispatched.push({ type: e.type, detail: (e as CustomEvent).detail }); return true; } });

const { REVEAL_PANEL_EVENT } = await import('./subsystems/registryCore');
const { __resetGenPanelOpen, followPeeked, holdGenPanelOpen, openGenPanel } = await import('./genPanelOpen');
const {
  followHost, followSelection, isCardPick,
} = await import('./genPanelFollow');

const reveals = () => dispatched.filter(d => d.type === REVEAL_PANEL_EVENT).map(d => d.detail);

beforeEach(() => {
  dispatched.length = 0;
  __resetGenPanelOpen();
});

describe('правило 1: клик по карточке', () => {
  it('закрытая панель не открывается — запроса показа нет вовсе', () => {
    expect(followSelection('chatContext', 's1', 'image:t1')).toBe(false);
    expect(reveals()).toEqual([]);
  });

  it('открытая колонка получает повторный показ с target — выбранным элементом', () => {
    const off = holdGenPanelOpen('chatContext', 'column');
    expect(followSelection('chatContext', 's1', 'image:t1')).toBe(true);
    expect(reveals()).toEqual([{ key: 'chatContext', tab: 'settings', sessionId: 's1', target: 'image:t1', follow: true }]);
    off();
    expect(openGenPanel()).toBeNull();
  });

  it('опущенная шторка не поднимается: новая шторка рождается опущенной', () => {
    holdGenPanelOpen('chatContext', 'peek');
    followSelection('chatContext', 's1', 'sound:t1');
    expect(reveals()).toEqual([expect.objectContaining({ key: 'chatContext', follow: true, peek: true })]);
    expect(followPeeked('chatContext')).toBe(true);
    expect(followPeeked('files')).toBe(false);
  });

  it('поднятая шторка остаётся поднятой', () => {
    holdGenPanelOpen('chatContext', 'sheet');
    followSelection('chatContext', 's1', 'sound:t1');
    expect(followPeeked('chatContext')).toBe(false);
  });

  it('снятие прежней панели не стирает отметку новой, смонтированной раньше её ухода', () => {
    const offOld = holdGenPanelOpen('chatContext', 'column');
    holdGenPanelOpen('voices', 'column');
    offOld();
    expect(openGenPanel()).toEqual({ key: 'voices', view: 'column' });
  });

  it('панель генерации одна — заменять некого; «Файлы» и уже открытая не трогаются', () => {
    expect(followHost(['files', 'chatContext'], 'chatContext')).toBeNull();
    expect(followHost(['files'], 'chatContext')).toBeNull();
    expect(followHost(['chatContext'], 'files')).toBeNull();
  });

  it('клик по кнопке карточки — не выбор, по самой карточке — выбор', () => {
    const card = { contains: (x: unknown) => x === btn || x === body };
    const btn = { closest: () => btn };
    const body = { closest: () => null };
    expect(isCardPick(btn as unknown as EventTarget, card as unknown as Element)).toBe(false);
    expect(isCardPick(body as unknown as EventTarget, card as unknown as Element)).toBe(true);
  });

  it('клик из портала (элемент вне DOM карточки) — не выбор карточки', () => {
    const card = { contains: () => false };
    const menuItem = { closest: () => menuItem };
    expect(isCardPick(menuItem as unknown as EventTarget, card as unknown as Element)).toBe(false);
  });
});
