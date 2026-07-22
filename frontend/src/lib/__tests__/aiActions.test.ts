import { describe, it, expect, vi } from 'vitest';

// Реестр тянет api/saveToNote (браузерные зависимости) — мокаем, тестируем чистую
// логику ранжирования rankedActions.
vi.mock('../api', () => ({ api: {} }));
vi.mock('../../features/notes/saveToNote', () => ({ openNoteById: () => {} }));

import { rankedActions, type AiActionCtx } from '../ai/actions';

function ctx(partial: Partial<AiActionCtx> & { nav: AiActionCtx['nav'] }): AiActionCtx {
  return { online: true, flag: () => false, caps: { semantic: false }, chat: { active: false, hasMessages: false }, ...partial };
}

describe('rankedActions — доступность и ранжирование', () => {
  it('на «Чатах» без открытого чата видны глобальные действия', () => {
    const r = rankedActions(ctx({ nav: { screen: 'chats' } }));
    // Все действия секции «Глобально» с when=online доступны вне домашнего экрана
    // (home.overview/home.resume контекстны только на Home, но видны везде). Порядок —
    // каталожный: контекстных на «Чатах» нет, поэтому все идут в порядке AI_ACTIONS.
    expect(r.map(x => x.action.id)).toEqual([
      'global.briefing', 'global.search', 'global.whatsnew',
      'home.overview', 'home.resume', 'global.capture',
    ]);
  });

  it('в открытой заметке действия заметки идут первыми (контекстные)', () => {
    const r = rankedActions(ctx({ nav: { screen: 'notes', note: 'n1' } }));
    const ids = r.map(x => x.action.id);
    expect(ids[0]).toBe('note.links');
    expect(r[0].contextual).toBe(true);
    expect(ids).toContain('note.ask');
    // глобальные — в конце и не контекстные
    expect(ids).toContain('global.whatsnew');
    expect(r.find(x => x.action.id === 'global.whatsnew')!.contextual).toBe(false);
  });

  it('«Поиск по смыслу» появляется только при caps.semantic', () => {
    const off = rankedActions(ctx({ nav: { screen: 'notes', note: 'n1' } }));
    expect(off.map(x => x.action.id)).not.toContain('note.semantic');
    const on = rankedActions(ctx({ nav: { screen: 'notes', note: 'n1' }, caps: { semantic: true } }));
    expect(on.map(x => x.action.id)).toContain('note.semantic');
  });

  it('действия чата требуют открытого чата с перепиской', () => {
    const openChat = { active: true, hasMessages: true };
    // Чат не открыт (нет переписки) → действий чата нет
    const noChat = rankedActions(ctx({ nav: { screen: 'chats' } }));
    expect(noChat.map(x => x.action.id)).not.toContain('chat.extract');

    const r = rankedActions(ctx({
      nav: { screen: 'chats', chatId: 'c1' },
      chat: openChat,
    }));
    const ids = r.map(x => x.action.id);
    expect(ids).toContain('chat.extract');
    expect(ids).toContain('chat.summary');
    expect(ids).toContain('global.briefing');
    expect(r[0].contextual).toBe(true); // открытый чат → контекстные наверху
  });

  it('в открытой задаче видны все три действия задачи', () => {
    const r = rankedActions(ctx({ nav: { screen: 'project', task: 't1' } }));
    const ids = r.map(x => x.action.id);
    expect(ids).toContain('task.subtasks');
    expect(ids).toContain('task.description');
    expect(ids).toContain('task.execute');
    expect(r[0].contextual).toBe(true);
  });

  it('офлайн скрывает серверные действия заметки, но «Спросить Claude» остаётся', () => {
    const r = rankedActions(ctx({ nav: { screen: 'notes', note: 'n1' }, online: false }));
    const ids = r.map(x => x.action.id);
    expect(ids).not.toContain('note.links');
    expect(ids).toContain('note.ask');
  });

  it('текстовый фильтр сужает выдачу', () => {
    const r = rankedActions(ctx({ nav: { screen: 'notes', note: 'n1' } }), 'связи');
    expect(r.map(x => x.action.id)).toEqual(['note.links']);
  });
});
