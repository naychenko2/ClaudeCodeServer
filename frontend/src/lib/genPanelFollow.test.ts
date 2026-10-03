import { beforeEach, describe, expect, it, vi } from 'vitest';

// «Панель следует за выбором» (image-editor-v4-panel-proposal.md): клик по карточке
// переключает только открытую панель, выбор агента её не двигает. Окружение node: window —
// приёмник событий revealWorkspacePanel
const dispatched: { type: string; detail: unknown }[] = [];
vi.stubGlobal('window', { dispatchEvent: (e: Event) => { dispatched.push({ type: e.type, detail: (e as CustomEvent).detail }); return true; } });

const { REVEAL_PANEL_EVENT } = await import('./subsystems/registryCore');
const { __resetGenPanelOpen, followPeeked, holdGenPanelOpen, openGenPanel } = await import('./genPanelOpen');
const {
  __resetAgentPicks, agentPickSlot, dropAgentPick, dropAgentPickOf, followHost, followSelection, getAgentPick, isCardPick, noteAgentPick,
} = await import('./genPanelFollow');

const reveals = () => dispatched.filter(d => d.type === REVEAL_PANEL_EVENT).map(d => d.detail);

beforeEach(() => {
  dispatched.length = 0;
  __resetGenPanelOpen();
  __resetAgentPicks();
});

describe('правило 1: клик по карточке', () => {
  it('закрытая панель не открывается — запроса показа нет вовсе', () => {
    expect(followSelection('images', 's1', 'images:t1')).toBe(false);
    expect(reveals()).toEqual([]);
  });

  it('открытая колонка переключается на раздел и вкладку карточки, target — выбранный элемент', () => {
    const off = holdGenPanelOpen('sound', 'column');
    expect(followSelection('images', 's1', 'images:t1')).toBe(true);
    expect(reveals()).toEqual([{ key: 'images', tab: 'settings', sessionId: 's1', target: 'images:t1', follow: true }]);
    off();
    expect(openGenPanel()).toBeNull();
  });

  it('опущенная шторка не поднимается: новая шторка рождается опущенной', () => {
    holdGenPanelOpen('images', 'peek');
    followSelection('sound', 's1', 'sound:t1');
    expect(reveals()).toEqual([expect.objectContaining({ key: 'sound', follow: true, peek: true })]);
    expect(followPeeked('sound')).toBe(true);
    expect(followPeeked('images')).toBe(false);
  });

  it('поднятая шторка остаётся поднятой', () => {
    holdGenPanelOpen('images', 'sheet');
    followSelection('sound', 's1', 'sound:t1');
    expect(followPeeked('sound')).toBe(false);
  });

  it('снятие прежней панели не стирает отметку новой, смонтированной раньше её ухода', () => {
    const offOld = holdGenPanelOpen('images', 'column');
    holdGenPanelOpen('sound', 'column');
    offOld();
    expect(openGenPanel()).toEqual({ key: 'sound', view: 'column' });
  });

  it('хост ставит новую панель на место открытой соперницы; «Файлы» и уже открытая — не трогаются', () => {
    expect(followHost(['files', 'images'], 'sound')).toBe('images');
    expect(followHost(['files'], 'sound')).toBeNull();
    expect(followHost(['sound'], 'sound')).toBeNull();
    expect(followHost(['images'], 'files')).toBeNull();
  });

  it('клик по кнопке карточки — не выбор, по самой карточке — выбор', () => {
    const card = { contains: (x: unknown) => x === btn };
    const btn = { closest: () => btn };
    const body = { closest: () => null };
    expect(isCardPick(btn as unknown as EventTarget, card as unknown as Element)).toBe(false);
    expect(isCardPick(body as unknown as EventTarget, card as unknown as Element)).toBe(true);
  });
});

describe('правило 2: выбор агентом', () => {
  it('подсказка видна в панели другого раздела, в своей — нет; «Открыть» переключает открытую панель', () => {
    noteAgentPick('s1', { panelKey: 'sound', target: 'sound:t2', label: 'song.mp3', tab: 'settings' });
    expect(reveals()).toEqual([]);
    expect(agentPickSlot('s1', 'sound')).toBeUndefined();
    const slot = agentPickSlot('s1', 'images');
    expect(slot?.label).toBe('song.mp3');
    holdGenPanelOpen('images', 'column');
    slot!.onOpen();
    expect(reveals()).toEqual([expect.objectContaining({ key: 'sound', target: 'sound:t2', follow: true })]);
    expect(getAgentPick('s1')).toBeNull();
  });

  it('✕ прячет; выбор человеком того же элемента убирает, другого — нет', () => {
    noteAgentPick('s1', { panelKey: 'sound', target: 'sound:t2', label: 'song.mp3' });
    followSelection('sound', 's1', 'sound:t9');
    expect(getAgentPick('s1')).not.toBeNull();
    followSelection('sound', 's1', 'sound:t2');
    expect(getAgentPick('s1')).toBeNull();
    noteAgentPick('s1', { panelKey: 'sound', target: 'sound:t2', label: 'song.mp3' });
    agentPickSlot('s1', 'images')!.onDismiss();
    expect(getAgentPick('s1')).toBeNull();
  });

  it('агент снял выбор в своём разделе — подсказка о нём уходит, о чужом — остаётся', () => {
    noteAgentPick('s1', { panelKey: 'sound', target: 'sound:t2', label: 'song.mp3' });
    dropAgentPickOf('s1', 'images');
    expect(getAgentPick('s1')).not.toBeNull();
    dropAgentPickOf('s1', 'sound');
    expect(getAgentPick('s1')).toBeNull();
    dropAgentPick('s1');
  });
});

describe('followHost при флаге composer-context-row', () => {
  it('панель одна — заменять некого; старые ключи вне набора', async () => {
    const { FLAGS, setAllFlags } = await import('./featureFlags');
    setAllFlags({ [FLAGS.composerContextRow]: true });
    try {
      expect(followHost(['files', 'chatContext'], 'chatContext')).toBeNull();
      expect(followHost(['files', 'images'], 'chatContext')).toBeNull();
      expect(followHost(['files', 'images'], 'sound')).toBeNull();
    } finally {
      setAllFlags({});
    }
  });
});
