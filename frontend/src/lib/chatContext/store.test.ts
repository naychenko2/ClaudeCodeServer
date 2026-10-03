import { beforeEach, describe, expect, it, vi } from 'vitest';

const h = vi.hoisted(() => ({
  request: vi.fn(),
  onMessage: null as null | ((m: unknown) => void),
  onReconnected: null as null | (() => void),
  toast: vi.fn(),
}));
vi.mock('../offline', () => ({ request: h.request }));
vi.mock('../signalr', () => ({
  onMessage: (fn: (m: unknown) => void) => { h.onMessage = fn; return () => { h.onMessage = null; }; },
  onReconnected: (fn: () => void) => { h.onReconnected = fn; return () => { h.onReconnected = null; }; },
}));
vi.mock('../toast', () => ({ showToast: h.toast }));

import { RELEASE_UNDO_MS } from '../../components/generation/useReleaseUndo';
import {
  __applyChatContext, __resetChatContextStore, ensureChatContext, forgetChatContext, getChatContextState,
  releasePrimary, setPrimary, undoReleasePrimary, attachRef, __subscribeForTests, __offerForTests,
} from './store';
import type { ChatContextDto, ChatContextPrimary } from './types';

const primary = (id = 'ci_1'): ChatContextPrimary => ({
  id, kind: 'image', ref: { threadId: 't1', versionId: 'v1' }, role: null, by: 'human',
  addedAt: '2026-10-03T09:00:00Z', label: 'hero.png', version: 'v1', thumb: null, missing: false,
});
const dto = (revision: number, withPrimary = true): ChatContextDto =>
  ({ revision, primary: withPrimary ? primary() : null, refs: [] });
const conflict = (context: ChatContextDto) =>
  Object.assign(new Error('Conflict'), { status: 409, body: { error: 'context_changed', context } });

// Чат показан: состояние пришло GET'ом. Подписки стора в node без рендера хука не ставятся —
// тесты событий и перечитки включают их через __subscribeForTests
async function showChat(sessionId: string, state: ChatContextDto) {
  h.request.mockResolvedValueOnce(state);
  await ensureChatContext(sessionId);
}

beforeEach(() => {
  vi.useRealTimers();
  h.request.mockReset();
  h.toast.mockReset();
  __resetChatContextStore();
});

describe('стор контекста чата', () => {
  it('GET кладёт состояние в кэш', async () => {
    await showChat('s1', dto(3));
    expect(getChatContextState('s1').revision).toBe(3);
    expect(getChatContextState('s1').primary?.label).toBe('hero.png');
  });

  it('событие chat_context_changed обновляет показанный чат, старое и чужое пропускает', async () => {
    await showChat('s1', dto(5));
    const off = __subscribeForTests();
    h.onMessage!({ type: 'chat_context_changed', sessionId: 's1', context: dto(6, false) });
    expect(getChatContextState('s1').revision).toBe(6);
    expect(getChatContextState('s1').primary).toBeNull();
    h.onMessage!({ type: 'chat_context_changed', sessionId: 's1', context: dto(4) });
    expect(getChatContextState('s1').revision).toBe(6);
    h.onMessage!({ type: 'chat_context_changed', sessionId: 'other', context: dto(9) });
    expect(getChatContextState('other').revision).toBe(0);
    off();
  });

  it('события не по порядку: принимается только ревизия больше известной, равная — нет', async () => {
    await showChat('s1', dto(5));
    const off = __subscribeForTests();
    h.onMessage!({ type: 'chat_context_changed', sessionId: 's1', context: dto(7, false) });
    h.onMessage!({ type: 'chat_context_changed', sessionId: 's1', context: dto(6) });
    expect(getChatContextState('s1').revision).toBe(7);
    expect(getChatContextState('s1').primary).toBeNull();
    // Та же ревизия с другим содержимым — дубль или гонка, состояние не меняется
    h.onMessage!({ type: 'chat_context_changed', sessionId: 's1', context: dto(7, true) });
    expect(getChatContextState('s1').primary).toBeNull();
    off();
  });

  it('после переподключения показанный чат перечитывается', async () => {
    await showChat('s1', dto(5));
    const off = __subscribeForTests();
    h.request.mockResolvedValueOnce(dto(8, false));
    h.onReconnected!();
    await vi.waitFor(() => expect(getChatContextState('s1').revision).toBe(8));
    expect(h.request).toHaveBeenCalledTimes(2);
    off();
  });

  it('мутация уходит с текущей ревизией, 409 подставляет DTO из тела', async () => {
    __applyChatContext('s1', dto(5));
    h.request.mockRejectedValueOnce(conflict(dto(7, false)));
    const r = await attachRef('s1', { kind: 'project-file', ref: { path: 'a.md' } });
    expect(r).toBe('conflict');
    expect(JSON.parse(h.request.mock.calls[0][1].body).revision).toBe(5);
    expect(getChatContextState('s1').revision).toBe(7);
    expect(getChatContextState('s1').primary).toBeNull();
    expect(h.toast).toHaveBeenCalled();
  });

  it('другая ошибка не меняет кэш и даёт failed', async () => {
    __applyChatContext('s1', dto(5));
    h.request.mockRejectedValueOnce(Object.assign(new Error('boom'), { status: 500 }));
    expect(await setPrimary('s1', { kind: 'image', ref: {} })).toBe('failed');
    expect(getChatContextState('s1').revision).toBe(5);
  });
});

describe('«Вернуть» после снятия основного объекта', () => {
  it('✕ человека ставит плашку, «Вернуть» ставит тот же объект и гасит плашку', async () => {
    __applyChatContext('s1', dto(5));
    h.request.mockResolvedValueOnce(dto(6, false));
    expect(await releasePrimary('s1')).toBe('ok');
    expect(__offerForTests('s1')?.text).toBe('hero.png');
    h.request.mockResolvedValueOnce(dto(7));
    expect(await undoReleasePrimary('s1')).toBe('ok');
    const body = JSON.parse(h.request.mock.calls[1][1].body);
    expect(body).toMatchObject({ kind: 'image', ref: { threadId: 't1', versionId: 'v1' }, revision: 6 });
    expect(__offerForTests('s1')).toBeNull();
    expect(await undoReleasePrimary('s1')).toBeNull();
  });

  it('плашка гаснет через 4 с', async () => {
    vi.useFakeTimers();
    __applyChatContext('s1', dto(5));
    h.request.mockResolvedValueOnce(dto(6, false));
    await releasePrimary('s1');
    expect(__offerForTests('s1')).not.toBeNull();
    vi.advanceTimersByTime(RELEASE_UNDO_MS + 1);
    expect(__offerForTests('s1')).toBeNull();
  });

  it('снятие не человеком плашку не ставит, а событие снятия гасит прежнюю', async () => {
    __applyChatContext('s1', dto(5));
    h.request.mockResolvedValueOnce(dto(6, false));
    await releasePrimary('s1', false);
    expect(__offerForTests('s1')).toBeNull();

    __applyChatContext('s2', dto(5));
    h.request.mockResolvedValueOnce(dto(6, false));
    await releasePrimary('s2');
    expect(__offerForTests('s2')).not.toBeNull();
    __applyChatContext('s2', dto(7));
    __applyChatContext('s2', dto(8, false)); // агент снял объект, поставленный им же заново
    expect(__offerForTests('s2')).toBeNull();
  });

  it('удаление чата убирает кэш и плашку', async () => {
    __applyChatContext('s1', dto(5));
    h.request.mockResolvedValueOnce(dto(6, false));
    await releasePrimary('s1');
    forgetChatContext('s1');
    expect(__offerForTests('s1')).toBeNull();
    expect(getChatContextState('s1').revision).toBe(0);
  });
});
